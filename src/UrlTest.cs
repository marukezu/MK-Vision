namespace MKVision.Desktop;

// --urltest <endereço>: o leitor de vídeo do Windows (Media Foundation) abre direto da internet? Lê ~60 quadros,
// pula para a metade e lê mais alguns (o pulo pela rede precisa funcionar).
internal static class UrlTest
{
    public static int Run(string url, bool stream = false)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        using var gpu = new Gpu();
        var clock = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            HttpRangeSource? source = stream && !File.Exists(url) ? new HttpRangeSource(url) : null;
            using var file = source is not null ? new VideoFile(url, gpu.Device, source.OpenReader())
                : stream && long.TryParse(Environment.GetEnvironmentVariable("MKV_GROW"), out long grow)
                    ? new VideoFile(url, gpu.Device, new GrowingFileStream(url, new TaskCompletionSource().Task, grow, CancellationToken.None), Environment.GetEnvironmentVariable("MKV_TYPE"))
                : stream ? new VideoFile(url, gpu.Device, File.OpenRead(url), Environment.GetEnvironmentVariable("MKV_TYPE"))
                : new VideoFile(url, gpu.Device);
            source?.Dispose();
            Console.WriteLine($"abriu em {clock.ElapsedMilliseconds} ms: {file.Width}x{file.Height}, {1 / file.FrameDuration:0.##} q/s, duração {file.Duration:0.0} s");
            double last = 0;
            int n = 0;
            int wanted = int.TryParse(Environment.GetEnvironmentVariable("MKV_FRAMES"), out int w) ? w : 60;
            for (; n < wanted && file.ReadFrame((_, _) => { }, out last); n++) { }
            Console.WriteLine($"{n} quadros até {last:0.00} s ({clock.ElapsedMilliseconds} ms)");
            if (file.Duration > 10)
            {
                var seek = System.Diagnostics.Stopwatch.StartNew();
                file.Seek(file.Duration / 2);
                file.ReadFrame((_, _) => { }, out double after);
                Console.WriteLine($"pulou para a metade: primeiro quadro em {after:0.00} s ({seek.ElapsedMilliseconds} ms)");
            }
            return 0;
        }
        catch (Exception error)
        {
            Console.WriteLine($"não abriu ({clock.ElapsedMilliseconds} ms): {error.Message}");
            return 1;
        }
    }
}
