using System.Runtime.InteropServices;

namespace MKVision.Desktop;

// Funções do Windows usadas pela janela do player (janela normal, com teclado, mouse, arrastar arquivo e
// tela cheia — a camada do modo Edge não precisa de nada disso).
internal static class PlayerNative
{
    public const uint WM_DESTROY = 0x0002, WM_SIZE = 0x0005, WM_CLOSE = 0x0010, WM_ERASEBKGND = 0x0014, WM_SETCURSOR = 0x0020,
        WM_KEYDOWN = 0x0100, WM_SYSKEYDOWN = 0x0104, WM_TIMER = 0x0113, WM_MOUSEMOVE = 0x0200, WM_LBUTTONDOWN = 0x0201,
        WM_LBUTTONUP = 0x0202, WM_LBUTTONDBLCLK = 0x0203, WM_MBUTTONDOWN = 0x0207, WM_MOUSEWHEEL = 0x020A, WM_DROPFILES = 0x0233;

    public const uint WS_OVERLAPPEDWINDOW = 0x00CF0000, WS_POPUP = 0x80000000, WS_VISIBLE = 0x10000000;
    public const uint WS_EX_ACCEPTFILES = 0x10;
    public const uint CS_DBLCLKS = 0x8, CS_HREDRAW = 0x2, CS_VREDRAW = 0x1;
    public const int GWL_STYLE = -16;
    public const int SW_SHOW = 5;
    public const uint SWP_NOZORDER = 0x4, SWP_FRAMECHANGED = 0x20, SWP_SHOWWINDOW = 0x40;
    public const uint MONITOR_DEFAULTTONEAREST = 2;
    public const int VK_BACK = 0x08, VK_RETURN = 0x0D, VK_SHIFT = 0x10, VK_CONTROL = 0x11, VK_ESCAPE = 0x1B, VK_SPACE = 0x20,
        VK_HOME = 0x24, VK_LEFT = 0x25, VK_UP = 0x26, VK_RIGHT = 0x27, VK_DOWN = 0x28;
    public const int IDC_ARROW = 32512;
    public const int IDI_APP = 32512; // o compilador grava o <ApplicationIcon> do .csproj com este número
    public const int SM_CXICON = 11, SM_CXSMICON = 49;
    public const uint IMAGE_ICON = 1;
    public const int BLACK_BRUSH = 4;
    public const int CW_USEDEFAULT = unchecked((int)0x80000000);

    [StructLayout(LayoutKind.Sequential)]
    public struct MONITORINFO
    {
        public uint cbSize;
        public Native.RECT rcMonitor, rcWork;
        public uint dwFlags;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct OPENFILENAME
    {
        public int lStructSize;
        public IntPtr hwndOwner, hInstance;
        public string lpstrFilter;
        public IntPtr lpstrCustomFilter;
        public int nMaxCustFilter, nFilterIndex;
        public IntPtr lpstrFile;
        public int nMaxFile;
        public IntPtr lpstrFileTitle;
        public int nMaxFileTitle;
        public string? lpstrInitialDir, lpstrTitle;
        public int Flags;
        public short nFileOffset, nFileExtension;
        public string? lpstrDefExt;
        public IntPtr lCustData, lpfnHook, lpTemplateName, pvReserved;
        public int dwReserved, FlagsEx;
    }

    public const int OFN_OVERWRITEPROMPT = 0x2, OFN_NOCHANGEDIR = 0x8, OFN_PATHMUSTEXIST = 0x800, OFN_FILEMUSTEXIST = 0x1000, OFN_EXPLORER = 0x80000;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern bool SetWindowText(IntPtr hwnd, string text);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    public static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    public static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);

    [DllImport("user32.dll")]
    public static extern bool GetWindowRect(IntPtr hwnd, out Native.RECT rect);

    [DllImport("user32.dll")]
    public static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [DllImport("user32.dll")]
    public static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);

    [DllImport("user32.dll")]
    public static extern bool ScreenToClient(IntPtr hwnd, ref Native.POINT point);

    [DllImport("user32.dll")]
    public static extern IntPtr SetCapture(IntPtr hwnd);

    [DllImport("user32.dll")]
    public static extern bool ReleaseCapture();

    [DllImport("user32.dll")]
    public static extern short GetKeyState(int key);

    [DllImport("user32.dll")]
    public static extern bool SetForegroundWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    public static extern IntPtr LoadCursor(IntPtr instance, IntPtr name);

    [DllImport("user32.dll")]
    public static extern IntPtr SetCursor(IntPtr cursor);

    [DllImport("user32.dll")]
    public static extern IntPtr LoadImage(IntPtr instance, IntPtr name, uint type, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    public static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll")]
    public static extern IntPtr SetTimer(IntPtr hwnd, IntPtr id, uint elapse, IntPtr func);

    [DllImport("gdi32.dll")]
    public static extern IntPtr GetStockObject(int index);

    [DllImport("shell32.dll")]
    public static extern void DragAcceptFiles(IntPtr hwnd, bool accept);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    public static extern uint DragQueryFile(IntPtr drop, uint index, char[]? file, uint size);

    [DllImport("shell32.dll")]
    public static extern void DragFinish(IntPtr drop);

    [DllImport("comdlg32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetOpenFileNameW")]
    public static extern bool GetOpenFileName(ref OPENFILENAME ofn);

    [DllImport("comdlg32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetSaveFileNameW")]
    public static extern bool GetSaveFileName(ref OPENFILENAME ofn);

    [DllImport("user32.dll")]
    public static extern bool PostMessage(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    // Versão Unicode (a janela é Unicode: com a versão ANSI, títulos saíam só com a primeira letra).
    [DllImport("user32.dll", EntryPoint = "DefWindowProcW")]
    public static extern IntPtr DefWindowProcW(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("dwmapi.dll")]
    public static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    // Barra de título escura (Windows 10 20H1+ / 11).
    public static void DarkTitleBar(IntPtr hwnd)
    {
        int on = 1;
        DwmSetWindowAttribute(hwnd, 20 /* DWMWA_USE_IMMERSIVE_DARK_MODE */, ref on, sizeof(int));
    }

    // ---------- Painel de ajustes (controles padrão do Windows) ----------

    public const uint WS_CHILD = 0x40000000, WS_CAPTION = 0x00C00000, WS_SYSMENU = 0x00080000, WS_TABSTOP = 0x00010000;
    public const uint WS_EX_TOOLWINDOW = 0x80;
    public const uint WM_SETFONT = 0x0030, WM_COMMAND = 0x0111, WM_HSCROLL = 0x0114, WM_CTLCOLORSTATIC = 0x0138, WM_USER = 0x0400;
    public const uint TBM_GETPOS = WM_USER, TBM_SETPOS = WM_USER + 5, TBM_SETRANGE = WM_USER + 6, TBM_SETPAGESIZE = WM_USER + 21;
    public const uint TBS_NOTICKS = 0x10;
    public const uint BS_AUTOCHECKBOX = 0x3, BS_AUTORADIOBUTTON = 0x9;
    public const uint BM_GETCHECK = 0xF0, BM_SETCHECK = 0xF1;
    public const uint SS_RIGHT = 0x2;
    public const int VK_TAB = 0x09, VK_ADD = 0x6B, VK_SUBTRACT = 0x6D, VK_OEM_PLUS = 0xBB, VK_OEM_MINUS = 0xBD;

    [StructLayout(LayoutKind.Sequential)]
    public struct INITCOMMONCONTROLSEX { public int dwSize, dwICC; }

    [DllImport("comctl32.dll")]
    public static extern bool InitCommonControlsEx(ref INITCOMMONCONTROLSEX init);

    [DllImport("user32.dll")]
    public static extern IntPtr SendMessage(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr SendMessage(IntPtr hwnd, uint msg, IntPtr wParam, string lParam);

    [DllImport("user32.dll")]
    public static extern bool AdjustWindowRectEx(ref Native.RECT rect, uint style, bool menu, uint exStyle);

    [DllImport("user32.dll")]
    public static extern uint GetDpiForWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    public static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr CreateFont(int height, int width, int escapement, int orientation, int weight, uint italic,
        uint underline, uint strikeOut, uint charSet, uint outPrecision, uint clipPrecision, uint quality, uint pitchAndFamily, string face);

    [DllImport("gdi32.dll")]
    public static extern IntPtr CreateSolidBrush(uint color);

    [DllImport("gdi32.dll")]
    public static extern bool DeleteObject(IntPtr obj);

    [DllImport("gdi32.dll")]
    public static extern uint SetTextColor(IntPtr hdc, uint color);

    [DllImport("gdi32.dll")]
    public static extern uint SetBkColor(IntPtr hdc, uint color);

    public static bool KeyDown(int key) => GetKeyState(key) < 0;
    public static int LowWord(IntPtr value) => (short)((long)value & 0xffff);
    public static int HighWord(IntPtr value) => (short)(((long)value >> 16) & 0xffff);
}
