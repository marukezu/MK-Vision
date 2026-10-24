using System.Runtime.InteropServices;
using Vortice.Direct3D11;

namespace MKVision.Desktop;

// --cadencetest <vídeo> [segundo]: o vídeo com quadros repetidos foi filmado em ritmo regular (as imagens
// só foram mal distribuídas no arquivo) ou tem buracos (imagens que faltam)? Mede, com a IA de quadros, quanto
// cada imagem nova se move até a seguinte: se o movimento acompanha o intervalo no arquivo (33 ms pouco,
// 100 ms três vezes mais), as imagens estão nos momentos certos; se é igual, o ritmo real é regular.
internal static class CadenceTest
{
    public static int Run(string path, double start)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        using var gpu = new Gpu();
        var gpuLock = new object();
        RifeHost.Start();
        var fi = new FrameInterpolator(gpu, gpuLock);
        using var file = new VideoFile(path, gpu.Device);
        if (start > 0) file.Seek(start);
        const int aw = 960, ah = 540;
        var images = new List<(double time, float[] rgb)>();
        ID3D11Texture2D? copy = null, staging = null;
        ID3D11ShaderResourceView? view = null;
        byte[]? previous = null;
        for (int n = 0; n < 3000 && images.Count < 25; n++)
        {
            byte[]? pixels = null;
            if (!file.ReadFrame((texture, index) =>
            {
                var d = texture.Description;
                if (copy is null)
                {
                    copy = gpu.Device.CreateTexture2D(new Texture2DDescription(d.Format, (uint)file.Width, (uint)file.Height, 1, 1, BindFlags.ShaderResource));
                    view = gpu.Device.CreateShaderResourceView(copy);
                    staging = gpu.Device.CreateTexture2D(new Texture2DDescription(d.Format, (uint)file.Width, (uint)file.Height, 1, 1,
                        BindFlags.None, ResourceUsage.Staging, CpuAccessFlags.Read));
                }
                var box = new Vortice.Mathematics.Box(0, 0, 0, file.Width, file.Height, 1);
                gpu.Context.CopySubresourceRegion(copy, 0, 0, 0, 0, texture, index, box);
                gpu.Context.CopySubresourceRegion(staging!, 0, 0, 0, 0, texture, index, box);
                var map = gpu.Context.Map(staging!, 0, MapMode.Read);
                pixels = new byte[file.Width * file.Height * 4];
                for (int y = 0; y < file.Height; y++)
                    Marshal.Copy(map.DataPointer + (nint)(y * map.RowPitch), pixels, y * file.Width * 4, file.Width * 4);
                gpu.Context.Unmap(staging!, 0);
            }, out double time)) break;
            if (time < start - 0.01) continue;
            if (previous is not null)
            {
                long sum = 0;
                for (int i = 0; i < pixels!.Length; i += 4 * 7) sum += Math.Abs(pixels[i + 1] - previous[i + 1]);
                if (sum / (double)(pixels.Length / (4 * 7)) < 0.3) continue; // cópia
            }
            previous = pixels;
            var rgb = new float[3 * aw * ah];
            fi.PrepareInput(view!, aw, ah, rgb);
            images.Add((time, rgb));
        }
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (RifeHost.Instance is null && clock.Elapsed.TotalSeconds < 240) Thread.Sleep(200);
        var rife = RifeHost.Instance;
        if (rife is null) { Console.WriteLine("IA não carregou."); return 1; }
        Console.WriteLine("intervalo no arquivo → movimento (média dos 10% que mais se movem, pixels em 960x540)");
        for (int i = 1; i < images.Count; i++)
        {
            double motion = 0;
            rife.RunMotion(images[i - 1].rgb, images[i].rgb, aw, ah, 0.5f, (flow, mask, pw, ph) =>
            {
                var f = flow.Span;
                int layer = pw * ph;
                var magnitudes = new float[aw * ah];
                int k = 0;
                for (int y = 0; y < ah; y++)
                    for (int x = 0; x < aw; x++)
                    {
                        int j = y * pw + x;
                        float ax = f[j], ay = f[layer + j], bx = f[2 * layer + j], by = f[3 * layer + j];
                        magnitudes[k++] = MathF.Sqrt(ax * ax + ay * ay) + MathF.Sqrt(bx * bx + by * by);
                    }
                Array.Sort(magnitudes);
                motion = magnitudes.Skip(magnitudes.Length * 9 / 10).Average();
            });
            double interval = (images[i].time - images[i - 1].time) * 1000;
            Console.WriteLine($"{images[i].time:0.000}s  +{interval:0} ms → {motion:0.0} px   ({motion / interval * 33.37:0.0} px por 33 ms)");
        }
        return 0;
    }
}
