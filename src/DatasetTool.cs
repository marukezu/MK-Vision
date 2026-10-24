using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Vortice.Direct3D11;

namespace MKVision.Desktop;

// --dataset <pasta dos vídeos> <pasta de saída> [momentos]: prepara exemplos para treinar a IA de câmera lenta com
// os vídeos do usuário. Cada exemplo = 5 quadros reais seguidos (ritmo regular, sem cópias, sem corte de cena),
// recortados em 256×256 onde há mais movimento: o treino cria o quadro do meio (e os de ¼ e ¾) a partir das
// pontas. Equilíbrio por pasta (peso pela raiz do nº de vídeos: pasta pequena rende mais por vídeo); 1 em cada 10
// vídeos de cada pasta fica de fora do treino, só para a prova (pasta "val"). Formato: .bin com 5×256×256×3 bytes
// (RGB, quadro a quadro; 7× com MKV_DATASET_FRAMES=7) + samples.csv. Nada sai do computador. Interrompido, continua de onde parou.
internal static class DatasetTool
{
    private const int Crop = 256;
    // Quadros por exemplo: 5 (IA de 2 quadros) ou 7 com MKV_DATASET_FRAMES=7 (IA com o quadro de antes e o de depois).
    private static readonly int Frames = int.TryParse(Environment.GetEnvironmentVariable("MKV_DATASET_FRAMES"), out int f) && f is >= 3 and <= 15 ? f : 5;
    private static readonly string[] Extensions = [".mp4", ".m4v", ".mkv", ".webm", ".mov", ".wmv", ".avi", ".ts"];

    // Prioridade (o caso crítico: pés e dedos cobrindo e mostrando a glande o tempo todo): vídeos indicados pelo
    // usuário rendem muitos exemplos; a pasta só de footjob pesa 3x. Nos vídeos prioritários a prova usa o fim
    // (87–98% da duração) e o treino o resto.
    private static readonly (string path, int moments)[] PriorityVideos =
    [
        (@"Joy Milks\", 320),
        (@"Random Footjob\14646334 4 K.mp4", 160),
        (@"Random Footjob\14645644 4 K.mp4", 160),
    ];
    private static readonly (string folder, double weight)[] PriorityFolders = [("jhonn womens feet", 3.0)];

    // Vídeos já interpolados por IA (RIFE, SVP, "150fps"...): os quadros do meio são inventados — como gabarito,
    // ensinariam os erros da própria IA. Ficam fora.
    private static bool Interpolated(string file) =>
        Regex.IsMatch(Path.GetFileName(file), @"rife|svp|interpol|(^|[^0-9])(90|100|120|144|150|165|240)\s*fps", RegexOptions.IgnoreCase);

    // label: nome do grupo para todos os vídeos (em vez da subpasta) — ex.: "RedGIFs" para a pasta dos downloads.
    public static int Run(string root, string output, int moments, string? label = null)
    {
        Console.OutputEncoding = Encoding.UTF8;
        var all = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Where(f => Extensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
        foreach (var f in all.Where(Interpolated)) Console.WriteLine($"  fora (já interpolado por IA): {Path.GetRelativePath(root, f)}");
        var videos = all.Where(f => !Interpolated(f)).ToList();
        string FolderOf(string f) => label ?? (Path.GetRelativePath(root, f).Split(Path.DirectorySeparatorChar) is { Length: > 1 } parts ? parts[0] : "(raiz)");
        var folders = videos.GroupBy(FolderOf).ToDictionary(g => g.Key, g => g.ToList());
        double FolderWeight(string folder) => Math.Sqrt(folders[folder].Count) *
            (PriorityFolders.FirstOrDefault(p => p.folder == folder).weight is > 0 and var w ? w : 1);
        double weights = folders.Keys.Sum(FolderWeight);
        var plan = new List<(string video, string folder, int count, bool val, bool tail)>();
        foreach (var (folder, list) in folders)
        {
            int budget = (int)Math.Round(moments * FolderWeight(folder) / weights);
            for (int i = 0; i < list.Count; i++)
            {
                string rel = Path.GetRelativePath(root, list[i]);
                var priority = PriorityVideos.FirstOrDefault(p => rel.StartsWith(p.path, StringComparison.OrdinalIgnoreCase));
                if (priority.moments > 0)
                {
                    plan.Add((list[i], folder, priority.moments, false, true));
                    continue;
                }
                bool val = list.Count >= 4 && i % 10 == 3; // prova: vídeos que o treino nunca vê
                int count = val ? 12 : Math.Clamp((int)Math.Ceiling(budget / (double)list.Count), 4, 120);
                plan.Add((list[i], folder, count, val, false));
            }
        }
        Console.WriteLine($"{videos.Count} vídeos em {folders.Count} pastas; {plan.Count(p => p.val)} separados para a prova (+ o fim dos {plan.Count(p => p.tail)} prioritários); ~{plan.Where(p => !p.val).Sum(p => p.count)} momentos de treino planejados");
        foreach (var (folder, list) in folders.OrderByDescending(f => f.Value.Count))
            Console.WriteLine($"  {list.Count,4} vídeos  {plan.Where(p => p.folder == folder && !p.val).Sum(p => p.count),5} momentos  {folder}");
        if (Environment.GetEnvironmentVariable("MKV_PLAN_ONLY") == "1") return 0;

        Directory.CreateDirectory(Path.Combine(output, "train"));
        Directory.CreateDirectory(Path.Combine(output, "val"));
        string indexPath = Path.Combine(output, "videos.csv");
        var done = File.Exists(indexPath) ? File.ReadAllLines(indexPath).Select(l => l.Split(';')[1]).ToHashSet() : [];
        using var index = new StreamWriter(indexPath, append: true, Encoding.UTF8);
        using var samples = new StreamWriter(Path.Combine(output, "samples.csv"), append: true, Encoding.UTF8);
        using var gpu = new Gpu();
        var random = new Random(1234 + done.Count);
        int saved = 0, skipped = 0;
        int n = Directory.GetFiles(Path.Combine(output, "train")).Length + Directory.GetFiles(Path.Combine(output, "val")).Length;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        for (int v = 0; v < plan.Count; v++)
        {
            var (video, folder, count, val, tail) = plan[v];
            string key = Path.GetRelativePath(root, video);
            if (done.Contains(key)) continue; // retomando: este já foi
            int got = 0;
            try
            {
                using var file = new VideoFile(video, gpu.Device);
                double duration = file.Duration > 0 ? file.Duration : 600;
                int total = count + (tail ? 16 : 0);
                for (int m = 0; m < total; m++)
                {
                    bool isVal = val || (tail && m >= count);
                    double at = tail
                        ? duration * (m < count ? 0.04 + 0.81 * (m + random.NextDouble() * 0.8) / count : 0.87 + 0.11 * (m - count + random.NextDouble() * 0.8) / 16)
                        : duration * (0.04 + 0.92 * (m + random.NextDouble() * 0.8) / count);
                    // 60 q/s: na maioria das vezes 1 quadro a cada 2 (como um vídeo de 30, movimento maior — com o
                    // quadro do meio real como gabarito); às vezes seguidos (movimento fino).
                    int stride = file.FrameDuration < 1 / 45.0 && random.NextDouble() < 0.7 ? 2 : 1;
                    var frames = ReadRun(gpu, file, at, stride);
                    if (frames is null) { skipped++; continue; }
                    // Duas regiões de cada momento (cortes diferentes = mais variedade com a mesma leitura).
                    for (int c = 0; c < (isVal ? 1 : 2); c++)
                    {
                        if (CropSample(frames.Value, random) is not { } sample) { skipped++; continue; }
                        string name = $"{n++:000000}.bin";
                        string set = isVal ? "val" : "train";
                        File.WriteAllBytes(Path.Combine(output, set, name), sample);
                        samples.WriteLine($"{set}/{name};{folder};{(tail ? "prioridade" : "")};{Path.GetFileName(video)};{at:0.0}");
                        got++;
                        saved++;
                    }
                }
            }
            catch (Exception e) { Console.WriteLine($"  não deu: {Path.GetFileName(video)} — {e.Message.Split('\n')[0]}"); }
            samples.Flush();
            index.WriteLine($"{(val ? "val" : tail ? "prioridade" : "train")};{key};{folder};{got}");
            index.Flush();
            double rate = saved / Math.Max(1, clock.Elapsed.TotalSeconds);
            Console.WriteLine($"[{v + 1}/{plan.Count}] {got,3} exemplos  {folder} / {Path.GetFileName(video)}  (total {saved}, {rate * 60:0}/min, descartados {skipped})");
        }
        Console.WriteLine($"Pronto: {saved} exemplos em {clock.Elapsed.TotalMinutes:0} min ({skipped} momentos descartados).");
        return 0;
    }

    private readonly record struct FrameRun(byte[][] Pixels, int Width, int Height);

    // 5 quadros reais seguidos a partir de "at" (BGRA na memória). null = não serve: quadro repetido, ritmo
    // irregular, corte de cena ou quase parado.
    private static FrameRun? ReadRun(Gpu gpu, VideoFile file, double at, int stride = 1)
    {
        file.Seek(at);
        ID3D11Texture2D? staging = null;
        var frames = new List<byte[]>();
        var times = new List<double>();
        int w = file.Width, h = file.Height;
        try
        {
            int needed = (Frames - 1) * stride + 1;
            for (int guard = 0; guard < 1500 && frames.Count < needed; guard++)
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
                }, out double time, t => t >= at - 0.001)) return null;
                if (pixels is null) continue; // antes do ponto: só decodificou
                // Quadro repetido (trecho congelado): recomeça a sequência dali e segue procurando (até ~6 s) —
                // há vídeos com longos trechos parados entre trechos de movimento real.
                if (frames.Count > 0 && MeanDiff(frames[^1], pixels) < 0.3)
                {
                    if (time - at > 6) return null;
                    frames.Clear();
                    times.Clear();
                }
                frames.Add(pixels);
                times.Add(time);
            }
        }
        finally { staging?.Dispose(); }
        if (frames.Count < (Frames - 1) * stride + 1) return null;
        // Ritmo regular (mesmo intervalo entre todos) e sem cópias (cada quadro muda).
        double step = times[1] - times[0];
        if (step <= 0 || step > 0.05) return null;
        for (int i = 1; i < times.Count; i++) if (Math.Abs(times[i] - times[i - 1] - step) > 0.002) return null;
        if (stride > 1) frames = Enumerable.Range(0, Frames).Select(i => frames[i * stride]).ToList();
        var diffs = new double[Frames - 1];
        for (int i = 1; i < Frames; i++) diffs[i - 1] = MeanDiff(frames[i - 1], frames[i]);
        if (diffs.Min() < 0.3) return null;                                     // cópia de quadro
        if (diffs.Max() > 25 || diffs.Max() > 4 * diffs.Min() + 8) return null; // corte de cena / flash
        if (MeanDiff(frames[0], frames[Frames - 1]) < 1.5) return null;         // quase parado
        return new FrameRun([.. frames], w, h);
    }

    private static double MeanDiff(byte[] a, byte[] b)
    {
        long sum = 0;
        int n = 0;
        for (int i = 1; i < a.Length; i += 4 * 13) { sum += Math.Abs(a[i] - b[i]); n++; }
        return sum / (double)n;
    }

    // Recorte 256×256 numa região que se move (sorteada com peso pelo movimento). Vídeo maior que 1080p: metade
    // do tamanho antes; às vezes também em meio tamanho (movimentos menores, como a análise reduzida do app).
    private static byte[]? CropSample(FrameRun run, Random random)
    {
        int w = run.Width, h = run.Height;
        var frames = run.Pixels;
        int factor = Math.Max(w, h) > 2200 ? 2 : 1;
        if (Math.Max(w / factor, h / factor) >= 1280 && random.NextDouble() < 0.3) factor *= 2;
        int sw = w / factor, sh = h / factor;
        if (sw < Crop || sh < Crop) return null;
        // Mapa de movimento (blocos de 32 px do tamanho final) entre o 1º e o último quadro.
        int bw = sw / 32, bh = sh / 32;
        var weight = new double[bw * bh];
        double total = 0;
        for (int by = 0; by < bh; by++)
            for (int bx = 0; bx < bw; bx++)
            {
                double s = 0;
                for (int y = by * 32; y < by * 32 + 32; y += 4)
                    for (int x = bx * 32; x < bx * 32 + 32; x += 4)
                    {
                        int i = ((y * factor) * w + x * factor) * 4 + 1;
                        s += Math.Abs(frames[0][i] - frames[Frames - 1][i]);
                    }
                double v = Math.Pow(s / 64, 1.5);
                weight[by * bw + bx] = v;
                total += v;
            }
        if (total <= 0) return null;
        double pick = random.NextDouble() * total;
        int block = 0;
        for (; block < weight.Length - 1 && pick > weight[block]; block++) pick -= weight[block];
        int cx = (block % bw) * 32 + 16 + random.Next(-48, 49), cy = (block / bw) * 32 + 16 + random.Next(-48, 49);
        int x0 = Math.Clamp(cx - Crop / 2, 0, sw - Crop), y0 = Math.Clamp(cy - Crop / 2, 0, sh - Crop);
        var sample = new byte[Frames * Crop * Crop * 3];
        double motion = 0;
        int area = factor * factor;
        for (int f = 0; f < Frames; f++)
            for (int y = 0; y < Crop; y++)
                for (int x = 0; x < Crop; x++)
                {
                    int o = ((f * Crop + y) * Crop + x) * 3;
                    int r = 0, g = 0, b = 0;
                    for (int dy = 0; dy < factor; dy++)
                        for (int dx = 0; dx < factor; dx++)
                        {
                            int i = (((y0 + y) * factor + dy) * w + (x0 + x) * factor + dx) * 4;
                            b += frames[f][i];
                            g += frames[f][i + 1];
                            r += frames[f][i + 2];
                        }
                    (sample[o], sample[o + 1], sample[o + 2]) = ((byte)(r / area), (byte)(g / area), (byte)(b / area));
                    if (f == Frames - 1) motion += Math.Abs(sample[o + 1] - sample[(y * Crop + x) * 3 + 1]);
                }
        if (motion / (Crop * Crop) < 3) return null; // recorte quase parado
        return sample;
    }
}
