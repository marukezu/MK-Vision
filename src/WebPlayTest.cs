namespace MKVision.Desktop;

// --webplaytest <página>: descobre o vídeo da página com o yt-dlp (sem login), toca no player direto da internet
// e confere: a imagem anda, o som abriu, pular para a metade funciona.
internal static class WebPlayTest
{
    public static int Run(string page)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        const string agent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/155.0.0.0 Safari/537.36 Edg/155.0.0.0";
        var clock = System.Diagnostics.Stopwatch.StartNew();
        WebMedia media;
        try { media = WebResolver.ResolveWorking(page, "teste", agent, null, CancellationToken.None).GetAwaiter().GetResult(); }
        catch (Exception e) { Console.WriteLine($"FALHOU yt-dlp: {e.Message}"); return 1; }
        Console.WriteLine($"yt-dlp em {clock.ElapsedMilliseconds} ms: \"{media.Title}\" {media.Extension}, HLS {media.Hls}, baixando {media.Download}, som separado {media.AudioUrl is not null}, {media.Duration:0} s");

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
        int failures = 0;
        void Check(string what, bool ok, string detail)
        {
            Console.WriteLine($"{(ok ? "OK    " : "FALHOU")} {what}: {detail}");
            if (!ok) failures++;
        }
        clock.Restart();
        player.OpenWeb(media, agent, null);
        while (player.State.fileTime <= 0 && clock.Elapsed.TotalSeconds < 20) Pump(0.1);
        Check("começou a tocar", player.State.fileTime > 0, $"em {clock.ElapsedMilliseconds} ms");
        double t0 = player.State.fileTime;
        Pump(3);
        Check("imagem anda", player.State.fileTime > t0 + 2, $"{t0:0.00} s → {player.State.fileTime:0.00} s");
        Check("som", player.HasAudioForTest, player.HasAudioForTest ? "abriu" : "sem som");
        player.TestSeek(media.Duration / 2);
        clock.Restart();
        while (Math.Abs(player.State.fileTime - media.Duration / 2) > 2 && clock.Elapsed.TotalSeconds < 15) Pump(0.1);
        Check("pular para a metade", Math.Abs(player.State.fileTime - media.Duration / 2) <= 2, $"{player.State.fileTime:0.0} s em {clock.ElapsedMilliseconds} ms");
        Console.WriteLine(player.TestTitle);
        player.Dispose();
        Console.WriteLine(failures == 0 ? "Tudo certo." : $"Falhou: {failures}");
        return failures == 0 ? 0 : 1;
    }
}
