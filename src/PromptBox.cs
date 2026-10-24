using System.Runtime.InteropServices;

namespace MKVision.Desktop;

// Janela da tecla G: uma aba por workflow do ComfyUI (ComfyClient.Workflows), cada uma com o prompt, o negativo e as
// LoRAs dela (liga/desliga e força, só para este pedido). Trocar de aba só troca o que a janela mostra — nada vai ao
// ComfyUI até o "Gerar". Os campos de texto aceitam várias linhas (Enter = linha nova); Ctrl+Enter ou "Gerar" envia,
// Esc cancela, Tab passa de um campo ao outro. Espera a resposta (a janela do player fica bloqueada, como no InputBox).
internal static class PromptBox
{
    // O que uma aba mostra (e guarda enquanto a janela está aberta).
    public sealed class Tab
    {
        public required string Label;
        public required bool CanScratch; // "Gerar do zero" (restaurar precisa de imagem)
        public string Prompt = "", Negative = "";
        public bool Scratch;
        public List<ComfyClient.LoraSetting> Loras = [];
        public List<string> LoraFiles = [];          // os arquivos que cada linha de LoRA pode escolher (da família do modelo)
        public ComfyClient.SamplerSetting? Sampler; // null = o workflow não tem sampler conhecido (campos desligados)
        public string Hint = "";                     // dica embaixo dos campos (ex.: as forças do restaurar)
        public bool SecondImage;                     // tem a imagem 2 (<image2>): escolhida num arquivo pelo botão
        public string SecondPath = "";
    }

    private const string ClassName = "MKVisionPromptBox";
    private const int IdOk = 1, IdCancel = 2, IdPrompt = 100, IdNegative = 101, IdScratch = 102, IdTab = 150,
        IdSteps = 170, IdCfg = 171, IdResolution = 172, IdSeed = 173, IdSeedFixed = 174, IdSeconds = 175, IdSecond = 176;
    private const int IdLora = 200; // + 3 por linha: caixa (liga/desliga), arquivo e força
    private static bool _registered;
    private static bool _done, _ok;
    private static IntPtr _window, _prompt, _negative, _scratch, _loraTitle, _loraStrengthTitle;
    private static IntPtr _steps, _cfg, _resolution, _seconds, _seed, _seedFixed, _hint, _secondButton, _secondLabel;
    private static readonly List<(IntPtr check, IntPtr file, IntPtr strength)> _rows = [];
    private const uint CB_ADDSTRING = 0x0143, CB_GETCURSEL = 0x0147, CB_RESETCONTENT = 0x014B, CB_SETCURSEL = 0x014E;
    private static IReadOnlyList<Tab> _tabs = [];
    private static string _find = "";        // último texto procurado (Ctrl+F; F3 = próximo)
    private static IntPtr _lastField;         // o campo de texto em que se estava (a busca é nele)
    private static int _current;
    private static readonly Native.WndProc Proc = WindowProc;

    [DllImport("user32.dll")] private static extern bool EnableWindow(IntPtr hwnd, bool enable);
    [DllImport("user32.dll")] private static extern bool IsDialogMessage(IntPtr hwnd, ref Native.MSG msg);
    [DllImport("user32.dll")] private static extern IntPtr SetFocus(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern IntPtr GetFocus();
    [DllImport("user32.dll")] private static extern IntPtr GetNextDlgTabItem(IntPtr dialog, IntPtr control, bool previous);
    [DllImport("user32.dll")] private static extern short GetKeyState(int key);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hwnd, int command);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool SetWindowTextW(IntPtr hwnd, string text);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextW(IntPtr hwnd, char[] text, int max);
    [DllImport("user32.dll")] private static extern int GetWindowTextLengthW(IntPtr hwnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr SendMessageW(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern IntPtr GetSysColorBrush(int index);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)] private static extern bool GetTextExtentPoint32W(IntPtr dc, string text, int length, out Native.POINT size);

    private const uint WM_KEYDOWN = 0x0100, BM_GETCHECK = 0x00F0, BM_SETCHECK = 0x00F1;
    private const int VK_RETURN = 0x0D, VK_ESCAPE = 0x1B, VK_TAB = 0x09, VK_CONTROL = 0x11, VK_F3 = 0x72;
    [DllImport("user32.dll")] private static extern bool MessageBeep(uint type);

    // Devolve a aba escolhida e o que ela tinha na hora do "Gerar" (second: o arquivo da imagem 2, nas abas que a têm);
    // null = cancelado. "tabs" volta com o que cada aba ficou (para lembrar na próxima vez).
    public static (int tab, string prompt, string negative, bool scratch, List<ComfyClient.LoraSetting> loras, ComfyClient.SamplerSetting? sampler, string? second)?
        Show(IntPtr owner, string title, IReadOnlyList<Tab> tabs, int selected)
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
        (_tabs, _current) = (tabs, Math.Clamp(selected, 0, tabs.Count - 1));
        double scale = Math.Max(1, GetDpiForWindow(owner) / 96.0);
        int S(int v) => (int)(v * scale);
        PlayerNative.GetWindowRect(owner, out var o);
        // Altura pela aba com mais LoRAs (as linhas a mais ficam escondidas nas outras).
        const int RowHeight = 30, Top = 48;
        int maxRows = tabs.Max(t => t.Loras.Count);
        int loraTop = Top + 468, loraEnd = maxRows > 0 ? loraTop + 26 + maxRows * RowHeight : loraTop;
        int settingsTop = loraEnd + 8, bottom = settingsTop + 62; // passos/CFG/resolução/seed e a dica embaixo
        int w = S(760), h = S(bottom + 136);
        const uint style = 0x00C00000 /* WS_CAPTION */ | 0x00080000 /* WS_SYSMENU */ | 0x10000000 /* WS_VISIBLE */;
        var hwnd = Native.CreateWindowEx(0x00000001 /* WS_EX_DLGMODALFRAME */, ClassName, title, style,
            o.Left + Math.Max(0, (o.Width - w) / 2), o.Top + Math.Max(0, (o.Height - h) / 3), w, h, owner, IntPtr.Zero, instance, IntPtr.Zero);
        PlayerNative.DarkTitleBar(hwnd);
        _window = hwnd;
        var font = UiCanvas.Font("Segoe UI", S(15));
        IntPtr Child(string type, string text, uint childStyle, int x, int y, int cw, int ch, int id, uint exStyle = 0)
        {
            var child = Native.CreateWindowEx(exStyle, type, text, 0x40000000 /* WS_CHILD */ | 0x10000000 /* WS_VISIBLE */ | childStyle,
                S(x), S(y), S(cw), S(ch), hwnd, id, instance, IntPtr.Zero);
            SendMessageW(child, 0x0030 /* WM_SETFONT */, font, 1);
            return child;
        }
        // Abas: botões de opção com cara de botão (um só fica apertado), cada uma da largura do nome (todas juntas cabem na
        // largura da janela; sobrando espaço, ele é dividido entre elas).
        var widths = tabs.Select(t => TextWidth(font, t.Label) / scale + 18).ToArray();
        double room = 716 - 6 * (tabs.Count - 1), total = widths.Sum();
        double extra = Math.Min(room - total, tabs.Count * 60.0) / tabs.Count; // negativo: aperta todas por igual
        for (int i = 0, x = 14; i < tabs.Count; i++)
        {
            int tabWidth = (int)Math.Max(60, total > room ? widths[i] * room / total : widths[i] + extra);
            var button = Child("BUTTON", tabs[i].Label, 0x0009 /* BS_AUTORADIOBUTTON */ | 0x1000 /* BS_PUSHLIKE */ | 0x00010000 /* WS_TABSTOP */ |
                (i == 0 ? 0x00020000u /* WS_GROUP */ : 0), x, 8, tabWidth, 32, IdTab + i);
            if (i == _current) SendMessageW(button, BM_SETCHECK, 1, 0);
            x += tabWidth + 6;
        }
        // Várias linhas, rolagem vertical, Enter = linha nova.
        const uint multi = 0x0004 /* ES_MULTILINE */ | 0x0040 /* ES_AUTOVSCROLL */ | 0x1000 /* ES_WANTRETURN */ |
                           0x00200000 /* WS_VSCROLL */ | 0x00010000 /* WS_TABSTOP */ | 0x00020000 /* WS_GROUP */;
        Child("STATIC", "Prompt", 0, 14, Top + 10, 700, 22, 0);
        _prompt = Child("EDIT", "", multi, 14, Top + 34, 716, 270, IdPrompt, 0x00000200 /* WS_EX_CLIENTEDGE */);
        Child("STATIC", "Negativo", 0, 14, Top + 314, 700, 22, 0);
        _negative = Child("EDIT", "", multi, 14, Top + 338, 716, 120, IdNegative, 0x00000200);
        // LoRAs: caixa = ligada (desmarcada = pulada, como o "ignorar" do ComfyUI), lista = o arquivo (qualquer um da pasta
        // que sirva no modelo da aba), campo = força.
        _loraTitle = Child("STATIC", "LoRAs (só neste pedido; o workflow salvo não muda) · escolha o arquivo na lista", 0, 14, loraTop, 600, 22, 0);
        _loraStrengthTitle = Child("STATIC", "Força", 0x0002 /* SS_RIGHT */, 610, loraTop, 120, 22, 0);
        _rows.Clear();
        for (int i = 0; i < maxRows; i++)
        {
            int y = loraTop + 26 + i * RowHeight;
            var check = Child("BUTTON", $"LoRA {i + 1}", 0x0003 /* BS_AUTOCHECKBOX */ | 0x00010000, 14, y, 86, 24, IdLora + i * 3);
            // Lista que só escolhe (não digita); a altura é a da lista aberta.
            var file = Child("COMBOBOX", "", 0x0003 /* CBS_DROPDOWNLIST */ | 0x00200000 /* WS_VSCROLL */ | 0x00010000, 104, y - 1, 532, 360, IdLora + i * 3 + 1);
            var strength = Child("EDIT", "", 0x0002 /* ES_RIGHT */ | 0x0080 /* ES_AUTOHSCROLL */ | 0x00010000, 646, y - 1, 84, 26, IdLora + i * 3 + 2, 0x00000200);
            _rows.Add((check, file, strength));
        }
        // Sampler: passos, CFG, resolução (Qwen 2.1) e seed (fixa = repete a mesma; desmarcada = sorteia a cada pedido).
        const uint field = 0x0002 /* ES_RIGHT */ | 0x0080 /* ES_AUTOHSCROLL */ | 0x00010000 /* WS_TABSTOP */;
        Child("STATIC", "Passos", 0, 14, settingsTop + 4, 46, 22, 0);
        _steps = Child("EDIT", "", field, 60, settingsTop, 46, 26, IdSteps, 0x00000200);
        Child("STATIC", "CFG", 0, 116, settingsTop + 4, 32, 22, 0);
        _cfg = Child("EDIT", "", field, 148, settingsTop, 48, 26, IdCfg, 0x00000200);
        Child("STATIC", "Resolução", 0, 206, settingsTop + 4, 68, 22, 0);
        _resolution = Child("EDIT", "", field, 274, settingsTop, 54, 26, IdResolution, 0x00000200);
        Child("STATIC", "Duração (s)", 0, 338, settingsTop + 4, 78, 22, 0);
        _seconds = Child("EDIT", "", field, 416, settingsTop, 44, 26, IdSeconds, 0x00000200);
        Child("STATIC", "Seed", 0, 470, settingsTop + 4, 34, 22, 0);
        _seed = Child("EDIT", "", field, 504, settingsTop, 156, 26, IdSeed, 0x00000200);
        _seedFixed = Child("BUTTON", "fixa", 0x0003 /* BS_AUTOCHECKBOX */ | 0x00010000, 670, settingsTop + 1, 60, 24, IdSeedFixed);
        _hint = Child("STATIC", "", 0, 14, settingsTop + 34, 716, 22, 0);
        // Do zero: só o texto, sem o quadro/foto da tela (o Qwen cria a imagem; o próximo G edita ela).
        _scratch = Child("BUTTON", "Gerar do zero (sem imagem de entrada: só o texto, imagem quadrada)", 0x0003 /* BS_AUTOCHECKBOX */ | 0x00010000,
            14, bottom + 10, 716, 24, IdScratch);
        // Imagem 2 (no lugar do "do zero", nas abas que a têm): o botão abre o Explorer; ao lado, o arquivo escolhido.
        _secondButton = Child("BUTTON", "Escolher a imagem 2...", 0x00010000, 14, bottom + 6, 170, 30, IdSecond);
        _secondLabel = Child("STATIC", "", 0x0080 /* SS_NOPREFIX */ | 0x4000 /* SS_ENDELLIPSIS */, 194, bottom + 12, 536, 22, 0);
        Child("STATIC", "Ctrl+Enter gera · Esc cancela · Ctrl+F procura (F3 próximo) · {a|b} sorteia", 0, 14, bottom + 54, 520, 22, 0);
        Child("BUTTON", "Gerar", 0x0001 /* BS_DEFPUSHBUTTON */ | 0x00010000, 548, bottom + 48, 86, 32, IdOk);
        Child("BUTTON", "Cancelar", 0x00010000, 640, bottom + 48, 90, 32, IdCancel);
        LoadTab(_current);
        SetFocus(_prompt);
        (_done, _ok) = (false, false);
        EnableWindow(owner, false);
        while (!_done && Native.GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            if (msg.message == WM_KEYDOWN && Key(msg.wParam)) continue;
            if (IsDialogMessage(hwnd, ref msg)) continue;
            Native.TranslateMessage(ref msg);
            Native.DispatchMessage(ref msg);
        }
        SaveTab(_current);
        EnableWindow(owner, true);
        Native.DestroyWindow(hwnd);
        UiCanvas.DeleteFont(font);
        PlayerNative.SetForegroundWindow(owner);
        var tab = tabs[_current];
        return _ok ? (_current, tab.Prompt.Trim(), tab.Negative.Trim(), tab.Scratch && tab.CanScratch, tab.Loras, tab.Sampler,
            tab.SecondImage ? tab.SecondPath : null) : null;
    }

    // Largura do texto na fonte (pixels da tela).
    private static int TextWidth(IntPtr font, string text)
    {
        var dc = CreateCompatibleDC(IntPtr.Zero);
        var old = SelectObject(dc, font);
        GetTextExtentPoint32W(dc, text, text.Length, out var size);
        SelectObject(dc, old);
        DeleteDC(dc);
        return size.X;
    }

    // "Gerar": na aba com a imagem 2, só com ela escolhida (senão avisa e fica). true = pode fechar.
    private static bool CanGenerate()
    {
        var tab = _tabs[_current];
        if (!tab.SecondImage || File.Exists(tab.SecondPath)) return true;
        MessageBeep(0x30 /* MB_ICONEXCLAMATION */);
        SetWindowTextW(_secondLabel, tab.SecondPath.Length > 0 ? "O arquivo da imagem 2 não existe mais: escolha de novo" : "Escolha a imagem 2 antes de gerar");
        SetFocus(_secondButton);
        return false;
    }

    // O botão da imagem 2: o "Abrir" do Windows (na pasta da última escolhida).
    private static void ChooseSecond()
    {
        var tab = _tabs[_current];
        const int max = 2048;
        var buffer = Marshal.AllocHGlobal(max * 2);
        try
        {
            Marshal.WriteInt16(buffer, 0);
            string? folder = tab.SecondPath.Length > 0 ? Path.GetDirectoryName(tab.SecondPath) : null;
            var dialog = new PlayerNative.OPENFILENAME
            {
                lStructSize = Marshal.SizeOf<PlayerNative.OPENFILENAME>(),
                hwndOwner = _window,
                lpstrFilter = "Imagens\0*.jpg;*.jpeg;*.jfif;*.png;*.bmp;*.gif;*.tif;*.tiff;*.webp;*.heic;*.heif;*.avif;*.jxr\0Todos os arquivos\0*.*\0\0",
                lpstrFile = buffer,
                nMaxFile = max,
                lpstrInitialDir = folder is not null && Directory.Exists(folder) ? folder : null,
                lpstrTitle = "MK Vision — a imagem 2 (<image2>)",
                Flags = PlayerNative.OFN_EXPLORER | PlayerNative.OFN_FILEMUSTEXIST | PlayerNative.OFN_PATHMUSTEXIST | PlayerNative.OFN_NOCHANGEDIR
            };
            string? path = TestSecondPath ?? (PlayerNative.GetOpenFileName(ref dialog) ? Marshal.PtrToStringUni(buffer) : null);
            if (path is null) return;
            tab.SecondPath = path;
            ShowSecond(tab);
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private static void ShowSecond(Tab tab) =>
        SetWindowTextW(_secondLabel, tab.SecondPath.Length == 0 ? "Nenhuma ainda · <image1> = a imagem da tela, <image2> = esta" : $"<image2> = {Path.GetFileName(tab.SecondPath)}");

    // Autoteste: o arquivo que o botão da imagem 2 "escolhe" (sem a janela do Windows).
    public static string? TestSecondPath;

    // Número de um campo (ponto ou vírgula); inválido = o de antes.
    private static double Read(IntPtr edit, double before)
    {
        string text = Text(edit).Trim().Replace(',', '.');
        return double.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double v) ? v : before;
    }

    // A aba na tela → a lista dela (textos, "do zero", LoRAs ligadas e forças).
    private static void SaveTab(int index)
    {
        var tab = _tabs[index];
        tab.Prompt = Text(_prompt).Replace("\r\n", "\n");
        tab.Negative = Text(_negative).Replace("\r\n", "\n");
        if (tab.CanScratch) tab.Scratch = SendMessageW(_scratch, BM_GETCHECK, 0, 0) == 1;
        if (tab.Sampler is { } s)
            tab.Sampler = s with
            {
                Steps = (int)Math.Round(Math.Clamp(Read(_steps, s.Steps), 1, 200)),
                Cfg = Math.Round(Math.Clamp(Read(_cfg, s.Cfg), 0, 30), 2),
                Resolution = s.Resolution > 0 ? (int)Math.Clamp(Read(_resolution, s.Resolution), s.Seconds > 0 ? 240 : 256, s.Seconds > 0 ? 1080 : 4096) / 16 * 16 : 0,
                Seconds = s.Seconds > 0 ? Math.Round(Math.Clamp(Read(_seconds, s.Seconds), 1, 10), 2) : 0,
                Seed = long.TryParse(Text(_seed).Trim(), out long seed) && seed >= 0 ? seed : s.Seed,
                FixedSeed = SendMessageW(_seedFixed, BM_GETCHECK, 0, 0) == 1
            };
        tab.Loras = tab.Loras.Select((l, i) => l with
        {
            On = SendMessageW(_rows[i].check, BM_GETCHECK, 0, 0) == 1,
            File = Choices(tab, l) is var choices && (int)SendMessageW(_rows[i].file, CB_GETCURSEL, 0, 0) is var pick && pick >= 0 && pick < choices.Count
                ? choices[pick] : l.File,
            Strength = double.TryParse(Text(_rows[i].strength).Trim().Replace(',', '.'), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out double s) ? Math.Clamp(s, -5, 5) : l.Strength // inválido: o de antes
        }).ToList();
    }

    // A lista de uma aba → a tela (as linhas de LoRA que sobram ficam escondidas).
    private static void LoadTab(int index)
    {
        var tab = _tabs[index];
        SetWindowTextW(_prompt, Lines(tab.Prompt));
        SetWindowTextW(_negative, Lines(tab.Negative));
        for (int i = 0; i < _rows.Count; i++)
        {
            bool shown = i < tab.Loras.Count;
            ShowWindow(_rows[i].check, shown ? 5 /* SW_SHOW */ : 0 /* SW_HIDE */);
            ShowWindow(_rows[i].file, shown ? 5 : 0);
            ShowWindow(_rows[i].strength, shown ? 5 : 0);
            if (!shown) continue;
            var lora = tab.Loras[i];
            SendMessageW(_rows[i].check, BM_SETCHECK, lora.On ? 1 : 0, 0);
            SendMessageW(_rows[i].file, CB_RESETCONTENT, 0, 0);
            var choices = Choices(tab, lora);
            foreach (var file in choices)
            {
                var name = Marshal.StringToHGlobalUni(file.EndsWith(".safetensors", StringComparison.OrdinalIgnoreCase) ? file[..^12] : file);
                SendMessageW(_rows[i].file, CB_ADDSTRING, 0, name);
                Marshal.FreeHGlobal(name);
            }
            SendMessageW(_rows[i].file, CB_SETCURSEL, choices.IndexOf(lora.File), 0);
            SetWindowTextW(_rows[i].strength, Number(lora.Strength));
        }
        ShowWindow(_loraTitle, tab.Loras.Count > 0 ? 5 : 0);
        ShowWindow(_loraStrengthTitle, tab.Loras.Count > 0 ? 5 : 0);
        SendMessageW(_scratch, BM_SETCHECK, tab.CanScratch && tab.Scratch ? 1 : 0, 0);
        EnableWindow(_scratch, tab.CanScratch);
        ShowWindow(_scratch, tab.SecondImage ? 0 : 5);
        ShowWindow(_secondButton, tab.SecondImage ? 5 : 0);
        ShowWindow(_secondLabel, tab.SecondImage ? 5 : 0);
        if (tab.SecondImage) ShowSecond(tab);
        var s = tab.Sampler;
        SetWindowTextW(_steps, s is null ? "" : s.Steps.ToString());
        SetWindowTextW(_cfg, s is null ? "" : Number(s.Cfg));
        SetWindowTextW(_resolution, s is { Resolution: > 0 } ? s.Resolution.ToString() : "—");
        SetWindowTextW(_seconds, s is { Seconds: > 0 } ? Number(s.Seconds) : "—");
        EnableWindow(_seconds, s is { Seconds: > 0 });
        SetWindowTextW(_seed, s is null ? "" : s.Seed.ToString());
        SendMessageW(_seedFixed, BM_SETCHECK, s is { FixedSeed: true } ? 1 : 0, 0);
        foreach (var edit in new[] { _steps, _cfg, _seed, _seedFixed }) EnableWindow(edit, s is not null);
        EnableWindow(_resolution, s is { Resolution: > 0 });
        SetWindowTextW(_hint, tab.Hint);
    }

    // Teclas que os campos de várias linhas engoliriam: Ctrl+Enter, Esc, Tab, Ctrl+A; e a busca (Ctrl+F, F3).
    // true = é desta janela.
    private static bool Key(IntPtr wParam)
    {
        bool ctrl = GetKeyState(VK_CONTROL) < 0;
        var focus = GetFocus();
        bool inField = focus == _prompt || focus == _negative;
        if (inField) _lastField = focus;
        var field = inField ? focus : _lastField != IntPtr.Zero ? _lastField : _prompt;
        switch ((int)wParam)
        {
            case 'F' when ctrl:
                if (InputBox.Show(_window, "Procurar no prompt", "Texto a procurar (F3 = o próximo):", _find) is { } term) _find = term;
                SetFocus(field);
                if (_find.Length > 0) FindNext(field);
                return true;
            case VK_F3:
                SetFocus(field);
                if (_find.Length > 0) FindNext(field);
                return true;
            case VK_RETURN when ctrl:
                if (CanGenerate()) (_ok, _done) = (true, true);
                return true;
            case VK_ESCAPE when SendMessageW(focus, 0x0157 /* CB_GETDROPPEDSTATE */, 0, 0) != 0:
                return false; // lista de LoRAs aberta: o Esc só fecha a lista
            case VK_ESCAPE:
                _done = true;
                return true;
            case VK_TAB when !ctrl && inField: // os campos de várias linhas engoliriam o Tab: passa ao próximo/anterior
                SetFocus(GetNextDlgTabItem(_window, focus, GetKeyState(0x10 /* Shift */) < 0));
                return true;
            case 'A' when ctrl && inField:
                SendMessageW(focus, 0x00B1 /* EM_SETSEL */, 0, -1);
                return true;
        }
        return false;
    }

    // Seleciona a próxima ocorrência de _find depois da seleção atual (no fim, volta ao começo) e rola até ela.
    private static void FindNext(IntPtr edit)
    {
        string text = Text(edit);
        long selection = SendMessageW(edit, 0x00B0 /* EM_GETSEL */, IntPtr.Zero, IntPtr.Zero).ToInt64();
        int from = (int)((selection >> 16) & 0xffff);
        int at = text.IndexOf(_find, Math.Min(from, text.Length), StringComparison.OrdinalIgnoreCase);
        if (at < 0) at = text.IndexOf(_find, StringComparison.OrdinalIgnoreCase);
        if (at < 0)
        {
            MessageBeep(0x30 /* MB_ICONEXCLAMATION */);
            return;
        }
        SendMessageW(edit, 0x00B1 /* EM_SETSEL */, at, at + _find.Length);
        SendMessageW(edit, 0x00B7 /* EM_SCROLLCARET */, 0, 0);
    }

    // Os arquivos de uma linha: os da família do modelo, mais o atual se não estiver entre eles (some da pasta, ou de
    // família desconhecida), para a escolha de antes nunca sumir.
    private static List<string> Choices(Tab tab, ComfyClient.LoraSetting lora) =>
        tab.LoraFiles.Contains(lora.File, StringComparer.OrdinalIgnoreCase) || lora.File.Length == 0 ? tab.LoraFiles : [lora.File, .. tab.LoraFiles];

    private static string Text(IntPtr edit)
    {
        int n = GetWindowTextLengthW(edit);
        var buffer = new char[n + 1];
        n = GetWindowTextW(edit, buffer, buffer.Length);
        return new string(buffer, 0, n);
    }

    private static string Number(double value) => value.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);

    // O campo de texto do Windows só quebra linha com \r\n.
    private static string Lines(string text) => text.Replace("\r\n", "\n").Replace("\n", "\r\n");

    private static IntPtr WindowProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case 0x0111: // WM_COMMAND
                int id = (int)wParam & 0xffff;
                if (id == IdOk && CanGenerate()) (_ok, _done) = (true, true);
                else if (id == IdCancel) _done = true;
                else if (id == IdSecond) ChooseSecond();
                else if (id >= IdTab && id < IdTab + _tabs.Count && id - IdTab != _current)
                {
                    // Troca de aba: só a janela muda (guarda a aba que sai, mostra a que entra).
                    SaveTab(_current);
                    _current = id - IdTab;
                    LoadTab(_current);
                }
                return IntPtr.Zero;
            case 0x0010: // WM_CLOSE
                _done = true;
                return IntPtr.Zero;
        }
        return Native.DefWindowProc(hwnd, msg, wParam, lParam);
    }
}
