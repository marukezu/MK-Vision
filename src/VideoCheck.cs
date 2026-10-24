using System.Runtime.InteropServices;
using Vortice.Direct3D11;

namespace MKVision.Desktop;

// --videocheck <pasta>: para cada vídeo, resolução, quadros/s, duração e quanto dos quadros são cópias (em 3
// pontos, 90 quadros cada) — serve para ver se é bom exemplo de treino (quadros reais, ritmo regular).
internal static class VideoCheck
{
    public static int Run(string folder)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        using var gpu = new Gpu();
        foreach (var path in Directory.GetFiles(folder).Where(f => Path.GetExtension(f).ToLowerInvariant() is ".mp4" or ".webm" or ".mkv" or ".mov" or ".m4v").OrderBy(f => f))
        {
            try
            {
                using var file = new VideoFile(path, gpu.Device);
                int w = file.Width, h = file.Height, repeats = 0, total = 0, irregular = 0;
                ID3D11Texture2D? staging = null;
                foreach (double at in new[] { 0.2, 0.5, 0.8 })
                {
                    file.Seek(file.Duration * at);
                    byte[]? previous = null;
                    double lastTime = -1;
                    for (int i = 0; i < 90; i++)
                    {
                        byte[]? pixels = null;
                        if (!file.ReadFrame((texture, index) =>
                        {
                            var d = texture.Description;
                            staging ??= gpu.Device.CreateTexture2D(new Texture2DDescription(d.Format, (uint)w, (uint)h, 1, 1, BindFlags.None, ResourceUsage.Staging, CpuAccessFlags.Read));
                            gpu.Context.CopySubresourceRegion(staging, 0, 0, 0, 0, texture, index, new Vortice.Mathematics.Box(0, 0, 0, w, h, 1));
                            var map = gpu.Context.Map(staging, 0, MapMode.Read);
                            pixels = new byte[w * h * 4];
                            for (int y = 0; y < h; y++) Marshal.Copy(map.DataPointer + (nint)(y * map.RowPitch), pixels, y * w * 4, w * 4);
                            gpu.Context.Unmap(staging, 0);
                        }, out double time)) break;
                        if (previous is not null)
                        {
                            long sum = 0;
                            int n = 0;
                            for (int k = 1; k < pixels!.Length; k += 4 * 13) { sum += Math.Abs(pixels[k] - previous[k]); n++; }
                            if (sum / (double)n < 0.3) repeats++;
                            if (Math.Abs(time - lastTime - file.FrameDuration) > 0.003) irregular++;
                            total++;
                        }
                        previous = pixels;
                        lastTime = time;
                    }
                }
                staging?.Dispose();
                Console.WriteLine($"{Path.GetFileName(path),-48} {w}x{h}  {1 / file.FrameDuration:0.##} q/s  {file.Duration:0} s  cópias {repeats * 100 / Math.Max(1, total)}%  ritmo irregular {irregular * 100 / Math.Max(1, total)}%");
            }
            catch (Exception e) { Console.WriteLine($"{Path.GetFileName(path),-48} não abriu: {e.Message.Split('\n')[0]}"); }
        }
        return 0;
    }
}
