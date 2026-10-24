namespace MKVision.Desktop;

// Navegador do app: o yt-dlp descobre o endereço do vídeo de uma página (sem baixar) e, nos sites que entregam
// em playlist (HLS), baixa para um arquivo que o player lê enquanto cresce. Sempre com o Referer da página, como o
// navegador: há sites (RedTube) que sem ele respondem 410 ao vídeo.
internal sealed partial class VideoDownload
{
    // userAgent/cookies null = o yt-dlp escolhe (no YouTube, sem login, ele usa o modo do app de celular, cujos
    // endereços tocam direto; com login, o modo do site, que hoje pede uma chave extra e dá 403).
    public async Task<string> DumpJson(string page, string formats, string? userAgent, string? cookies, CancellationToken token)
    {
        await EnsureTools((_, _) => { }, token);
        var args = new List<string>
        {
            "--js-runtimes", $"deno:{Deno}", "--referer", page,
            "--no-playlist", "--encoding", "utf-8", "-j", "-f", formats
        };
        if (userAgent is not null) args.AddRange(["--user-agent", userAgent]);
        if (cookies is not null) args.AddRange(["--cookies", cookies]);
        args.AddRange(YouTubeClients(page));
        args.AddRange(["--", page]);
        string? error = null;
        var (code, output) = await RunTool(YtDlp, [.. args], line => { if (line.StartsWith("ERROR:")) error = line[6..].Trim(); }, token);
        if (code != 0) throw new InvalidOperationException(error ?? $"o yt-dlp terminou com código {code}");
        // A saída tem avisos e uma linha JSON.
        string? json = output.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.StartsWith('{'));
        return json ?? throw new InvalidOperationException("o yt-dlp não devolveu as informações do vídeo");
    }

    // Post com vários itens (carrossel do Instagram): uma linha JSON por item, na ordem do post, com o número dele
    // (playlist_index). As fotos vêm também (sem formato de vídeo, com o endereço da imagem nas miniaturas).
    // Itens que dão erro são pulados (--ignore-errors).
    public async Task<List<string>> DumpJsonItems(string page, string formats, string? userAgent, string? cookies, CancellationToken token)
    {
        await EnsureTools((_, _) => { }, token);
        var args = new List<string>
        {
            "--js-runtimes", $"deno:{Deno}", "--referer", page,
            "--yes-playlist", "--ignore-errors", "--ignore-no-formats-error", "--encoding", "utf-8", "-j", "-f", formats
        };
        if (userAgent is not null) args.AddRange(["--user-agent", userAgent]);
        if (cookies is not null) args.AddRange(["--cookies", cookies]);
        args.AddRange(["--", page]);
        string? error = null;
        var (_, output) = await RunTool(YtDlp, [.. args], line => { if (line.StartsWith("ERROR:")) error ??= line[6..].Trim(); }, token);
        var items = output.Split('\n').Select(l => l.Trim()).Where(l => l.StartsWith('{')).ToList();
        return items.Count > 0 ? items : throw new InvalidOperationException(error ?? "o yt-dlp não devolveu as informações do vídeo");
    }

    // YouTube: além dos modos padrão do yt-dlp, o do Safari (HLS em até 1080p, sem a chave que o modo do site
    // pede). O download usa os mesmos modos, para o número do formato existir.
    private static string[] YouTubeClients(string page) =>
        Uri.TryCreate(page, UriKind.Absolute, out var u) && System.Text.RegularExpressions.Regex.IsMatch(u.Host, @"(^|\.)(youtube\.com|youtu\.be)$")
            ? ["--extractor-args", "youtube:player_client=default,web_safari"] : [];

    // Baixa um formato da página para "path" (HLS em MPEG-TS, que o Windows lê enquanto o arquivo cresce).
    // "item" > 0: o item do post com vários (carrossel) — senão o yt-dlp pegaria o primeiro.
    // Termina quando o download termina (ou é cancelado).
    public async Task DownloadFormat(string page, string? formatId, string path, string? userAgent, string? cookies, CancellationToken token, int item = 0)
    {
        await EnsureTools((_, _) => { }, token);
        List<string> args =
        [
            "--js-runtimes", $"deno:{Deno}", "--referer", page,
            .. item > 0 ? ["--yes-playlist", "--playlist-items", item.ToString()] : new[] { "--no-playlist" },
            "--encoding", "utf-8", "--no-part", "--hls-use-mpegts", "--no-mtime",
            "--concurrent-fragments", "4", "-o", path
        ];
        if (userAgent is not null) args.AddRange(["--user-agent", userAgent]);
        if (formatId is not null) args.AddRange(["-f", formatId]);
        if (cookies is not null) args.AddRange(["--cookies", cookies]);
        args.AddRange(YouTubeClients(page));
        args.AddRange(["--", page]);
        string? error = null;
        var (code, _) = await RunTool(YtDlp, [.. args], line => { if (line.StartsWith("ERROR:")) error = line[6..].Trim(); }, token);
        token.ThrowIfCancellationRequested();
        if (code != 0) throw new InvalidOperationException(error ?? $"o yt-dlp terminou com código {code}");
    }
}
