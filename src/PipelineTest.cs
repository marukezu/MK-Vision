using System.Diagnostics;
using System.Runtime.InteropServices;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace MKVision.Desktop;

// "--pipelinetest": simula um vídeo 480p esticado pelo navegador numa tela 2560x1440 e mede,
// para cada potência, o quanto o resultado se aproxima da imagem original nítida (PSNR, em dB).
internal static class PipelineTest
{
    private const int W = 2560, H = 1440;      // tela
    private const int LowW = 854, LowH = 480;  // vídeo 480p

    public static int Run()
    {
        using var gpu = new Gpu();
        using var pipeline = new Pipeline(gpu);

        // Imagem "real": digitais (frequência variável), bordas e gradiente — com antisserrilhado 4x4.
        float[] truth = new float[W * H * 3];
        Parallel.For(0, H, y =>
        {
            for (int x = 0; x < W; x++)
            {
                float r = 0, g = 0, b = 0;
                for (int sy = 0; sy < 4; sy++)
                for (int sx = 0; sx < 4; sx++)
                {
                    var c = Scene((x + (sx + 0.5f) / 4) / W, (y + (sy + 0.5f) / 4) / H);
                    r += c.r; g += c.g; b += c.b;
                }
                int i = (y * W + x) * 3;
                truth[i] = r / 16; truth[i + 1] = g / 16; truth[i + 2] = b / 16;
            }
        });

        // 480p: média de área. Depois o "navegador" estica de volta com bilinear.
        float[] low = Resize(truth, W, H, LowW, LowH, area: true);
        float[] browser = Resize(low, LowW, LowH, W, H, area: false);
        Console.WriteLine($"Navegador (o que você vê hoje): {Psnr(browser, truth):0.00} dB");

        using var captured = Upload(gpu, browser);
        using var output = gpu.Device.CreateTexture2D(new Texture2DDescription(Format.B8G8R8A8_UNorm, W, H, 1, 1, BindFlags.RenderTarget));
        using var target = gpu.Device.CreateRenderTargetView(output);
        var view = new VideoView(new Box(0, 0, 0, W, H, 1), LowW, LowH, 0, 0, 1, 1);

        var waited = Stopwatch.StartNew();
        while (!pipeline.AiReady && waited.Elapsed.TotalSeconds < 120) Thread.Sleep(100);
        Console.WriteLine($"IA pronta em {waited.Elapsed.TotalSeconds:0.0} s");

        foreach (int power in new[] { 1, 2, 3, 4, 5 })
        {
            var settings = new Settings { Power = power, Detail = 0, Texture = 0 }; // só reconstrução, sem nitidez extra
            for (int k = 0; k < 3; k++) pipeline.Process(captured, view, target, W, H, settings); // aquece (temporal)
            float psnr = Psnr(Download(gpu, output), truth);

            var clock = Stopwatch.StartNew();
            for (int k = 0; k < 30; k++) pipeline.Process(captured, view, target, W, H, settings);
            Download(gpu, output); // espera a GPU terminar
            Console.WriteLine($"Potência {power}: {psnr:0.00} dB   {clock.Elapsed.TotalMilliseconds / 30:0.0} ms/quadro   [{pipeline.LastInfo}]");
        }
        return 0;
    }

    private static (float r, float g, float b) Scene(float u, float v)
    {
        float x = u * 16 / 9, y = v;
        float ridge = 0.5f + 0.5f * MathF.Sin(MathF.Sqrt((x - 0.5f) * (x - 0.5f) + (y - 0.5f) * (y - 0.5f) * 1.7f) * (180 + 120 * MathF.Sin(y * 6)));
        float skin = 0.72f - 0.18f * ridge;
        if (x > 1.05f && x < 1.6f && y > 0.15f && y < 0.85f) skin = ((int)(x * 40) + (int)(y * 40)) % 2 == 0 ? 0.25f : 0.8f; // bordas
        if (x < 0.15f) skin = 0.3f + 0.5f * y;                                                                         // gradiente
        return (skin + 0.12f, skin, skin - 0.08f);
    }

    private static float[] Resize(float[] src, int sw, int sh, int dw, int dh, bool area)
    {
        float[] dst = new float[dw * dh * 3];
        Parallel.For(0, dh, y =>
        {
            for (int x = 0; x < dw; x++)
            for (int c = 0; c < 3; c++)
            {
                float value;
                if (area)
                {
                    int x0 = x * sw / dw, x1 = Math.Max(x0 + 1, (x + 1) * sw / dw);
                    int y0 = y * sh / dh, y1 = Math.Max(y0 + 1, (y + 1) * sh / dh);
                    float sum = 0;
                    for (int yy = y0; yy < y1; yy++) for (int xx = x0; xx < x1; xx++) sum += src[(yy * sw + xx) * 3 + c];
                    value = sum / ((x1 - x0) * (y1 - y0));
                }
                else
                {
                    float fx = Math.Clamp((x + 0.5f) * sw / dw - 0.5f, 0, sw - 1), fy = Math.Clamp((y + 0.5f) * sh / dh - 0.5f, 0, sh - 1);
                    int ix = Math.Min((int)fx, sw - 2), iy = Math.Min((int)fy, sh - 2);
                    float ax = fx - ix, ay = fy - iy;
                    float Get(int xx, int yy) => src[(yy * sw + xx) * 3 + c];
                    value = (Get(ix, iy) * (1 - ax) + Get(ix + 1, iy) * ax) * (1 - ay) + (Get(ix, iy + 1) * (1 - ax) + Get(ix + 1, iy + 1) * ax) * ay;
                }
                dst[(y * dw + x) * 3 + c] = value;
            }
        });
        return dst;
    }

    private static ID3D11Texture2D Upload(Gpu gpu, float[] rgb)
    {
        byte[] bgra = new byte[W * H * 4];
        for (int p = 0; p < W * H; p++)
        {
            bgra[p * 4] = (byte)Math.Clamp(rgb[p * 3 + 2] * 255 + 0.5f, 0, 255);
            bgra[p * 4 + 1] = (byte)Math.Clamp(rgb[p * 3 + 1] * 255 + 0.5f, 0, 255);
            bgra[p * 4 + 2] = (byte)Math.Clamp(rgb[p * 3] * 255 + 0.5f, 0, 255);
            bgra[p * 4 + 3] = 255;
        }
        var handle = GCHandle.Alloc(bgra, GCHandleType.Pinned);
        try
        {
            return gpu.Device.CreateTexture2D(new Texture2DDescription(Format.B8G8R8A8_UNorm, W, H, 1, 1, BindFlags.ShaderResource),
                [new SubresourceData(handle.AddrOfPinnedObject(), W * 4)]);
        }
        finally { handle.Free(); }
    }

    private static float[] Download(Gpu gpu, ID3D11Texture2D texture)
    {
        using var staging = gpu.Device.CreateTexture2D(new Texture2DDescription(Format.B8G8R8A8_UNorm, W, H, 1, 1,
            BindFlags.None, ResourceUsage.Staging, CpuAccessFlags.Read));
        gpu.Context.CopyResource(staging, texture);
        var map = gpu.Context.Map(staging, 0, MapMode.Read);
        float[] rgb = new float[W * H * 3];
        byte[] row = new byte[W * 4];
        for (int y = 0; y < H; y++)
        {
            Marshal.Copy(map.DataPointer + (nint)(y * map.RowPitch), row, 0, row.Length);
            for (int x = 0; x < W; x++)
            {
                int i = (y * W + x) * 3;
                rgb[i] = row[x * 4 + 2] / 255f; rgb[i + 1] = row[x * 4 + 1] / 255f; rgb[i + 2] = row[x * 4] / 255f;
            }
        }
        gpu.Context.Unmap(staging, 0);
        return rgb;
    }

    // PSNR da luminância, ignorando 24 px de borda.
    private static float Psnr(float[] a, float[] b)
    {
        double sum = 0; long n = 0;
        for (int y = 24; y < H - 24; y++)
        for (int x = 24; x < W - 24; x++)
        {
            int i = (y * W + x) * 3;
            double la = 0.2126 * a[i] + 0.7152 * a[i + 1] + 0.0722 * a[i + 2];
            double lb = 0.2126 * b[i] + 0.7152 * b[i + 1] + 0.0722 * b[i + 2];
            sum += (la - lb) * (la - lb); n++;
        }
        return (float)(10 * Math.Log10(1.0 / (sum / n)));
    }
}
