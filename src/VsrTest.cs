using System.Runtime.InteropServices;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace MKVision.Desktop;

// --vsrtest <foto> <pasta>: o NVIDIA RTX Video Super Resolution funciona nesta placa? Reduz a foto a 960x540 (BGRA,
// ou NV12 com MKV_VSR_NV12=1),
// amplia 2x pelo processador de vídeo do Direct3D 11 com a extensão da NVIDIA desligada e ligada e compara (se
// o driver aplicar a IA, as duas saem diferentes). Salva as duas em PNG.
internal static class VsrTest
{
    private static readonly Guid NvidiaPpe = new("d43ce1b3-1f4b-48ac-baee-c3c25375e6f7"); // extensão de pós-processamento da NVIDIA

    [StructLayout(LayoutKind.Sequential)]
    private struct NvidiaSuperResolution { public uint Version, Method, Enable; }

    public static int Run(string photo, string folder)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        Directory.CreateDirectory(folder);
        using var gpu = new Gpu();
        using (var dxgi = gpu.Device.QueryInterface<IDXGIDevice>())
        using (var adapter = dxgi.GetAdapter())
            Console.WriteLine($"placa: {adapter.Description.Description}");

        // Entrada: a foto reduzida a 960x540 (BGRA).
        const int iw = 960, ih = 540, ow = iw * 2, oh = ih * 2;
        var (photoTex, pw, ph) = ImageFile.Load(photo, gpu.Device);
        var full = new byte[pw * ph * 4];
        using (photoTex)
        using (var read = gpu.Device.CreateTexture2D(new Texture2DDescription(photoTex.Description.Format, (uint)pw, (uint)ph, 1, 1, BindFlags.None, ResourceUsage.Staging, CpuAccessFlags.Read)))
        {
            gpu.Context.CopyResource(read, photoTex);
            var map = gpu.Context.Map(read, 0, MapMode.Read);
            for (int y = 0; y < ph; y++) Marshal.Copy(map.DataPointer + (nint)(y * map.RowPitch), full, y * pw * 4, pw * 4);
            gpu.Context.Unmap(read, 0);
        }
        var input = new byte[iw * ih * 4];
        for (int y = 0; y < ih; y++)
        for (int x = 0; x < iw; x++)
        {
            int sx = x * pw / iw, sy = y * ph / ih;
            Array.Copy(full, (sy * pw + sx) * 4, input, (y * iw + x) * 4, 4);
        }
        bool nv12 = Environment.GetEnvironmentVariable("MKV_VSR_NV12") == "1";
        Console.WriteLine($"entrada: {(nv12 ? "NV12 (como sai do decodificador)" : "BGRA")}");
        byte[] upload = input;
        int pitch = iw * 4;
        if (nv12)
        {
            upload = new byte[iw * ih * 3 / 2];
            pitch = iw;
            for (int y = 0; y < ih; y++)
            for (int x = 0; x < iw; x++)
            {
                int i = (y * iw + x) * 4;
                double b0 = input[i], g0 = input[i + 1], r0 = input[i + 2];
                upload[y * iw + x] = (byte)Math.Clamp(16 + (0.2126 * r0 + 0.7152 * g0 + 0.0722 * b0) * 219 / 255, 0, 255);
                if (y % 2 == 0 && x % 2 == 0)
                {
                    double u = (-0.1146 * r0 - 0.3854 * g0 + 0.5 * b0) * 224 / 255 + 128, v = (0.5 * r0 - 0.4542 * g0 - 0.0458 * b0) * 224 / 255 + 128;
                    int o = iw * ih + (y / 2) * iw + x;
                    upload[o] = (byte)Math.Clamp(u, 0, 255);
                    upload[o + 1] = (byte)Math.Clamp(v, 0, 255);
                }
            }
        }
        using var inTex = gpu.Device.CreateTexture2D(new Texture2DDescription(nv12 ? Format.NV12 : Format.B8G8R8A8_UNorm, iw, ih, 1, 1,
            BindFlags.ShaderResource | BindFlags.RenderTarget));
        // Sobe pela cópia de uma textura "staging" (NV12 não aceita dados iniciais): Y e depois UV, linha a linha.
        using (var up = gpu.Device.CreateTexture2D(new Texture2DDescription(nv12 ? Format.NV12 : Format.B8G8R8A8_UNorm, iw, ih, 1, 1,
                   BindFlags.None, ResourceUsage.Staging, CpuAccessFlags.Write)))
        {
            var map = gpu.Context.Map(up, 0, MapMode.Write);
            int rows = nv12 ? ih * 3 / 2 : ih;
            for (int y = 0; y < rows; y++) Marshal.Copy(upload, y * pitch, map.DataPointer + (nint)(y * map.RowPitch), pitch);
            gpu.Context.Unmap(up, 0);
            gpu.Context.CopyResource(inTex, up);
        }
        using var outTex = gpu.Device.CreateTexture2D(new Texture2DDescription(Format.B8G8R8A8_UNorm, ow, oh, 1, 1, BindFlags.RenderTarget | BindFlags.ShaderResource));
        using var staging = gpu.Device.CreateTexture2D(new Texture2DDescription(Format.B8G8R8A8_UNorm, ow, oh, 1, 1, BindFlags.None, ResourceUsage.Staging, CpuAccessFlags.Read));

        using var video = gpu.Device.QueryInterface<ID3D11VideoDevice>();
        using var context = gpu.Context.QueryInterface<ID3D11VideoContext>();
        var content = new VideoProcessorContentDescription
        {
            InputFrameFormat = VideoFrameFormat.Progressive,
            InputFrameRate = new Rational(30, 1), OutputFrameRate = new Rational(30, 1),
            InputWidth = iw, InputHeight = ih, OutputWidth = ow, OutputHeight = oh,
            Usage = VideoUsage.PlaybackNormal
        };
        using var enumerator = video.CreateVideoProcessorEnumerator(content);
        using var processor = video.CreateVideoProcessor(enumerator, 0);
        using var inView = video.CreateVideoProcessorInputView(inTex, enumerator,
            new VideoProcessorInputViewDescription { FourCC = 0, ViewDimension = VideoProcessorInputViewDimension.Texture2D, Texture2D = new Texture2DVideoProcessorInputView { MipSlice = 0 } });
        using var outView = video.CreateVideoProcessorOutputView(outTex, enumerator,
            new VideoProcessorOutputViewDescription { ViewDimension = VideoProcessorOutputViewDimension.Texture2D, Texture2D = new Texture2DVideoProcessorOutputView { MipSlice = 0 } });
        context.VideoProcessorSetStreamFrameFormat(processor, 0, VideoFrameFormat.Progressive);
        context.VideoProcessorSetStreamSourceRect(processor, 0, true, new Vortice.RawRect(0, 0, iw, ih));
        context.VideoProcessorSetStreamDestRect(processor, 0, true, new Vortice.RawRect(0, 0, ow, oh));
        context.VideoProcessorSetOutputTargetRect(processor, true, new Vortice.RawRect(0, 0, ow, oh));

        byte[] Run(bool enable)
        {
            var sr = new NvidiaSuperResolution { Version = 1, Method = 2, Enable = enable ? 1u : 0u };
            int hr;
            unsafe { hr = context.VideoProcessorSetStreamExtension(processor, 0, NvidiaPpe, (uint)sizeof(NvidiaSuperResolution), (IntPtr)(&sr)).Code; }
            Console.WriteLine($"extensão NVIDIA {(enable ? "ligada" : "desligada")}: resposta 0x{hr:X8}");
            var stream = new VideoProcessorStream { Enable = true, InputSurface = inView };
            context.VideoProcessorBlt(processor, outView, 0, 1, [stream]).CheckError();
            gpu.Context.CopyResource(staging, outTex);
            var map = gpu.Context.Map(staging, 0, MapMode.Read);
            var pixels = new byte[ow * oh * 4];
            for (int y = 0; y < oh; y++) Marshal.Copy(map.DataPointer + (nint)(y * map.RowPitch), pixels, y * ow * 4, ow * 4);
            gpu.Context.Unmap(staging, 0);
            return pixels;
        }
        var plain = Run(false);
        var ai = Run(true);
        Thread.Sleep(300);
        ai = Run(true); // o driver pode precisar de um quadro para ligar
        InterpTest.Png(Path.Combine(folder, "vsr-desligado.png"), plain, ow, 0, 0, ow, oh);
        InterpTest.Png(Path.Combine(folder, "vsr-ligado.png"), ai, ow, 0, 0, ow, oh);
        long diff = 0;
        for (int i = 0; i < plain.Length; i += 4) diff += Math.Abs(plain[i + 1] - ai[i + 1]);
        double mean = diff / (double)(plain.Length / 4);
        Console.WriteLine($"diferença média entre desligado e ligado: {mean:0.000} níveis");
        Console.WriteLine(mean > 0.2 ? "RTX Video Super Resolution: FUNCIONA nesta placa (a imagem mudou)" :
            "RTX Video Super Resolution: sem efeito (desligado no Painel de Controle NVIDIA, driver antigo, ou não aceita esta entrada)");
        return 0;
    }
}
