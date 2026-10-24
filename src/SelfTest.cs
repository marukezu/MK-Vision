using System.Runtime.InteropServices;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace MKVision.Desktop;

// "MKVisionDesktop --selftest <hwnd em hexa>": captura a janela por alguns quadros, processa numa
// textura fora da tela e confere o resultado. Não mostra nada na tela.
internal static class SelfTest
{
    // "--overlaytest": mostra a camada em magenta por 4 s num canto da tela, para conferir se ela aparece.
    public static int Overlay()
    {
        using var gpu = new Gpu();
        using var overlay = new Overlay(gpu);
        overlay.Place(new Native.RECT { Left = 100, Top = 100, Right = 400, Bottom = 300 }, true);
        var until = DateTime.Now.AddSeconds(4);
        while (DateTime.Now < until)
        {
            var target = overlay.BeginFrame(300, 200);
            gpu.Context.ClearRenderTargetView(target, new Vortice.Mathematics.Color4(1f, 0f, 1f, 1f));
            overlay.Present();
            while (Native.PeekMessage(out var msg, IntPtr.Zero, 0, 0, 1)) Native.DispatchMessage(ref msg);
        }
        return 0;
    }

    public static int Run(IntPtr hwnd)
    {
        using var gpu = new Gpu();
        Console.WriteLine("GPU e shaders: ok");
        using var overlay = new Overlay(gpu); // valida DirectComposition + swap chain sem mostrar a janela
        overlay.BeginFrame(64, 64);
        Console.WriteLine("Camada (DirectComposition): ok");

        var settings = new Settings();
        int frames = 0;
        string? result = null;
        using var done = new ManualResetEventSlim();
        using var capture = new Capture(gpu, hwnd);
        capture.FrameArrived += (texture, size, _) =>
        {
            if (done.IsSet) return;
            frames++;
            int w = size.Width, h = size.Height;
            using var output = gpu.Device.CreateTexture2D(new Texture2DDescription(Format.B8G8R8A8_UNorm, (uint)w, (uint)h, 1, 1, BindFlags.RenderTarget));
            using var target = gpu.Device.CreateRenderTargetView(output);
            gpu.Process(texture, new Box(0, 0, 0, w, h, 1), target, settings);
            // janela parada só manda um quadro: o primeiro já basta

            // Lê o resultado de volta e compara com a captura original.
            using var staging = gpu.Device.CreateTexture2D(new Texture2DDescription(Format.B8G8R8A8_UNorm, (uint)w, (uint)h, 1, 1,
                BindFlags.None, ResourceUsage.Staging, CpuAccessFlags.Read, 1, 0, ResourceOptionFlags.None));
            using var stagingIn = gpu.Device.CreateTexture2D(new Texture2DDescription(Format.B8G8R8A8_UNorm, (uint)w, (uint)h, 1, 1,
                BindFlags.None, ResourceUsage.Staging, CpuAccessFlags.Read, 1, 0, ResourceOptionFlags.None));
            gpu.Context.CopyResource(staging, output);
            gpu.Context.CopySubresourceRegion(stagingIn, 0, 0, 0, 0, texture, 0, new Box(0, 0, 0, w, h, 1));
            (double mean, double contrast) Stats(ID3D11Texture2D tex)
            {
                var map = gpu.Context.Map(tex, 0, MapMode.Read);
                double sum = 0, sum2 = 0; long n = 0;
                for (int y = 0; y < h; y += 2)
                for (int x = 0; x < w; x += 2)
                {
                    int offset = (int)(y * map.RowPitch) + x * 4;
                    double l = 0.0722 * Marshal.ReadByte(map.DataPointer, offset) + 0.7152 * Marshal.ReadByte(map.DataPointer, offset + 1) + 0.2126 * Marshal.ReadByte(map.DataPointer, offset + 2);
                    sum += l; sum2 += l * l; n++;
                }
                gpu.Context.Unmap(tex, 0);
                double m = sum / n;
                return (m, Math.Sqrt(sum2 / n - m * m));
            }
            var before = Stats(stagingIn);
            var after = Stats(staging);
            result = $"Quadros capturados: {frames} ({w}x{h})\n" +
                     $"Original:    brilho {before.mean:0.0}  contraste {before.contrast:0.00}\n" +
                     $"Processado:  brilho {after.mean:0.0}  contraste {after.contrast:0.00}";
            done.Set();
        };

        bool ok = done.Wait(8000);
        Console.WriteLine(ok ? result : $"Captura: nenhum quadro em 8 s (quadros: {frames})");
        return ok ? 0 : 1;
    }
}
