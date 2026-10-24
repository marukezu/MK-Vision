using System.Diagnostics;
using System.Runtime.InteropServices;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace MKVision.Desktop;

// Câmera lenta suave no app: IA de interpolação (RIFE + TensorRT) entre os quadros reais do vídeo.
//
// A captura chega o tempo todo; uma miniatura revela quando o vídeo mostrou um quadro real novo. Cada
// quadro novo é guardado (5 slots: 4 em uso + 1 livre para o próximo) e o par anterior→novo vai para a
// IA numa tarefa em segundo plano, em vários momentos (quantos couberem no tempo) e na maior resolução
// que couber. Um laço na taxa da tela desenha o quadro intermediário do momento atual, com 2,5 quadros
// reais de atraso, para a análise do próximo par já estar pronta quando chega a hora.
//
// Sem travadinhas:
//   • relógio em quadros, constante, com correção lenta: o navegador mostra os quadros com ±20 ms de
//     tremor, e segui-lo fazia o movimento acelerar e frear;
//   • hora do quadro = a que o Windows marcou na captura (não a de quando terminamos de lê-lo);
//   • a captura não segura a GPU enquanto espera a leitura de volta (a tela continua sendo desenhada);
//   • memória reaproveitada: sem blocos grandes novos a cada quadro (o .NET pararia tudo para limpar).
internal sealed class SlowMotion : IDisposable
{
    private sealed class Frame
    {
        public int Slot;
        public long Number;      // sequência dos quadros reais
        public double Time;      // segundos (relógio do sistema) em que o quadro apareceu na captura
        public float[] Rgb = []; // entrada da IA (RGB planar 0–1, Width×Height)
        public int Width, Height;
    }

    private const int Slots = 5;     // 4 quadros em uso + 1 livre para o próximo
    private const int Kept = 4;      // quadros reais guardados (o atraso de 2,5 quadros precisa de 4)
    private const int ThumbW = 64, ThumbH = 36;
    private const int MaxW = 1920, MaxH = 1088; // limite do motor otimizado do TensorRT (Rife.cs)

    private readonly Gpu _gpu;
    private readonly object _gpuLock;
    private readonly FrameInterpolator _fi; // análise da IA e desenho do quadro intermediário (comum ao player)
    private readonly RenderTexture[] _frames;
    private readonly RenderTexture _interp, _thumb;
    private readonly ID3D11Texture2D _thumbStaging;
    private byte[] _thumbBytes = new byte[ThumbW * ThumbH * 4], _lastThumb = new byte[ThumbW * ThumbH * 4];
    private bool _hasThumb;
    private readonly float[][] _rgbRing = new float[Slots + 1][]; // entrada da IA de cada quadro, reaproveitada
    private int _rgbNext;

    private readonly object _lock = new();
    private readonly List<Frame> _list = [];
    private readonly Motion?[] _motions = new Motion?[Slots]; // pelo slot do quadro mais antigo do par
    private (Frame A, Frame B)? _queued;
    private bool _busy;
    private int _nextSlot;
    private long _nextNumber;
    private double _span = 1 / 30.0;     // duração de um quadro real em tempo real (média móvel)
    private bool _spanKnown;
    private int _spanSamples;

    // Relógio da exibição, em quadros reais (Frame.Number + fração), 2,5 quadros atrás do mais novo.
    // Os quadros do arquivo têm espaçamento igual, mas o navegador os mostra alguns ciclos do monitor antes ou
    // depois (±20 ms medidos num vídeo de 60 fps em 0.1x). Seguir a hora de chegada de cada um — direto ou com
    // correção rápida — fazia o movimento acelerar e frear. Aqui o relógio anda num ritmo constante e só se
    // corrige devagar (~1,5 s), o que absorve esse tremor.
    private const double Lag = 2.5;
    private const double SyncSeconds = 1.5;
    private double _pos;
    private bool _posValid;
    private double _lastDraw;
    private int _slowRun, _fastRun; // intervalos seguidos bem acima/abaixo da média: a velocidade do vídeo mudou

    // Medições para o log (zeradas a cada leitura).
    private int _statWaiting, _statNoMotion, _statFrames, _statResyncs, _statGaps;
    private double _statFeedMs, _statMinSpan = double.MaxValue, _statMaxSpan;

    public bool Ready => RifeHost.Instance is not null;
    public string Info { get; private set; } = "";

    public static double Now => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;

    public SlowMotion(Gpu gpu, object gpuLock)
    {
        _gpu = gpu;
        _gpuLock = gpuLock;
        _fi = new FrameInterpolator(gpu, gpuLock);
        _frames = Enumerable.Range(0, Slots).Select(_ => new RenderTexture(gpu.Device, Format.R16G16B16A16_Float)).ToArray();
        _interp = new RenderTexture(gpu.Device, Format.R16G16B16A16_Float);
        _thumb = new RenderTexture(gpu.Device, Format.B8G8R8A8_UNorm);
        _thumb.Ensure(ThumbW, ThumbH);
        _thumbStaging = gpu.Device.CreateTexture2D(new Texture2DDescription(Format.B8G8R8A8_UNorm, ThumbW, ThumbH, 1, 1,
            BindFlags.None, ResourceUsage.Staging, CpuAccessFlags.Read));

    }

    public void Reset()
    {
        lock (_lock)
        {
            _list.Clear();
            _queued = null;
            for (int i = 0; i < Slots; i++) { _motions[i]?.Dispose(); _motions[i] = null; }
            _hasThumb = false;
            _posValid = false;
        }
    }

    // Thread da captura. "extract" (chamado com a GPU travada) devolve o vídeo na resolução original;
    // "time" = hora da captura. Devolve true se era um quadro real novo.
    public bool Feed(Func<RenderTexture> extract, double time)
    {
        RifeHost.Start(); // modo Edge: a IA de quadros carrega na primeira captura (a mesma do player)
        if (RifeHost.Instance is null) return false;
        var clock = Stopwatch.StartNew();
        RenderTexture source;
        double held;

        // 1. Mudou? Miniatura idêntica = mesmo quadro (o decodificador repete os pixels exatamente).
        lock (_gpuLock)
        {
            source = extract();
            _gpu.BeginPasses();
            _fi.Resample(source.View!, _thumb);
            _gpu.Context.CopyResource(_thumbStaging, _thumb.Texture!);
            _gpu.Context.Flush();
        }
        held = clock.Elapsed.TotalMilliseconds;
        _fi.ReadStaging(_thumbStaging, ThumbW, ThumbH, _thumbBytes);
        if (_hasThumb)
        {
            // Sem a faixa de baixo: a barra de progresso do player muda sozinha e não é quadro novo.
            long diff = 0;
            int end = ThumbW * (ThumbH * 4 / 5) * 4;
            for (int i = 0; i < end; i += 4)
                diff += Math.Abs(_thumbBytes[i] - _lastThumb[i]) + Math.Abs(_thumbBytes[i + 1] - _lastThumb[i + 1]) + Math.Abs(_thumbBytes[i + 2] - _lastThumb[i + 2]);
            if (diff < ThumbW * ThumbH / 4) return false;
        }
        (_thumbBytes, _lastThumb) = (_lastThumb, _thumbBytes);
        _hasThumb = true;

        // 2. Guarda o quadro no slot livre (nenhum quadro na tela usa) e prepara a entrada da IA.
        int sw = source.Width, sh = source.Height;
        int slot = _nextSlot;
        _nextSlot = (_nextSlot + 1) % Slots;
        var (aw, ah) = AnalysisSize(sw, sh);
        var start = Stopwatch.StartNew();
        lock (_gpuLock)
        {
            _frames[slot].Ensure(sw, sh);
            _gpu.Context.CopyResource(_frames[slot].Texture!, source.Texture!);
        }
        held = Math.Max(held, start.Elapsed.TotalMilliseconds);
        int plane = aw * ah;
        var rgb = _rgbRing[_rgbNext];
        if (rgb is null || rgb.Length != 3 * plane) rgb = _rgbRing[_rgbNext] = new float[3 * plane];
        _rgbNext = (_rgbNext + 1) % _rgbRing.Length;
        _fi.PrepareInput(_frames[slot].View!, aw, ah, rgb);

        lock (_lock)
        {
            var last = _list.Count > 0 ? _list[^1] : null;
            if (last is not null)
            {
                double span = time - last.Time;
                if (span > 1.5 || span <= 0)
                {
                    _list.Clear(); // pausou / pulou: recomeça
                    _posValid = false;
                }
                else
                {
                    // Quantos quadros do arquivo se passaram: normalmente 1; ~2 = o navegador pulou um quadro
                    // (o movimento se espalha por dois tempos, em vez de acelerar).
                    int steps = 1;
                    if (!_spanKnown)
                    {
                        _span = span;
                        _spanKnown = true;
                        _spanSamples = 1;
                    }
                    else
                    {
                        steps = Math.Clamp((int)Math.Round(span / _span), 1, 4);
                        _slowRun = span > _span * 1.6 ? _slowRun + 1 : 0;
                        _fastRun = span < _span * 0.6 ? _fastRun + 1 : 0;
                        if (_slowRun >= 4 || _fastRun >= 4)
                        {
                            _span = span; // vários seguidos: a velocidade do vídeo mudou, não é quadro pulado
                            _spanSamples = 1;
                            steps = 1;
                            _slowRun = _fastRun = 0;
                        }
                        else
                        {
                            // Média: aprende rápido nos primeiros quadros (média simples), depois estável.
                            double weight = Math.Max(0.05, 1.0 / ++_spanSamples);
                            _span = _span * (1 - weight) + span / steps * weight;
                        }
                        if (steps > 1) _statGaps++;
                    }
                    _nextNumber = last.Number + steps;
                    _statMinSpan = Math.Min(_statMinSpan, span);
                    _statMaxSpan = Math.Max(_statMaxSpan, span);
                }
            }
            var frame = new Frame { Slot = slot, Number = _nextNumber++, Time = time, Rgb = rgb, Width = aw, Height = ah };
            _motions[slot]?.Dispose(); // o par que começava neste slot não existe mais
            _motions[slot] = null;
            _list.Add(frame);
            if (_list.Count > Kept) _list.RemoveAt(0);
            if (_list.Count >= 2) _queued = (_list[^2], frame);
            _statFeedMs = Math.Max(_statFeedMs, held);
        }
        StartAnalysis();
        return true;
    }

    private double BudgetMs => _span * 1000 * 0.75;

    // Maior resolução que cabe no tempo (precisão nas bordas — testado ao vivo), até o limite do motor.
    // Só muda com folga (>12%): os dois quadros de um par precisam do mesmo tamanho.
    private double _analysisScale;
    private (int, int) _analysisSource;
    private double _analysisFactorUsed;

    // Tamanho da análise em relação ao máximo (Ctrl+Alt+A). Em movimento grande, a IA às vezes acerta mais
    // olhando a imagem menor: o deslocamento cabe no que ela "enxerga" de uma vez (menos deformação).
    private static readonly double[] AnalysisFactors = [1.0, 0.75, 0.5];
    private volatile int _factorIndex;
    public double AnalysisFactor => AnalysisFactors[_factorIndex];
    public double CycleAnalysisFactor()
    {
        _factorIndex = (_factorIndex + 1) % AnalysisFactors.Length;
        return AnalysisFactor;
    }

    private (int, int) AnalysisSize(int sw, int sh)
    {
        double factor = AnalysisFactor;
        double cap = Math.Min(1, Math.Min((double)MaxW / sw, (double)MaxH / sh)) * factor;
        double scale = cap;
        double affordable = BudgetMs / _fi.MsPerMegapixel;            // megapixels que cabem
        double megapixels = sw * scale * sh * scale / 1e6;
        if (megapixels > affordable) scale *= Math.Sqrt(affordable / megapixels);
        scale = Math.Max(scale, 320.0 / Math.Max(sw, sh));
        if (_analysisSource != (sw, sh) || _analysisFactorUsed != factor || Math.Abs(scale / _analysisScale - 1) > 0.12 || (scale >= cap && _analysisScale < cap))
        {
            _analysisScale = scale;
            _analysisSource = (sw, sh);
            _analysisFactorUsed = factor;
        }
        return (Math.Max(2, (int)(sw * _analysisScale / 2) * 2), Math.Max(2, (int)(sh * _analysisScale / 2) * 2));
    }

    private void StartAnalysis()
    {
        (Frame A, Frame B) pair;
        lock (_lock)
        {
            if (_busy || _queued is null || RifeHost.Instance is null) return;
            pair = _queued.Value;
            _queued = null;
            _busy = true;
        }
        Task.Run(() =>
        {
            try { Analyze(pair.A, pair.B); }
            catch (Exception error) { Log.Write($"Câmera lenta: falha na análise — {error.Message}"); }
            finally
            {
                lock (_lock) _busy = false;
                StartAnalysis(); // o par mais novo que chegou enquanto analisava
            }
        });
    }

    private void Analyze(Frame a, Frame b)
    {
        if (a.Width != b.Width || a.Height != b.Height) return;
        double megapixels = a.Width * a.Height / 1e6;
        // Quantos momentos analisar: os que couberem no tempo, até 8. Cada momento calculado de verdade
        // (sem estimar a partir do vizinho) deixa curvas e balanços mais fiéis.
        int count = (int)(BudgetMs / (_fi.MsPerMegapixel * megapixels));
        var motion = _fi.Analyze(RifeHost.Instance!, a.Rgb, b.Rgb, a.Width, a.Height, count, () => { lock (_lock) return _list.Contains(b); });
        if (motion is null) return; // o par saiu da fila
        motion.Key = b;
        lock (_lock)
        {
            if (_list.Contains(a) && _list.Contains(b))
            {
                _motions[a.Slot]?.Dispose();
                _motions[a.Slot] = motion;
                Info = $"IA de quadros: {a.Width}x{a.Height} (análise {AnalysisFactor * 100:0}%), {motion.Steps.Length} análise(s) por par, {_fi.MsPerMegapixel * megapixels:0} ms cada";
                return;
            }
        }
        motion.Dispose();
    }

    // Laço da tela (com o lock da GPU): quadro a mostrar agora. null = ainda não há quadros.
    // Segura a lista até o fim do desenho: a análise não descarta a textura em uso no meio.
    public RenderTexture? Interpolate()
    {
        lock (_lock) return InterpolateNow();
    }

    private RenderTexture? InterpolateNow()
    {
        if (_list.Count == 0) return null;
        double now = Now;
        var newest = _list[^1];
        Frame a, b;
        Motion? motion = null;
        float s = 0;
        _statFrames++;

        double target = newest.Number - Lag + (now - newest.Time) / _span;
        if (_list.Count < 2 || (!_posValid && target < _list[0].Number))
        {
            // Começando: segura o primeiro quadro até o relógio poder nascer no lugar certo (nascer adiantado
            // obrigava a correção lenta a frear o movimento por alguns segundos).
            a = b = _list[0];
            _posValid = false;
        }
        else
        {
            if (!_posValid || Math.Abs(target - _pos) > 2)
            {
                if (_posValid) _statResyncs++;
                _pos = target; // longe demais (pulo no vídeo, pausa longa): vai direto
            }
            else
            {
                // Ritmo constante + correção lenta da diferença; nunca volta para trás.
                double dt = now - _lastDraw;
                _pos += Math.Max(0, dt / _span + (target - _pos) * (1 - Math.Exp(-dt / SyncSeconds)));
            }
            _posValid = true;
            if (_pos >= newest.Number)
            {
                _pos = newest.Number; // passou do último quadro real: pausado ou o próximo atrasou
                if (now - newest.Time < Math.Max(0.5, _span * 3)) _statWaiting++;
            }
            _pos = Math.Max(_pos, _list[0].Number);
            int i = _list.FindLastIndex(f => f.Number <= _pos);
            if (i >= _list.Count - 1)
            {
                a = b = newest;
            }
            else
            {
                (a, b) = (_list[i], _list[i + 1]);
                s = (float)Math.Clamp((_pos - a.Number) / (b.Number - a.Number), 0, 1);
                motion = _motions[a.Slot] is { } m && m.Key == b ? m : null;
                if (motion is null) _statNoMotion++;
            }
        }
        _lastDraw = now;

        var frameA = _frames[a.Slot];
        int w = frameA.Width, h = frameA.Height;
        _interp.Ensure(w, h);
        _fi.Draw(_interp, frameA.View!, _frames[b.Slot].View!, motion, s);
        return _interp;
    }

    // Medições desde a última leitura, para o log.
    public string TakeStats()
    {
        lock (_lock)
        {
            string spans = _statMaxSpan > 0 ? $"{_statMinSpan * 1000:0}–{_statMaxSpan * 1000:0} ms" : "—";
            string text = $"quadro real a cada {_span * 1000:0} ms (chegaram entre {spans}), pulados pelo navegador {_statGaps}; esperando quadro {_statWaiting}, sem análise pronta {_statNoMotion}, ressincronias {_statResyncs} (de {_statFrames}); captura segurou a GPU até {_statFeedMs:0.0} ms";
            _statWaiting = _statNoMotion = _statFrames = _statResyncs = _statGaps = 0;
            _statFeedMs = _statMaxSpan = 0;
            _statMinSpan = double.MaxValue;
            return text;
        }
    }

    public void Dispose()
    {
        Reset();
        _thumbStaging.Dispose();
        foreach (var f in _frames) f.Dispose();
        _interp.Dispose();
        _thumb.Dispose();
        _fi.Dispose();
    }
}
