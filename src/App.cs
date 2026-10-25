using System.Diagnostics;
using Vortice.Direct3D11;
using Vortice.Mathematics;
using Windows.Graphics;

namespace MKVision.Desktop;

internal sealed class App : IDisposable
{
    private const int HotkeyToggle = 1, HotkeyBypass = 2, HotkeyQuit = 3, HotkeyStronger = 4, HotkeyWeaker = 5, HotkeyAnalysis = 6, HotkeyPlayer = 7;
    private const int HotkeyPower1 = 11; // 11–15 = Ctrl+Alt+1…5
    private const uint VK_UP = 0x26, VK_DOWN = 0x28;

    private readonly Gpu _gpu;
    private readonly Overlay _overlay;
    private readonly Settings _settings = new();
    private readonly Pipeline _pipeline;
    private readonly Link _link;
    private readonly SlowMotion _slow;
    private readonly object _lock = new();
    private readonly object _gpuLock = new(); // sequências de passos na GPU: captura e exibição não se misturam
    private readonly object _presentLock = new(); // a camada (BeginFrame → Present) é de uma thread por vez

    private Capture? _capture;
    private IntPtr _target;
    private string? _browser; // "edge" / "chrome" se a janela capturada for um desses

    // Atualizado pela thread principal, lido pela thread da captura.
    private Native.RECT _crop;     // área a processar dentro do quadro capturado
    private VideoView? _video;     // com a extensão: onde está o vídeo e em que resolução
    private bool _visible;
    private bool _slowMode;        // câmera lenta suave: a captura só alimenta a IA, a thread de exibição desenha
    private (VideoView View, int Width, int Height)? _slowJob;

    private readonly Thread _renderThread;
    private readonly AutoResetEvent _slowWake = new(false);
    private volatile bool _running = true;
    private bool _slowActive; // thread da captura

    private readonly Stopwatch _statsClock = Stopwatch.StartNew();
    private int _framesSinceStats;
    private string _mode = "";

    public App(Gpu gpu, Overlay overlay)
    {
        _gpu = gpu;
        _overlay = overlay;
        _pipeline = new Pipeline(gpu);
        _link = new Link(_settings);
        _link.OpenRequested += path => _openRequests.Enqueue(path); // abre na thread principal (timer)
        _slow = new SlowMotion(gpu, _gpuLock); // carrega a IA de quadros (TensorRT) em segundo plano
        _renderThread = new Thread(RenderLoop) { IsBackground = true, Name = "Câmera lenta", Priority = ThreadPriority.AboveNormal };
        _renderThread.Start();
    }

    public void Run()
    {
        const uint mods = Native.MOD_CONTROL | Native.MOD_ALT | Native.MOD_NOREPEAT;
        if (!Native.RegisterHotKey(IntPtr.Zero, HotkeyToggle, mods, 'M') ||
            !Native.RegisterHotKey(IntPtr.Zero, HotkeyBypass, mods, 'B') ||
            !Native.RegisterHotKey(IntPtr.Zero, HotkeyQuit, mods, 'Q'))
        {
            // O player continua funcionando; só os atalhos globais do modo Edge ficam sem efeito.
            Log.Write("Não consegui registrar os atalhos Ctrl+Alt (outro programa já usa Ctrl+Alt+M/B/Q?).");
        }
        // Força: segurar repete (sem MOD_NOREPEAT).
        Native.RegisterHotKey(IntPtr.Zero, HotkeyStronger, Native.MOD_CONTROL | Native.MOD_ALT, VK_UP);
        Native.RegisterHotKey(IntPtr.Zero, HotkeyWeaker, Native.MOD_CONTROL | Native.MOD_ALT, VK_DOWN);
        for (int level = 1; level <= 5; level++) Native.RegisterHotKey(IntPtr.Zero, HotkeyPower1 + level - 1, mods, (uint)('0' + level));
        Native.RegisterHotKey(IntPtr.Zero, HotkeyAnalysis, mods, 'A'); // câmera lenta: tamanho da análise
        Native.RegisterHotKey(IntPtr.Zero, HotkeyPlayer, mods, 'P');   // player de arquivos
        Native.SetTimer(IntPtr.Zero, IntPtr.Zero, 33, IntPtr.Zero); // ~30x por segundo: segue a janela e o vídeo

        while (Native.GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            if (msg.message == Native.WM_HOTKEY) OnHotkey((int)msg.wParam);
            else if (msg.message == Native.WM_TIMER && msg.hwnd == IntPtr.Zero)
            {
                UpdatePlacement();
                while (_openRequests.TryDequeue(out var path))
                {
                    Log.Write("Abrindo no player (pedido da extensão).");
                    OpenPlayer(path);
                    _player?.BringToFront();
                }
            }
            Native.TranslateMessage(ref msg);
            Native.DispatchMessage(ref msg);
        }
    }

    private void OnHotkey(int id)
    {
        switch (id)
        {
            case HotkeyToggle when _capture is null:
                Start(Native.GetAncestor(Native.GetForegroundWindow(), Native.GA_ROOT));
                break;
            case HotkeyToggle:
                Stop();
                Log.Write("Desligado.");
                break;
            case HotkeyBypass:
                _settings.Bypass = !_settings.Bypass;
                Log.Write(_settings.Bypass ? "Comparação: SEM processamento" : "Comparação: COM processamento");
                break;
            case HotkeyQuit:
                Native.PostQuitMessage(0);
                break;
            case HotkeyPlayer:
                OpenPlayer(null);
                break;
            case HotkeyAnalysis:
                Log.Write($"Câmera lenta: análise em {_slow.CycleAnalysisFactor() * 100:0}% do tamanho máximo");
                break;
            case HotkeyStronger or HotkeyWeaker:
                _settings.Strength = Math.Clamp(_settings.Strength + (id == HotkeyStronger ? 0.25f : -0.25f), 0f, 4f);
                Log.Write($"Força: {_settings.Strength * 100:0}%");
                break;
            case >= HotkeyPower1 and < HotkeyPower1 + 5:
                _settings.Power = id - HotkeyPower1 + 1;
                Log.Write($"Potência: {_settings.Power}");
                break;
        }
    }

    private readonly System.Collections.Concurrent.ConcurrentQueue<string> _openRequests = new();

    // Player (janela principal; Ctrl+Alt+P traz para a frente).
    private Player? _player;
    internal Player? PlayerForTest => _player;

    // O player é a janela principal do MK Vision: abre junto com o app e, ao ser fechado, fecha o app.
    // "browser": sem vídeo, abre no navegador (o começo do app).
    public void OpenPlayer(string? path, bool browser = false)
    {
        try
        {
            if (_player is null)
            {
                var player = new Player(_gpu, _gpuLock);
                player.Closed += () =>
                {
                    if (_player != player) return;
                    _player = null;
                    Native.PostQuitMessage(0);
                };
                _player = player;
            }
            else
            {
                _player.BringToFront();
            }
            if (path is not null) _player.OpenTab(path);
            else if (browser) _player.ShowBrowser();
        }
        catch (Exception error)
        {
            Log.Write($"Não consegui abrir o player: {error.Message}");
        }
    }

    private void Start(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || hwnd == _overlay.Hwnd || hwnd == _player?.Hwnd) return;
        try
        {
            _target = hwnd;
            _browser = Native.ProcessName(hwnd) switch { "msedge" => "edge", "chrome" => "chrome", _ => null };
            UpdatePlacement();
            _capture = new Capture(_gpu, hwnd);
            _capture.FrameArrived += OnFrame;
            Log.Write("Ligado na janela em foco.");
            if (_browser is not null && !_link.IsConnected(_browser))
                Log.Write("  (extensão MK Vision não conectada: processando a janela inteira, sem IA)");
        }
        catch (Exception error)
        {
            Log.Write($"Não consegui capturar essa janela: {error.Message}");
            Stop();
        }
    }

    private void Stop()
    {
        if (_capture is not null)
        {
            _capture.FrameArrived -= OnFrame;
            _capture.Dispose();
            _capture = null;
        }
        _target = IntPtr.Zero;
        _browser = null;
        _link.SetState(null, false);
        _takingOver = false;
        lock (_lock)
        {
            _visible = false;
            _slowMode = false;
            _slowJob = null;
        }
        _overlay.Place(default, false);
    }

    // Thread principal: decide o que processar e posiciona a camada.
    // Com a extensão conectada: só o vídeo. Sem ela: a área cliente inteira (fase 1).
    // Só mostra com o navegador em primeiro plano, para não cobrir outros programas.
    private void UpdatePlacement()
    {
        if (_target == IntPtr.Zero) return;
        if (!Native.IsWindow(_target))
        {
            Stop();
            Log.Write("A janela foi fechada. Desligado.");
            return;
        }

        var foreground = Native.GetForegroundWindow();
        bool visible = !Native.IsIconic(_target) && (foreground == _target || foreground == _overlay.Hwnd);
        var client = Native.ClientRectOnScreen(_target);
        var frame = Native.FrameBounds(_target);
        var area = client;
        VideoView? video = null;

        bool linked = _browser is not null && _link.IsConnected(_browser);
        bool slow = false;
        string? skipReason = null;
        if (linked)
        {
            var g = _link.GeometryFor(_browser!);
            // Só vale assumir quando há muito o que ampliar: vídeo de baixa resolução ou lupa com zoom.
            // Em vídeo já nítido para a tela, a extensão (que lê o vídeo original) é melhor.
            double scale = g is null ? 0 : g.OutWidth * g.Dpr / Math.Max(1, g.VideoWidth) / g.UvWidth;
            // Câmera lenta suave também: a IA de quadros do app (TensorRT) é bem mais rápida que a do navegador.
            slow = g is not null && g.Slow && _slow.Ready;
            _takingOver = g is not null && (slow || scale >= (_takingOver ? TakeOverExit : TakeOverEnter));
            _link.SetState(_browser, _takingOver);

            if (g is null)
            {
                visible = false; // nenhum vídeo na aba: não cobre a página
            }
            else if (!_takingOver)
            {
                visible = false;
                skipReason = $"vídeo {g.VideoWidth}x{g.VideoHeight} ampliado só {scale:0.0}x: a extensão cuida (o app entra a partir de {TakeOverEnter}x — vídeo de baixa resolução ou lupa)";
            }
            else
            {
                // O viewport fica embaixo das barras do navegador (em tela cheia, ocupa tudo).
                int viewportTop = g.Fullscreen ? 0 : client.Height - (int)Math.Round(g.InnerHeight * g.Dpr);
                Native.RECT ToScreen(double x, double y, double w, double h)
                {
                    int left = Math.Max(client.Left + (int)Math.Round(x * g.Dpr), client.Left);
                    int top = Math.Max(client.Top + viewportTop + (int)Math.Round(y * g.Dpr), client.Top);
                    return new Native.RECT
                    {
                        Left = left, Top = top,
                        Right = Math.Min(client.Left + (int)Math.Round((x + w) * g.Dpr), client.Right),
                        Bottom = Math.Min(client.Top + viewportTop + (int)Math.Round((y + h) * g.Dpr), client.Bottom)
                    };
                }
                // Captura onde o navegador mostra a imagem; desenha onde o resultado deve aparecer
                // (com a lupa, pode avançar sobre as faixas pretas do player).
                var source = ToScreen(g.X, g.Y, g.Width, g.Height);
                area = ToScreen(g.OutX, g.OutY, g.OutWidth, g.OutHeight);
                var region = new Box(source.Left - frame.Left, source.Top - frame.Top, 0, source.Right - frame.Left, source.Bottom - frame.Top, 1);
                video = new VideoView(region, g.VideoWidth, g.VideoHeight, (float)g.UvX, (float)g.UvY, (float)g.UvWidth, (float)g.UvHeight);
            }
        }

        lock (_lock)
        {
            _crop = new Native.RECT { Left = area.Left - frame.Left, Top = area.Top - frame.Top, Right = area.Right - frame.Left, Bottom = area.Bottom - frame.Top };
            _video = video;
            _visible = visible;
            _slowMode = slow && video is not null;
        }
        _overlay.Place(area, visible);

        string state = Native.IsIconic(_target) || (foreground != _target && foreground != _overlay.Hwnd)
            ? $"pausado: outra janela em primeiro plano ({Native.ProcessName(foreground)})"
            : !linked ? "sem a extensão: processando a janela inteira"
            : skipReason is not null ? skipReason
            : video is null ? "aguardando a extensão informar um vídeo"
            : $"processando o vídeo {video.Value.NativeWidth}x{video.Value.NativeHeight} em {area.Width}x{area.Height}" +
              (slow ? " — câmera lenta com IA de quadros" : "");
        if (state != _placementState)
        {
            _placementState = state;
            Log.Write($"Estado: {state}");
        }
    }

    private string _placementState = "";

    // Ampliação (vídeo → tela, incluindo a lupa) a partir da qual o app assume; a saída é um pouco
    // menor para não ficar alternando no limite.
    private const double TakeOverEnter = 1.5, TakeOverExit = 1.35;
    private bool _takingOver;

    // Thread da captura: processa o quadro e apresenta na camada.
    private void OnFrame(ID3D11Texture2D texture, SizeInt32 contentSize, double captureTime)
    {
        Native.RECT crop;
        VideoView? video;
        bool slow;
        lock (_lock)
        {
            if (!_visible) return;
            crop = _crop;
            video = _video;
            slow = _slowMode;
        }
        if (_slowActive && !slow)
        {
            lock (_gpuLock) _slow.Reset();
            _slowActive = false;
        }
        int left = Math.Max(crop.Left, 0), top = Math.Max(crop.Top, 0);
        int right = Math.Min(crop.Right, contentSize.Width), bottom = Math.Min(crop.Bottom, contentSize.Height);
        int width = right - left, height = bottom - top;
        if (width < 16 || height < 16) return;

        if (video is { } v)
        {
            var r = v.Region;
            var region = new Box(Math.Max(r.Left, 0), Math.Max(r.Top, 0), 0, Math.Min(r.Right, contentSize.Width), Math.Min(r.Bottom, contentSize.Height), 1);
            if (region.Right - region.Left < 16 || region.Bottom - region.Top < 16) return;
            var view = v with { Region = region };
            if (slow)
            {
                // Câmera lenta: só guarda o quadro (resolução original) para a IA; a thread de exibição desenha.
                _slow.Feed(() => _pipeline.Extract(texture, view, out _), captureTime); // trava a GPU só no que precisa
                _slowActive = true;
                lock (_lock) _slowJob = (view, width, height);
                _slowWake.Set();
                return;
            }
            lock (_presentLock)
            {
                lock (_gpuLock)
                {
                    var target = _overlay.BeginFrame(width, height);
                    _pipeline.Process(texture, view, target, width, height, _settings);
                }
                _overlay.Present();
            }
            _mode = _pipeline.LastInfo;
        }
        else
        {
            lock (_presentLock)
            {
                lock (_gpuLock)
                {
                    var target = _overlay.BeginFrame(width, height);
                    _gpu.Process(texture, new Box(left, top, 0, right, bottom, 1), target, _settings);
                }
                _overlay.Present();
            }
            _mode = $"janela inteira {width}x{height}";
        }
        CountFrame();
    }

    // Thread de exibição da câmera lenta: um quadro intermediário a cada atualização da tela (Present espera o vsync).
    private void RenderLoop()
    {
        while (_running)
        {
            (VideoView View, int Width, int Height)? job;
            lock (_lock) job = _visible && _slowMode ? _slowJob : null;
            if (job is not { } j)
            {
                _slowWake.WaitOne(100);
                continue;
            }
            bool drawn = false;
            try
            {
                // Present (que espera o vsync) fica fora do lock da GPU: a captura pode guardar o próximo
                // quadro real enquanto a tela espera.
                lock (_presentLock)
                {
                    lock (_gpuLock)
                    {
                        var frame = _slow.Interpolate();
                        if (frame is not null)
                        {
                            var target = _overlay.BeginFrame(j.Width, j.Height);
                            _pipeline.Finish(frame.View!, frame.Width, frame.Height, j.View, target, j.Width, j.Height, _settings, resized: false, allowTemporal: false);
                            drawn = true;
                        }
                    }
                    if (drawn) _overlay.Present();
                }
            }
            catch (Exception error)
            {
                Log.Write($"Câmera lenta: falha ao desenhar — {error.Message}");
                Thread.Sleep(500);
            }
            if (!drawn)
            {
                _slowWake.WaitOne(10);
                continue;
            }
            _mode = $"{_pipeline.LastInfo} | {_slow.Info}";
            MeasureSlowFrame();
            CountFrame();
        }
    }

    // Travadinhas na câmera lenta: intervalo entre quadros de tela bem acima do normal (o normal é
    // aprendido: ~7 ms num monitor de 144 Hz). Vai para o log junto com as medições da SlowMotion e do .NET.
    private long _lastPresent;
    private double _normalInterval = 1000 / 144.0;
    private int _hitches;
    private double _worstInterval;
    private int _gcStart = GC.CollectionCount(2);
    private TimeSpan _gcPauseStart = GC.GetTotalPauseDuration();

    private void MeasureSlowFrame()
    {
        long now = Stopwatch.GetTimestamp();
        if (_lastPresent != 0)
        {
            double ms = (now - _lastPresent) * 1000.0 / Stopwatch.Frequency;
            if (ms > _normalInterval * 1.6) _hitches++;
            else _normalInterval = _normalInterval * 0.98 + ms * 0.02;
            _worstInterval = Math.Max(_worstInterval, ms);
        }
        _lastPresent = now;
    }

    private string TakeSlowStats()
    {
        int gc = GC.CollectionCount(2);
        var pause = GC.GetTotalPauseDuration();
        string text = $"    travadas na tela: {_hitches} (pior intervalo {_worstInterval:0} ms, normal {_normalInterval:0.0} ms) | " +
                      $"limpezas grandes do .NET: {gc - _gcStart} ({(pause - _gcPauseStart).TotalMilliseconds:0} ms parado) | {_slow.TakeStats()}";
        _hitches = 0;
        _worstInterval = 0;
        _gcStart = gc;
        _gcPauseStart = pause;
        return text;
    }

    private void CountFrame()
    {
        _framesSinceStats++;
        if (_statsClock.ElapsedMilliseconds >= 2000)
        {
            Log.Write($"  {_framesSinceStats * 1000.0 / _statsClock.ElapsedMilliseconds:0} quadros/s — {_mode}");
            if (_slowActive) Log.Write(TakeSlowStats());
            else _lastPresent = 0;
            _framesSinceStats = 0;
            _statsClock.Restart();
        }
    }

    public void Dispose()
    {
        Stop();
        _running = false;
        _slowWake.Set();
        _renderThread.Join(1000);
        lock (_gpuLock) _slow.Dispose();
        _player?.Dispose();
        _link.Dispose();
        _pipeline.Dispose();
    }
}
