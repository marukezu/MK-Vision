using System.Runtime.InteropServices;

namespace MKVision.Desktop;

// Janelinha para digitar um texto (nome de pasta, de favorito): OK / Cancelar, Enter e Esc. Espera a resposta
// (a janela do player fica bloqueada enquanto isso, como numa caixa de mensagem).
internal static class InputBox
{
    private const string ClassName = "MKVisionInputBox";
    private const int IdOk = 1, IdCancel = 2, IdText = 100;
    private static bool _registered;
    private static string? _result;
    private static bool _done;
    private static IntPtr _edit;
    private static readonly Native.WndProc Proc = WindowProc;

    [DllImport("user32.dll")] private static extern bool EnableWindow(IntPtr hwnd, bool enable);
    [DllImport("user32.dll")] private static extern bool IsDialogMessage(IntPtr hwnd, ref Native.MSG msg);
    [DllImport("user32.dll")] private static extern IntPtr SetFocus(IntPtr hwnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextW(IntPtr hwnd, char[] text, int max);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr SendMessageW(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern IntPtr GetSysColorBrush(int index);

    public static string? Show(IntPtr owner, string title, string prompt, string initial)
    {
        var instance = Native.GetModuleHandle(null);
        if (!_registered)
        {
            var wc = new Native.WNDCLASSEX
            {
                cbSize = (uint)Marshal.SizeOf<Native.WNDCLASSEX>(),
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(Proc),
                hInstance = instance,
                hCursor = PlayerNative.LoadCursor(IntPtr.Zero, PlayerNative.IDC_ARROW),
                hbrBackground = GetSysColorBrush(15 /* COLOR_BTNFACE */),
                lpszClassName = ClassName
            };
            Native.RegisterClassEx(ref wc);
            _registered = true;
        }
        double scale = Math.Max(1, GetDpiForWindow(owner) / 96.0);
        int S(int v) => (int)(v * scale);
        PlayerNative.GetWindowRect(owner, out var o);
        int w = S(420), h = S(170);
        const uint style = 0x00C00000 /* WS_CAPTION */ | 0x00080000 /* WS_SYSMENU */ | 0x10000000 /* WS_VISIBLE */;
        var hwnd = Native.CreateWindowEx(0x00000001 /* WS_EX_DLGMODALFRAME */, ClassName, title, style,
            o.Left + (o.Width - w) / 2, o.Top + (o.Height - h) / 3, w, h, owner, IntPtr.Zero, instance, IntPtr.Zero);
        PlayerNative.DarkTitleBar(hwnd);
        var font = UiCanvas.Font("Segoe UI", S(15));
        IntPtr Child(string type, string text, uint childStyle, int x, int y, int cw, int ch, int id, uint exStyle = 0)
        {
            var child = Native.CreateWindowEx(exStyle, type, text, 0x40000000 /* WS_CHILD */ | 0x10000000 /* WS_VISIBLE */ | childStyle,
                S(x), S(y), S(cw), S(ch), hwnd, id, instance, IntPtr.Zero);
            SendMessageW(child, 0x0030 /* WM_SETFONT */, font, 1);
            return child;
        }
        Child("STATIC", prompt, 0, 14, 12, 380, 22, 0);
        _edit = Child("EDIT", initial, 0x0080 /* ES_AUTOHSCROLL */ | 0x00010000 /* WS_TABSTOP */, 14, 40, 376, 26, IdText, 0x00000200 /* WS_EX_CLIENTEDGE */);
        Child("BUTTON", "OK", 0x0001 /* BS_DEFPUSHBUTTON */ | 0x00010000, 214, 84, 84, 30, IdOk);
        Child("BUTTON", "Cancelar", 0x00010000, 306, 84, 84, 30, IdCancel);
        SetFocus(_edit);
        SendMessageW(_edit, 0x00B1 /* EM_SETSEL */, 0, -1);
        (_result, _done) = (null, false);
        EnableWindow(owner, false);
        while (!_done && Native.GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            if (IsDialogMessage(hwnd, ref msg)) continue; // Tab, Enter (OK) e Esc (Cancelar)
            Native.TranslateMessage(ref msg);
            Native.DispatchMessage(ref msg);
        }
        EnableWindow(owner, true);
        Native.DestroyWindow(hwnd);
        UiCanvas.DeleteFont(font);
        PlayerNative.SetForegroundWindow(owner);
        return _result is { Length: > 0 } r ? r.Trim() : null;
    }

    private static IntPtr WindowProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case 0x0111: // WM_COMMAND
                int id = (int)wParam & 0xffff;
                if (id == IdOk)
                {
                    var buffer = new char[1024];
                    int n = GetWindowTextW(_edit, buffer, buffer.Length);
                    _result = new string(buffer, 0, n);
                    _done = true;
                }
                else if (id == IdCancel) _done = true;
                return IntPtr.Zero;
            case 0x0010: // WM_CLOSE
                _done = true;
                return IntPtr.Zero;
        }
        return Native.DefWindowProc(hwnd, msg, wParam, lParam);
    }
}
