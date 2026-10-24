using System.Runtime.InteropServices;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace MKVision.Desktop;

// NVIDIA RTX Video Super Resolution: a ampliação por IA do próprio driver (placas RTX), pelo processador de vídeo
// do Direct3D 11 com a extensão da NVIDIA — o mesmo caminho do Chrome e do VLC. Só age com "Super Resolution"
// ligado no app NVIDIA / Painel de Controle; o driver não avisa, então na primeira vez um teste pequeno (ampliar
// um padrão com a extensão ligada e desligada) diz se ele está agindo. Entrada e saída BGRA (8 bits).
internal sealed class RtxVideo : IDisposable
{
    private static readonly Guid NvidiaPpe = new("d43ce1b3-1f4b-48ac-baee-c3c25375e6f7");

    [StructLayout(LayoutKind.Sequential)]
    private struct SuperResolution { public uint Version, Method, Enable; }

    private readonly ID3D11Device _device;
    private readonly ID3D11DeviceContext _context;
    private readonly ID3D11VideoDevice? _video;
    private readonly ID3D11VideoContext1? _videoContext;
    private ID3D11VideoProcessorEnumerator? _enumerator;
    private ID3D11VideoProcessor? _processor;
    private (int iw, int ih, int ow, int oh) _size;
    private ID3D11VideoProcessorInputView? _inView;
    private ID3D11VideoProcessorOutputView? _outView;
    private IntPtr _inTexture, _outTexture;
    private bool? _works;

    public RtxVideo(ID3D11Device device, ID3D11DeviceContext context)
    {
        (_device, _context) = (device, context);
        try
        {
            using var dxgi = device.QueryInterface<IDXGIDevice>();
            using var adapter = dxgi.GetAdapter();
            if (adapter.Description.VendorId != 0x10DE) { _works = false; return; } // só NVIDIA
            _video = device.QueryInterface<ID3D11VideoDevice>();
            _videoContext = context.QueryInterface<ID3D11VideoContext1>();
        }
        catch (Exception error)
        {
            _works = false;
            Log.Write($"RTX Video: indisponível — {error.Message}");
        }
    }

    // Está agindo? (com o teste na primeira vez; null = ainda não testou)
    public bool? Works => _works;

    // Esquece o resultado do teste (o usuário ligou o Super Resolution no driver depois de abrir o app).
    public void Recheck()
    {
        if (_video is not null) _works = null;
    }

    // Amplia "input" (BGRA, inteiro) para "output" (BGRA, inteiro). false = não agiu (sem placa NVIDIA, desligado
    // no driver ou erro): quem chama amplia do jeito normal.
    public bool Upscale(ID3D11Texture2D input, int iw, int ih, ID3D11Texture2D output, int ow, int oh)
    {
        if (_video is null || _videoContext is null || _works == false) return false;
        try
        {
            if (_works is null)
            {
                _works = SelfTest();
                Log.Write(_works == true ? "RTX Video Super Resolution: ativo." : "RTX Video Super Resolution: sem efeito (ligue \"Super Resolution\" no app NVIDIA).");
                if (_works != true) return false;
            }
            Blt(input, iw, ih, output, ow, oh, enable: true);
            return true;
        }
        catch (Exception error)
        {
            Log.Write($"RTX Video: erro — {error.Message}; usando a ampliação normal.");
            _works = false;
            return false;
        }
    }

    private void Blt(ID3D11Texture2D input, int iw, int ih, ID3D11Texture2D output, int ow, int oh, bool enable)
    {
        var video = _video!;
        var context = _videoContext!;
        if (_processor is null || _size != (iw, ih, ow, oh))
        {
            ReleaseViews();
            _processor?.Dispose();
            _enumerator?.Dispose();
            _enumerator = video.CreateVideoProcessorEnumerator(new VideoProcessorContentDescription
            {
                InputFrameFormat = VideoFrameFormat.Progressive,
                InputFrameRate = new Rational(60, 1), OutputFrameRate = new Rational(60, 1),
                InputWidth = (uint)iw, InputHeight = (uint)ih, OutputWidth = (uint)ow, OutputHeight = (uint)oh,
                Usage = VideoUsage.PlaybackNormal
            });
            _processor = video.CreateVideoProcessor(_enumerator, 0);
            _size = (iw, ih, ow, oh);
            context.VideoProcessorSetStreamFrameFormat(_processor, 0, VideoFrameFormat.Progressive);
            // RGB completo (0–255) na entrada e na saída: o processador não mexe na cor.
            context.VideoProcessorSetStreamColorSpace1(_processor, 0, ColorSpaceType.RgbFullG22NoneP709);
            context.VideoProcessorSetOutputColorSpace1(_processor, ColorSpaceType.RgbFullG22NoneP709);
            context.VideoProcessorSetStreamAutoProcessingMode(_processor, 0, false);
            context.VideoProcessorSetStreamSourceRect(_processor, 0, true, new Vortice.RawRect(0, 0, iw, ih));
            context.VideoProcessorSetStreamDestRect(_processor, 0, true, new Vortice.RawRect(0, 0, ow, oh));
            context.VideoProcessorSetOutputTargetRect(_processor, true, new Vortice.RawRect(0, 0, ow, oh));
        }
        if (_inTexture != input.NativePointer)
        {
            _inView?.Dispose();
            _inView = video.CreateVideoProcessorInputView(input, _enumerator,
                new VideoProcessorInputViewDescription { FourCC = 0, ViewDimension = VideoProcessorInputViewDimension.Texture2D, Texture2D = new Texture2DVideoProcessorInputView { MipSlice = 0 } });
            _inTexture = input.NativePointer;
        }
        if (_outTexture != output.NativePointer)
        {
            _outView?.Dispose();
            _outView = video.CreateVideoProcessorOutputView(output, _enumerator,
                new VideoProcessorOutputViewDescription { ViewDimension = VideoProcessorOutputViewDimension.Texture2D, Texture2D = new Texture2DVideoProcessorOutputView { MipSlice = 0 } });
            _outTexture = output.NativePointer;
        }
        var sr = new SuperResolution { Version = 1, Method = 2, Enable = enable ? 1u : 0u };
        unsafe { context.VideoProcessorSetStreamExtension(_processor, 0, NvidiaPpe, (uint)sizeof(SuperResolution), (IntPtr)(&sr)); }
        context.VideoProcessorBlt(_processor, _outView!, 0, 1, [new VideoProcessorStream { Enable = true, InputSurface = _inView }]).CheckError();
    }

    // Amplia 2x um padrão de linhas finas com a extensão desligada e ligada: diferente = o driver está agindo.
    private bool SelfTest()
    {
        const int iw = 960, ih = 540, ow = 1920, oh = 1080; // o driver só age a partir de 360p
        var pattern = new byte[iw * ih * 4];
        for (int y = 0; y < ih; y++)
        for (int x = 0; x < iw; x++)
        {
            byte v = (byte)(((x / 3 + y / 5) % 2 == 0) ^ ((x * y) % 7 == 0) ? 230 : 30);
            int i = (y * iw + x) * 4;
            (pattern[i], pattern[i + 1], pattern[i + 2], pattern[i + 3]) = (v, v, v, 255);
        }
        var handle = GCHandle.Alloc(pattern, GCHandleType.Pinned);
        using var input = _device.CreateTexture2D(new Texture2DDescription(Format.B8G8R8A8_UNorm, iw, ih, 1, 1, BindFlags.ShaderResource | BindFlags.RenderTarget),
            [new SubresourceData(handle.AddrOfPinnedObject(), iw * 4)]);
        handle.Free();
        using var output = _device.CreateTexture2D(new Texture2DDescription(Format.B8G8R8A8_UNorm, ow, oh, 1, 1, BindFlags.ShaderResource | BindFlags.RenderTarget));
        using var staging = _device.CreateTexture2D(new Texture2DDescription(Format.B8G8R8A8_UNorm, ow, oh, 1, 1, BindFlags.None, ResourceUsage.Staging, CpuAccessFlags.Read));
        byte[] Read()
        {
            _context.CopyResource(staging, output);
            var map = _context.Map(staging, 0, MapMode.Read);
            var pixels = new byte[ow * oh * 4];
            for (int y = 0; y < oh; y++) Marshal.Copy(map.DataPointer + (nint)(y * map.RowPitch), pixels, y * ow * 4, ow * 4);
            _context.Unmap(staging, 0);
            return pixels;
        }
        Blt(input, iw, ih, output, ow, oh, enable: false);
        var plain = Read();
        Blt(input, iw, ih, output, ow, oh, enable: true);
        Blt(input, iw, ih, output, ow, oh, enable: true); // o driver pode precisar de um quadro para ligar
        var ai = Read();
        ReleaseViews();
        long diff = 0;
        for (int i = 0; i < plain.Length; i += 4) diff += Math.Abs(plain[i + 1] - ai[i + 1]);
        return diff / (double)(plain.Length / 4) > 0.2;
    }

    private void ReleaseViews()
    {
        _inView?.Dispose();
        _outView?.Dispose();
        (_inView, _outView, _inTexture, _outTexture) = (null, null, IntPtr.Zero, IntPtr.Zero);
    }

    public void Dispose()
    {
        ReleaseViews();
        _processor?.Dispose();
        _enumerator?.Dispose();
        _videoContext?.Dispose();
        _video?.Dispose();
    }
}
