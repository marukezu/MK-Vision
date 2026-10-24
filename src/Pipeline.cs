using System.Runtime.InteropServices;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace MKVision.Desktop;

// Onde está o vídeo e o que mostrar dele (vem da extensão).
internal readonly record struct VideoView(
    Box Region,           // área desenhada do vídeo dentro do quadro capturado (pixels)
    int NativeWidth,      // resolução real do vídeo
    int NativeHeight,
    float UvX, float UvY, // parte do vídeo a mostrar (lupa / object-fit), em frações 0–1
    float UvWidth, float UvHeight);

// Fase 3: volta o vídeo à resolução original e reconstrói até o tamanho da tela.
//   captura → resolução original → limpeza → ruído temporal → IA 2x (×2) → bicúbica + brilho da IA → nitidez
internal sealed class Pipeline : IDisposable
{
    [StructLayout(LayoutKind.Sequential)]
    private struct UpscaleParams
    {
        public float SrcX, SrcY, SrcW, SrcH;
        public float SrcSizeW, SrcSizeH, FootprintW, FootprintH;
        public float AiX, AiY, AiW, AiH;
        public float UseAi, TemporalWeight, TemporalThreshold, Pad;
    }

    private const float AiMinUpscale = 1.3f; // abaixo disso a ampliação é pequena demais para valer a IA
    private const int AiMargin = 8;          // contexto das convoluções em volta da região

    private readonly Gpu _gpu;
    private readonly ID3D11PixelShader _extract, _temporal, _luma, _compose, _copy;
    private readonly RtxVideo _rtx;
    private readonly RenderTexture _rtxIn;
    private readonly Fsrcnnx _fast, _enhance;
    private readonly Fsrcnnx.Runner _ai1, _ai2;
    private readonly RenderTexture _captured, _source, _cleaned, _luma1, _composed;
    private readonly RenderTexture[] _history;
    private int _historyIndex;
    private bool _historyValid;

    // Para o console: o que rodou no último quadro.
    public string LastInfo { get; private set; } = "";

    public Pipeline(Gpu gpu)
    {
        _gpu = gpu;
        _extract = gpu.CreatePixelShader("Upscale.hlsl", "PSExtract");
        _temporal = gpu.CreatePixelShader("Upscale.hlsl", "PSTemporal");
        _luma = gpu.CreatePixelShader("Upscale.hlsl", "PSLuma");
        _compose = gpu.CreatePixelShader("Upscale.hlsl", "PSCompose");
        _copy = gpu.CreatePixelShader("Upscale.hlsl", "PSCopy");
        _rtx = new RtxVideo(gpu.Device, gpu.Context);
        _rtxIn = new RenderTexture(gpu.Device, Format.B8G8R8A8_UNorm);
        string models = Path.Combine(AppContext.BaseDirectory, "Models");
        _fast = new Fsrcnnx(gpu, Path.Combine(models, "FSRCNNX_x2_8-0-4-1.glsl"));
        _enhance = new Fsrcnnx(gpu, Path.Combine(models, "FSRCNNX_x2_16-0-4-1_enhance.glsl"));
        _ai1 = new Fsrcnnx.Runner(gpu);
        _ai2 = new Fsrcnnx.Runner(gpu);
        _captured = new RenderTexture(gpu.Device, Format.B8G8R8A8_UNorm);
        _source = new RenderTexture(gpu.Device, Format.R16G16B16A16_Float);
        _cleaned = new RenderTexture(gpu.Device, Format.R16G16B16A16_Float);
        _luma1 = new RenderTexture(gpu.Device, Format.R16G16B16A16_Float);
        _composed = new RenderTexture(gpu.Device, Format.B8G8R8A8_UNorm);
        _history = [new(gpu.Device, Format.R16G16B16A16_Float), new(gpu.Device, Format.R16G16B16A16_Float)];
    }

    public bool AiReady => _enhance.Ready;

    // RTX Video: null = ainda não testou (testa no primeiro quadro com ele ligado), true = agindo, false = não.
    public bool? RtxWorks => _rtx.Works;
    public void RtxRecheck() => _rtx.Recheck();

    // Nível de potência → limpeza, ruído temporal, modelo e passadas da IA.
    private static (float deband, bool temporal, int aiPasses, bool quality) Level(int power) => power switch
    {
        <= 1 => (2f, false, 0, false),
        2 => (2f, false, 1, false),
        3 => (3f, false, 1, true),
        4 => (4f, true, 1, true),
        _ => (4f, true, 2, true)
    };

    public void Process(ID3D11Texture2D capturedFrame, VideoView view, ID3D11RenderTargetView target, int outWidth, int outHeight, Settings settings)
    {
        var source = Extract(capturedFrame, view, out bool resized);
        Finish(source.View!, source.Width, source.Height, view, target, outWidth, outHeight, settings, resized, allowTemporal: true);
    }

    // 1. Recorta o vídeo da captura e volta à resolução original (média da área que cada pixel original
    // virou na tela). Vídeo maior que a área na tela (ex.: 4K num espaço menor): a captura já é a melhor fonte.
    public RenderTexture Extract(ID3D11Texture2D capturedFrame, VideoView view, out bool resized)
    {
        var context = _gpu.Context;
        int dw = view.Region.Right - view.Region.Left;
        int dh = view.Region.Bottom - view.Region.Top;
        int sw = Math.Min(view.NativeWidth, dw);
        int sh = Math.Min(view.NativeHeight, dh);

        _captured.Ensure(dw, dh);
        context.CopySubresourceRegion(_captured.Texture!, 0, 0, 0, 0, capturedFrame, 0, view.Region);
        _gpu.BeginPasses();
        resized = _source.Ensure(sw, sh);
        _gpu.SetConstants(new UpscaleParams { SrcX = 0, SrcY = 0, SrcW = 1, SrcH = 1, SrcSizeW = dw, SrcSizeH = dh, FootprintW = 1f / sw, FootprintH = 1f / sh });
        context.PSSetSampler(0, _gpu.LinearSampler);
        _gpu.Draw(_extract, _source.Target!, sw, sh, _captured.View!);
        return _source;
    }

    // 2–6. Da imagem na resolução original até a tela: limpeza, ruído temporal, IA, composição, nitidez.
    public void Finish(ID3D11ShaderResourceView sourceView, int sw, int sh, VideoView view, ID3D11RenderTargetView target,
        int outWidth, int outHeight, Settings settings, bool resized, bool allowTemporal)
    {
        var context = _gpu.Context;
        var (deband, temporal, aiPasses, quality) = Level(settings.Power);
        if (settings.ManualDeband) deband = settings.Deband; // player: o controle "remover artefatos" decide
        if (settings.LightAi) (quality, aiPasses) = (false, Math.Min(aiPasses, 1)); // desempenho "Leve" (usar com VR)
        temporal &= allowTemporal; // na câmera lenta os quadros já são interpolados: sem mistura temporal
        _gpu.BeginPasses();
        ID3D11ShaderResourceView current = sourceView;

        // 2. Limpeza de blocos e faixas.
        if (deband > 0)
        {
            _cleaned.Ensure(sw, sh);
            _gpu.Clean(current, _cleaned.Target!, sw, sh, settings, deband);
            current = _cleaned.View!;
        }

        // 3. Ruído temporal: mistura com o resultado anterior onde a imagem não mudou.
        if (temporal)
        {
            foreach (var h in _history) resized |= h.Ensure(sw, sh);
            var previous = _history[_historyIndex];
            var next = _history[1 - _historyIndex];
            _gpu.SetConstants(new UpscaleParams { SrcSizeW = sw, SrcSizeH = sh, TemporalWeight = _historyValid && !resized ? 0.6f : 0f, TemporalThreshold = 6f / 255f });
            context.PSSetSampler(0, _gpu.LinearSampler);
            _gpu.Draw(_temporal, next.Target!, sw, sh, current, previous.View!);
            _historyIndex = 1 - _historyIndex;
            _historyValid = true;
            current = next.View!;
        }
        else
        {
            _historyValid = false;
        }

        // 4. IA na região visível, se a ampliação até a tela for grande o bastante.
        float upscale = outWidth / (view.UvWidth * sw);
        var model = quality ? _enhance : _fast;
        ID3D11ShaderResourceView? ai = null;
        var aiRect = (x: 0f, y: 0f, w: 1f, h: 1f);
        int passes = 0;

        // 4–5 pelo RTX Video (a IA de ampliação do driver da NVIDIA), na imagem inteira sem zoom: com zoom a
        // região tem frações de pixel (o processador de vídeo só aceita inteiros) e a nossa IA segue.
        bool rtx = false;
        if (settings.RtxVideo && !settings.Bypass && upscale >= 1.05f && _rtx.Works != false &&
            view.UvX <= 0.001f && view.UvY <= 0.001f && view.UvWidth >= 0.999f && view.UvHeight >= 0.999f)
        {
            _rtxIn.Ensure(sw, sh);
            context.PSSetSampler(0, _gpu.PointSampler);
            _gpu.Draw(_copy, _rtxIn.Target!, sw, sh, current);
            _composed.Ensure(outWidth, outHeight);
            rtx = _rtx.Upscale(_rtxIn.Texture!, sw, sh, _composed.Texture!, outWidth, outHeight);
        }
        if (rtx) aiPasses = 0;
        if (aiPasses > 0 && model.Ready && upscale >= AiMinUpscale && !settings.Bypass)
        {
            int x0 = Math.Max(0, (int)MathF.Floor(view.UvX * sw) - AiMargin);
            int y0 = Math.Max(0, (int)MathF.Floor(view.UvY * sh) - AiMargin);
            int x1 = Math.Min(sw, (int)MathF.Ceiling((view.UvX + view.UvWidth) * sw) + AiMargin);
            int y1 = Math.Min(sh, (int)MathF.Ceiling((view.UvY + view.UvHeight) * sh) + AiMargin);
            int rw = x1 - x0, rh = y1 - y0;
            aiRect = ((float)x0 / sw, (float)y0 / sh, (float)rw / sw, (float)rh / sh);

            _luma1.Ensure(rw, rh);
            _gpu.SetConstants(new UpscaleParams { SrcX = aiRect.x, SrcY = aiRect.y, SrcW = aiRect.w, SrcH = aiRect.h, SrcSizeW = sw, SrcSizeH = sh });
            context.PSSetSampler(0, _gpu.LinearSampler);
            _gpu.Draw(_luma, _luma1.Target!, rw, rh, current);

            ai = model.Run(_ai1, _luma1.View!, rw, rh);
            passes = 1;
            if (aiPasses > 1 && upscale / 2 >= AiMinUpscale)
            {
                ai = model.Run(_ai2, ai, rw * 2, rh * 2);
                passes = 2;
            }
        }

        // 5. Até o tamanho da tela: cor pela bicúbica, brilho pela IA (o RTX Video já fez os dois).
        if (!rtx)
        {
            _composed.Ensure(outWidth, outHeight);
            _gpu.SetConstants(new UpscaleParams
            {
                SrcX = view.UvX, SrcY = view.UvY, SrcW = view.UvWidth, SrcH = view.UvHeight,
                SrcSizeW = sw, SrcSizeH = sh,
                AiX = aiRect.x, AiY = aiRect.y, AiW = aiRect.w, AiH = aiRect.h,
                UseAi = ai is null ? 0 : 1
            });
            context.PSSetSampler(0, _gpu.LinearSampler);
            _gpu.Draw(_compose, _composed.Target!, outWidth, outHeight, current, ai ?? current);
        }

        // 6. Nitidez final na resolução da tela.
        _gpu.Sharpen(_composed.View!, target, outWidth, outHeight, settings);

        LastInfo = $"vídeo {view.NativeWidth}x{view.NativeHeight} → origem {sw}x{sh} → tela {outWidth}x{outHeight} ({upscale:0.0}x)" +
                   (rtx ? ", RTX Video" : passes > 0 ? $", IA {(quality ? "qualidade" : "rápida")} ×{passes}" : model.Ready ? "" : ", IA compilando...") +
                   (temporal ? ", temporal" : "");
    }

    public void Dispose()
    {
        foreach (var h in _history) h.Dispose();
        _composed.Dispose();
        _rtxIn.Dispose();
        _rtx.Dispose();
        _luma1.Dispose();
        _cleaned.Dispose();
        _source.Dispose();
        _captured.Dispose();
        _ai2.Dispose();
        _ai1.Dispose();
        _enhance.Dispose();
        _fast.Dispose();
        _compose.Dispose();
        _luma.Dispose();
        _temporal.Dispose();
        _extract.Dispose();
    }
}
