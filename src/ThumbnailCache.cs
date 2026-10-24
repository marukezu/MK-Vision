using System.Runtime.InteropServices;

namespace MKVision.Desktop;

// Miniaturas dos vídeos para a lista da pasta: as mesmas do Explorer (o Windows gera e guarda em cache), pedidas
// ao Shell numa thread própria (STA, como ele pede), as mais recentes primeiro (o que está na tela agora).
// Ficam na memória enquanto o player estiver aberto. Também os ícones das pastas e discos (árvore de pastas).
internal sealed class ThumbnailCache : IDisposable
{
    public sealed record Thumb(byte[] Bgra, int Width, int Height);

    private const int Size = 256;

    [StructLayout(LayoutKind.Sequential)]
    private struct SIZE { public int cx, cy; }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAP { public int bmType, bmWidth, bmHeight, bmWidthBytes; public ushort bmPlanes, bmBitsPixel; public IntPtr bmBits; }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public int biSize, biWidth, biHeight;
        public short biPlanes, biBitCount;
        public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant;
    }

    [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        [PreserveSig] int GetImage(SIZE size, int flags, out IntPtr bitmap);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    private static extern void SHCreateItemFromParsingName(string path, IntPtr bindContext, ref Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory item);

    [DllImport("gdi32.dll")] private static extern int GetObject(IntPtr obj, int size, out BITMAP bitmap);
    [DllImport("gdi32.dll")] private static extern int GetDIBits(IntPtr dc, IntPtr bitmap, uint start, uint lines, byte[] bits, ref BITMAPINFOHEADER info, uint usage);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);

    private readonly object _lock = new();
    private readonly Dictionary<string, Thumb?> _done = new(StringComparer.OrdinalIgnoreCase); // null = sem miniatura
    private readonly List<string> _wanted = [];
    private readonly AutoResetEvent _wake = new(false);
    private readonly Thread _thread;
    private volatile bool _running = true;
    private int _version;

    // Muda quando chega uma miniatura nova (a lista redesenha).
    public int Version => Volatile.Read(ref _version);
    public event Action? Loaded;

    public ThumbnailCache()
    {
        _thread = new Thread(Loop) { IsBackground = true, Name = "Player: miniaturas" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }

    private const string IconPrefix = "icon|", FolderPrefix = "folder|"; // "|" não existe em caminhos do Windows

    // Ícone do Windows (pasta, disco, "Este Computador"...), com transparência; null enquanto carrega.
    public Thumb? GetIcon(string parsingName) => Get(IconPrefix + parsingName);

    // Pasta em tamanho de miniatura: o ícone grande do Windows (com a prévia do conteúdo, como no Explorer).
    public Thumb? GetFolder(string path) => Get(FolderPrefix + path);

    // A miniatura, se já carregou; senão pede (vai para a frente da fila) e devolve null.
    public Thumb? Get(string path)
    {
        lock (_lock)
        {
            if (_done.TryGetValue(path, out var thumb)) return thumb;
            _wanted.Remove(path);
            _wanted.Add(path);
            if (_wanted.Count > 64) _wanted.RemoveAt(0); // o que saiu da tela há muito tempo deixa de interessar
        }
        _wake.Set();
        return null;
    }

    private void Loop()
    {
        while (_running)
        {
            string? path = null;
            lock (_lock)
            {
                if (_wanted.Count > 0)
                {
                    path = _wanted[^1];
                    _wanted.RemoveAt(_wanted.Count - 1);
                }
            }
            if (path is null)
            {
                _wake.WaitOne(500);
                continue;
            }
            var thumb = path.StartsWith(IconPrefix) ? Load(path[IconPrefix.Length..], 32, 0x4)
                : path.StartsWith(FolderPrefix) ? Load(path[FolderPrefix.Length..], Size, 0x1)
                : Load(path, Size, 0x1 | 0x8);
            lock (_lock) _done[path] = thumb;
            Interlocked.Increment(ref _version);
            Loaded?.Invoke();
        }
    }

    // flags: 0x1 = maior serve, 0x4 = só ícone, 0x8 = só miniatura (sem ícone genérico).
    private static Thumb? Load(string path, int size, int flags)
    {
        IntPtr bitmap = IntPtr.Zero;
        try
        {
            var iid = typeof(IShellItemImageFactory).GUID;
            SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out var item);
            try
            {
                if (item.GetImage(new SIZE { cx = size, cy = size }, flags, out bitmap) != 0 || bitmap == IntPtr.Zero) return null;
            }
            finally { Marshal.ReleaseComObject(item); }
            GetObject(bitmap, Marshal.SizeOf<BITMAP>(), out var info);
            int w = info.bmWidth, h = Math.Abs(info.bmHeight);
            if (w <= 0 || h <= 0) return null;
            var header = new BITMAPINFOHEADER { biSize = 40, biWidth = w, biHeight = -h, biPlanes = 1, biBitCount = 32 };
            var pixels = new byte[w * h * 4];
            var dc = CreateCompatibleDC(IntPtr.Zero);
            try { GetDIBits(dc, bitmap, 0, (uint)h, pixels, ref header, 0); }
            finally { DeleteDC(dc); }
            return new Thumb(pixels, w, h);
        }
        catch
        {
            return null; // arquivo sem miniatura (formato que o Windows não lê, arquivo em uso...)
        }
        finally
        {
            if (bitmap != IntPtr.Zero) DeleteObject(bitmap);
        }
    }

    public void Dispose()
    {
        _running = false;
        _wake.Set();
        _thread.Join(2000);
    }
}
