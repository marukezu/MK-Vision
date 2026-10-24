using System.IO.Compression;
using System.Runtime.InteropServices;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace MKVision.Desktop;

// --interptest <vídeo> <segundo> <pasta>: acha o par de imagens com mais movimento a partir do segundo dado,
// cria os intermediários com a IA (2 análises por par, como no player) e salva recortes em PNG da região que
// mais se move: A, B e os momentos 0,25 (uma análise exata), 0,375 e 0,5 (entre as duas análises).
internal static class InterpTest
{
    public static int Run(string path, double start, string folder)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        Directory.CreateDirectory(folder);
        using var gpu = new Gpu();
        var gpuLock = new object();
        RifeHost.Start();
        var fi = new FrameInterpolator(gpu, gpuLock);
        using var file = new VideoFile(path, gpu.Device);
        file.Seek(start);
        int vw = file.Width, vh = file.Height;
        int aw = Math.Min(int.TryParse(Environment.GetEnvironmentVariable("MKV_AW"), out int envAw) ? envAw : 1792, vw) / 2 * 2, ah = (int)Math.Round(aw * (double)vh / vw / 2) * 2;
        var images = new List<(double time, float[] rgb, RenderTexture tex)>();
        ID3D11Texture2D? copy = null, staging = null;
        ID3D11ShaderResourceView? view = null;
        byte[]? previous = null;
        for (int n = 0; n < 3000 && images.Count < 30; n++)
        {
            byte[]? pixels = null;
            if (!file.ReadFrame((texture, index) =>
            {
                var d = texture.Description;
                if (copy is null)
                {
                    copy = gpu.Device.CreateTexture2D(new Texture2DDescription(d.Format, (uint)vw, (uint)vh, 1, 1, BindFlags.ShaderResource));
                    view = gpu.Device.CreateShaderResourceView(copy);
                    staging = gpu.Device.CreateTexture2D(new Texture2DDescription(d.Format, (uint)vw, (uint)vh, 1, 1,
                        BindFlags.None, ResourceUsage.Staging, CpuAccessFlags.Read));
                }
                var box = new Vortice.Mathematics.Box(0, 0, 0, vw, vh, 1);
                gpu.Context.CopySubresourceRegion(copy, 0, 0, 0, 0, texture, index, box);
                gpu.Context.CopySubresourceRegion(staging!, 0, 0, 0, 0, texture, index, box);
                pixels = Read(gpu, staging!, vw, vh);
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
            var tex = new RenderTexture(gpu.Device, Format.B8G8R8A8_UNorm);
            tex.Ensure(vw, vh);
            fi.Resample(view!, tex); // cópia do quadro (mesmo tamanho)
            images.Add((time, rgb, tex));
        }
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (RifeHost.Instance is null && clock.Elapsed.TotalSeconds < 240) Thread.Sleep(200);
        var rife = RifeHost.Instance;
        if (rife is null) { Console.WriteLine("IA não carregou."); return 1; }

        // Par com mais movimento e o bloco (1/8 da imagem) onde ele se move mais.
        int best = 1, bx = 0, by = 0;
        double bestMotion = -1;
        for (int i = 1; i < images.Count; i++)
        {
            int index = i;
            rife.RunMotion(images[i - 1].rgb, images[i].rgb, aw, ah, 0.5f, (flow, mask, pw, ph) =>
            {
                var f = flow.Span;
                int layer = pw * ph, cells = 8;
                for (int cy = 0; cy < cells; cy++)
                    for (int cx = 0; cx < cells; cx++)
                    {
                        double sum = 0;
                        int count = 0;
                        for (int y = cy * ah / cells; y < (cy + 1) * ah / cells; y += 2)
                            for (int x = cx * aw / cells; x < (cx + 1) * aw / cells; x += 2)
                            {
                                int j = y * pw + x;
                                sum += MathF.Sqrt(f[j] * f[j] + f[layer + j] * f[layer + j]) +
                                       MathF.Sqrt(f[2 * layer + j] * f[2 * layer + j] + f[3 * layer + j] * f[3 * layer + j]);
                                count++;
                            }
                        if (sum / count > bestMotion) { bestMotion = sum / count; best = index; bx = cx; by = cy; }
                    }
            });
        }
        var (ta, ra, texA) = images[best - 1];
        var (tb, rb, texB) = images[best];
        Console.WriteLine($"par {ta:0.000}s → {tb:0.000}s, movimento {bestMotion * vw / aw:0.0} px no bloco ({bx},{by})");
        int cw = vw / 4, ch = vh / 4;
        int x0 = Math.Clamp((int)((bx + 0.5) * vw / 8) - cw / 2, 0, vw - cw), y0 = Math.Clamp((int)((by + 0.5) * vh / 8) - ch / 2, 0, vh - ch);

        var motion = fi.Analyze(rife, ra, rb, aw, ah, 2)!;
        var target = new RenderTexture(gpu.Device, Format.B8G8R8A8_UNorm);
        target.Ensure(vw, vh);
        var stage = gpu.Device.CreateTexture2D(new Texture2DDescription(Format.B8G8R8A8_UNorm, (uint)vw, (uint)vh, 1, 1,
            BindFlags.None, ResourceUsage.Staging, CpuAccessFlags.Read));
        void Save(string name, RenderTexture source)
        {
            gpu.Context.CopyResource(stage, source.Texture!);
            Png(Path.Combine(folder, name), Read(gpu, stage, vw, vh), vw, x0, y0, cw, ch);
        }
        Save("a.png", texA);
        Save("b.png", texB);
        foreach (var s in new[] { 0.25f, 0.375f, 0.5f })
        {
            fi.Draw(target, texA.View!, texB.View!, motion, s);
            Save($"s{s * 1000:000}.png", target);
        }
        Console.WriteLine("recortes salvos");
        return 0;
    }

    // --interpscore <vídeo> <segundo>: nota da câmera lenta com a resposta certa. Em trios de imagens reais
    // igualmente espaçadas, cria a do meio a partir da 1ª e da 3ª e compara com a verdadeira (PSNR, em dB:
    // maior = mais perto; "mov" só nos pontos que se movem). 2 análises por par (o momento 0,5 fica entre elas,
    // o caso mais difícil), com e sem "conjunto"; e 1 análise exatamente em 0,5 como referência.
    public static int Score(string path, double start)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        using var gpu = new Gpu();
        RifeHost.Start();
        var fi = new FrameInterpolator(gpu, new object());
        using var file = new VideoFile(path, gpu.Device);
        file.Seek(start);
        int vw = file.Width, vh = file.Height;
        int aw = Math.Min(int.TryParse(Environment.GetEnvironmentVariable("MKV_AW"), out int envAw) ? envAw : 1792, vw) / 2 * 2, ah = (int)Math.Round(aw * (double)vh / vw / 2) * 2;
        var images = new List<(double time, float[] rgb, RenderTexture tex, byte[] pixels)>();
        ID3D11Texture2D? copy = null, staging = null;
        ID3D11ShaderResourceView? view = null;
        byte[]? previous = null;
        for (int n = 0; n < 5000 && images.Count < 40; n++)
        {
            byte[]? pixels = null;
            if (!file.ReadFrame((texture, index) =>
            {
                var d = texture.Description;
                if (copy is null)
                {
                    copy = gpu.Device.CreateTexture2D(new Texture2DDescription(d.Format, (uint)vw, (uint)vh, 1, 1, BindFlags.ShaderResource));
                    view = gpu.Device.CreateShaderResourceView(copy);
                    staging = gpu.Device.CreateTexture2D(new Texture2DDescription(d.Format, (uint)vw, (uint)vh, 1, 1,
                        BindFlags.None, ResourceUsage.Staging, CpuAccessFlags.Read));
                }
                var box = new Vortice.Mathematics.Box(0, 0, 0, vw, vh, 1);
                gpu.Context.CopySubresourceRegion(copy, 0, 0, 0, 0, texture, index, box);
                gpu.Context.CopySubresourceRegion(staging!, 0, 0, 0, 0, texture, index, box);
                pixels = Read(gpu, staging!, vw, vh);
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
            var tex = new RenderTexture(gpu.Device, Format.B8G8R8A8_UNorm);
            tex.Ensure(vw, vh);
            fi.Resample(view!, tex);
            images.Add((time, rgb, tex, pixels!));
        }
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (RifeHost.Instance is null && clock.Elapsed.TotalSeconds < 240) Thread.Sleep(200);
        var rife = RifeHost.Instance;
        if (rife is null) { Console.WriteLine("IA não carregou."); return 1; }

        var target = new RenderTexture(gpu.Device, Format.B8G8R8A8_UNorm);
        target.Ensure(vw, vh);
        var stage = gpu.Device.CreateTexture2D(new Texture2DDescription(Format.B8G8R8A8_UNorm, (uint)vw, (uint)vh, 1, 1,
            BindFlags.None, ResourceUsage.Staging, CpuAccessFlags.Read));
        // Tempo real de cada imagem: o meio do tempo em que aparece no arquivo (como o player faz).
        var real = new double[images.Count];
        for (int i = 0; i < images.Count; i++)
            real[i] = i + 1 < images.Count ? images[i].time + Math.Max(0, images[i + 1].time - images[i].time - file.FrameDuration) / 2 : images[i].time;
        var totals = new double[6];
        int trios = 0;
        for (int i = 2; i < images.Count; i++)
        {
            var (t0, r0, x0, p0) = images[i - 2];
            var (t1, _, _, p1) = images[i - 1];
            var (t2, r2, x2, p2) = images[i];
            if (i + 1 >= images.Count || Math.Abs((real[i - 1] - real[i - 2]) - (real[i] - real[i - 1])) > 0.005) continue;
            // Pontos que se movem: mudam entre a 1ª e a 3ª.
            var moving = new bool[vw * vh];
            for (int j = 0; j < moving.Length; j++) moving[j] = Math.Abs(p0[j * 4 + 1] - p2[j * 4 + 1]) > 12;
            // Nitidez: soma das diferenças entre vizinhos (bordas) nos pontos que se movem, comparada com a imagem real.
            double Edges(byte[] px)
            {
                double sum = 0;
                for (int y = 1; y < vh; y++)
                    for (int x = 1; x < vw; x++)
                    {
                        int j = y * vw + x;
                        if (!moving[j]) continue;
                        sum += Math.Abs(px[j * 4 + 1] - px[(j - 1) * 4 + 1]) + Math.Abs(px[j * 4 + 1] - px[(j - vw) * 4 + 1]);
                    }
                return sum;
            }
            double truthEdges = Edges(p1);
            (double mov, double sharp) Score(Motion motion, float crisp)
            {
                FrameInterpolator.CrispLevel = crisp;
                fi.Draw(target, x0.View!, x2.View!, motion, 0.5f);
                FrameInterpolator.CrispLevel = 0;
                gpu.Context.CopyResource(stage, target.Texture!);
                var got = Read(gpu, stage, vw, vh);
                double seMov = 0;
                long nMov = 0;
                for (int j = 0; j < moving.Length; j++)
                {
                    if (!moving[j]) continue;
                    for (int c = 0; c < 3; c++) { double d = got[j * 4 + c] - p1[j * 4 + c]; seMov += d * d; }
                    nMov++;
                }
                double db = 10 * Math.Log10(255.0 * 255.0 / Math.Max(seMov / Math.Max(1, nMov * 3), 1e-6));
                return (db, truthEdges > 0 ? Edges(got) / truthEdges : 1);
            }
            using var two = fi.Analyze(rife, r0, r2, aw, ah, 2)!;
            var normal = Score(two, 0);
            var crispy = Score(two, 1);
            Console.WriteLine($"{t1:0.000}s: mistura {normal.mov:0.00} dB, nitidez {normal.sharp * 100:0}%   nítida {crispy.mov:0.00} dB, nitidez {crispy.sharp * 100:0}%");
            totals[0] += normal.mov; totals[1] += normal.sharp; totals[2] += crispy.mov; totals[3] += crispy.sharp;
            trios++;
        }
        if (trios == 0) { Console.WriteLine("nenhum trio igualmente espaçado"); return 1; }
        Console.WriteLine($"média de {trios} trios (partes que se movem): mistura {totals[0] / trios:0.00} dB, nitidez {totals[1] / trios * 100:0}%   nítida {totals[2] / trios:0.00} dB, nitidez {totals[3] / trios * 100:0}%");
        return 0;
    }

    private static byte[] Read(Gpu gpu, ID3D11Texture2D staging, int w, int h)
    {
        var map = gpu.Context.Map(staging, 0, MapMode.Read);
        var pixels = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
            Marshal.Copy(map.DataPointer + (nint)(y * map.RowPitch), pixels, y * w * 4, w * 4);
        gpu.Context.Unmap(staging, 0);
        return pixels;
    }

    // PNG RGB 8 bits de um recorte de uma imagem BGRA.
    internal static void Png(string path, byte[] bgra, int stride, int x0, int y0, int w, int h)
    {
        var raw = new MemoryStream();
        for (int y = 0; y < h; y++)
        {
            raw.WriteByte(0);
            for (int x = 0; x < w; x++)
            {
                int i = ((y0 + y) * stride + x0 + x) * 4;
                raw.WriteByte(bgra[i + 2]);
                raw.WriteByte(bgra[i + 1]);
                raw.WriteByte(bgra[i]);
            }
        }
        var zipped = new MemoryStream();
        using (var z = new ZLibStream(zipped, CompressionLevel.Fastest, true)) raw.WriteTo(z);
        using var file = new BinaryWriter(File.Create(path));
        file.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
        void Chunk(string type, byte[] data)
        {
            var bytes = System.Text.Encoding.ASCII.GetBytes(type).Concat(data).ToArray();
            file.Write(BigEndian((uint)data.Length));
            file.Write(bytes);
            file.Write(BigEndian(Crc(bytes)));
        }
        Chunk("IHDR", [.. BigEndian((uint)w), .. BigEndian((uint)h), 8, 2, 0, 0, 0]);
        Chunk("IDAT", zipped.ToArray());
        Chunk("IEND", []);
    }

    private static byte[] BigEndian(uint v) => [(byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v];

    private static uint Crc(byte[] data)
    {
        uint c = 0xFFFFFFFF;
        foreach (byte b in data)
        {
            c ^= b;
            for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
        }
        return ~c;
    }
}
