using System.Runtime.InteropServices;

namespace MKVision.Desktop;

// Janelinha escura sobre o navegador (o site é uma janela à parte: o que o player desenha fica por baixo dele).
// Serve para o título completo da aba ao parar o mouse nela e para as sugestões da barra de endereço. Não rouba o
// foco (o cursor continua no endereço) e some sozinha quando o dono pede.
internal sealed class WebPopup : IDisposable
{
    public sealed record Row(string Text, string? Detail = null, ThumbnailCache.Thumb? Icon = null);

    private const string ClassName = "MKVisionPopup";
    private static bool _registered;
    private static readonly Native.WndProc Proc = StaticProc;
    private static readonly Dictionary<IntPtr, WebPopup> Popups = [];

    private readonly IntPtr _owner;
    private readonly bool _list; // lista de sugestões (linhas clicáveis, ícone, texto e endereço na mesma linha); senão, dica
    private IntPtr _hwnd, _font, _fontSmall;
    private double _scale;
    private IReadOnlyList<Row> _rows = [];
    private int _selected = -1, _hover = -1, _rowH;

    public event Action<int>? Picked; // linha clicada

    public WebPopup(IntPtr owner, bool list)
    {
        (_owner, _list) = (owner, list);
    }

    public bool Visible => _hwnd != IntPtr.Zero && IsWindowVisible(_hwnd);
    public IntPtr Handle => _hwnd; // autoteste: foto da janelinha
    public int Count => _rows.Count;

    public int Selected
    {
        get => _selected;
        set
        {
            _selected = Math.Clamp(value, -1, _rows.Count - 1);
            if (_hwnd != IntPtr.Zero) InvalidateRect(_hwnd, IntPtr.Zero, false);
        }
    }

    // Mostra (ou atualiza) com o canto de cima à esquerda em (x, y) da tela; a largura é pelo menos "minWidth".
    public void Show(int x, int y, int minWidth, IReadOnlyList<Row> rows, int selected = -1)
    {
        if (rows.Count == 0) { Hide(); return; }
        Create();
        (_rows, _selected, _hover) = (rows, selected, -1);
        int S(double v) => (int)Math.Round(v * _scale);
        var dc = GetDC(_hwnd);
        int width, height;
        if (_list)
        {
            _rowH = S(34);
            (width, height) = (minWidth, rows.Count * _rowH + S(8));
        }
        else
        {
            // Dica: título (até 2 linhas) e o endereço embaixo.
            int max = S(440), pad = S(10);
            var old = SelectObject(dc, _font);
            var r = new RECT { Right = max - 2 * pad };
            DrawTextW(dc, rows[0].Text, -1, ref r, DT_CALCRECT | DT_WORDBREAK | DT_NOPREFIX);
            int textW = r.Right, textH = Math.Min(r.Bottom, S(40));
            int detailW = 0, detailH = 0;
            if (rows[0].Detail is { Length: > 0 } detail)
            {
                SelectObject(dc, _fontSmall);
                var d = new RECT { Right = max - 2 * pad };
                DrawTextW(dc, detail, -1, ref d, DT_CALCRECT | DT_SINGLELINE | DT_NOPREFIX);
                (detailW, detailH) = (Math.Min(d.Right, max - 2 * pad), d.Bottom + S(2));
            }
            SelectObject(dc, old);
            (width, height) = (Math.Max(minWidth, Math.Max(textW, detailW) + 2 * pad), textH + detailH + 2 * S(7));
        }
        ReleaseDC(_hwnd, dc);
        // Não passa da tela.
        var monitor = new PlayerNative.MONITORINFO { cbSize = (uint)Marshal.SizeOf<PlayerNative.MONITORINFO>() };
        PlayerNative.GetMonitorInfo(PlayerNative.MonitorFromWindow(_owner, PlayerNative.MONITOR_DEFAULTTONEAREST), ref monitor);
        x = Math.Max(monitor.rcWork.Left, Math.Min(x, monitor.rcWork.Right - width));
        Native.SetWindowPos(_hwnd, (IntPtr)(-1) /* HWND_TOPMOST */, x, y, width, height, 0x0010 | 0x0040 /* NOACTIVATE SHOWWINDOW */);
        InvalidateRect(_hwnd, IntPtr.Zero, false);
    }

    public void Hide()
    {
        if (_hwnd != IntPtr.Zero && IsWindowVisible(_hwnd)) Native.ShowWindow(_hwnd, 0);
        _hover = -1;
    }

    public void Dispose()
    {
        if (_hwnd != IntPtr.Zero)
        {
            lock (Popups) Popups.Remove(_hwnd);
            Native.DestroyWindow(_hwnd);
            _hwnd = IntPtr.Zero;
        }
        if (_font != IntPtr.Zero) UiCanvas.DeleteFont(_font);
        if (_fontSmall != IntPtr.Zero) UiCanvas.DeleteFont(_fontSmall);
        _font = _fontSmall = IntPtr.Zero;
    }

    private void Create()
    {
        double scale = Math.Max(1, PlayerNative.GetDpiForWindow(_owner) / 96.0);
        if (_hwnd != IntPtr.Zero && scale == _scale) return;
        Dispose();
        _scale = scale;
        var instance = Native.GetModuleHandle(null);
        if (!_registered)
        {
            var wc = new Native.WNDCLASSEX
            {
                cbSize = (uint)Marshal.SizeOf<Native.WNDCLASSEX>(),
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(Proc),
                hInstance = instance,
                hCursor = PlayerNative.LoadCursor(IntPtr.Zero, PlayerNative.IDC_ARROW),
                lpszClassName = ClassName
            };
            Native.RegisterClassEx(ref wc);
            _registered = true;
        }
        // Dica: o mouse passa através dela (WS_EX_TRANSPARENT); a lista recebe cliques, mas sem pegar o foco.
        uint ex = 0x00000080 /* TOOLWINDOW */ | 0x08000000 /* NOACTIVATE */ | 0x00000008 /* TOPMOST */ | (_list ? 0u : 0x00000020 /* TRANSPARENT */);
        _hwnd = Native.CreateWindowEx(ex, ClassName, "", 0x80000000 /* WS_POPUP */, 0, 0, 10, 10, _owner, IntPtr.Zero, instance, IntPtr.Zero);
        lock (Popups) Popups[_hwnd] = this;
        int corner = 3; // cantos arredondados pequenos (Windows 11)
        DwmSetWindowAttribute(_hwnd, 33 /* DWMWA_WINDOW_CORNER_PREFERENCE */, ref corner, sizeof(int));
        _font = UiCanvas.Font("Segoe UI", (int)Math.Round((_list ? 15 : 14) * _scale));
        _fontSmall = UiCanvas.Font("Segoe UI", (int)Math.Round(13 * _scale));
    }

    private static IntPtr StaticProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        WebPopup? popup;
        lock (Popups) Popups.TryGetValue(hwnd, out popup);
        return popup?.WindowProc(hwnd, msg, wParam, lParam) ?? Native.DefWindowProc(hwnd, msg, wParam, lParam);
    }

    private IntPtr WindowProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case 0x0021: // WM_MOUSEACTIVATE: clicar não tira o foco do endereço
                return 3; // MA_NOACTIVATE
            case 0x0014: // WM_ERASEBKGND
                return 1;
            case 0x0200 when _list: // WM_MOUSEMOVE
            {
                int row = RowAt(PlayerNative.HighWord(lParam));
                if (row != _hover) { _hover = row; InvalidateRect(hwnd, IntPtr.Zero, false); }
                var track = new TRACKMOUSEEVENT { cbSize = Marshal.SizeOf<TRACKMOUSEEVENT>(), dwFlags = 2 /* TME_LEAVE */, hwndTrack = hwnd };
                TrackMouseEvent(ref track);
                return IntPtr.Zero;
            }
            case 0x02A3 when _list: // WM_MOUSELEAVE
                _hover = -1;
                InvalidateRect(hwnd, IntPtr.Zero, false);
                return IntPtr.Zero;
            case 0x0201 when _list: // WM_LBUTTONDOWN
            {
                int row = RowAt(PlayerNative.HighWord(lParam));
                if (row >= 0) Picked?.Invoke(row);
                return IntPtr.Zero;
            }
            case 0x000F: // WM_PAINT
                Paint(hwnd);
                return IntPtr.Zero;
        }
        return Native.DefWindowProc(hwnd, msg, wParam, lParam);
    }

    private int RowAt(int y)
    {
        int row = (y - (int)Math.Round(4 * _scale)) / Math.Max(1, _rowH);
        return y >= 0 && row >= 0 && row < _rows.Count ? row : -1;
    }

    private const uint Back = 0x302C2B, Line = 0x4D4745, Hover = 0x403B3A, Chosen = 0x55402B, Text = 0xEDEAE8, Detail = 0xA6A09A; // 0x00BBGGRR

    private void Paint(IntPtr hwnd)
    {
        var ps = new PAINTSTRUCT();
        var dc = BeginPaint(hwnd, ref ps);
        GetClientRect(hwnd, out var client);
        // Desenha numa imagem e copia de uma vez (sem piscar).
        var mem = CreateCompatibleDC(dc);
        var bitmap = CreateCompatibleBitmap(dc, client.Right, client.Bottom);
        var oldBitmap = SelectObject(mem, bitmap);
        Fill(mem, client, Back);
        Frame(mem, client, Line);
        SetBkMode(mem, 1 /* TRANSPARENT */);
        int S(double v) => (int)Math.Round(v * _scale);
        var oldFont = SelectObject(mem, _font);
        if (_list)
        {
            for (int i = 0; i < _rows.Count; i++)
            {
                var row = _rows[i];
                var r = new RECT { Left = S(4), Top = S(4) + i * _rowH, Right = client.Right - S(4), Bottom = S(4) + (i + 1) * _rowH };
                if (i == _selected) Fill(mem, r, Chosen);
                else if (i == _hover) Fill(mem, r, Hover);
                int iconSize = S(16), ix = r.Left + S(10), iy = r.Top + (_rowH - iconSize) / 2;
                if (row.Icon is { } icon) DrawIcon(mem, icon, ix, iy, iconSize, i == _selected ? Chosen : i == _hover ? Hover : Back);
                else
                {
                    var star = new RECT { Left = ix, Top = r.Top, Right = ix + iconSize, Bottom = r.Bottom };
                    SetTextColor(mem, 0x42C5F5); // ★ amarela dos favoritos
                    DrawTextW(mem, "★", -1, ref star, DT_SINGLELINE | DT_VCENTER | DT_CENTER | DT_NOPREFIX);
                }
                var text = new RECT { Left = ix + iconSize + S(12), Top = r.Top, Right = r.Right - S(10), Bottom = r.Bottom };
                SetTextColor(mem, Text);
                SelectObject(mem, _font);
                var measure = text;
                DrawTextW(mem, row.Text, -1, ref measure, DT_CALCRECT | DT_SINGLELINE | DT_NOPREFIX);
                int nameRight = Math.Min(text.Right, measure.Right);
                var name = text with { Right = nameRight };
                DrawTextW(mem, row.Text, -1, ref name, DT_SINGLELINE | DT_VCENTER | DT_END_ELLIPSIS | DT_NOPREFIX);
                if (row.Detail is { Length: > 0 } detail && nameRight + S(40) < text.Right)
                {
                    SelectObject(mem, _fontSmall);
                    SetTextColor(mem, Detail);
                    var url = text with { Left = nameRight + S(10) };
                    DrawTextW(mem, "— " + detail, -1, ref url, DT_SINGLELINE | DT_VCENTER | DT_END_ELLIPSIS | DT_NOPREFIX);
                }
            }
        }
        else if (_rows.Count > 0)
        {
            int pad = S(10);
            bool hasDetail = _rows[0].Detail is { Length: > 0 };
            int detailH = hasDetail ? S(18) : 0;
            var title = new RECT { Left = pad, Top = S(7), Right = client.Right - pad, Bottom = client.Bottom - S(7) - detailH };
            SetTextColor(mem, Text);
            DrawTextW(mem, _rows[0].Text, -1, ref title, DT_WORDBREAK | DT_END_ELLIPSIS | DT_NOPREFIX);
            if (hasDetail)
            {
                SelectObject(mem, _fontSmall);
                SetTextColor(mem, Detail);
                var detail = new RECT { Left = pad, Top = client.Bottom - S(7) - detailH, Right = client.Right - pad, Bottom = client.Bottom - S(5) };
                DrawTextW(mem, _rows[0].Detail!, -1, ref detail, DT_SINGLELINE | DT_VCENTER | DT_END_ELLIPSIS | DT_NOPREFIX);
            }
        }
        SelectObject(mem, oldFont);
        BitBlt(dc, 0, 0, client.Right, client.Bottom, mem, 0, 0, 0x00CC0020 /* SRCCOPY */);
        SelectObject(mem, oldBitmap);
        DeleteObject(bitmap);
        DeleteDC(mem);
        EndPaint(hwnd, ref ps);
    }

    private static void Fill(IntPtr dc, RECT r, uint color)
    {
        var brush = CreateSolidBrush(color);
        FillRect(dc, ref r, brush);
        DeleteObject(brush);
    }

    private static void Frame(IntPtr dc, RECT r, uint color)
    {
        var brush = CreateSolidBrush(color);
        FrameRect(dc, ref r, brush);
        DeleteObject(brush);
    }

    // Ícone do site (BGRA com transparência) misturado com o fundo, no tamanho pedido.
    private static void DrawIcon(IntPtr dc, ThumbnailCache.Thumb icon, int x, int y, int size, uint background)
    {
        byte br = (byte)(background & 0xff), bg = (byte)((background >> 8) & 0xff), bb = (byte)((background >> 16) & 0xff);
        var pixels = new byte[size * size * 4];
        for (int py = 0; py < size; py++)
        for (int px = 0; px < size; px++)
        {
            int sx = Math.Min(icon.Width - 1, px * icon.Width / size), sy = Math.Min(icon.Height - 1, py * icon.Height / size);
            int s = (sy * icon.Width + sx) * 4, d = (py * size + px) * 4;
            int a = icon.Bgra[s + 3];
            pixels[d] = (byte)((icon.Bgra[s] * a + bb * (255 - a)) / 255);
            pixels[d + 1] = (byte)((icon.Bgra[s + 1] * a + bg * (255 - a)) / 255);
            pixels[d + 2] = (byte)((icon.Bgra[s + 2] * a + br * (255 - a)) / 255);
        }
        var info = new BITMAPINFOHEADER { biSize = 40, biWidth = size, biHeight = -size, biPlanes = 1, biBitCount = 32 };
        StretchDIBits(dc, x, y, size, size, 0, 0, size, size, pixels, ref info, 0, 0x00CC0020);
    }

    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct PAINTSTRUCT { public IntPtr hdc; public int fErase; public RECT rcPaint; public int fRestore, fIncUpdate; [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] public byte[] rgbReserved; }
    [StructLayout(LayoutKind.Sequential)] private struct BITMAPINFOHEADER { public int biSize, biWidth, biHeight; public short biPlanes, biBitCount; public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant; }
    [StructLayout(LayoutKind.Sequential)] private struct TRACKMOUSEEVENT { public int cbSize; public uint dwFlags; public IntPtr hwndTrack; public uint dwHoverTime; }
    private const uint DT_CENTER = 0x1, DT_VCENTER = 0x4, DT_WORDBREAK = 0x10, DT_SINGLELINE = 0x20, DT_CALCRECT = 0x400, DT_NOPREFIX = 0x800, DT_END_ELLIPSIS = 0x8000;

    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool InvalidateRect(IntPtr hwnd, IntPtr rect, bool erase);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
    [DllImport("user32.dll")] private static extern IntPtr BeginPaint(IntPtr hwnd, ref PAINTSTRUCT ps);
    [DllImport("user32.dll")] private static extern bool EndPaint(IntPtr hwnd, ref PAINTSTRUCT ps);
    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")] private static extern int FillRect(IntPtr dc, ref RECT rect, IntPtr brush);
    [DllImport("user32.dll")] private static extern int FrameRect(IntPtr dc, ref RECT rect, IntPtr brush);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int DrawTextW(IntPtr dc, string text, int count, ref RECT rect, uint format);
    [DllImport("user32.dll")] private static extern bool TrackMouseEvent(ref TRACKMOUSEEVENT track);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateSolidBrush(uint color);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] private static extern uint SetTextColor(IntPtr dc, uint color);
    [DllImport("gdi32.dll")] private static extern int SetBkMode(IntPtr dc, int mode);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleBitmap(IntPtr dc, int w, int h);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern bool BitBlt(IntPtr dest, int x, int y, int w, int h, IntPtr src, int sx, int sy, uint rop);
    [DllImport("gdi32.dll")] private static extern int StretchDIBits(IntPtr dc, int x, int y, int w, int h, int sx, int sy, int sw, int sh, byte[] bits, ref BITMAPINFOHEADER info, uint usage, uint rop);
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
