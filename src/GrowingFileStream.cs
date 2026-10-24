namespace MKVision.Desktop;

// Arquivo que ainda está sendo baixado (HLS / download pelo yt-dlp), lido enquanto cresce: o tamanho informado é
// o que já chegou, ou a estimativa do site se for maior; ler além do que já chegou espera chegar. O leitor do
// Windows só lê até o tamanho que viu ao abrir: o player reabre quando chega lá (Player.ReopenGrowing).
internal sealed class GrowingFileStream : Stream
{
    private readonly FileStream _file;
    private readonly Task _download;
    private readonly long _estimate;
    private readonly CancellationToken _token;
    private long _position;

    public GrowingFileStream(string path, Task download, long estimate, CancellationToken token)
    {
        _file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        _download = download;
        _estimate = estimate;
        _token = token;
    }

    private bool Done => _download.IsCompleted;

    public override long Length => Done ? _file.Length : Math.Max(_file.Length, _estimate);
    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;

    public override int Read(byte[] buffer, int offset, int count)
    {
        if (_position >= Length) return 0;
        while (!_token.IsCancellationRequested)
        {
            long available = _file.Length - _position;
            if (available > 0)
            {
                _file.Position = _position;
                int n = _file.Read(buffer, offset, (int)Math.Min(count, available));
                _position += n;
                return n;
            }
            if (Done) return 0; // fim de verdade
            Thread.Sleep(50);   // ainda não chegou
        }
        return 0;
    }

    public override long Position
    {
        get => _position;
        set => _position = Math.Max(0, value);
    }

    public override long Seek(long offset, SeekOrigin origin) =>
        Position = origin switch { SeekOrigin.Begin => offset, SeekOrigin.Current => _position + offset, _ => Length + offset };

    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing) _file.Dispose();
        base.Dispose(disposing);
    }
}
