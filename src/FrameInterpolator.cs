using System.Diagnostics;
using System.Runtime.InteropServices;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace MKVision.Desktop;

// A IA de quadros (RIFE + TensorRT), carregada uma vez para o app inteiro: a câmera lenta do modo Edge e a do
// player usam a mesma. Carrega em segundo plano (~5 s; na 1ª vez otimiza o modelo para a placa, ~2 min).
// Dois modelos: o original (RIFE 4.26) e o treinado com os vídeos do usuário (rife_v4.26_mk.onnx, se existir —
// nome próprio: o cache do TensorRT é pelo nome do arquivo). O escolhido carrega primeiro; o outro, na 1ª troca.
internal static class RifeHost
{
    private static readonly string Folder = Path.Combine(AppContext.BaseDirectory, "Models", "rife");
    private static readonly string OriginalPath = Path.Combine(Folder, "rife_v4.26.onnx");
    private static readonly string TrainedPath = Path.Combine(Folder, "rife_v4.26_mk.onnx");
    private static volatile Rife? _original, _trained;
    private static volatile bool _useTrained;
    private static int _started, _loadingOther;

    public static bool HasTrained => File.Exists(TrainedPath);
    public static bool Started => _started == 1;
    public static bool UsingTrained => _useTrained && _trained is not null;
    // O escolhido; enquanto ele carrega, o outro.
    public static Rife? Instance => _useTrained ? _trained ?? _original : _original ?? _trained;

    public static void Start(bool preferTrained = true)
    {
        if (Interlocked.Exchange(ref _started, 1) == 1) return;
        // Autoteste: MKV_RIFE_MODEL=original|treinado.
        if (Environment.GetEnvironmentVariable("MKV_RIFE_MODEL") is { Length: > 0 } forced) preferTrained = forced == "treinado";
        _useTrained = preferTrained && HasTrained;
        // Thread de prioridade baixa: montar o TensorRT pesa no processador (as páginas e o vídeo vêm antes).
        new Thread(() => Load(_useTrained)) { IsBackground = true, Priority = ThreadPriority.BelowNormal, Name = "Câmera lenta: IA" }.Start();
    }

    // Troca de modelo (tecla M). Carrega o outro na primeira vez.
    public static void Use(bool trained)
    {
        _useTrained = trained && HasTrained;
        if (_started == 0) return; // ainda não carregou nenhuma: o Start carrega a escolhida
        if (_useTrained ? _trained is null : _original is null)
            if (Interlocked.Exchange(ref _loadingOther, 1) == 0) Task.Run(() => { Load(_useTrained); _loadingOther = 0; });
    }

    // Descarrega a IA (libera a memória da placa, ex.: para o ComfyUI na tecla G). O próximo Start carrega de novo
    // (do cache do TensorRT: segundos). Quem estiver no meio de uma análise termina antes. false = não havia nada.
    public static bool Release()
    {
        var (original, trained) = (_original, _trained);
        if (original is null && trained is null) return false;
        (_original, _trained) = (null, null);
        Interlocked.Exchange(ref _started, 0);
        original?.Dispose();
        trained?.Dispose();
        Log.Write("Câmera lenta: IA descarregada (memória da placa livre).");
        return true;
    }

    private static void Load(bool trained)
    {
        string name = trained ? "a IA treinada com os seus vídeos" : "a IA";
        try
        {
            Log.Write($"Câmera lenta: carregando {name} (TensorRT)... na primeira vez leva ~2 minutos.");
            var clock = Stopwatch.StartNew();
            var rife = new Rife(trained ? TrainedPath : OriginalPath);
            if (trained) _trained = rife; else _original = rife;
            Log.Write($"Câmera lenta: {name} pronta ({clock.Elapsed.TotalSeconds:0} s).");
        }
        catch (Exception error)
        {
            Log.Write($"Câmera lenta: {name} indisponível — {error.Message}");
            if (trained) { _useTrained = false; if (_original is null) Load(false); }
        }
    }
}

// Movimento de um par de quadros analisado pela IA em vários momentos: fluxo (para A e para B) e máscara (peso
// de A), uma camada por momento, na resolução da análise.
internal sealed class Motion : IDisposable
{
    public required ID3D11Texture2D Flow, Mask;
    public required ID3D11ShaderResourceView FlowView, MaskView;
    public required float[] Steps;
    public required int AnalysisW, AnalysisH, PaddedW, PaddedH;
    public object? Key; // de quem chamou (ex.: o quadro final do par)

    public void Dispose()
    {
        FlowView.Dispose();
        MaskView.Dispose();
        Flow.Dispose();
        Mask.Dispose();
    }
}

// As partes da câmera lenta comuns ao modo Edge (SlowMotion) e ao player: preparar a entrada da IA, analisar um
// par e desenhar o quadro intermediário (Slow.hlsl). Cada usuário tem a sua instância (buffers próprios).
internal sealed class FrameInterpolator : IDisposable
{
    [StructLayout(LayoutKind.Sequential)]
    private struct InterpolateParams
    {
        public float S;
        public int Count;
        public float FlowUvX, FlowUvY, AnalysisW, AnalysisH, Crisp, Pad1;
        public float Step0, Step1, Step2, Step3, Step4, Step5, Step6, Step7;
        public float MapX0, MapX1, MapX2, MapX3, MapY0, MapY1, MapY2, MapY3; // só PSInterpolateView
        public float Delta00, Delta01, Delta10, Delta11;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ResampleParams
    {
        public float FootprintX, FootprintY, Pad0, Pad1;
    }

    public const int MaxSteps = 8; // momentos analisados por par (o shader aceita até 8)

    private readonly Gpu _gpu;
    private readonly object _gpuLock;
    private readonly ID3D11PixelShader _interpolate, _interpolateView, _resample;
    private readonly RenderTexture _aiInput;
    private ID3D11Texture2D? _aiStaging;
    private (int, int) _aiStagingSize;
    private byte[] _bgra = [];
    private ushort[][] _flowHalf = [], _maskHalf = [];

    // Custo de UMA rodada da IA nesta placa (média móvel; no "conjunto" cada momento são duas), para escolher
    // tamanho e quantos momentos cabem no tempo.
    public double MsPerMegapixel { get; private set; } = 30;

    public FrameInterpolator(Gpu gpu, object gpuLock)
    {
        _gpu = gpu;
        _gpuLock = gpuLock;
        _interpolate = gpu.CreatePixelShader("Slow.hlsl", "PSInterpolate");
        _interpolateView = gpu.CreatePixelShader("Slow.hlsl", "PSInterpolateView");
        _resample = gpu.CreatePixelShader("Resample.hlsl", "PSResample");
        _aiInput = new RenderTexture(gpu.Device, Format.B8G8R8A8_UNorm);
    }

    // Reduz "source" para o tamanho de "target" (média de área). Com o lock da GPU.
    public void Resample(ID3D11ShaderResourceView source, RenderTexture target)
    {
        _gpu.BeginPasses();
        _gpu.SetConstants(new ResampleParams { FootprintX = 1f / target.Width, FootprintY = 1f / target.Height });
        _gpu.Context.PSSetSampler(0, _gpu.LinearSampler);
        _gpu.Draw(_resample, target.Target!, target.Width, target.Height, source);
    }

    // Lê a cópia de volta para a CPU SEM segurar a GPU: tenta mapear sem esperar e, enquanto a placa não
    // terminou, cede a vez (a tela continua sendo desenhada nesse meio-tempo).
    public void ReadStaging(ID3D11Texture2D staging, int width, int height, byte[] into)
    {
        MappedSubresource map;
        var deadline = Stopwatch.StartNew();
        while (_gpu.Context.Map(staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.DoNotWait, out map).Failure)
        {
            if (deadline.ElapsedMilliseconds > 500) { map = _gpu.Context.Map(staging, 0, MapMode.Read); break; }
            Thread.Yield();
        }
        try
        {
            for (int y = 0; y < height; y++)
                Marshal.Copy(map.DataPointer + (nint)(y * map.RowPitch), into, y * width * 4, width * 4);
        }
        finally { _gpu.Context.Unmap(staging, 0); }
    }

    // Entrada da IA: o quadro reduzido para aw×ah em RGB planar 0–1, em "rgb" (3·aw·ah). Trava a GPU só para
    // reduzir e copiar; a leitura de volta espera fora do lock.
    public void PrepareInput(ID3D11ShaderResourceView frame, int aw, int ah, float[] rgb)
    {
        lock (_gpuLock)
        {
            _aiInput.Ensure(aw, ah);
            if (_aiStaging is null || _aiStagingSize != (aw, ah))
            {
                _aiStaging?.Dispose();
                _aiStaging = _gpu.Device.CreateTexture2D(new Texture2DDescription(Format.B8G8R8A8_UNorm, (uint)aw, (uint)ah, 1, 1,
                    BindFlags.None, ResourceUsage.Staging, CpuAccessFlags.Read));
                _aiStagingSize = (aw, ah);
            }
            Resample(frame, _aiInput);
            _gpu.Context.CopyResource(_aiStaging, _aiInput.Texture!);
            _gpu.Context.Flush();
        }
        int plane = aw * ah;
        if (_bgra.Length != plane * 4) _bgra = new byte[plane * 4];
        ReadStaging(_aiStaging, aw, ah, _bgra);
        var bgra = _bgra;
        Parallel.For(0, ah, y =>
        {
            for (int i = y * aw, end = i + aw; i < end; i++)
            {
                rgb[i] = bgra[i * 4 + 2] / 255f;
                rgb[plane + i] = bgra[i * 4 + 1] / 255f;
                rgb[2 * plane + i] = bgra[i * 4] / 255f;
            }
        });
    }

    // Analisa o par (a → b) em "count" momentos espalhados. "stillWanted" é consultado entre um momento e
    // outro: false = o par não interessa mais (null). Uma análise por vez por instância.
    //
    // ensemble ("conjunto"): cada momento é analisado duas vezes — a → b e b → a (no momento 1 − t, que é o
    // mesmo instante) — e as duas leituras do movimento são combinadas (média do fluxo para cada quadro e da
    // máscara). Erros que só uma das leituras comete ficam pela metade: menos deformação, o dobro de cálculo.
    private float[] _ensembleFlow = [], _ensembleMask = [];

    public Motion? Analyze(Rife rife, float[] a, float[] b, int width, int height, int count, Func<bool>? stillWanted = null, bool ensemble = false)
    {
        count = Math.Clamp(count, 1, MaxSteps);
        double megapixels = width * height / 1e6;
        var steps = Enumerable.Range(0, count).Select(k => (k + 0.5f) / count).ToArray();
        if (_flowHalf.Length < count)
        {
            Array.Resize(ref _flowHalf, count);
            Array.Resize(ref _maskHalf, count);
        }
        int pw = 0, ph = 0;
        for (int k = 0; k < count; k++)
        {
            var clock = Stopwatch.StartNew();
            int layerIndex = k;
            if (ensemble)
            {
                // 1ª leitura (b → a, no momento 1 − t): guardada para a média.
                rife.RunMotion(b, a, width, height, 1 - steps[k], (flow, mask, paddedW, paddedH) =>
                {
                    int layer = paddedW * paddedH;
                    if (_ensembleFlow.Length != layer * 4) _ensembleFlow = new float[layer * 4];
                    if (_ensembleMask.Length != layer) _ensembleMask = new float[layer];
                    flow.Span[..(layer * 4)].CopyTo(_ensembleFlow);
                    mask.Span[..layer].CopyTo(_ensembleMask);
                });
                if (stillWanted is not null && !stillWanted()) return null;
            }
            rife.RunMotion(a, b, width, height, steps[k], (flow, mask, paddedW, paddedH) =>
            {
                // Fluxo intercalado (RGBA) e máscara em meia precisão, direto da saída da IA.
                (pw, ph) = (paddedW, paddedH);
                int layer = pw * ph;
                if (_flowHalf[layerIndex]?.Length != layer * 4) _flowHalf[layerIndex] = new ushort[layer * 4];
                if (_maskHalf[layerIndex]?.Length != layer) _maskHalf[layerIndex] = new ushort[layer];
                var fh = _flowHalf[layerIndex];
                var mh = _maskHalf[layerIndex];
                int rowWidth = pw;
                bool combine = ensemble && _ensembleMask.Length == layer;
                var ef = _ensembleFlow;
                var em = _ensembleMask;
                Parallel.For(0, ph, y =>
                {
                    var f = flow.Span;
                    var m = mask.Span;
                    for (int i = y * rowWidth, end = i + rowWidth; i < end; i++)
                    {
                        float ax = f[i], ay = f[layer + i], bx = f[2 * layer + i], by = f[3 * layer + i], w = m[i];
                        if (combine)
                        {
                            // Na leitura invertida, xy aponta para b e zw para a; a máscara é o peso de b.
                            ax = (ax + ef[2 * layer + i]) * 0.5f;
                            ay = (ay + ef[3 * layer + i]) * 0.5f;
                            bx = (bx + ef[i]) * 0.5f;
                            by = (by + ef[layer + i]) * 0.5f;
                            w = (w + 1 - em[i]) * 0.5f;
                        }
                        fh[i * 4] = BitConverter.HalfToUInt16Bits((Half)ax);
                        fh[i * 4 + 1] = BitConverter.HalfToUInt16Bits((Half)ay);
                        fh[i * 4 + 2] = BitConverter.HalfToUInt16Bits((Half)bx);
                        fh[i * 4 + 3] = BitConverter.HalfToUInt16Bits((Half)by);
                        mh[i] = BitConverter.HalfToUInt16Bits((Half)w);
                    }
                });
            });
            MsPerMegapixel = MsPerMegapixel * 0.8 + clock.Elapsed.TotalMilliseconds / megapixels / (ensemble ? 2 : 1) * 0.2;
            if (stillWanted is not null && !stillWanted()) return null;
        }

        var flowTex = CreateArray(Format.R16G16B16A16_Float, pw, ph, _flowHalf, count, 8);
        var maskTex = CreateArray(Format.R16_Float, pw, ph, _maskHalf, count, 2);
        return new Motion
        {
            Flow = flowTex, Mask = maskTex,
            FlowView = _gpu.Device.CreateShaderResourceView(flowTex), MaskView = _gpu.Device.CreateShaderResourceView(maskTex),
            Steps = steps, AnalysisW = width, AnalysisH = height, PaddedW = pw, PaddedH = ph
        };
    }

    // A criação de recursos no Direct3D 11 pode ser feita de qualquer thread (os dados são copiados na hora).
    private ID3D11Texture2D CreateArray(Format format, int width, int height, ushort[][] layers, int count, int bytesPerPixel)
    {
        var handles = layers.Take(count).Select(l => GCHandle.Alloc(l, GCHandleType.Pinned)).ToArray();
        try
        {
            var data = handles.Select(h => new SubresourceData(h.AddrOfPinnedObject(), (uint)(width * bytesPerPixel))).ToArray();
            return _gpu.Device.CreateTexture2D(new Texture2DDescription(format, (uint)width, (uint)height, (uint)count, 1,
                BindFlags.ShaderResource, ResourceUsage.Immutable), data);
        }
        finally { foreach (var h in handles) h.Free(); }
    }

    // Câmera lenta "nítida" (0 = mistura de sempre; 1 = sem fantasma onde A e B discordam). Ver Slow.hlsl.
    public static float CrispLevel;

    private static InterpolateParams Params(Motion? motion, float s)
    {
        var p = new InterpolateParams { S = s, Count = motion?.Steps.Length ?? 0, Crisp = CrispLevel };
        if (motion is not null)
        {
            p.FlowUvX = (float)motion.AnalysisW / motion.PaddedW;
            p.FlowUvY = (float)motion.AnalysisH / motion.PaddedH;
            p.AnalysisW = motion.AnalysisW;
            p.AnalysisH = motion.AnalysisH;
            var st = new float[MaxSteps];
            motion.Steps.CopyTo(st, 0);
            (p.Step0, p.Step1, p.Step2, p.Step3, p.Step4, p.Step5, p.Step6, p.Step7) = (st[0], st[1], st[2], st[3], st[4], st[5], st[6], st[7]);
        }
        return p;
    }

    // Quadro intermediário no momento s (0 = a, 1 = b) em "target" (do tamanho dos quadros). Sem análise:
    // transição suave entre os dois. Com o lock da GPU.
    public void Draw(RenderTexture target, ID3D11ShaderResourceView a, ID3D11ShaderResourceView b, Motion? motion, float s)
    {
        var p = Params(motion, s);
        _gpu.BeginPasses();
        _gpu.SetConstants(p);
        _gpu.Context.PSSetSampler(0, _gpu.LinearSampler);
        if (motion is not null)
            _gpu.Draw(_interpolate, target.Target!, target.Width, target.Height, a, b, motion.FlowView, motion.MaskView);
        else
            _gpu.Draw(_interpolate, target.Target!, target.Width, target.Height, a, b);
    }

    // Player: A e B já processados no tamanho e recorte da tela (lupa, giro, IA, nitidez). O movimento é lido no
    // ponto do vídeo de cada pixel da tela: video = (mapX · (x,y,1), mapY · (x,y,1)); delta leva o deslocamento
    // (uv do vídeo) para a tela. O pesado roda uma vez por quadro real; isto, a cada quadro de tela, é leve.
    public void DrawView(RenderTexture target, ID3D11ShaderResourceView a, ID3D11ShaderResourceView b, Motion? motion, float s,
        (double x0, double x1, double x2) mapX, (double y0, double y1, double y2) mapY, (double d00, double d01, double d10, double d11) delta)
    {
        var p = Params(motion, s);
        (p.MapX0, p.MapX1, p.MapX2) = ((float)mapX.x0, (float)mapX.x1, (float)mapX.x2);
        (p.MapY0, p.MapY1, p.MapY2) = ((float)mapY.y0, (float)mapY.y1, (float)mapY.y2);
        (p.Delta00, p.Delta01, p.Delta10, p.Delta11) = ((float)delta.d00, (float)delta.d01, (float)delta.d10, (float)delta.d11);
        _gpu.BeginPasses();
        _gpu.SetConstants(p);
        _gpu.Context.PSSetSampler(0, _gpu.LinearSampler);
        if (motion is not null)
            _gpu.Draw(_interpolateView, target.Target!, target.Width, target.Height, a, b, motion.FlowView, motion.MaskView);
        else
            _gpu.Draw(_interpolateView, target.Target!, target.Width, target.Height, a, b);
    }

    public void Dispose()
    {
        _interpolateView.Dispose();
        _aiStaging?.Dispose();
        _aiInput.Dispose();
        _interpolate.Dispose();
        _resample.Dispose();
    }
}
