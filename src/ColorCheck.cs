using System.Runtime.InteropServices;
using Vortice.Direct3D11;
using Vortice.MediaFoundation;

namespace MKVision.Desktop;

// --colorcheck <pasta> [máx]: confere se a cor dos vídeos sai certa da decodificação. Para cada vídeo: o que o
// arquivo diz (faixa limitada 16–235 ou completa 0–255, matriz BT.601/709) e o brilho mais escuro/mais claro
// dos quadros (em 3 pontos). Se os pretos nunca descem de ~16 nem os brancos passam de ~235, o vídeo está sendo
// mostrado "lavado" (faixa limitada tratada como completa).
internal static class ColorCheck
{
    private static readonly Guid NominalRange = new("c21b8ee5-b956-4071-8daf-325edf5cab11");
    private static readonly Guid YuvMatrix = new("3e23d450-2c75-4d25-a00e-b91670d12327");
    private static readonly Guid Primaries = new("dbfbe4d7-0740-4ee0-8192-850ab0e21935");
    private static readonly Guid Transfer = new("5fb0fce9-be5c-4935-a811-ec838f8eed93");

    public static string Describe(IMFMediaType type)
    {
        string Get(Guid key, Func<uint, string> name) => type.GetUInt32(key, out uint v).Success ? name(v) : "?";
        string range = Get(NominalRange, v => v switch { 1 => "completa", 2 => "limitada", _ => $"{v}" });
        string matrix = Get(YuvMatrix, v => v switch { 1 => "709", 2 => "601", 3 => "240M", 4 => "2020", _ => $"{v}" });
        string primaries = Get(Primaries, v => v switch { 2 => "709", 5 => "601-625", 6 => "601-525", 9 => "2020", _ => $"{v}" });
        string transfer = Get(Transfer, v => v switch { 5 => "709", 13 => "sRGB", 15 => "PQ", 16 => "HLG", _ => $"{v}" });
        return $"faixa {range}, matriz {matrix}, cores {primaries}, curva {transfer}";
    }

    public static int Run(string folder, int max)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        using var gpu = new Gpu();
        var files = Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
            .Where(f => Path.GetExtension(f).ToLowerInvariant() is ".mp4" or ".webm" or ".mkv" or ".mov" or ".m4v")
            .OrderBy(_ => Random.Shared.Next()).Take(max).ToList();
        int washed = 0, total = 0;
        foreach (var path in files)
        {
            try
            {
                using var file = new VideoFile(path, gpu.Device);
                int w = file.Width, h = file.Height;
                var histogram = new long[256];
                ID3D11Texture2D? staging = null;
                foreach (double at in new[] { 0.2, 0.5, 0.8 })
                {
                    file.Seek(file.Duration * at);
                    for (int i = 0; i < 20; i++)
                    {
                        if (!file.ReadFrame((texture, index) =>
                        {
                            var d = texture.Description;
                            staging ??= gpu.Device.CreateTexture2D(new Texture2DDescription(d.Format, (uint)w, (uint)h, 1, 1, BindFlags.None, ResourceUsage.Staging, CpuAccessFlags.Read));
                            gpu.Context.CopySubresourceRegion(staging, 0, 0, 0, 0, texture, index, new Vortice.Mathematics.Box(0, 0, 0, w, h, 1));
                            var map = gpu.Context.Map(staging, 0, MapMode.Read);
                            var row = new byte[w * 4];
                            for (int y = 0; y < h; y += 3)
                            {
                                Marshal.Copy(map.DataPointer + (nint)(y * map.RowPitch), row, 0, row.Length);
                                for (int x = 0; x < w; x += 3)
                                {
                                    int b = row[x * 4], g = row[x * 4 + 1], r = row[x * 4 + 2];
                                    histogram[(r * 54 + g * 183 + b * 19) >> 8]++;
                                }
                            }
                            gpu.Context.Unmap(staging, 0);
                        }, out _)) break;
                    }
                }
                staging?.Dispose();
                long count = histogram.Sum();
                int Percentile(double p)
                {
                    long target = (long)(count * p), sum = 0;
                    for (int v = 0; v < 256; v++) if ((sum += histogram[v]) > target) return v;
                    return 255;
                }
                int dark = Percentile(0.001), bright = Percentile(0.999);
                double below16 = histogram.Take(14).Sum() * 100.0 / Math.Max(1, count), above235 = histogram.Skip(238).Sum() * 100.0 / Math.Max(1, count);
                bool looksWashed = dark >= 14 && bright <= 238 && below16 < 0.05 && above235 < 0.05;
                if (looksWashed) washed++;
                total++;
                Console.WriteLine($"{Path.GetFileName(path),-50} {w}x{h}  {file.ColorInfo}  |  escuro {dark}, claro {bright}, abaixo de 14: {below16:0.00}%, acima de 238: {above235:0.00}%{(looksWashed ? "  ← parece lavado" : "")}");
            }
            catch (Exception e) { Console.WriteLine($"{Path.GetFileName(path),-50} não abriu: {e.Message.Split('\n')[0]}"); }
        }
        Console.WriteLine($"{washed} de {total} parecem lavados (pretos nunca abaixo de ~16 e brancos nunca acima de ~235).");
        return 0;
    }
}
