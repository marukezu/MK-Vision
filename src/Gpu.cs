using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace MKVision.Desktop;

// Ajustes do processamento (mesmos nomes e escalas da extensão).
internal sealed class Settings
{
    public float Detail = 1.6f;        // perfil "Pele / Detalhe"
    public float DetailRadius = 0.7f;
    public float Texture = 0.5f;
    public float TextureRadius = 3f;
    public float Threshold = 2f;       // níveis 0–255
    public float Deband = 2f;          // níveis 0–10 (0 = sem limpeza); com a extensão, a potência decide
    public float Strength = 1f;        // multiplica micro-detalhe e textura (Ctrl+Alt+↑/↓)
    public int Power = 3;              // 1–5 (Ctrl+Alt+1…5): IA, limpeza forte, ruído temporal, 2ª passada
    public bool Bypass;                // mostra a captura sem processar
    public bool ManualDeband;          // limpeza pelo valor de Deband (player), em vez de pela potência (modo Edge)
    public bool LightAi;               // desempenho "Leve": IA de ampliação rápida e uma passada só
    // Cor e luz (player): 0 = sem mudança.
    public float Brightness;           // -0,5…0,5
    public float Contrast;             // -0,5…0,5
    public float Saturation;           // -1…1
    public float Vibrance;             // 0…1
    public float Clarity;              // 0…1
    public bool RtxVideo;              // ampliar pelo NVIDIA RTX Video Super Resolution (sem zoom), se estiver agindo
    public float PhotoTexture = 0.4f;  // IA de fotos: realce de textura na ampliada (0–1)
    public float PhotoGrain;           // IA de fotos: grão fino contra o efeito plástico (0–1)
    public bool SharpenOff;            // tecla D do player: sem micro-detalhe e textura (para comparar; Cor e luz continua)
    public bool PhotoSkin = true;      // IA de fotos: realce e grão mais fortes na pele (40% no resto)
    public bool PhotoFullRes;          // IA de fotos: fotos grandes (mais de 2048) em resolução cheia, saída 2x
    public bool PhotoFaceClean = true; // IA de fotos: as de rosto passam antes pela limpeza da compressão (Máxima)
    public float ToneExposure = 0.7f;  // tecla Y: força da exposição (IAT) sobre a cor corrigida (Deep WB); 0 = só a cor
}

// Dispositivo Direct3D 11 + passos básicos (limpeza, nitidez) e utilidades para os outros passos.
internal sealed class Gpu : IDisposable
{
    [StructLayout(LayoutKind.Sequential)]
    private struct EnhanceParams
    {
        public float TexelX, TexelY, Detail, DetailRadius;
        public float Texture, TextureRadius, Threshold, Deband;
        public float Bypass, Pad0, Pad1, Pad2;
        public float Brightness, Contrast, Saturation, Vibrance;
        public float Clarity, Pad3, Pad4, Pad5;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SizeParams
    {
        public float Width, Height, Pad0, Pad1;
    }

    public ID3D11Device Device { get; }
    public ID3D11DeviceContext Context { get; }
    public ID3D11SamplerState LinearSampler { get; }
    public ID3D11SamplerState PointSampler { get; }

    private readonly ID3D11VertexShader _vertexShader;
    private readonly ID3D11PixelShader _cleanShader;
    private readonly ID3D11PixelShader _sharpenShader;
    private readonly ID3D11PixelShader _downShader;
    private readonly RenderTexture _wide1, _wide2, _wide3, _wide4; // 1/4…1/256 da imagem: clareza e brilho médio
    private readonly Dictionary<int, ID3D11Buffer> _constantBuffers = [];

    // Fase 1: cópia do recorte capturado e resultado da limpeza.
    private readonly RenderTexture _source;
    private readonly RenderTexture _cleaned;

    public Gpu()
    {
        // VideoSupport: o player decodifica vídeo na placa (Media Foundation) com este mesmo dispositivo.
        D3D11.D3D11CreateDevice(null, DriverType.Hardware, DeviceCreationFlags.BgraSupport | DeviceCreationFlags.VideoSupport,
            [FeatureLevel.Level_11_1, FeatureLevel.Level_11_0], out ID3D11Device device, out ID3D11DeviceContext context).CheckError();
        Device = device;
        Context = context;
        // A captura chega em outra thread: protege o contexto contra uso simultâneo.
        using (var multithread = Device.QueryInterface<ID3D11Multithread>()) multithread.SetMultithreadProtected(true);

        _vertexShader = Device.CreateVertexShader(CompileFile("Enhance.hlsl", "VSMain", "vs_5_0"));
        _cleanShader = CreatePixelShader("Enhance.hlsl", "PSClean");
        _sharpenShader = CreatePixelShader("Enhance.hlsl", "PSSharpen");
        _downShader = CreatePixelShader("Enhance.hlsl", "PSDown");
        _wide1 = new RenderTexture(Device, Format.B8G8R8A8_UNorm);
        _wide2 = new RenderTexture(Device, Format.B8G8R8A8_UNorm);
        _wide3 = new RenderTexture(Device, Format.B8G8R8A8_UNorm);
        _wide4 = new RenderTexture(Device, Format.B8G8R8A8_UNorm);
        LinearSampler = Device.CreateSamplerState(new SamplerDescription(Filter.MinMagMipLinear, TextureAddressMode.Clamp));
        PointSampler = Device.CreateSamplerState(new SamplerDescription(Filter.MinMagMipPoint, TextureAddressMode.Clamp));
        _source = new RenderTexture(Device, Format.B8G8R8A8_UNorm);
        _cleaned = new RenderTexture(Device, Format.B8G8R8A8_UNorm);
    }

    private static byte[] CompileFile(string file, string entry, string profile)
    {
        string source = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Shaders", file));
        var result = Compiler.Compile(source, entry, file, profile, out Blob blob, out Blob? errors);
        if (result.Failure) throw new InvalidOperationException($"Shader {entry}: {errors?.AsString()}");
        return blob.AsBytes();
    }

    public ID3D11PixelShader CreatePixelShader(string file, string entry) =>
        Device.CreatePixelShader(CompileFile(file, entry, "ps_5_0"));

    // Prepara o desenho de um triângulo que cobre o alvo inteiro (todos os passos usam isso).
    public void BeginPasses()
    {
        Context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        Context.VSSetShader(_vertexShader);
    }

    // Constantes do passo atual (b0). O tamanho da struct precisa ser múltiplo de 16 bytes.
    public void SetConstants<T>(in T value) where T : unmanaged
    {
        int size = Unsafe.SizeOf<T>();
        if (!_constantBuffers.TryGetValue(size, out var buffer))
            _constantBuffers[size] = buffer = Device.CreateBuffer(new BufferDescription((uint)size, BindFlags.ConstantBuffer));
        Context.UpdateSubresource(in value, buffer);
        Context.PSSetConstantBuffer(0, buffer);
    }

    // Usado pela IA: tamanho da entrada + leitura exata de texels.
    public void SetSize(int width, int height)
    {
        SetConstants(new SizeParams { Width = width, Height = height });
        Context.PSSetSampler(0, PointSampler);
    }

    // Desenha "shader" em "target" (width×height) lendo as texturas dadas (t0, t1...).
    public void Draw(ID3D11PixelShader shader, ID3D11RenderTargetView target, int width, int height, params ID3D11ShaderResourceView[] inputs)
    {
        Context.OMSetRenderTargets(target);
        Context.RSSetViewport(0, 0, width, height);
        Context.PSSetShader(shader);
        for (int i = 0; i < inputs.Length; i++) Context.PSSetShaderResource((uint)i, inputs[i]);
        Context.Draw(3, 0);
        for (int i = 0; i < inputs.Length; i++) Context.PSUnsetShaderResource((uint)i);
        Context.OMSetRenderTargets((ID3D11RenderTargetView?)null);
    }

    // Desenha uma camada por cima (interface do player) na região x,y,width,height do alvo, com transparência
    // (a textura tem alfa pré-multiplicado).
    private ID3D11BlendState? _overBlend;
    private ID3D11PixelShader? _overlayShader;

    public void DrawOverlay(ID3D11RenderTargetView target, int x, int y, int width, int height, ID3D11ShaderResourceView layer)
    {
        _overlayShader ??= CreatePixelShader("Overlay.hlsl", "PSOverlay");
        if (_overBlend is null)
        {
            var blend = new BlendDescription();
            blend.RenderTarget[0] = new RenderTargetBlendDescription
            {
                BlendEnable = true,
                SourceBlend = Blend.One, DestinationBlend = Blend.InverseSourceAlpha, BlendOperation = BlendOperation.Add,
                SourceBlendAlpha = Blend.One, DestinationBlendAlpha = Blend.InverseSourceAlpha, BlendOperationAlpha = BlendOperation.Add,
                RenderTargetWriteMask = ColorWriteEnable.All
            };
            _overBlend = Device.CreateBlendState(blend);
        }
        BeginPasses();
        Context.OMSetRenderTargets(target);
        Context.OMSetBlendState(_overBlend);
        Context.RSSetViewport(x, y, width, height);
        Context.PSSetShader(_overlayShader);
        Context.PSSetSampler(0, PointSampler);
        Context.PSSetShaderResource(0, layer);
        Context.Draw(3, 0);
        Context.PSUnsetShaderResource(0);
        Context.OMSetBlendState(null);
        Context.OMSetRenderTargets((ID3D11RenderTargetView?)null);
    }

    private void SetEnhanceParams(int width, int height, Settings settings, float debandLevel)
    {
        SetConstants(new EnhanceParams
        {
            TexelX = 1f / width,
            TexelY = 1f / height,
            Detail = settings.SharpenOff ? 0 : settings.Detail * settings.Strength,
            DetailRadius = settings.DetailRadius,
            Texture = settings.SharpenOff ? 0 : settings.Texture * settings.Strength,
            TextureRadius = settings.TextureRadius,
            Threshold = settings.Threshold / 255f,
            Deband = debandLevel * 1.5f / 255f,
            Bypass = settings.Bypass ? 1f : 0f,
            Brightness = settings.Brightness,
            Contrast = settings.Contrast,
            Saturation = settings.Saturation,
            Vibrance = settings.Vibrance,
            Clarity = settings.Clarity
        });
        Context.PSSetSampler(0, LinearSampler);
    }

    // Remove blocos e faixas (nível 0–10).
    public void Clean(ID3D11ShaderResourceView input, ID3D11RenderTargetView target, int width, int height, Settings settings, float level)
    {
        SetEnhanceParams(width, height, settings, level);
        Draw(_cleanShader, target, width, height, input);
    }

    // Micro-detalhe (CAS) + textura + limiar, e cor e luz, na resolução final.
    public void Sharpen(ID3D11ShaderResourceView input, ID3D11RenderTargetView target, int width, int height, Settings settings)
    {
        ID3D11ShaderResourceView wide = input, whole = input;
        if ((settings.Clarity > 0 || settings.Contrast != 0) && !settings.Bypass && width >= 64 && height >= 64)
        {
            // Reduções sucessivas a 1/4 (baratas): a de 1/16 é o desfoque largo (~16 px) da clareza; a de ~1/256,
            // lida em 4 pontos, dá o brilho médio da imagem (centro do contraste).
            var levels = new[] { _wide1, _wide2, _wide3, _wide4 };
            var current = input;
            int w = width, h = height;
            for (int i = 0; i < levels.Length; i++)
            {
                int nw = Math.Max(1, w / 4), nh = Math.Max(1, h / 4);
                levels[i].Ensure(nw, nh);
                SetEnhanceParams(w, h, settings, 0);
                Draw(_downShader, levels[i].Target!, nw, nh, current);
                (current, w, h) = (levels[i].View!, nw, nh);
                if (i == 1) wide = current;
                whole = current;
                if (w < 8 || h < 8) break;
            }
        }
        SetEnhanceParams(width, height, settings, 0);
        Draw(_sharpenShader, target, width, height, input, wide, whole);
    }

    // Fase 1 (sem a extensão): processa o recorte "crop" da captura direto na resolução da tela.
    public void Process(ID3D11Texture2D captured, Box crop, ID3D11RenderTargetView target, Settings settings)
    {
        int width = crop.Right - crop.Left;
        int height = crop.Bottom - crop.Top;
        _source.Ensure(width, height);
        _cleaned.Ensure(width, height);
        Context.CopySubresourceRegion(_source.Texture!, 0, 0, 0, 0, captured, 0, crop);
        BeginPasses();

        var input = _source.View!;
        if (settings.Deband > 0 && !settings.Bypass)
        {
            Clean(_source.View!, _cleaned.Target!, width, height, settings, settings.Deband);
            input = _cleaned.View!;
        }
        Sharpen(input, target, width, height, settings);
    }

    public void Dispose()
    {
        _cleaned.Dispose();
        _source.Dispose();
        foreach (var buffer in _constantBuffers.Values) buffer.Dispose();
        PointSampler.Dispose();
        LinearSampler.Dispose();
        _sharpenShader.Dispose();
        _downShader.Dispose();
        _wide1.Dispose();
        _wide2.Dispose();
        _wide3.Dispose();
        _wide4.Dispose();
        _cleanShader.Dispose();
        _vertexShader.Dispose();
        Context.Dispose();
        Device.Dispose();
    }
}
