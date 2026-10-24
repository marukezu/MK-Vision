using System.Runtime.InteropServices;
using Vortice.Direct3D11;
using static MKVision.Desktop.PlayerNative;

namespace MKVision.Desktop;

// No navegador, a janela não tem a barra de título do Windows: a faixa das abas é o topo, como no Edge. Os botões
// de minimizar, maximizar e fechar ficam no canto direito da faixa (desenhados aqui); o espaço vazio da faixa arrasta
// a janela (duplo clique maximiza, botão direito abre o menu da janela) e a borda de cima redimensiona. O Windows
// continua cuidando da moldura (bordas, sombra, cantos arredondados, Snap — no Windows 11, o maximizar mostra os
// layouts). No player e em tela cheia, a barra de título volta.
internal sealed partial class Player
{
    private const uint WM_NCCALCSIZE = 0x0083, WM_NCHITTEST = 0x0084, WM_NCMOUSEMOVE = 0x00A0, WM_NCLBUTTONDOWN = 0x00A1,
        WM_NCLBUTTONUP = 0x00A2, WM_NCLBUTTONDBLCLK = 0x00A3, WM_NCMOUSELEAVE = 0x02A2;
    private const int HTCLIENT = 1, HTCAPTION = 2, HTMINBUTTON = 8, HTMAXBUTTON = 9, HTTOP = 12, HTTOPLEFT = 13, HTTOPRIGHT = 14, HTCLOSE = 20;
    private const int CaptionButton = 46; // largura de cada botão (a do Windows)
    private const string IconMinimize = "", IconMaximize = "", IconRestore = "", IconCloseWindow = "";

    private int _captionHover, _captionPressed; // botão sob o mouse / apertado (HTMINBUTTON, HTMAXBUTTON, HTCLOSE; 0 = nenhum)
    private bool _captionTracking;

    [DllImport("user32.dll")] private static extern bool IsZoomed(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int GetSystemMetricsForDpi(int index, uint dpi);

    // Sem a barra de título? (navegador fora da tela cheia)
    private bool CustomFrame { get { lock (_lock) return _webMode && !_fullscreen; } }

    // O modo mudou (navegador/player, tela cheia): o Windows recalcula a moldura.
    private void RefreshFrame() =>
        Native.SetWindowPos(Hwnd, IntPtr.Zero, 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0004 | 0x0010 | SWP_FRAMECHANGED); // NOSIZE NOMOVE NOZORDER NOACTIVATE

    // Espessura da borda de redimensionar (e quanto a janela maximizada passa da tela).
    private int FrameThickness()
    {
        uint dpi = GetDpiForWindow(Hwnd);
        return GetSystemMetricsForDpi(33 /* SM_CYFRAME */, dpi) + GetSystemMetricsForDpi(92 /* SM_CXPADDEDBORDER */, dpi);
    }

    // Mensagens da moldura. null = não é nossa (segue o padrão).
    private IntPtr? FrameMessage(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case WM_NCCALCSIZE when wParam != IntPtr.Zero && CustomFrame:
            {
                // O Windows tira as bordas e a barra de título; a de cima volta a ser da janela. Maximizada, a janela
                // passa da tela pela espessura da borda: o topo desce isso, senão as abas ficariam cortadas.
                int top = Marshal.ReadInt32(lParam, 4);
                DefWindowProcW(hwnd, msg, wParam, lParam);
                Marshal.WriteInt32(lParam, 4, top + (IsZoomed(hwnd) ? FrameThickness() : 0));
                return IntPtr.Zero;
            }
            case WM_NCHITTEST when CustomFrame:
                return FrameHitTest(hwnd, wParam, lParam);
            case WM_NCMOUSEMOVE when CustomFrame:
                SetCaptionHover(IsCaptionButton((int)wParam) ? (int)wParam : 0);
                if (!_captionTracking)
                {
                    var track = new TRACKMOUSEEVENT { cbSize = Marshal.SizeOf<TRACKMOUSEEVENT>(), dwFlags = 0x2 | 0x10 /* TME_LEAVE | TME_NONCLIENT */, hwndTrack = hwnd };
                    _captionTracking = TrackMouseEvent(ref track);
                }
                return IsCaptionButton((int)wParam) ? IntPtr.Zero : null;
            case WM_NCMOUSELEAVE:
                _captionTracking = false;
                _captionPressed = 0;
                SetCaptionHover(0);
                return null;
            case WM_NCLBUTTONDOWN or WM_NCLBUTTONDBLCLK when CustomFrame && IsCaptionButton((int)wParam):
                // Os botões são nossos: o Windows não pode tratar o clique (desenharia os botões antigos por cima).
                _captionPressed = (int)wParam;
                WebDirty();
                return IntPtr.Zero;
            case WM_NCLBUTTONUP when CustomFrame && IsCaptionButton((int)wParam):
            {
                int pressed = _captionPressed;
                _captionPressed = 0;
                WebDirty();
                if (pressed != (int)wParam) return IntPtr.Zero; // apertou num, soltou em outro
                if (pressed == HTMINBUTTON) Native.ShowWindow(hwnd, 6 /* SW_MINIMIZE */);
                else if (pressed == HTMAXBUTTON) Native.ShowWindow(hwnd, IsZoomed(hwnd) ? 9 /* SW_RESTORE */ : 3 /* SW_MAXIMIZE */);
                else PostMessage(hwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
                return IntPtr.Zero;
            }
        }
        return null;
    }

    private static bool IsCaptionButton(int hit) => hit is HTMINBUTTON or HTMAXBUTTON or HTCLOSE;

    private void SetCaptionHover(int hit)
    {
        if (_captionHover == hit) return;
        _captionHover = hit;
        WebDirty();
    }

    // Onde o mouse está na moldura: bordas (do Windows), borda de cima, botões, espaço vazio da faixa (arrasta).
    private IntPtr FrameHitTest(IntPtr hwnd, IntPtr wParam, IntPtr lParam)
    {
        var point = new Native.POINT { X = LowWord(lParam), Y = HighWord(lParam) };
        ScreenToClient(hwnd, ref point);
        int x = point.X, y = point.Y, cw, ch;
        lock (_lock) (cw, ch) = (_clientW, _clientH);
        // Fora da área do app: as bordas dos lados e de baixo (do Windows). Dentro, só nós: o Windows ainda acha que
        // há uma barra de título ali e responderia com os botões dela.
        if (x < 0 || x >= cw || y < 0 || y >= ch) return DefWindowProcW(hwnd, WM_NCHITTEST, wParam, lParam);
        if (!IsZoomed(hwnd))
        {
            int border = FrameThickness();
            if (y < border) return x < border * 2 ? HTTOPLEFT : x >= cw - border * 2 ? HTTOPRIGHT : HTTOP;
        }
        if (CaptionButtonAt(x, y, cw) is int button) return button;
        if (y >= U(TabStrip)) return HTCLIENT;
        lock (_lock)
        {
            int hover = _webHoverIndex; // o teste abaixo mexe nele (é do desenho)
            var part = WebHitTest(x, y);
            _webHoverIndex = hover;
            return part == UiPart.TopBar ? HTCAPTION : HTCLIENT;
        }
    }

    // Botões no canto direito da faixa das abas (com ou sem _lock).
    private int? CaptionButtonAt(int x, int y, int cw)
    {
        if (y < 0 || y >= U(TabStrip) || x < cw - 3 * U(CaptionButton) || x >= cw) return null;
        return ((cw - 1 - x) / U(CaptionButton)) switch { 0 => HTCLOSE, 1 => HTMAXBUTTON, _ => HTMINBUTTON };
    }

    // Largura reservada aos botões na faixa das abas (0 com a barra de título do Windows).
    private int CaptionWidth() => CustomFrame ? 3 * U(CaptionButton) : 0;

    // Chave do desenho dos botões (muda quando algo neles muda).
    private string CaptionKey() => CustomFrame ? $"{_captionHover}{_captionPressed}{IsZoomed(Hwnd)}" : "";

    // Desenha os botões na faixa das abas (thread da tela, dentro do desenho das faixas).
    private void DrawCaptionButtons(UiCanvas c, int cw)
    {
        if (!CustomFrame) return;
        int w = U(CaptionButton), h = U(TabStrip), glyphH = c.TextHeight(_fontChevron);
        (int hit, string glyph)[] buttons = [(HTMINBUTTON, IconMinimize), (HTMAXBUTTON, IsZoomed(Hwnd) ? IconRestore : IconMaximize), (HTCLOSE, IconCloseWindow)];
        for (int i = 0; i < 3; i++)
        {
            var (hit, glyph) = buttons[i];
            int x = cw - (3 - i) * w;
            bool hover = _captionHover == hit, pressed = _captionPressed == hit && hover;
            if (hit == HTCLOSE && hover) c.Rect(x, 0, w, h, pressed ? 0x941A12u : 0xC42B1Cu, 1); // vermelho do Windows
            else if (hover) c.Rect(x, 0, w, h, White, pressed ? 0.06f : 0.1f);
            c.Text(glyph, _fontChevron, x + (w - c.TextWidth(glyph, _fontChevron)) / 2, (h - glyphH) / 2, White, hover ? 1f : 0.85f);
        }
    }

    // Autoteste: tela cheia (como a de um vídeo do site) e onde ficam os botões.
    public void TestFullscreen() => ToggleFullscreen();
    public (int min, int max, int close, int strip) TestCaptionPoints
    {
        get { int cw; lock (_lock) cw = _clientW; int w = U(CaptionButton); return (cw - 3 * w + w / 2, cw - 2 * w + w / 2, cw - w / 2, U(TabStrip)); }
    }
}
