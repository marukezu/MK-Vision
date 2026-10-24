namespace MKVision.Desktop;

// --audiourltest <endereço> <nome> [stream]: o som de um endereço abre no tocador do Windows?
internal static class AudioUrlTest
{
    public static int Run(string url, string name, bool stream)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        using var audio = new PlayerAudio();
        if (File.Exists(url))
        {
            // Arquivo local servido pelo servidor do app (como o som de um HLS que está baixando).
            long.TryParse(Environment.GetEnvironmentVariable("MKV_GROW"), out long grow);
            var never = new TaskCompletionSource().Task;
            audio.Open(LocalMediaServer.Register(() => grow > 0 ? new GrowingFileStream(url, never, grow, CancellationToken.None) : File.OpenRead(url), "video/mp2t", ".ts"));
        }
        else if (stream)
        {
            var source = new HttpRangeSource(url);
            audio.Open(LocalMediaServer.Register(source.OpenReader, name.EndsWith(".webm") ? "audio/webm" : "audio/mp4", Path.GetExtension(name)));
        }
        else audio.Open(url);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (!audio.HasAudio && clock.Elapsed.TotalSeconds < 10)
        {
            while (Native.PeekMessage(out var msg, IntPtr.Zero, 0, 0, 1)) { Native.TranslateMessage(ref msg); Native.DispatchMessage(ref msg); }
            Thread.Sleep(20);
        }
        Console.WriteLine($"{name} ({(stream ? "fluxo" : "endereço")}): {(audio.HasAudio ? $"som em {clock.ElapsedMilliseconds} ms" : "sem som")}");
        return audio.HasAudio ? 0 : 1;
    }
}
