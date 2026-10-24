namespace MKVision.Desktop;

// --growtest <arquivo .ts> <tamanho informado> <MB/s>: simula um download (cópia que cresce) e abre como o
// player abre um HLS enquanto baixa; lê 120 quadros e pula para a metade.
internal static class GrowTest
{
    public static int Run(string source, long estimate, double speed)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        string copy = source + ".crescendo.ts";
        var data = File.ReadAllBytes(source);
        var started = new ManualResetEventSlim();
        var download = Task.Run(() =>
        {
            using var file = new FileStream(copy, FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
            var clock = System.Diagnostics.Stopwatch.StartNew();
            int written = 0, chunk = 256 * 1024;
            while (written < data.Length)
            {
                int n = Math.Min(chunk, data.Length - written);
                file.Write(data, written, n);
                file.Flush();
                written += n;
                if (written >= 1 << 20) started.Set();
                double ahead = written / (speed * 1e6) - clock.Elapsed.TotalSeconds;
                if (ahead > 0) Thread.Sleep((int)(ahead * 1000));
            }
            started.Set();
        });
        started.Wait();
        using var gpu = new Gpu();
        var clock = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var growing = new GrowingFileStream(copy, download, estimate, CancellationToken.None);
            using var video = new VideoFile("teste.ts", gpu.Device, growing, "video/mp2t");
            Console.WriteLine($"abriu em {clock.ElapsedMilliseconds} ms: {video.Width}x{video.Height}, duração {video.Duration:0.0} s");
            int frames = 0;
            double last = 0;
            int want = int.TryParse(Environment.GetEnvironmentVariable("MKV_FRAMES"), out int w) ? w : 120;
            for (; frames < want && video.ReadFrame((_, _) => { }, out last); frames++) { }
            Console.WriteLine($"{frames} quadros até {last:0.00} s ({clock.ElapsedMilliseconds} ms)");
            var seek = System.Diagnostics.Stopwatch.StartNew();
            video.Seek(200);
            video.ReadFrame((_, _) => { }, out double after);
            Console.WriteLine($"pulou para 200 s: primeiro quadro em {after:0.00} s ({seek.ElapsedMilliseconds} ms)");
        }
        catch (Exception e) { Console.WriteLine($"não abriu ({clock.ElapsedMilliseconds} ms): {e.Message.Split('\n')[0]}"); }
        download.Wait();
        File.Delete(copy);
        return 0;
    }
}
