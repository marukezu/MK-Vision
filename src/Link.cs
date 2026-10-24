using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace MKVision.Desktop;

// O que a extensão informa sobre o vídeo principal da aba em foco (pixels CSS do viewport).
internal sealed record Geometry(
    double Dpr, double InnerWidth, double InnerHeight, bool Fullscreen,
    double X, double Y, double Width, double Height,   // onde o navegador mostra a imagem (captura)
    double OutX, double OutY, double OutWidth, double OutHeight, // onde o resultado aparece (lupa pode passar das bordas)
    int VideoWidth, int VideoHeight,                    // resolução real
    double UvX, double UvY, double UvWidth, double UvHeight, // parte do vídeo a mostrar (lupa / object-fit)
    bool Slow, double Speed,                            // câmera lenta suave ligada e velocidade do vídeo
    DateTime Received);

// Conexão local com a extensão MK Vision (WebSocket em 127.0.0.1).
// Só aceita origem chrome-extension:// — páginas da web não conseguem falsificar esse cabeçalho.
internal sealed class Link : IDisposable
{
    public const int Port = 47613;

    private sealed class Client(WebSocket socket)
    {
        public WebSocket Socket { get; } = socket;
        public string Browser = "";
        public Geometry? Geometry;
        public bool Watching;
        public bool Capturing;
        public bool ReportedEmpty;
        public readonly SemaphoreSlim Sending = new(1, 1); // o WebSocket não aceita dois envios ao mesmo tempo
    }

    private static async Task Send(Client client, object message, CancellationToken token = default)
    {
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(message));
        await client.Sending.WaitAsync(token);
        try { await client.Socket.SendAsync(bytes, WebSocketMessageType.Text, true, token); }
        catch { }
        finally { client.Sending.Release(); }
    }

    private readonly VideoDownload _download = new();
    private readonly HttpListener _listener = new();
    private readonly List<Client> _clients = [];
    private readonly Settings _settings;
    private readonly CancellationTokenSource _stop = new();

    public Link(Settings settings)
    {
        _settings = settings;
        _listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
        try
        {
            _listener.Start();
            _ = AcceptLoop();
            _ = KeepAliveLoop();
        }
        catch (Exception error)
        {
            Log.Write($"Ligação com a extensão indisponível (porta {Port}): {error.Message}");
        }
    }

    public int ConnectedCount { get { lock (_clients) return _clients.Count; } }

    private async Task AcceptLoop()
    {
        while (!_stop.IsCancellationRequested)
        {
            HttpListenerContext context;
            try { context = await _listener.GetContextAsync(); }
            catch { return; }

            string origin = context.Request.Headers["Origin"] ?? "";
            if (!context.Request.IsWebSocketRequest || !origin.StartsWith("chrome-extension://"))
            {
                context.Response.StatusCode = 403;
                context.Response.Close();
                continue;
            }
            var socket = (await context.AcceptWebSocketAsync(null)).WebSocket;
            var client = new Client(socket);
            lock (_clients) _clients.Add(client);
            _ = ReceiveLoop(client);
        }
    }

    // O Chrome desliga o service worker da extensão após ~30 s sem atividade, e a conexão cai junto.
    // Receber mensagens mantém o worker acordado: manda um "sinal de vida" a cada 10 s.
    private async Task KeepAliveLoop()
    {
        while (!_stop.IsCancellationRequested)
        {
            try { await Task.Delay(10_000, _stop.Token); } catch { return; }
            List<Client> clients;
            lock (_clients) clients = [.. _clients];
            foreach (var client in clients)
            {
                try { await Send(client, new { type = "ping" }, _stop.Token); } catch { }
            }
        }
    }

    private async Task ReceiveLoop(Client client)
    {
        var buffer = new byte[64 * 1024];
        try
        {
            while (client.Socket.State == WebSocketState.Open)
            {
                var result = await client.Socket.ReceiveAsync(buffer, _stop.Token);
                if (result.MessageType == WebSocketMessageType.Close) break;
                int length = result.Count;
                while (!result.EndOfMessage && length < buffer.Length)
                {
                    result = await client.Socket.ReceiveAsync(new ArraySegment<byte>(buffer, length, buffer.Length - length), _stop.Token);
                    length += result.Count;
                }
                Handle(client, Encoding.UTF8.GetString(buffer, 0, length));
            }
        }
        catch { }
        lock (_clients) _clients.Remove(client);
        client.Socket.Dispose(); // fecha a conexão do lado do app (senão ela fica pendurada)
        Log.Write($"Extensão desconectada ({client.Browser}).");
    }

    private void Handle(Client client, string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        switch (root.GetProperty("type").GetString())
        {
            case "hello":
                client.Browser = root.GetProperty("browser").GetString() ?? "";
                Log.Write($"Extensão conectada ({client.Browser}).");
                break;

            case "settings":
                lock (_settings)
                {
                    _settings.Detail = root.GetProperty("detail").GetSingle();
                    _settings.DetailRadius = root.GetProperty("detailRadius").GetSingle();
                    _settings.Texture = root.GetProperty("texture").GetSingle();
                    _settings.TextureRadius = root.GetProperty("textureRadius").GetSingle();
                    _settings.Threshold = root.GetProperty("threshold").GetSingle();
                    _settings.Power = root.GetProperty("power").GetInt32();
                }
                break;

            case "download":
                _ = DownloadFor(client, root.TryGetProperty("url", out var url) ? url.GetString() ?? "" : "",
                    root.TryGetProperty("open", out var openAfter) && openAfter.ValueKind == JsonValueKind.True);
                break;

            case "open": // tecla "." na extensão: o vídeo baixado abre no player
                if (root.TryGetProperty("path", out var path) && path.GetString() is { } file && IsVideoFile(file))
                    OpenRequested?.Invoke(file);
                break;

            case "download-cancel":
                _download.Cancel();
                break;

            case "geometry":
                if (!root.TryGetProperty("video", out var video) || video.ValueKind != JsonValueKind.Object)
                {
                    if (client.Geometry is not null || !client.ReportedEmpty) Log.Write($"Extensão ({client.Browser}): aba sem vídeo");
                    client.ReportedEmpty = true;
                    client.Geometry = null;
                    break;
                }
                if (client.Geometry is null) Log.Write($"Extensão ({client.Browser}): vídeo {video.GetProperty("vw").GetInt32()}x{video.GetProperty("vh").GetInt32()} informado");
                client.ReportedEmpty = false;
                client.Geometry = new Geometry(
                    root.GetProperty("dpr").GetDouble(), root.GetProperty("innerWidth").GetDouble(),
                    root.GetProperty("innerHeight").GetDouble(), root.GetProperty("fullscreen").GetBoolean(),
                    video.GetProperty("x").GetDouble(), video.GetProperty("y").GetDouble(),
                    video.GetProperty("w").GetDouble(), video.GetProperty("h").GetDouble(),
                    Optional(video, "ox", "x"), Optional(video, "oy", "y"), Optional(video, "ow", "w"), Optional(video, "oh", "h"),
                    video.GetProperty("vw").GetInt32(), video.GetProperty("vh").GetInt32(),
                    video.GetProperty("ux").GetDouble(), video.GetProperty("uy").GetDouble(),
                    video.GetProperty("uw").GetDouble(), video.GetProperty("uh").GetDouble(),
                    root.TryGetProperty("slow", out var slow) && slow.ValueKind == JsonValueKind.True,
                    root.TryGetProperty("speed", out var speed) && speed.ValueKind == JsonValueKind.Number ? speed.GetDouble() : 1,
                    DateTime.UtcNow);
                break;
        }
    }

    // Vídeo do YouTube pedido pela extensão (ela não consegue baixar sozinha): yt-dlp, com o progresso de volta.
    // Vídeo para abrir no player (pedido da extensão); chamado fora da thread principal.
    public event Action<string>? OpenRequested;

    private static readonly string[] VideoExtensions = [".mp4", ".m4v", ".mkv", ".webm", ".mov", ".ts", ".avi", ".wmv"];
    private static bool IsVideoFile(string path) =>
        Path.IsPathFullyQualified(path) && File.Exists(path) && VideoExtensions.Contains(Path.GetExtension(path).ToLowerInvariant());

    private async Task DownloadFor(Client client, string url, bool openAfter = false)
    {
        if (_download.Busy)
        {
            await Send(client, new { type = "download-error", message = "já tem um download em andamento no app" });
            return;
        }
        Log.Write($"Download de vídeo: {url}");
        long lastReport = 0;
        try
        {
            string file = await _download.Run(url, (percent, text) =>
            {
                if (percent >= 0 && percent < 100 && Environment.TickCount64 - lastReport < 250) return;
                lastReport = Environment.TickCount64;
                _ = Send(client, new { type = "download-progress", percent, text });
            });
            Log.Write($"Download de vídeo: salvo em {file}");
            await Send(client, new { type = "download-done", file });
            if (openAfter && IsVideoFile(file)) OpenRequested?.Invoke(file);
        }
        catch (Exception error)
        {
            bool cancelled = error is OperationCanceledException;
            Log.Write($"Download de vídeo: {(cancelled ? "cancelado" : "falhou — " + error.Message)}");
            await Send(client, new { type = "download-error", message = cancelled ? "cancelado" : error.Message });
        }
    }

    // Campo novo com reserva (extensões antigas não mandam a área de saída).
    private static double Optional(JsonElement element, string name, string fallback) =>
        element.TryGetProperty(name, out var value) ? value.GetDouble() : element.GetProperty(fallback).GetDouble();

    // Geometria mais recente do navegador capturado ("edge" ou "chrome"), se ainda for recente.
    public Geometry? GeometryFor(string browser)
    {
        lock (_clients)
        {
            var client = _clients.FirstOrDefault(c => c.Browser == browser);
            var geometry = client?.Geometry;
            return geometry is not null && (DateTime.UtcNow - geometry.Received).TotalSeconds < 3 ? geometry : null;
        }
    }

    public bool IsConnected(string browser)
    {
        lock (_clients) return _clients.Any(c => c.Browser == browser);
    }

    // Estado para a extensão de cada navegador:
    //   watch     = o app está ligado neste navegador → a extensão manda a posição do vídeo;
    //   capturing = o app assumiu o vídeo (vale a pena) → a extensão sai da frente.
    public void SetState(string? browser, bool capturing)
    {
        List<Client> clients;
        lock (_clients) clients = [.. _clients];
        foreach (var client in clients)
        {
            bool watch = client.Browser == browser;
            bool takeOver = watch && capturing;
            if (watch == client.Watching && takeOver == client.Capturing) continue;
            client.Watching = watch;
            client.Capturing = takeOver;
            _ = Send(client, new { type = "state", watch, capturing = takeOver });
        }
    }

    public void Dispose()
    {
        SetState(null, false);
        _download.Cancel();
        _stop.Cancel();
        try { _listener.Stop(); } catch { }
    }
}
