using Vortice.DXGI;

namespace MKVision.Desktop;

// Desempenho com o ComfyUI e o VR (Quest + OWL3D) disputando a mesma placa:
// - Prioridades: o MK Vision acima do normal (processador e placa) e a thread da tela na mais alta; o ComfyUI abaixo do
//   normal (ComfyClient.Tame).
// - Modo ComfyUI: enquanto o ComfyUI gera (pela tecla G ou por fora, pela página dele), o MK Vision se alivia sozinho —
//   a ampliação usa a IA rápida, a câmera lenta com IA pausa (o vídeo fica lento sem quadros novos) e as IAs saem da
//   memória da placa. Terminou: tudo volta.
// - Desenhar só no ritmo do vídeo: tocando, sem câmera lenta e sem nada mudando na tela além do vídeo, a tela só é
//   desenhada quando há quadro novo (num vídeo de 30 quadros, 30 vezes por segundo, não 144) — sobra placa para o
//   OWL3D, que também processa cada quadro que a janela mostra. MKV_FULL_REDRAW volta ao antigo (para comparar).
internal sealed partial class Player
{
    // Com _lock: o quadro do vídeo do último desenho (Draw).
    private Frame? _drawnFrame;
    private long _drawnSerial = -1;

    private static readonly bool FullRedraw = Environment.GetEnvironmentVariable("MKV_FULL_REDRAW") is not null;
    private IDXGIOutput? _vblankOutput;
    private long _vblankOutputAt;

    private volatile bool _comfyBusy;
    private Timer? _comfyPoll;
    private int _polling, _polls;
    private bool? _comfyBusyTest; // autoteste: finge o ComfyUI gerando (ou não)

    private void StartPerformance()
    {
        Priorities.RaiseSelf();
        try { _renderThread.Priority = ThreadPriority.Highest; } catch { }
        if (ComfyClient.Installed) _comfyPoll = new Timer(_ => _ = PollComfy(), null, 3000, 2000);
    }

    // ---------- Modo ComfyUI ----------

    private async Task PollComfy()
    {
        if (Interlocked.Exchange(ref _polling, 1) == 1 || _disposed) return;
        try
        {
            bool busy = _comfyBusyTest ?? (_comfyJob is { IsCancellationRequested: false } || await ComfyClient.Busy());
            if (busy != _comfyBusy && !_disposed) PostUi(() => SetComfyBusy(busy));
            // Gerando: a prioridade da placa só pega depois que o processo abriu a placa — confere de novo a cada ~10 s.
            if (busy && ++_polls % 5 == 0) ComfyClient.Tame();
        }
        finally { Volatile.Write(ref _polling, 0); }
    }

    private void SetComfyBusy(bool busy)
    {
        if (busy == _comfyBusy) return;
        _comfyBusy = busy;
        bool own = _comfyJob is not null; // pela tecla G: os avisos dela já contam o andamento
        lock (_lock)
        {
            _settings.LightAi = busy || _prefs.Performance == 2; // a IA de ampliação rápida
            _settingsVersion++;
            _dirty = true;
        }
        if (busy)
        {
            Log.Write("Desempenho: o ComfyUI está gerando — modo leve (IA rápida, sem a câmera lenta com IA).");
            if (!own) Notify("ComfyUI gerando: MK Vision em modo leve (câmera lenta com IA pausada)");
            ComfyClient.Tame(); // um ComfyUI aberto por fora também fica abaixo do normal
            _ = Task.Run(() => { if (RifeHost.Release() | PhotoAi.Release() | ToneAi.Release()) GC.Collect(); });
        }
        else
        {
            Log.Write("Desempenho: o ComfyUI terminou — qualidade normal.");
            if (!own) Notify("ComfyUI terminou: MK Vision com a qualidade normal");
            bool video;
            lock (_lock) video = _file is not null;
            if (video) StartSlowAi(); // a câmera lenta com IA volta (do cache: segundos)
        }
        _renderWake.Set();
    }

    // ---------- Desenhar só no ritmo do vídeo ----------

    // Na thread da tela: nada a desenhar nesta atualização da tela? (o mesmo quadro do vídeo, sem mais nada mudando)
    private bool SkipThisRefresh()
    {
        if (FullRedraw) return false;
        lock (_lock)
        {
            string reason = Reason();
            if (TestPacing is { } counts) counts[reason] = counts.GetValueOrDefault(reason) + 1;
            return reason == "pula";
        }
        string Reason()
        {
            if (!_playing || _file is null || _webMode || _queue.Count == 0) return "parado/vazio";
            if (OsdActive()) return "aviso";
            if (SlowActive()) return "câmera lenta";
            if (_seekDragging || _volumeDragging || _annotating || _cropping) return "mexendo";
            var front = _queue[0];
            if (!ReferenceEquals(front, _drawnFrame) || front.Serial != _drawnSerial) return "quadro novo na frente";
            if (_ended && _queue.Count == 1) return "fim"; // o fim do vídeo (o Draw para o relógio)
            return _queue.Count >= 2 && _queue[1].Time <= MediaNow() ? "chegou o próximo" : "pula";
        }
    }

    // Espera a próxima atualização da tela sem desenhar (pelo monitor onde a janela está).
    private void WaitRefresh()
    {
        try
        {
            if (_vblankOutput is null || Environment.TickCount64 - _vblankOutputAt > 2000) // a janela pode ter mudado de monitor
            {
                _vblankOutput?.Dispose();
                _vblankOutput = _swapChain?.GetContainingOutput();
                _vblankOutputAt = Environment.TickCount64;
            }
            if (_vblankOutput is not null)
            {
                _vblankOutput.WaitForVBlank();
                return;
            }
        }
        catch
        {
            _vblankOutput?.Dispose();
            _vblankOutput = null;
        }
        _renderWake.WaitOne(4);
    }

    // Autoteste: por que cada atualização da tela desenhou ou pulou (com _lock).
    public Dictionary<string, int>? TestPacing;
    public bool TestComfyBusy
    {
        get => _comfyBusy;
        set
        {
            _comfyBusyTest = value;
            PostUi(() => SetComfyBusy(value));
        }
    }
    public bool TestLightAi { get { lock (_lock) return _settings.LightAi; } }
    public bool TestSlowActive { get { lock (_lock) return SlowActive(); } }
}
