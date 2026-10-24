using System.Runtime.InteropServices;
using Vortice.Direct3D11;

namespace MKVision.Desktop;

// --framediff <vídeo> [segundo]: quanto cada quadro muda em relação ao anterior (diferença média, 0–255) e o
// tempo de cada um. Mostra quadros repetidos (diferença ~0) — o vídeo "para" entre eles, com ou sem IA.
internal static class FrameDiffTest
{
    // --slowstarttest <vídeo> <segundo> <desempenho> <conjunto 0/1>: abre em 0.1x e mostra o diagnóstico da
    // câmera lenta (quadros sem análise logo depois de abrir e de pular).
    public static int RunSlowStart(string path, double start, int performance, bool ensemble)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        using var gpu = new Gpu();
        var player = new Player(gpu, new object());
        void Pump(double seconds)
        {
            var until = DateTime.UtcNow.AddSeconds(seconds);
            while (DateTime.UtcNow < until)
            {
                while (Native.PeekMessage(out var msg, IntPtr.Zero, 0, 0, 1))
                {
                    Native.TranslateMessage(ref msg);
                    Native.DispatchMessage(ref msg);
                }
                Thread.Sleep(5);
            }
        }
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (RifeHost.Instance is null && clock.Elapsed.TotalSeconds < 240) Pump(0.2);
        player.TestPerformanceMode(performance);
        player.TestEnsemble(ensemble);
        player.Open(path);
        Pump(1.0);
        for (int i = 0; i < 9; i++) PlayerNative.PostMessage(player.Hwnd, PlayerNative.WM_KEYDOWN, PlayerNative.VK_DOWN, IntPtr.Zero); // 0.1x
        Pump(0.3);
        player.TestSeek(start);
        Console.WriteLine($"--- aberto ({player.State.speed:0.0#}x)");
        Pump(10);
        player.TestSeek(start + 60);
        Console.WriteLine("--- pulou");
        Pump(7);
        player.Dispose();
        return 0;
    }

    // --dedupetest <vídeo> [segundo]: o player descarta as cópias e espaça as imagens reais por igual?
    public static int RunPlayer(string path, double start)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        using var gpu = new Gpu();
        var player = new Player(gpu, new object());
        void Pump(double seconds)
        {
            var until = DateTime.UtcNow.AddSeconds(seconds);
            while (DateTime.UtcNow < until)
            {
                while (Native.PeekMessage(out var msg, IntPtr.Zero, 0, 0, 1))
                {
                    Native.TranslateMessage(ref msg);
                    Native.DispatchMessage(ref msg);
                }
                Thread.Sleep(5);
            }
        }
        Player.DedupeTrace = [];
        player.Open(path);
        Pump(1.0);
        player.TestSeek(start);
        Pump(0.5);
        lock (Player.DedupeTrace) Player.DedupeTrace.Clear();
        for (int i = 0; i < 12; i++) { Pump(1); int n; lock (Player.DedupeTrace) n = Player.DedupeTrace.Count; Console.WriteLine($"  {i + 1} s: {n} decisões, vídeo em {player.State.fileTime:0.00} s"); } // 1x: ~360 quadros lidos
        List<(double time, bool repeated)> trace;
        lock (Player.DedupeTrace) trace = [.. Player.DedupeTrace];
        // Referência: diferença no tamanho original (FrameDiffTest.Run).
        var truth = Truth(path, trace.Count > 0 ? trace[0].time : start, trace.Count);
        int wrongDrop = 0, missed = 0;
        foreach (var (time, repeated) in trace)
        {
            if (!truth.TryGetValue(Math.Round(time, 3), out double diff)) continue;
            if (repeated && diff >= 0.3) { wrongDrop++; Console.WriteLine($"  descartou imagem nova em {time:0.000}s (dif {diff:0.00})"); }
            if (!repeated && diff < 0.3) missed++;
        }
        Console.WriteLine($"{trace.Count} quadros: {trace.Count(t => t.repeated)} descartados; imagens novas descartadas por engano: {wrongDrop}; cópias mantidas: {missed}");
        PlayerNative.PostMessage(player.Hwnd, PlayerNative.WM_KEYDOWN, 0x20, IntPtr.Zero); // espaço: pausa (a fila enche)
        Pump(4);
        var times = player.TestQueueTimes();
        Console.WriteLine($"fila: {times.Length} quadros, de {(times.Length > 0 ? times[0] : -1):0.000}s");
        for (int i = 1; i < times.Length; i++) Console.WriteLine($"{times[i]:0.000}s  +{(times[i] - times[i - 1]) * 1000:0} ms");
        player.Dispose();
        return 0;
    }

    private static Dictionary<double, double> Truth(string path, double start, int frames)
    {
        var result = new Dictionary<double, double>();
        using var gpu = new Gpu();
        using var file = new VideoFile(path, gpu.Device);
        file.Seek(start);
        ID3D11Texture2D? staging = null;
        byte[]? previous = null;
        for (int n = 0; n < 5000 && result.Count < frames; n++)
        {
            byte[]? pixels = null;
            if (!file.ReadFrame((texture, index) =>
            {
                var d = texture.Description;
                staging ??= gpu.Device.CreateTexture2D(new Texture2DDescription(d.Format, d.Width, d.Height, 1, 1, BindFlags.None, ResourceUsage.Staging, CpuAccessFlags.Read));
                gpu.Context.CopySubresourceRegion(staging, 0, 0, 0, 0, texture, index, new Vortice.Mathematics.Box(0, 0, 0, file.Width, file.Height, 1));
                var map = gpu.Context.Map(staging, 0, MapMode.Read);
                pixels = new byte[file.Width * file.Height * 4];
                for (int y = 0; y < file.Height; y++)
                    Marshal.Copy(map.DataPointer + (nint)(y * map.RowPitch), pixels, y * file.Width * 4, file.Width * 4);
                gpu.Context.Unmap(staging, 0);
            }, out double time)) break;
            if (time < start - 0.01) continue;
            if (previous is not null)
            {
                long sum = 0;
                for (int i = 0; i < pixels!.Length; i += 4 * 7) sum += Math.Abs(pixels[i + 1] - previous[i + 1]);
                result[Math.Round(time, 3)] = sum / (double)(pixels.Length / (4 * 7));
            }
            previous = pixels;
        }
        staging?.Dispose();
        return result;
    }

    public static int Run(string path, double start)
    {
        using var gpu = new Gpu();
        using var file = new VideoFile(path, gpu.Device);
        if (start > 0) file.Seek(start);
        ID3D11Texture2D? staging = null;
        byte[]? previous = null;
        double previousTime = 0;
        int repeats = 0, count = 0;
        var line = new System.Text.StringBuilder();
        for (int n = 0; n < 3000 && count < 150; n++)
        {
            byte[]? pixels = null;
            int w = 0, h = 0;
            if (!file.ReadFrame((texture, index) =>
            {
                var d = texture.Description;
                (w, h) = ((int)d.Width, (int)d.Height);
                if (staging is null)
                    staging = gpu.Device.CreateTexture2D(new Texture2DDescription(d.Format, d.Width, d.Height, 1, 1,
                        BindFlags.None, ResourceUsage.Staging, CpuAccessFlags.Read));
                gpu.Context.CopySubresourceRegion(staging, 0, 0, 0, 0, texture, index, new Vortice.Mathematics.Box(0, 0, 0, file.Width, file.Height, 1));
                var map = gpu.Context.Map(staging, 0, MapMode.Read);
                pixels = new byte[file.Width * file.Height * 4];
                for (int y = 0; y < file.Height; y++)
                    Marshal.Copy(map.DataPointer + (nint)(y * map.RowPitch), pixels, y * file.Width * 4, file.Width * 4);
                gpu.Context.Unmap(staging, 0);
            }, out double time)) break;
            if (time < start - 0.01) continue; // o Windows volta ao quadro-chave anterior
            if (previous is not null)
            {
                long sum = 0;
                for (int i = 0; i < pixels!.Length; i += 4 * 7) sum += Math.Abs(pixels[i + 1] - previous[i + 1]);
                double diff = sum / (double)(pixels.Length / (4 * 7));
                if (diff < 0.3) repeats++;
                count++;
                line.Append($"{time:0.000}s +{(time - previousTime) * 1000:0}ms dif {diff:0.00}{(diff < 0.3 ? " REPETIDO" : "")}\n");
            }
            previous = pixels;
            previousTime = time;
        }
        Console.Write(line);
        Console.WriteLine($"{repeats} de {count} quadros repetidos");
        staging?.Dispose();
        return 0;
    }
}
