using System.Net;
using System.Net.Http.Headers;

namespace MKVision.Desktop;

// Vídeo da internet como se fosse um arquivo: o leitor do Windows pede trechos (posição + tamanho) e esta
// classe busca em pedaços de 2 MB com "Range" (o YouTube corta quem pede o arquivo inteiro de uma vez),
// com os cabeçalhos do site (navegador, página de origem, login). Uma thread busca sempre um pouco à frente de
// cada leitor; pular no vídeo = o leitor pede outra posição e a busca recomeça de lá. Os pedaços ficam na
// memória (os mais antigos saem quando passa do limite) e são divididos entre os leitores do mesmo endereço
// (imagem e som de um vídeo com os dois juntos: baixa uma vez só).
internal sealed class HttpRangeSource : IDisposable
{
    private const int Block = 1 << 20;            // 1 MB
    private const int BlocksPerRequest = 2;       // pedidos de 2 MB
    private const int ReadAheadBlocks = 24;       // ~24 MB à frente de cada leitor
    private const int MaxBlocks = 384;            // ~384 MB na memória

    private readonly HttpClient _http;
    private readonly string _url;
    private readonly IReadOnlyDictionary<string, string> _headers;
    private readonly object _lock = new();
    private readonly Dictionary<long, byte[]> _blocks = [];
    private readonly LinkedList<long> _recent = new();
    private readonly HashSet<long> _fetching = [];
    private readonly List<HttpRangeStream> _readers = [];
    private readonly Thread _ahead;
    private readonly CancellationTokenSource _cancel = new();
    private Exception? _failure;
    private int _references = 1;

    public long Length { get; }
    public string? ContentType { get; }

    public HttpRangeSource(string url, IReadOnlyDictionary<string, string>? headers = null)
    {
        _url = url;
        _headers = headers ?? new Dictionary<string, string>();
        _http = new HttpClient(new SocketsHttpHandler { AutomaticDecompression = DecompressionMethods.None, UseCookies = false })
        {
            Timeout = TimeSpan.FromSeconds(30)
        };
        // Primeiro pedaço: diz o tamanho total (Content-Range) e se o servidor aceita trechos.
        using var response = Send(0, (long)BlocksPerRequest * Block - 1, _cancel.Token);
        var total = response.Content.Headers.ContentRange?.Length;
        if (response.StatusCode != HttpStatusCode.PartialContent || total is null)
            throw new IOException("o servidor não entrega o vídeo em trechos");
        Length = total.Value;
        ContentType = response.Content.Headers.ContentType?.MediaType;
        Store(0, response.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult());
        _ahead = new Thread(ReadAhead) { IsBackground = true, Name = "Vídeo da internet: à frente" };
        _ahead.Start();
    }

    // Um leitor novo (posição própria). A fonte fecha quando ela e todos os leitores fecharem.
    public HttpRangeStream OpenReader()
    {
        var reader = new HttpRangeStream(this);
        lock (_lock)
        {
            _readers.Add(reader);
            _references++;
        }
        return reader;
    }

    internal void Release(HttpRangeStream? reader)
    {
        bool last;
        lock (_lock)
        {
            if (reader is not null) _readers.Remove(reader);
            last = --_references == 0;
        }
        if (!last) return;
        _cancel.Cancel();
        lock (_lock) Monitor.PulseAll(_lock);
        _ahead.Join(2000);
        _http.Dispose();
    }

    public void Dispose() => Release(null);

    private HttpResponseMessage Send(long from, long to, CancellationToken token)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, _url);
        foreach (var (name, value) in _headers) request.Headers.TryAddWithoutValidation(name, value);
        request.Headers.Range = new RangeHeaderValue(from, to);
        var response = _http.Send(request, HttpCompletionOption.ResponseHeadersRead, token);
        if (!response.IsSuccessStatusCode)
        {
            response.Dispose();
            throw new IOException($"o servidor respondeu {(int)response.StatusCode}");
        }
        return response;
    }

    private void Store(long first, byte[] data)
    {
        lock (_lock)
        {
            for (int i = 0; i * (long)Block < data.Length; i++)
            {
                int size = (int)Math.Min(Block, data.Length - i * (long)Block);
                var block = new byte[size];
                Buffer.BlockCopy(data, i * Block, block, 0, size);
                _blocks[first + i] = block;
                _recent.Remove(first + i);
                _recent.AddLast(first + i);
            }
            while (_blocks.Count > MaxBlocks && _recent.First is { } old)
            {
                _recent.RemoveFirst();
                _blocks.Remove(old.Value);
            }
            Monitor.PulseAll(_lock);
        }
    }

    private long BlockCount => (Length + Block - 1) / Block;

    // Busca um pedaço a partir do bloco "first" (até BlocksPerRequest blocos que ainda faltam). Tenta 3 vezes.
    private void Fetch(long first)
    {
        long last;
        lock (_lock)
        {
            if (_blocks.ContainsKey(first) || !_fetching.Add(first)) return;
            last = first;
            while (last + 1 < Math.Min(BlockCount, first + BlocksPerRequest) && !_blocks.ContainsKey(last + 1) && _fetching.Add(last + 1)) last++;
        }
        try
        {
            for (int attempt = 1; ; attempt++)
            {
                try
                {
                    using var response = Send(first * Block, Math.Min(Length, (last + 1) * Block) - 1, _cancel.Token);
                    Store(first, response.Content.ReadAsByteArrayAsync(_cancel.Token).GetAwaiter().GetResult());
                    return;
                }
                catch (Exception) when (attempt < 3 && !_cancel.IsCancellationRequested)
                {
                    Thread.Sleep(300 * attempt);
                }
            }
        }
        catch (Exception error)
        {
            lock (_lock) _failure ??= error;
        }
        finally
        {
            lock (_lock)
            {
                for (long b = first; b <= last; b++) _fetching.Remove(b);
                Monitor.PulseAll(_lock);
            }
        }
    }

    // Thread: mantém os próximos ~24 MB a partir de onde cada leitor está.
    private void ReadAhead()
    {
        while (!_cancel.IsCancellationRequested)
        {
            long next = -1;
            lock (_lock)
            {
                foreach (var reader in _readers)
                {
                    long from = reader.Wanted / Block;
                    for (long b = from; b < Math.Min(BlockCount, from + ReadAheadBlocks) && next < 0; b++)
                        if (!_blocks.ContainsKey(b) && !_fetching.Contains(b)) next = b;
                    if (next >= 0) break;
                }
                if (next < 0)
                {
                    Monitor.Wait(_lock, 200);
                    continue;
                }
            }
            Fetch(next);
        }
    }

    // Copia de "position" para o buffer (espera o pedaço chegar). Devolve quantos bytes.
    internal int Read(long position, byte[] buffer, int offset, int count)
    {
        while (true)
        {
            long index = position / Block;
            byte[]? block;
            lock (_lock)
            {
                Monitor.PulseAll(_lock); // a busca à frente segue o leitor
                _blocks.TryGetValue(index, out block);
                if (block is not null)
                {
                    _recent.Remove(index);
                    _recent.AddLast(index);
                }
                else if (_failure is { } failure) throw new IOException("não consegui baixar o vídeo", failure);
            }
            if (block is null)
            {
                Fetch(index); // ninguém buscando: busca agora; se já estiver vindo, espera chegar
                lock (_lock)
                    while (!_blocks.ContainsKey(index) && _fetching.Contains(index) && _failure is null && !_cancel.IsCancellationRequested)
                        Monitor.Wait(_lock, 500);
                if (_cancel.IsCancellationRequested) throw new ObjectDisposedException(nameof(HttpRangeSource));
                continue;
            }
            int inBlock = (int)(position - index * Block);
            int n = Math.Min(count, block.Length - inBlock);
            if (n <= 0) return 0;
            Buffer.BlockCopy(block, inBlock, buffer, offset, n);
            return n;
        }
    }
}

// Um leitor de um HttpRangeSource (posição própria).
internal sealed class HttpRangeStream : Stream
{
    private readonly HttpRangeSource _source;
    private long _position;
    private bool _closed;

    internal HttpRangeStream(HttpRangeSource source) => _source = source;

    internal long Wanted => Volatile.Read(ref _position);

    public override long Length => _source.Length;
    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;

    public override int Read(byte[] buffer, int offset, int count)
    {
        int total = 0;
        while (count > 0 && _position < Length)
        {
            int n = _source.Read(_position, buffer, offset, count);
            if (n <= 0) break;
            (offset, count, total) = (offset + n, count - n, total + n);
            Volatile.Write(ref _position, _position + n);
        }
        return total;
    }

    public override long Position
    {
        get => _position;
        set => Volatile.Write(ref _position, Math.Clamp(value, 0, Length));
    }

    public override long Seek(long offset, SeekOrigin origin) =>
        Position = origin switch { SeekOrigin.Begin => offset, SeekOrigin.Current => _position + offset, _ => Length + offset };

    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_closed)
        {
            _closed = true;
            _source.Release(this);
        }
        base.Dispose(disposing);
    }
}
