using System.Text;
using System.Text.Json;

namespace MKVision.Desktop;

// Vídeo de um site, pronto para o player: o endereço da imagem (e do som, se vier separado, como no YouTube),
// com os cabeçalhos que o servidor pede. Hls = o site entrega em pedaços de playlist (.m3u8): o yt-dlp baixa e o
// player lê o arquivo enquanto ele cresce. Download = os endereços não deixam o app ler direto (403): o yt-dlp
// baixa a imagem e o som (cada um no seu arquivo) e o player lê enquanto crescem.
// ToolAgent/ToolCookies: como o yt-dlp foi chamado (o download repete igual).
internal sealed record WebMedia(
    string Title,
    string Page,
    string VideoUrl,
    IReadOnlyDictionary<string, string> VideoHeaders,
    string? AudioUrl,
    IReadOnlyDictionary<string, string>? AudioHeaders,
    bool Hls,
    string? FormatId,
    double Duration,
    long SizeHint,
    string Extension,
    string? AudioFormatId = null,
    long AudioSize = 0,
    bool Download = false,
    string? ToolAgent = null,
    string? ToolCookies = null,
    int Height = 0,
    int Item = 0,  // item do post com vários (carrossel do Instagram); 0 = o post tem um só
    IReadOnlyList<WebPostItem>? Post = null); // os itens desse post, na ordem (Page Up/Down no player)

// Um item de um post com vários: vídeo (pronto para o player) ou foto (endereço da imagem).
internal sealed record WebPostItem(int Index, WebMedia? Video, string? Image);

// O vídeo como está na página (duração e tamanho), para achar o item certo num post com vários. 0 = não se sabe.
internal readonly record struct MediaHint(double Duration, int Width, int Height);

internal static class WebResolver
{
    // Preferência: arquivos que o leitor do Windows toca direto pela internet, em HD. No YouTube, WebM (VP9) com
    // o som em WebM — o MP4 de lá é "DASH" e o Windows não entrega os quadros; quando o YouTube não entrega esses
    // (hoje ele força o "SABR" no modo do site), o HLS do modo Safari em até 1080p (o yt-dlp baixa e o player lê
    // enquanto baixa) vem antes do MP4 de 360p. Fora dele, o melhor MP4 inteiro em HD; depois HLS; depois o resto.
    private const string Formats =
        "bv*[ext=webm][protocol^=http][height<=1440]+ba[ext=webm][protocol^=http]/" +
        "bv*[ext=webm][protocol^=http][height<=1440]+ba[protocol^=http]/" +
        "b[ext=mp4][protocol^=http][height>=720]/" +
        "b[protocol^=m3u8][height<=1440]/" +
        "b[ext=mp4][protocol^=http]/b[protocol^=http]/b";

    // Pergunta ao yt-dlp (sem baixar) o que tocar na página. "cookies" = arquivo no formato Netscape com o login
    // do navegador do app.
    public static async Task<WebMedia> Resolve(VideoDownload tools, string page, string title, string? userAgent, string? cookies, CancellationToken token, MediaHint hint = default)
    {
        if (HasItems(page)) return FromPost(await tools.DumpJsonItems(page, Formats, userAgent, cookies, token), page, title, userAgent, cookies, hint);
        using var doc = JsonDocument.Parse(await tools.DumpJson(page, Formats, userAgent, cookies, token));
        return FromJson(doc.RootElement, page, title, userAgent, cookies)
            ?? throw new InvalidOperationException("o yt-dlp não achou o endereço do vídeo");
    }

    // Post com vários itens: todos, na ordem; o vídeo aberto é o que bate com o da tela (Choose).
    internal static WebMedia FromPost(IReadOnlyList<string> lines, string page, string title, string? userAgent, string? cookies, MediaHint hint)
    {
        var post = new List<WebPostItem>();
        var videos = new List<string>();
        foreach (var line in lines)
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            int index = root.TryGetProperty("playlist_index", out var i) && i.ValueKind == JsonValueKind.Number ? i.GetInt32() : post.Count + 1;
            if (FromJson(root, page, title, userAgent, cookies) is { } video)
            {
                post.Add(new WebPostItem(index, video with { Item = lines.Count > 1 ? index : 0 }, null));
                videos.Add(line);
            }
            else if (BestImage(root) is { } image) post.Add(new WebPostItem(index, null, image));
        }
        if (videos.Count == 0) throw new InvalidOperationException("não há vídeo neste post (só fotos)");
        var (_, item) = Choose(videos, hint);
        var chosen = post.First(p => p.Video is not null && (item == 0 || p.Index == item)).Video!;
        return chosen with { Post = post.Count > 1 ? post : null };
    }

    // A foto de um item sem vídeo: a maior miniatura (no Instagram, a foto em tamanho cheio).
    private static string? BestImage(JsonElement root)
    {
        string? best = null;
        double area = -1;
        if (root.TryGetProperty("thumbnails", out var thumbs) && thumbs.ValueKind == JsonValueKind.Array)
            foreach (var t in thumbs.EnumerateArray())
            {
                if (!t.TryGetProperty("url", out var u) || u.GetString() is not { } url || !url.StartsWith("http")) continue;
                double w = t.TryGetProperty("width", out var tw) && tw.ValueKind == JsonValueKind.Number ? tw.GetDouble() : 0;
                double h = t.TryGetProperty("height", out var th) && th.ValueKind == JsonValueKind.Number ? th.GetDouble() : 0;
                if (w * h >= area) (best, area) = (url, w * h); // sem tamanho: vale a última (o yt-dlp põe a maior no fim)
            }
        if (best is null && root.TryGetProperty("thumbnail", out var one) && one.GetString() is { } single && single.StartsWith("http")) best = single;
        return best;
    }

    // As informações de um vídeo (uma linha do yt-dlp). null = o item não tem vídeo (foto).
    private static WebMedia? FromJson(JsonElement root, string page, string title, string? userAgent, string? cookies)
    {
        string name = root.TryGetProperty("title", out var t) && t.GetString() is { Length: > 0 } s ? s : title;
        double duration = root.TryGetProperty("duration", out var d) && d.ValueKind == JsonValueKind.Number ? d.GetDouble() : 0;
        JsonElement video = root, audio = default;
        bool separate = false;
        if (root.TryGetProperty("requested_formats", out var formats) && formats.ValueKind == JsonValueKind.Array && formats.GetArrayLength() >= 1)
        {
            video = formats[0];
            if (formats.GetArrayLength() > 1) { audio = formats[1]; separate = true; }
        }
        if (!video.TryGetProperty("url", out var address) || address.GetString() is not { Length: > 0 }) return null;
        string protocol = video.TryGetProperty("protocol", out var p) ? p.GetString() ?? "" : "";
        long Size(JsonElement f) => f.ValueKind != JsonValueKind.Object ? 0
            : f.TryGetProperty("filesize", out var fs) && fs.ValueKind == JsonValueKind.Number ? fs.GetInt64()
            : f.TryGetProperty("filesize_approx", out var fa) && fa.ValueKind == JsonValueKind.Number ? fa.GetInt64()
            : f.TryGetProperty("tbr", out var tbr) && tbr.ValueKind == JsonValueKind.Number ? (long)(tbr.GetDouble() * 1000 / 8 * duration) : 0;
        long size = Size(video);
        return new WebMedia(
            name, page,
            video.GetProperty("url").GetString()!, Headers(video, page),
            separate ? audio.GetProperty("url").GetString() : null, separate ? Headers(audio, page) : null,
            protocol.StartsWith("m3u8"),
            video.TryGetProperty("format_id", out var id) ? id.GetString() : null,
            duration, size,
            video.TryGetProperty("ext", out var ext) ? "." + ext.GetString() : ".mp4",
            separate && audio.TryGetProperty("format_id", out var aid) ? aid.GetString() : null,
            separate ? Size(audio) : 0,
            false, userAgent, cookies,
            video.TryGetProperty("height", out var hh) && hh.ValueKind == JsonValueKind.Number ? hh.GetInt32() : 0);
    }

    // Sites cujo post pode ter vários itens (carrossel: fotos e vídeos misturados). O "." pede o post inteiro e fica com
    // o vídeo que está na tela — pedir só o primeiro item dava "No video formats found" quando ele era uma foto.
    private static bool HasItems(string page) =>
        Uri.TryCreate(page, UriKind.Absolute, out var u) && System.Text.RegularExpressions.Regex.IsMatch(u.Host, @"(^|\.)instagram\.com$");

    // O item cuja duração (e formato da imagem) mais se parece com o vídeo da tela; sem essa informação, o primeiro vídeo.
    // Devolve também o número do item (0 se o post tem um só).
    internal static (string json, int item) Choose(IReadOnlyList<string> items, MediaHint hint)
    {
        if (items.Count == 1) return (items[0], 0);
        string best = items[0];
        double bestScore = double.MaxValue;
        foreach (var json in items)
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            double Num(string name) => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0;
            double duration = Num("duration"), width = Num("width"), height = Num("height");
            double score = 0;
            if (hint.Duration > 0 && duration > 0) score += Math.Abs(duration - hint.Duration);
            else if (hint.Duration > 0) score += 30; // sem duração: só se nenhum outro servir
            if (hint.Width > 0 && hint.Height > 0 && width > 0 && height > 0)
                score += Math.Abs(width / height - (double)hint.Width / hint.Height) * 10; // vertical × quadrado × deitado
            if (score < bestScore) (best, bestScore) = (json, score);
        }
        using var chosen = JsonDocument.Parse(best);
        int index = chosen.RootElement.TryGetProperty("playlist_index", out var i) && i.ValueKind == JsonValueKind.Number ? i.GetInt32() : 0;
        return (best, index);
    }

    // Pergunta ao yt-dlp de um jeito e, se o endereço não responder (403) ou o yt-dlp falhar, do outro: no YouTube
    // primeiro sem o login do navegador (o login só é preciso em vídeo com restrição de idade); nos outros sites,
    // primeiro com ele. Confere o endereço da imagem (e do som) antes de entregar.
    public static async Task<WebMedia> ResolveWorking(string page, string title, string userAgent, string? cookies, CancellationToken token, MediaHint hint = default)
    {
        // 1) com os cookies e a identificação do navegador; 2) com os cookies, deixando o yt-dlp escolher a
        //    identificação (a que combina com o modo dele); 3) sem login. Fica com a primeira que responder em
        //    720p ou mais; senão, com a melhor que respondeu. Se nenhum endereço responder (403), o yt-dlp baixa ele
        //    mesmo (a melhor que achou o vídeo) e o player toca enquanto baixa.
        var attempts = new (string? agent, string? cookies, string name)[]
        {
            (userAgent, cookies, "com o login e a identificação do navegador"),
            (null, cookies, "com o login do navegador"),
            (null, null, "sem login")
        };
        Exception? last = null;
        WebMedia? bestFound = null, bestWorking = null;
        bool force = Environment.GetEnvironmentVariable("MKV_FORCE_DOWNLOAD") == "1"; // autoteste do último recurso
        bool updated = false;
        for (int a = 0; a < attempts.Length; a++)
        {
            var (agent, cookieFile, name) = attempts[a];
            if (cookieFile is null && name != "sem login") continue;
            try
            {
                var media = await Resolve(new VideoDownload(), page, title, agent, cookieFile, token, hint);
                if (bestFound is null || media.Height > bestFound.Height) bestFound = media;
                if (force) continue;
                if (!media.Hls)
                {
                    new HttpRangeSource(media.VideoUrl, media.VideoHeaders).Dispose(); // responde?
                    if (media.AudioUrl is not null) new HttpRangeSource(media.AudioUrl, media.AudioHeaders).Dispose();
                }
                Log.Write($"Navegador: endereço do vídeo pelo yt-dlp ({name}, {(media.Height > 0 ? media.Height + "p" : "qualidade ?")}).");
                if (media.Height == 0 || media.Height >= 720) return media;
                if (bestWorking is null || media.Height > bestWorking.Height) bestWorking = media;
            }
            catch (Exception error)
            {
                Log.Write($"Navegador: yt-dlp {name} não deu ({error.Message}).");
                last = error;
                // "Confirme que está na última versão": atualiza agora e tenta esta mesma forma de novo (uma vez).
                if (!updated && VideoDownload.LooksOutdated(error.Message))
                {
                    updated = true;
                    if (await new VideoDownload().UpdateNow(token)) a--;
                }
            }
        }
        if (bestFound is not null && (bestWorking is null || bestFound.Height >= 720 && bestWorking.Height < 720))
        {
            Log.Write($"Navegador: o site não deixa ler direto; o yt-dlp baixa ({bestFound.Height}p) e o player toca enquanto baixa.");
            return bestFound with { Download = true };
        }
        if (bestWorking is not null) return bestWorking;
        throw last!;
    }

    // Os cabeçalhos que o yt-dlp diz para usar e, se ele não puser, o Referer da página (como o navegador manda): sem
    // ele, sites como o RedTube respondem 410 ao arquivo do vídeo (e aos pedaços do HLS).
    private static Dictionary<string, string> Headers(JsonElement format, string page)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (format.TryGetProperty("http_headers", out var h) && h.ValueKind == JsonValueKind.Object)
            foreach (var item in h.EnumerateObject())
                if (item.Value.GetString() is { } value) headers[item.Name] = value;
        if (!headers.ContainsKey("Referer") && Uri.TryCreate(page, UriKind.Absolute, out var u) && u.Scheme is "http" or "https") headers["Referer"] = page;
        return headers;
    }

    // Arquivo de cookies (formato Netscape, o que o yt-dlp lê).
    public static string WriteCookies(IEnumerable<(string domain, string path, bool secure, long expires, string name, string value)> cookies)
    {
        var text = new StringBuilder("# Netscape HTTP Cookie File\n");
        foreach (var c in cookies)
            text.Append($"{c.domain}\t{(c.domain.StartsWith('.') ? "TRUE" : "FALSE")}\t{c.path}\t{(c.secure ? "TRUE" : "FALSE")}\t{c.expires}\t{c.name}\t{c.value}\n");
        Directory.CreateDirectory(CookieFolder);
        string file = Path.Combine(CookieFolder, $"cookies-{Guid.NewGuid():N}.txt"); // um por vídeo (o download do HLS usa o mesmo)
        File.WriteAllText(file, text.ToString());
        return file;
    }

    private static readonly string CookieFolder = Path.Combine(Path.GetTempPath(), "MKVision");

    // Privacidade: os arquivos de cookies passados ao yt-dlp não ficam no computador (ao abrir o navegador e ao
    // fechar o app).
    public static void DeleteCookieFiles()
    {
        try
        {
            if (!Directory.Exists(CookieFolder)) return;
            foreach (var file in Directory.GetFiles(CookieFolder, "cookies*.txt"))
                try { File.Delete(file); } catch { } // em uso por um download que ainda termina
        }
        catch { }
    }
}
