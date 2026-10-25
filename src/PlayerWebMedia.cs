using System.Security.Cryptography;
using System.Text;

namespace MKVision.Desktop;

// Vídeos e imagens vindos do navegador do app. O vídeo é lido direto da internet (HttpRangeSource: começa a
// tocar na hora e pula para qualquer ponto) ou, nos sites em playlist (HLS), do arquivo que o yt-dlp vai
// baixando. As imagens são baixadas para uma pasta temporária e abertas como fotos; as outras imagens grandes da
// página viram a "pasta" (Page Up/Down).
internal sealed partial class Player
{
    private readonly Dictionary<string, WebMedia> _webMedia = []; // "web:N" → vídeo pedido pelo navegador
    private int _webMediaCount;
    private string _webUserAgent = "";
    private string? _webCookies;
    private CancellationTokenSource? _webDownload;  // HLS em andamento (um por vez)
    private IReadOnlyDictionary<string, string> _webImageHeaders = new Dictionary<string, string>();
    private HttpRangeSource? _webAudioSource;        // fonte do som do vídeo atual (fica aberta enquanto ele toca)
    private string? _webAudioUrl;                    // endereço dele no servidor local

    private bool _fromWeb; // o que está aberto veio do navegador (ESC em janela volta para ele)

    // Vídeo lido enquanto baixa (HLS / download pelo yt-dlp): o leitor do Windows só lê até o tamanho que o arquivo
    // tinha ao abrir; chegando lá (ou pulando além), o player reabre o arquivo, já maior, no mesmo ponto.
    private Func<VideoFile>? _webReopen;
    private Task? _webGrowTask;
    private string? _webGrowPath;
    private long _webOpenedLength, _webGrowTotal;
    private double _lastDecodedTime;
    private volatile bool _webAudioGrows;  // som lido do arquivo que cresce (reabrir perto do fim)
    private double _webAudioReopenedAt;

    // Thread da decodificação: reabre o arquivo que cresce, se cresceu desde a última abertura. null = ainda não.
    private VideoFile? ReopenGrowing(VideoFile old)
    {
        if (_webReopen is null || _webGrowPath is null || !File.Exists(_webGrowPath)) return null;
        long size = new FileInfo(_webGrowPath).Length;
        bool done = _webGrowTask?.IsCompleted == true;
        if (size <= _webOpenedLength + (done ? 0 : 1 << 20)) return null; // nada novo (ainda)
        VideoFile fresh;
        try { fresh = _webReopen(); }
        catch (Exception e)
        {
            Log.Write($"Player: não consegui reabrir o vídeo que está baixando — {e.Message}");
            return null;
        }
        _webOpenedLength = size;
        Log.Write($"Player: vídeo baixando reaberto com {size / 1048576.0:0} MB{(done ? " (download terminado)" : "")}.");
        lock (_gpuLock)
        lock (_lock)
        {
            if (_file != old)
            {
                fresh.Dispose();
                return null;
            }
            _file = fresh;
        }
        old.Dispose();
        return fresh;
    }

    // Pulou para um ponto que o arquivo aberto ainda não tinha: espera o download chegar lá (pela proporção do
    // tamanho) e reabre. Desiste se outro pulo ou outro vídeo vier antes.
    private VideoFile? ReopenFor(double target, VideoFile old, int generation)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (clock.Elapsed.TotalSeconds < 120 && _webGrowPath is not null)
        {
            lock (_lock) if (_seekGeneration != generation || _pendingOpen is not null) return null;
            long size = File.Exists(_webGrowPath) ? new FileInfo(_webGrowPath).Length : 0;
            double duration;
            lock (_lock) duration = _duration;
            long needed = duration > 0 && _webGrowTotal > 0 ? (long)(_webGrowTotal * Math.Min(1, target / duration)) + (2 << 20) : 0;
            if (_webGrowTask?.IsCompleted == true || size >= needed) break;
            Thread.Sleep(150);
        }
        return ReopenGrowing(old);
    }

    private static readonly string WebCache = Path.Combine(Path.GetTempPath(), "MKVision", "web");

    private static bool IsWeb(string path) => path.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || path.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

    // Qualquer thread: toca o vídeo do site. Post com vários itens (carrossel do Instagram): todos viram a "pasta" —
    // Page Up/Down passa pelas fotos e vídeos dele, na ordem do post, a partir do que estava na tela.
    public void OpenWeb(WebMedia media, string userAgent, string? cookies)
    {
        if (!PrepareTab(WebIdentity(media), "web:")) return; // já aberto numa aba: vai para ela (PlayerTabs)
        StartSlowAi();
        lock (_lock)
        {
            // (os vídeos dos posts anteriores ficam: outras abas podem abrir de novo)
            string Key(WebMedia video)
            {
                string key = $"web:{++_webMediaCount}";
                _webMedia[key] = video;
                return key;
            }
            _fromWeb = true;
            (_webUserAgent, _webCookies) = (userAgent, cookies);
            if (media.Post is { Count: > 1 } post)
            {
                // O vídeo escolhido entra como o yt-dlp o entregou (já conferido); os outros, como vieram do post.
                var files = post.Select(item => item.Video is not null ? Key(item.Index == media.Item ? media : item.Video) : item.Image!).ToArray();
                int index = Math.Max(0, post.ToList().FindIndex(item => item.Video is not null && item.Index == media.Item));
                _webImageHeaders = new Dictionary<string, string> { ["User-Agent"] = userAgent, ["Referer"] = media.Page };
                (_folderFiles, _folderIndex) = (files, index);
                _pendingOpen = files[index];
            }
            else
            {
                _pendingOpen = Key(media);
                (_folderFiles, _folderIndex) = ([], -1);
            }
            if (_activeTab is { } tab)
            {
                (tab.Path, tab.Identity, tab.Title, tab.Video) = (_pendingOpen, WebIdentity(media), media.Title, true);
            }
            _listScroll = -1;
        }
        _decodeWake.Set();
    }

    // "web:N": vídeo de um site (guardado em _webMedia).
    private static bool IsWebKey(string path) => path.StartsWith("web:", StringComparison.Ordinal);

    // Qualquer thread: abre a imagem "index" de uma galeria de endereços (Page Up/Down passa pelas outras).
    public void OpenWebGallery(string[] urls, int index, IReadOnlyDictionary<string, string> headers)
    {
        if (urls.Length == 0) return;
        string url = urls[Math.Clamp(index, 0, urls.Length - 1)];
        if (!PrepareTab(IdentityOf(url), url)) return; // já aberta numa aba: vai para ela (PlayerTabs)
        lock (_lock)
        {
            _webImageHeaders = headers;
            _fromWeb = true;
            (_folderFiles, _folderIndex) = (urls, Math.Clamp(index, 0, urls.Length - 1));
            _pendingOpen = urls[_folderIndex];
            _listScroll = -1;
        }
        _decodeWake.Set();
    }

    // Thread da decodificação: abre o vídeo do site. Devolve o leitor da imagem e o endereço (servidor local) do
    // som — o tocador de som do Windows lê por endereço.
    private (VideoFile? file, string error, string? audio) OpenWebVideo(WebMedia media)
    {
        _webDownload?.Cancel();
        _webDownload = null;
        (_webReopen, _webGrowTask, _webGrowPath, _webOpenedLength, _webGrowTotal) = (null, null, null, 0, 0);
        _webAudioGrows = false;
        if (_webAudioUrl is not null) LocalMediaServer.Unregister(_webAudioUrl);
        _webAudioSource?.Dispose();
        (_webAudioSource, _webAudioUrl) = (null, null);
        try
        {
            if (media.Hls || media.Download)
            {
                // O yt-dlp baixa em ordem (HLS: para um .ts); o player começa assim que chega o primeiro 1 MB e lê
                // enquanto o arquivo cresce. Imagem e som separados: um download para cada.
                Directory.CreateDirectory(WebCache);
                string stamp = $"{(media.Hls ? "hls" : "dl")}-{Environment.TickCount64}";
                string ext = media.Hls ? ".ts" : media.Extension;
                string path = Path.Combine(WebCache, stamp + "-v" + ext);
                var cancel = new CancellationTokenSource();
                _webDownload = cancel;
                var tools = new VideoDownload();
                var download = tools.DownloadFormat(media.Page, media.FormatId, path, media.ToolAgent, media.ToolCookies, cancel.Token, media.Item);
                Task? audioDownload = null;
                string? audioPath = null;
                if (!media.Hls && media.AudioFormatId is not null)
                {
                    audioPath = Path.Combine(WebCache, stamp + "-a" + (media.Extension == ".webm" ? ".webm" : ".m4a"));
                    audioDownload = new VideoDownload().DownloadFormat(media.Page, media.AudioFormatId, audioPath, media.ToolAgent, media.ToolCookies, cancel.Token, media.Item);
                }
                long estimate = media.SizeHint;
                _webGrowTotal = estimate;
                if (media.Hls)
                {
                    // HLS do YouTube vem sem relógio (PCR) e o Windows não entrega os quadros: o player lê a cópia
                    // consertada, feita enquanto baixa (arquivos que já têm PCR passam iguais).
                    string fixedPath = Path.Combine(WebCache, stamp + "-v.fixed.ts");
                    download = TsFixer.Run(path, fixedPath, download, cancel.Token);
                    path = fixedPath;
                    estimate = (long)(estimate * 1.12);
                }
                // Começa com ~4 MB (alguns segundos de vídeo): a primeira reabertura demora a chegar.
                var clock = System.Diagnostics.Stopwatch.StartNew();
                while (!download.IsCompleted && (!File.Exists(path) || new FileInfo(path).Length < (4 << 20)) && clock.Elapsed.TotalSeconds < 60)
                    Thread.Sleep(100);
                if (download.IsFaulted) throw download.Exception!.InnerException ?? download.Exception;
                if (!File.Exists(path)) throw new IOException("o download não começou");
                string type = media.Hls ? "video/mp2t" : ContentType(ext) ?? "video/mp4";
                // O leitor da imagem vê o tamanho real (o que já baixou); ao chegar ao fim dele, o player reabre.
                string name = media.Title + ext, videoPath = path;
                var finished = download;
                _webReopen = () => new VideoFile(name, _gpu.Device, new GrowingFileStream(videoPath, finished, 0, cancel.Token), type);
                (_webGrowTask, _webGrowPath) = (download, path);
                _webOpenedLength = new FileInfo(path).Length;
                var video = _webReopen();
                if (audioPath is not null && audioDownload is not null)
                {
                    string aType = audioPath.EndsWith(".webm") ? "audio/webm" : "audio/mp4";
                    _webAudioUrl = LocalMediaServer.Register(() => WaitAndOpen(audioPath, audioDownload, media.AudioSize, cancel.Token), aType, Path.GetExtension(audioPath));
                }
                else
                {
                    // O tocador de som também lê só até o tamanho que viu ao abrir: ele recebe o tamanho real e é
                    // reaberto (SyncAudio) quando chega perto do fim do que conhecia.
                    _webAudioUrl = LocalMediaServer.Register(() => new GrowingFileStream(path, download, 0, cancel.Token), type, ext);
                    _webAudioGrows = true;
                }
                return (video, "", _webAudioUrl);
            }
            var source = new HttpRangeSource(media.VideoUrl, media.VideoHeaders);
            var file = new VideoFile(media.Title + media.Extension, _gpu.Device, source.OpenReader(), ContentType(media.Extension));
            if (media.AudioUrl is null)
            {
                // Imagem e som no mesmo arquivo: o som lê dos mesmos pedaços (baixa uma vez só).
                _webAudioSource = source;
                _webAudioUrl = LocalMediaServer.Register(source.OpenReader, ContentType(media.Extension) ?? "video/mp4", media.Extension);
            }
            else
            {
                source.Dispose(); // o leitor da imagem mantém a fonte aberta
                try
                {
                    _webAudioSource = new HttpRangeSource(media.AudioUrl, media.AudioHeaders);
                    string ext = media.Extension == ".webm" ? ".webm" : ".m4a";
                    _webAudioUrl = LocalMediaServer.Register(_webAudioSource.OpenReader, ext == ".webm" ? "audio/webm" : "audio/mp4", ext);
                }
                catch (Exception e) { Log.Write($"Player: o som do vídeo não abriu — {e.Message}"); }
            }
            return (file, "", _webAudioUrl);
        }
        catch (Exception e)
        {
            Log.Write($"Player: não consegui tocar o vídeo do site — {e.Message}");
            return (null, e.Message, null);
        }
    }

    // O som pode ainda não ter começado a baixar quando o tocador pede.
    private static Stream WaitAndOpen(string path, Task download, long size, CancellationToken token)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (!File.Exists(path) && !download.IsCompleted && clock.Elapsed.TotalSeconds < 30 && !token.IsCancellationRequested) Thread.Sleep(100);
        return new GrowingFileStream(path, download, size, token);
    }

    private static string? ContentType(string extension) => extension.ToLowerInvariant() switch
    {
        ".webm" => "video/webm",
        ".mp4" or ".m4v" => "video/mp4",
        ".ts" => "video/mp2t",
        _ => null
    };

    // Thread da decodificação: baixa a imagem (fica em cache na pasta temporária) e devolve o arquivo.
    private string DownloadImage(string url)
    {
        Directory.CreateDirectory(WebCache);
        string name = Convert.ToHexStringLower(SHA1.HashData(Encoding.UTF8.GetBytes(url)))[..16];
        string path = Path.Combine(WebCache, "img-" + name);
        if (File.Exists(path)) return path;
        IReadOnlyDictionary<string, string> headers;
        lock (_lock) headers = _webImageHeaders;
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        foreach (var (key, value) in headers) request.Headers.TryAddWithoutValidation(key, value);
        using var response = http.Send(request);
        response.EnsureSuccessStatusCode();
        using (var file = File.Create(path + ".part"))
            response.Content.ReadAsStream().CopyTo(file);
        File.Move(path + ".part", path, overwrite: true);
        return path;
    }

    // Nome para mostrar de um endereço de imagem ("foto.jpg").
    private static string WebName(string url)
    {
        try
        {
            string name = Uri.UnescapeDataString(Path.GetFileName(new Uri(url).AbsolutePath));
            return name.Length > 0 ? name : new Uri(url).Host;
        }
        catch { return url; }
    }
}
