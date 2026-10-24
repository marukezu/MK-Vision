using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;

namespace MKVision.Desktop;

// Servidor de mídia só para este computador (127.0.0.1, porta escolhida pelo Windows): entrega um fluxo do app
// (vídeo da internet com os cabeçalhos do site, arquivo de HLS crescendo) como um endereço http comum, com
// pedidos por trecho ("Range"). O tocador de som do Windows lê pela internet sem problemas, mas não lê fluxos
// do .NET direto; assim ele lê do app.
internal static partial class LocalMediaServer
{
    private sealed record Entry(Func<Stream> Open, string ContentType);

    private static readonly object Lock = new();
    private static readonly Dictionary<string, Entry> Entries = [];
    private static TcpListener? _listener;
    private static int _port;

    // Endereço para "open" (uma leitura nova a cada pedido).
    public static string Register(Func<Stream> open, string contentType, string extension)
    {
        lock (Lock)
        {
            if (_listener is null)
            {
                _listener = new TcpListener(IPAddress.Loopback, 0);
                _listener.Start();
                _port = ((IPEndPoint)_listener.LocalEndpoint).Port;
                var thread = new Thread(Accept) { IsBackground = true, Name = "Servidor de mídia local" };
                thread.Start(_listener);
            }
            string id = Guid.NewGuid().ToString("N");
            Entries[id] = new Entry(open, contentType);
            return $"http://127.0.0.1:{_port}/m/{id}{extension}";
        }
    }

    public static void Unregister(string url)
    {
        var m = Regex.Match(url, @"/m/([0-9a-f]{32})");
        if (m.Success) lock (Lock) Entries.Remove(m.Groups[1].Value);
    }

    private static void Accept(object? state)
    {
        var listener = (TcpListener)state!;
        while (true)
        {
            TcpClient client;
            try { client = listener.AcceptTcpClient(); }
            catch { return; }
            var thread = new Thread(() => Serve(client)) { IsBackground = true, Name = "Servidor de mídia: pedido" };
            thread.Start();
        }
    }

    [GeneratedRegex(@"^Range:\s*bytes=(\d*)-(\d*)", RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex RangeHeader();

    private static void Serve(TcpClient client)
    {
        using (client)
        {
            try
            {
                var net = client.GetStream();
                while (true)
                {
                    string? head = ReadHead(net);
                    if (head is null) return;
                    var first = head.Split("\r\n")[0].Split(' ');
                    if (first.Length < 2) return;
                    bool headOnly = first[0] == "HEAD";
                    var m = Regex.Match(first[1], @"^/m/([0-9a-f]{32})");
                    Entry? entry = null;
                    if (m.Success) lock (Lock) Entries.TryGetValue(m.Groups[1].Value, out entry);
                    if (entry is null)
                    {
                        Write(net, "HTTP/1.1 404 Not Found\r\nContent-Length: 0\r\n\r\n");
                        continue;
                    }
                    using var stream = entry.Open();
                    long length = stream.Length;
                    long from = 0, to = length - 1;
                    var range = RangeHeader().Match(head);
                    bool partial = range.Success;
                    if (partial)
                    {
                        if (range.Groups[1].Value.Length > 0) from = long.Parse(range.Groups[1].Value);
                        if (range.Groups[2].Value.Length > 0) to = Math.Min(length - 1, long.Parse(range.Groups[2].Value));
                        else if (range.Groups[1].Value.Length == 0) return;
                    }
                    if (from >= length && length > 0)
                    {
                        Write(net, $"HTTP/1.1 416 Range Not Satisfiable\r\nContent-Range: bytes */{length}\r\nContent-Length: 0\r\n\r\n");
                        continue;
                    }
                    long count = Math.Max(0, to - from + 1);
                    Write(net, (partial ? "HTTP/1.1 206 Partial Content\r\n" : "HTTP/1.1 200 OK\r\n") +
                               $"Content-Type: {entry.ContentType}\r\nAccept-Ranges: bytes\r\nContent-Length: {count}\r\n" +
                               (partial ? $"Content-Range: bytes {from}-{to}/{length}\r\n" : "") + "\r\n");
                    if (headOnly) continue;
                    stream.Position = from;
                    var buffer = new byte[256 * 1024];
                    while (count > 0)
                    {
                        int n = stream.Read(buffer, 0, (int)Math.Min(buffer.Length, count));
                        if (n <= 0) return; // acabou antes (estimativa do HLS): fecha a conexão
                        net.Write(buffer, 0, n);
                        count -= n;
                    }
                }
            }
            catch
            {
                // O tocador fechou a conexão (pulou para outro ponto, trocou de vídeo...).
            }
        }
    }

    private static string? ReadHead(NetworkStream net)
    {
        var bytes = new List<byte>(512);
        int b;
        while ((b = net.ReadByte()) >= 0)
        {
            bytes.Add((byte)b);
            int n = bytes.Count;
            if (n >= 4 && bytes[n - 4] == '\r' && bytes[n - 3] == '\n' && bytes[n - 2] == '\r' && bytes[n - 1] == '\n')
                return Encoding.ASCII.GetString(bytes.ToArray());
            if (n > 16384) return null;
        }
        return null;
    }

    private static void Write(NetworkStream net, string text)
    {
        var bytes = Encoding.ASCII.GetBytes(text);
        net.Write(bytes, 0, bytes.Length);
    }
}
