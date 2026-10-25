using System.Runtime.InteropServices;
using static MKVision.Desktop.PlayerNative;

namespace MKVision.Desktop;

// Painel de ajustes do player (tecla A), em 3 abas: Imagem (perfil, nitidez, limpeza, força), Câmera lenta
// (liga/desliga, desempenho e as opções da IA de quadros, com a situação ao vivo) e Zoom (potência da IA).
// Janelinha escura presa à janela do player: fica por cima dela, também em tela cheia, sem roubar o teclado ao abrir.
// Os controles são posicionados por linhas (Layout): cada aba mostra só as suas e a janela acompanha a altura.
internal sealed class PlayerPanel : IDisposable
{
    private sealed class Slider
    {
        public required string Label;
        public required int Min, Max;
        public required Func<Settings, float> Get;
        public required Action<Settings, float> Set;
        public required Func<int, float> FromPos;
        public required Func<float, int> ToPos;
        public required Func<float, string> Format;
        public IntPtr Track, Value;
    }

    // Uma linha do painel: em que aba aparece (-1 = sempre: abas no topo e rodapé), altura e seus controles
    // (posição relativa ao topo da linha).
    private sealed class Row
    {
        public required int Page;
        public required int Height;
        public bool Advanced;
        public readonly List<(IntPtr hwnd, int x, int dy, int w, int h)> Items = [];
    }

    // Controles desenhados pelo painel: abas, botões de escolha (perfil, desempenho), "Avançado" e a caixa de aviso.
    private enum Look { Tab, Chip, Link, Info }
    private sealed class Drawn
    {
        public required Look Look;
        public required string Text;
        public Func<bool> On = () => false;
    }

    private const string ClassName = "MKVisionPlayerPanel";
    private const int IdProfile = 100, IdSmooth = 200, IdCompare = 201, IdEnsemble = 202, IdCrisp = 203, IdTrained = 204,
        IdPerformance = 300, IdTab = 400, IdAdvanced = 410, IdColorReset = 420, IdRtx = 205, IdPhotoModel = 430, IdPhotoSkin = 206, IdPhotoFullRes = 207, IdPhotoFaceClean = 208;
    private const int Width = 400, Inner = 372, Left = 14;
    // Cores (BGR): fundo #202124, texto #E8EAED, secundário #9AA0A6, apagado #6B6F74, destaque #8AB4F8,
    // botão escolhido #2B3A55, borda #5F6368, divisória #3C4043, caixa de aviso #2A2B2E.
    private const uint Background = 0x00242120, Text = 0x00EDEAE8, Secondary = 0x00A6A09A, Muted = 0x00746F6B,
        Accent = 0x00F8B48A, ChipFill = 0x00553A2B, Border = 0x0068635F, Divider = 0x0043403C, InfoFill = 0x002E2B2A;
    private const uint BS_OWNERDRAW = 0xB, SS_OWNERDRAW = 0xD, WM_DRAWITEM = 0x002B;
    private const uint DT_CENTER = 0x1, DT_VCENTER = 0x4, DT_WORDBREAK = 0x10, DT_SINGLELINE = 0x20, DT_NOPREFIX = 0x800;
    private static readonly string[] PowerText = ["", "só nitidez", "IA rápida", "IA qualidade", "IA qualidade", "IA dupla (pesada)"];
    // Abas na ordem da tela; o número de cada uma (o que fica guardado) é o da página: 0 imagem, 3 cor e luz,
    // 1 câmera lenta, 2 zoom (a "Cor e luz" veio depois).
    private static readonly string[] TabNames = ["Imagem", "Cor e luz", "Câmera lenta", "Ampliação", "Fotos"];
    private static readonly int[] TabWidths = [72, 80, 104, 88, 56]; // pela largura dos nomes (somam a largura do painel)
    private static readonly int[] TabPages = [0, 3, 1, 2, 4];
    private static readonly string[] PerformanceText = ["Máxima", "Equilibrado", "Leve (VR)"];

    private static readonly Native.WndProc StaticProc = Dispatch;
    private static readonly Dictionary<IntPtr, PlayerPanel> Panels = [];
    private static PlayerPanel? _creating;
    private static bool _registered;
    private static readonly IntPtr BackgroundBrush = CreateSolidBrush(Background), AccentBrush = CreateSolidBrush(Accent),
        ChipBrush = CreateSolidBrush(ChipFill), DividerBrush = CreateSolidBrush(Divider), InfoBrush = CreateSolidBrush(InfoFill);

    [StructLayout(LayoutKind.Sequential)]
    private struct DRAWITEMSTRUCT
    {
        public uint CtlType, CtlID, itemID, itemAction, itemState;
        public IntPtr hwndItem, hDC;
        public Native.RECT rcItem;
        public IntPtr itemData;
    }

    [DllImport("user32.dll")] private static extern int FillRect(IntPtr dc, ref Native.RECT rect, IntPtr brush);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "DrawTextW")] private static extern int DrawText(IntPtr dc, string text, int length, ref Native.RECT rect, uint format);
    [DllImport("user32.dll")] private static extern bool InvalidateRect(IntPtr hwnd, IntPtr rect, bool erase);
    [DllImport("user32.dll")] private static extern bool RedrawWindow(IntPtr hwnd, IntPtr rect, IntPtr region, uint flags);
    [DllImport("gdi32.dll")] private static extern int SetBkMode(IntPtr dc, int mode);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll")] private static extern IntPtr CreatePen(int style, int width, uint color);
    [DllImport("gdi32.dll")] private static extern bool RoundRect(IntPtr dc, int left, int top, int right, int bottom, int w, int h);

    public IntPtr Hwnd { get; }

    private readonly IntPtr _owner;
    private readonly PlayerSettings _settings;
    private readonly Action _changed;
    private readonly Func<string> _status, _zoomStatus, _photoStatus;
    private readonly IntPtr _font, _bold, _accentPen, _borderPen;
    private readonly IntPtr[] _profiles = new IntPtr[PlayerProfile.All.Length];
    private readonly IntPtr[] _tabs = new IntPtr[TabNames.Length];
    private readonly IntPtr _smooth, _compare, _ensemble, _crisp, _trained, _profileHeader, _advancedLink, _slowStatus, _rtx, _rtxStatus, _photoStatusBox, _photoSkin, _photoFullRes, _photoFaceClean;
    private readonly IntPtr[] _photoModels = new IntPtr[PhotoAi.Models.Length];
    private readonly IntPtr[] _performance = new IntPtr[PlayerSettings.PerformanceNames.Length];
    private readonly List<Slider> _sliders = [];
    private readonly List<Row> _rows = [];
    private readonly Dictionary<IntPtr, Drawn> _drawn = [];
    private readonly HashSet<IntPtr> _headers = [], _muted = [];
    private readonly double _scale;
    private readonly uint _style;
    private bool _refreshing, _advanced;

    public PlayerPanel(IntPtr owner, PlayerSettings settings, Action changed, Func<string> status, Func<string> zoomStatus, Func<string> photoStatus)
    {
        _owner = owner;
        _settings = settings;
        _changed = changed;
        _status = status;
        _zoomStatus = zoomStatus;
        _photoStatus = photoStatus;
        _scale = Math.Max(1, GetDpiForWindow(owner) / 96.0);
        var instance = Native.GetModuleHandle(null);
        if (!_registered)
        {
            var controls = new INITCOMMONCONTROLSEX { dwSize = Marshal.SizeOf<INITCOMMONCONTROLSEX>(), dwICC = 0x4 }; // controles deslizantes
            InitCommonControlsEx(ref controls);
            var windowClass = new Native.WNDCLASSEX
            {
                cbSize = (uint)Marshal.SizeOf<Native.WNDCLASSEX>(),
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(StaticProc),
                hInstance = instance,
                hCursor = LoadCursor(IntPtr.Zero, IDC_ARROW),
                hbrBackground = BackgroundBrush,
                lpszClassName = ClassName
            };
            Native.RegisterClassEx(ref windowClass);
            _registered = true;
        }
        _font = CreateFont(-S(14), 0, 0, 0, 400, 0, 0, 0, 1, 0, 0, 5, 0, "Segoe UI");
        _bold = CreateFont(-S(14), 0, 0, 0, 600, 0, 0, 0, 1, 0, 0, 5, 0, "Segoe UI");
        _accentPen = CreatePen(0, S(1), Accent);
        _borderPen = CreatePen(0, S(1), Border);

        _style = WS_CAPTION | WS_SYSMENU | 0x02000000; // WS_CLIPCHILDREN: o fundo não pinta por cima dos controles
        var rect = new Native.RECT { Right = S(Width), Bottom = S(600) };
        AdjustWindowRectEx(ref rect, _style, false, WS_EX_TOOLWINDOW);
        _creating = this;
        Hwnd = Native.CreateWindowEx(WS_EX_TOOLWINDOW, ClassName, "Ajustes — MK Vision", _style, 0, 0, rect.Width, rect.Height,
            owner, IntPtr.Zero, instance, IntPtr.Zero);
        _creating = null;
        if (Hwnd == IntPtr.Zero) throw new InvalidOperationException($"não consegui criar o painel ({Marshal.GetLastWin32Error()})");
        lock (Panels) Panels[Hwnd] = this;
        DarkTitleBar(Hwnd);

        // ---- Abas (sempre) ----
        var row = AddRow(-1, 34);
        for (int i = 0; i < _tabs.Length; i++)
        {
            int page = TabPages[i];
            _tabs[i] = Draw(row, Look.Tab, TabNames[i], TabWidths[..i].Sum(), 0, TabWidths[i], 34, IdTab + i, () => _settings.PanelTab == page);
        }
        Line(AddRow(-1, 11), 0, 0, Width);

        // ---- Imagem ----
        _profileHeader = Header(AddRow(0, 22), "Perfil (P)");
        row = AddRow(0, 36);
        int[] chipWidths = [70, 104, 88, 58];
        for (int i = 0, x = Left; i < _profiles.Length; x += chipWidths[i] + 4, i++)
        {
            var profile = PlayerProfile.All[i];
            _profiles[i] = Draw(row, Look.Chip, profile.Name, x, 0, chipWidths[i], 26, IdProfile + i, () => _settings.CurrentProfile == profile);
        }
        Header(AddRow(0, 22), "Nitidez");
        AddSlider(0, "Micro-detalhe", 0, 60, s => s.Detail, (s, v) => s.Detail = v, p => p / 20f, v => (int)Math.Round(v * 20), v => $"{v * 100:0}%");
        AddSlider(0, "Textura da pele", 0, 40, s => s.Texture, (s, v) => s.Texture = v, p => p / 20f, v => (int)Math.Round(v * 20), v => $"{v * 100:0}%");
        Header(AddRow(0, 22), "Limpeza");
        AddSlider(0, "Ruído / granulado", 0, 10, s => s.Threshold, (s, v) => s.Threshold = v, p => p, v => (int)Math.Round(v), v => $"{v:0}");
        AddSlider(0, "Blocos e faixas", 0, 10, s => s.Deband, (s, v) => s.Deband = v, p => p, v => (int)Math.Round(v), v => $"{v:0}");
        AddSlider(0, "Força geral (+/−)", 0, 16, s => s.Strength, (s, v) => s.Strength = v, p => p * 0.25f, v => (int)Math.Round(v * 4), v => $"{v * 100:0}%");
        _advancedLink = Draw(AddRow(0, 28), Look.Link, "Avançado (raios)", Left, 0, Inner, 24, IdAdvanced);
        AddSlider(0, "Raio do micro-detalhe", 3, 25, s => s.DetailRadius, (s, v) => s.DetailRadius = v, p => p / 10f, v => (int)Math.Round(v * 10), v => $"{v:0.0} px", advanced: true);
        AddSlider(0, "Raio da textura", 3, 16, s => s.TextureRadius, (s, v) => s.TextureRadius = v, p => p / 2f, v => (int)Math.Round(v * 2), v => $"{v:0.0} px", advanced: true);

        // ---- Cor e luz ----
        static string Signed(float v) => v > 0.5f ? $"+{v:0}" : v < -0.5f ? $"−{-v:0}" : "0";
        Header(AddRow(3, 22), "Luz");
        AddSlider(3, "Brilho (meios-tons)", -20, 20, s => s.Brightness, (s, v) => s.Brightness = v, p => p * 0.025f, v => (int)Math.Round(v * 40), v => Signed(v * 200));
        AddSlider(3, "Contraste", -20, 20, s => s.Contrast, (s, v) => s.Contrast = v, p => p * 0.025f, v => (int)Math.Round(v * 40), v => Signed(v * 200));
        AddSlider(3, "Clareza (relevo, profundidade)", 0, 20, s => s.Clarity, (s, v) => s.Clarity = v, p => p * 0.05f, v => (int)Math.Round(v * 20), v => Signed(v * 100));
        Header(AddRow(3, 22), "Cor");
        AddSlider(3, "Vibração (cores fracas; poupa a pele)", 0, 20, s => s.Vibrance, (s, v) => s.Vibrance = v, p => p * 0.05f, v => (int)Math.Round(v * 20), v => Signed(v * 100));
        AddSlider(3, "Saturação (todas as cores)", -20, 20, s => s.Saturation, (s, v) => s.Saturation = v, p => p * 0.05f, v => (int)Math.Round(v * 20), v => Signed(v * 100));
        Draw(AddRow(3, 36), Look.Chip, "Voltar ao normal", Left, 4, 150, 26, IdColorReset);
        Draw(AddRow(3, 72), Look.Info, "Para o Quest 2 (OWL3D): a tela do óculos apaga um pouco as cores — vibração +30 e contraste +10 costumam compensar.", Left, 0, Inner, 64, 0);

        // ---- Câmera lenta ----
        row = AddRow(1, 32);
        _smooth = Put(row, "BUTTON", "Criar quadros com IA abaixo de 1x", BS_AUTOCHECKBOX | WS_TABSTOP, Left, 0, Inner, 24, IdSmooth);
        Header(AddRow(1, 22), "Desempenho");
        row = AddRow(1, 36);
        for (int i = 0; i < _performance.Length; i++)
        {
            int index = i;
            _performance[i] = Draw(row, Look.Chip, PerformanceText[i], Left + i * 112, 0, 108, 26, IdPerformance + i, () => _settings.Performance == index);
        }
        Header(AddRow(1, 24), "Qualidade da IA");
        _ensemble = Put(AddRow(1, 26), "BUTTON", "Análise dupla — menos deformação (E)", BS_AUTOCHECKBOX | WS_TABSTOP, Left, 0, Inner, 24, IdEnsemble);
        _crisp = Put(AddRow(1, 26), "BUTTON", "Sem fantasma onde algo cobre (N)", BS_AUTOCHECKBOX | WS_TABSTOP, Left, 0, Inner, 24, IdCrisp);
        _trained = Put(AddRow(1, 32), "BUTTON", "IA treinada com seus vídeos (M)", BS_AUTOCHECKBOX | WS_TABSTOP, Left, 0, Inner, 24, IdTrained);
        _slowStatus = Draw(AddRow(1, 50), Look.Info, "", Left, 0, Inner, 42, 0);

        // ---- Ampliação ----
        _rtx = Put(AddRow(2, 28), "BUTTON", "RTX Video da NVIDIA (R)", BS_AUTOCHECKBOX | WS_TABSTOP, Left, 0, Inner, 24, IdRtx);
        _rtxStatus = Draw(AddRow(2, 62), Look.Info, "", Left, 0, Inner, 54, 0);
        Header(AddRow(2, 22), "Potência da nossa IA (1–5)");
        AddSlider(2, "Nível", 1, 5, s => s.Power, (s, v) => s.Power = (int)v, p => p, v => (int)v, v => $"{v:0} · {PowerText[(int)v]}", valueWidth: 170);
        row = AddRow(2, 26);
        _muted.Add(Put(row, "STATIC", "Rápida", 0, Left, -6, 120, 20, 0));
        _muted.Add(Put(row, "STATIC", "Máxima", SS_RIGHT, Left + Inner - 120, -6, 120, 20, 0));
        Draw(AddRow(2, 72), Look.Info, "Usada com zoom (roda do mouse) e quando o RTX Video está desligado. No modo Leve (VR) usa sempre a IA rápida.", Left, 0, Inner, 64, 0);

        // ---- Fotos (IA pesada, uma vez por foto) ----
        Header(AddRow(4, 22), "IA para fotos (amplia 4x, uma vez por foto)");
        for (int i = 0; i < _photoModels.Length; i++)
        {
            if (i % 4 == 0) row = AddRow(4, 34);
            int index = i;
            _photoModels[i] = Draw(row, Look.Chip, PhotoAi.Models[i].Label, Left + i % 4 * 94, 0, 88, 26, IdPhotoModel + i, () => _settings.PhotoAi == index);
        }
        Draw(AddRow(4, 204), Look.Info, "Natural — Real-ESRGAN: limpa compressão e ruído; às vezes alisa a pele. Nítida — UltraSharp: mais textura; " +
            "em foto muito comprimida pode inventar formas. Máxima — a mais realista, bem mais lenta.\nRosto / Rosto nítido — FaceUp: treinados só com " +
            "rostos em close (pele, cílios, cabelo); lentos como a Máxima. Máxima+ — a Máxima conferida também espelhada: menos defeitos, o dobro do tempo.\n" +
            "~6 s numa foto de 1080x1350 nas rápidas; a 1ª vez de cada uma leva uns minutos.", Left, 0, Inner, 196, 0);
        _photoFullRes = Put(AddRow(4, 28), "BUTTON", "Fotos grandes em resolução cheia (bem mais lento)", BS_AUTOCHECKBOX | WS_TABSTOP, Left, 0, Inner, 24, IdPhotoFullRes);
        _photoFaceClean = Put(AddRow(4, 28), "BUTTON", "Rosto: limpar a compressão antes (~2x o tempo)", BS_AUTOCHECKBOX | WS_TABSTOP, Left, 0, Inner, 24, IdPhotoFaceClean);
        Header(AddRow(4, 22), "Na foto ampliada");
        AddSlider(4, "Realce de textura (pele, digitais, poros)", 0, 20, s => s.PhotoTexture, (s, v) => s.PhotoTexture = v, p => p * 0.05f, v => (int)Math.Round(v * 20), v => $"{v * 100:0}");
        AddSlider(4, "Grão (contra o efeito plástico)", 0, 20, s => s.PhotoGrain, (s, v) => s.PhotoGrain = v, p => p * 0.05f, v => (int)Math.Round(v * 20), v => $"{v * 100:0}");
        _photoSkin = Put(AddRow(4, 30), "BUTTON", "Mais forte na pele (no fundo, 40%)", BS_AUTOCHECKBOX | WS_TABSTOP, Left, 0, Inner, 24, IdPhotoSkin);
        Header(AddRow(4, 22), "Luz e cor (tecla Y)");
        AddSlider(4, "Exposição (0 = só a cor da luz)", 0, 20, s => s.ToneExposure, (s, v) => s.ToneExposure = v, p => p * 0.05f, v => (int)Math.Round(v * 20), v => $"{v * 100:0}%");
        _photoStatusBox = Draw(AddRow(4, 70), Look.Info, "", Left, 0, Inner, 62, 0);
        Header(AddRow(4, 26), "I melhora · U liga/desliga · T original/ComfyUI · Y luz e cor · só na memória", muted: true);

        // ---- Rodapé (sempre) ----
        Line(AddRow(-1, 10), 0, 2, Width);
        _compare = Put(AddRow(-1, 28), "BUTTON", "Comparar sem ajustes (B)", BS_AUTOCHECKBOX | WS_TABSTOP, Left, 0, Inner, 24, IdCompare);
        Header(AddRow(-1, 28), "P perfil · +/− força · 1–5 potência · D nitidez · A fecha", muted: true);

        SetTimer(Hwnd, 1, 400, IntPtr.Zero); // situação da câmera lenta
        Layout();
        Refresh();
    }

    private int S(double value) => (int)Math.Round(value * _scale);

    private Row AddRow(int page, int height, bool advanced = false)
    {
        var row = new Row { Page = page, Height = height, Advanced = advanced };
        _rows.Add(row);
        return row;
    }

    private IntPtr Put(Row row, string type, string text, uint style, int x, int dy, int w, int h, int id)
    {
        var control = Native.CreateWindowEx(0, type, text, WS_CHILD | style, S(x), 0, S(w), S(h),
            Hwnd, id, Native.GetModuleHandle(null), IntPtr.Zero);
        SendMessage(control, WM_SETFONT, _font, 1);
        row.Items.Add((control, x, dy, w, h));
        return control;
    }

    private IntPtr Draw(Row row, Look look, string text, int x, int dy, int w, int h, int id, Func<bool>? on = null)
    {
        var control = Put(row, look == Look.Info ? "STATIC" : "BUTTON", text, look == Look.Info ? SS_OWNERDRAW : BS_OWNERDRAW, x, dy, w, h, id);
        _drawn[control] = new Drawn { Look = look, Text = text, On = on ?? (() => false) };
        return control;
    }

    private IntPtr Header(Row row, string text, bool muted = false)
    {
        var label = Put(row, "STATIC", text, 0, Left, 0, Inner, 20, 0);
        (muted ? _muted : _headers).Add(label);
        return label;
    }

    private void Line(Row row, int x, int dy, int w) => _headers.Add(Put(row, "STATIC", "", 0, x, dy, w, 1, -2));

    private void AddSlider(int page, string label, int min, int max, Func<Settings, float> get, Action<Settings, float> set,
        Func<int, float> fromPos, Func<float, int> toPos, Func<float, string> format, int valueWidth = 92, bool advanced = false)
    {
        var row = AddRow(page, 52, advanced);
        var slider = new Slider { Label = label, Min = min, Max = max, Get = get, Set = set, FromPos = fromPos, ToPos = toPos, Format = format };
        Put(row, "STATIC", label, 0, Left, 0, Inner - valueWidth - 20, 20, 0);
        slider.Value = Put(row, "STATIC", "", SS_RIGHT, Left + Inner - valueWidth, 0, valueWidth, 20, 0);
        slider.Track = Put(row, "msctls_trackbar32", "", TBS_NOTICKS | WS_TABSTOP, 6, 20, Inner - 4, 26, 0);
        SendMessage(slider.Track, 0x0407 /* TBM_SETRANGEMIN */, 0, min); // aceita negativos (o TBM_SETRANGE não)
        SendMessage(slider.Track, 0x0408 /* TBM_SETRANGEMAX */, 1, max);
        SendMessage(slider.Track, TBM_SETPAGESIZE, 0, 1);
        _sliders.Add(slider);
    }

    // Mostra as linhas da aba atual (e as de "Avançado", se aberto), empilhadas, e ajusta a altura da janela.
    private void Layout()
    {
        int y = 0;
        foreach (var row in _rows)
        {
            bool shown = (row.Page < 0 || row.Page == _settings.PanelTab) && (!row.Advanced || _advanced);
            foreach (var (hwnd, x, dy, w, h) in row.Items)
            {
                if (shown) Native.SetWindowPos(hwnd, IntPtr.Zero, S(x), S(y + dy), S(w), S(h), SWP_NOZORDER | 0x10 /* SWP_NOACTIVATE */);
                Native.ShowWindow(hwnd, shown ? Native.SW_SHOWNOACTIVATE : Native.SW_HIDE);
            }
            if (shown) y += row.Height;
        }
        var rect = new Native.RECT { Right = S(Width), Bottom = S(y) };
        AdjustWindowRectEx(ref rect, _style, false, WS_EX_TOOLWINDOW);
        Native.SetWindowPos(Hwnd, IntPtr.Zero, 0, 0, rect.Width, rect.Height, SWP_NOZORDER | 0x2 /* SWP_NOMOVE */ | 0x10 /* SWP_NOACTIVATE */);
        foreach (var control in _drawn.Keys) InvalidateRect(control, IntPtr.Zero, true);
        // Redesenha tudo já (as linhas mudaram de lugar: sem isso, sobra o desenho antigo até o próximo pintar).
        RedrawWindow(Hwnd, IntPtr.Zero, IntPtr.Zero, 0x1 /* INVALIDATE */ | 0x4 /* ERASE */ | 0x80 /* ALLCHILDREN */ | 0x100 /* UPDATENOW */);
    }

    public void SelectTab(int tab, bool advanced = false)
    {
        _settings.PanelTab = Math.Clamp(tab, 0, TabNames.Length - 1); // número da página (ver TabPages)
        _advanced = advanced;
        Layout();
        Refresh();
    }

    // Mostra os valores atuais (depois de trocar perfil por tecla, por exemplo).
    public void Refresh()
    {
        _refreshing = true;
        var filter = _settings.Filter;
        foreach (var slider in _sliders)
        {
            float value = slider.Get(filter);
            SendMessage(slider.Track, TBM_SETPOS, 1, slider.ToPos(value));
            SendMessage(slider.Value, 0x000C /* WM_SETTEXT */, IntPtr.Zero, slider.Format(value));
        }
        ShowProfile();
        SendMessage(_smooth, BM_SETCHECK, _settings.Smooth ? 1 : 0, IntPtr.Zero);
        SendMessage(_ensemble, BM_SETCHECK, _settings.Ensemble ? 1 : 0, IntPtr.Zero);
        SendMessage(_crisp, BM_SETCHECK, _settings.Crisp ? 1 : 0, IntPtr.Zero);
        SendMessage(_trained, BM_SETCHECK, _settings.TrainedModel && RifeHost.HasTrained ? 1 : 0, IntPtr.Zero);
        SendMessage(_trained, 0x000C /* WM_SETTEXT */, IntPtr.Zero, RifeHost.HasTrained ? "IA treinada com seus vídeos (M)" : "IA treinada com seus vídeos (ainda não instalada)");
        foreach (var control in _performance) InvalidateRect(control, IntPtr.Zero, true);
        foreach (var control in _photoModels) InvalidateRect(control, IntPtr.Zero, true);
        SendMessage(_compare, BM_SETCHECK, filter.Bypass ? 1 : 0, IntPtr.Zero);
        SendMessage(_rtx, BM_SETCHECK, filter.RtxVideo ? 1 : 0, IntPtr.Zero);
        SendMessage(_photoSkin, BM_SETCHECK, filter.PhotoSkin ? 1 : 0, IntPtr.Zero);
        SendMessage(_photoFullRes, BM_SETCHECK, filter.PhotoFullRes ? 1 : 0, IntPtr.Zero);
        SendMessage(_photoFaceClean, BM_SETCHECK, filter.PhotoFaceClean ? 1 : 0, IntPtr.Zero);
        UpdateStatus();
        _refreshing = false;
    }

    // Perfil marcado (vira "personalizado" quando algum controle não bate com nenhum perfil).
    private void ShowProfile()
    {
        SendMessage(_profileHeader, 0x000C /* WM_SETTEXT */, IntPtr.Zero, _settings.CurrentProfile is null ? "Perfil (P) · personalizado" : "Perfil (P)");
        foreach (var control in _profiles) InvalidateRect(control, IntPtr.Zero, true);
    }

    private void UpdateStatus()
    {
        foreach (var (control, text) in new[] { (_slowStatus, _status()), (_rtxStatus, _zoomStatus()), (_photoStatusBox, _photoStatus()) })
        {
            if (_drawn[control].Text == text) continue;
            _drawn[control].Text = text;
            InvalidateRect(control, IntPtr.Zero, true);
        }
    }

    public bool Visible => IsWindowVisible(Hwnd);

    // Abre no canto de cima à direita da janela do player (por cima do vídeo) ou fecha.
    public void Toggle()
    {
        if (Visible) Hide();
        else Show();
    }

    public void Hide() => Native.ShowWindow(Hwnd, Native.SW_HIDE);

    public void Show()
    {
        if (Visible) return;
        GetWindowRect(_owner, out var owner);
        GetWindowRect(Hwnd, out var panel);
        int x = owner.Right - panel.Width - S(16), y = owner.Top + S(48);
        Native.SetWindowPos(Hwnd, IntPtr.Zero, x, y, 0, 0, 0x1 /* SWP_NOSIZE */ | SWP_NOZORDER | 0x10 /* SWP_NOACTIVATE */);
        Refresh();
        Native.ShowWindow(Hwnd, Native.SW_SHOWNOACTIVATE);
    }

    private void Paint(IntPtr dc, Native.RECT rect, Drawn item)
    {
        bool on = item.On();
        FillRect(dc, ref rect, item.Look == Look.Info ? InfoBrush : BackgroundBrush);
        SetBkMode(dc, 1); // transparente
        var oldFont = SelectObject(dc, item.Look == Look.Tab && on ? _bold : _font);
        var text = rect;
        switch (item.Look)
        {
            case Look.Tab:
                SetTextColor(dc, on ? Text : Secondary);
                DrawText(dc, item.Text, -1, ref text, DT_CENTER | DT_VCENTER | DT_SINGLELINE | DT_NOPREFIX);
                if (on)
                {
                    var line = new Native.RECT { Left = rect.Left + S(16), Right = rect.Right - S(16), Top = rect.Bottom - S(2), Bottom = rect.Bottom };
                    FillRect(dc, ref line, AccentBrush);
                }
                break;
            case Look.Chip:
                var oldPen = SelectObject(dc, on ? _accentPen : _borderPen);
                var oldBrush = SelectObject(dc, on ? ChipBrush : BackgroundBrush);
                RoundRect(dc, rect.Left, rect.Top, rect.Right, rect.Bottom, S(10), S(10));
                SelectObject(dc, oldPen);
                SelectObject(dc, oldBrush);
                SetTextColor(dc, on ? Accent : Text);
                DrawText(dc, item.Text, -1, ref text, DT_CENTER | DT_VCENTER | DT_SINGLELINE | DT_NOPREFIX);
                break;
            case Look.Link:
                SetTextColor(dc, Secondary);
                DrawText(dc, (_advanced ? "▾  " : "▸  ") + item.Text, -1, ref text, DT_VCENTER | DT_SINGLELINE | DT_NOPREFIX);
                break;
            case Look.Info:
                SetTextColor(dc, Secondary);
                text = new Native.RECT { Left = rect.Left + S(10), Top = rect.Top + S(6), Right = rect.Right - S(10), Bottom = rect.Bottom - S(4) };
                DrawText(dc, item.Text, -1, ref text, DT_WORDBREAK | DT_NOPREFIX);
                break;
        }
        SelectObject(dc, oldFont);
    }

    private static IntPtr Dispatch(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        PlayerPanel? panel;
        lock (Panels) Panels.TryGetValue(hwnd, out panel);
        panel ??= _creating;
        return panel is not null ? panel.WindowProc(hwnd, msg, wParam, lParam) : DefWindowProcW(hwnd, msg, wParam, lParam);
    }

    private IntPtr WindowProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case WM_HSCROLL when !_refreshing:
            {
                var slider = _sliders.FirstOrDefault(s => s.Track == lParam);
                if (slider is null) break;
                int position = (int)SendMessage(slider.Track, TBM_GETPOS, IntPtr.Zero, IntPtr.Zero);
                float value = slider.FromPos(position);
                slider.Set(_settings.Filter, value);
                SendMessage(slider.Value, 0x000C /* WM_SETTEXT */, IntPtr.Zero, slider.Format(value));
                ShowProfile(); // mexeu no controle: o perfil marcado pode virar "personalizado"
                _changed();
                return 0;
            }
            case WM_COMMAND when !_refreshing:
            {
                int id = (int)((long)wParam & 0xffff);
                if (id >= IdTab && id < IdTab + _tabs.Length)
                {
                    SelectTab(TabPages[id - IdTab], _advanced);
                    _settings.Save();
                    return 0;
                }
                if (id == IdAdvanced)
                {
                    _advanced = !_advanced;
                    Layout();
                    return 0;
                }
                if (id >= IdPhotoModel && id < IdPhotoModel + _photoModels.Length)
                {
                    _settings.PhotoAi = id - IdPhotoModel;
                    Refresh();
                }
                else if (id == IdColorReset)
                {
                    var f = _settings.Filter;
                    (f.Brightness, f.Contrast, f.Saturation, f.Vibrance, f.Clarity) = (0, 0, 0, 0, 0);
                    Refresh();
                }
                else if (id >= IdProfile && id < IdProfile + PlayerProfile.All.Length)
                {
                    _settings.Apply(PlayerProfile.All[id - IdProfile]);
                    Refresh();
                }
                else if (id >= IdPerformance && id < IdPerformance + _performance.Length)
                {
                    _settings.Performance = id - IdPerformance;
                    Refresh();
                }
                else if (id == IdSmooth) _settings.Smooth = SendMessage(_smooth, BM_GETCHECK, IntPtr.Zero, IntPtr.Zero) == 1;
                else if (id == IdEnsemble) _settings.Ensemble = SendMessage(_ensemble, BM_GETCHECK, IntPtr.Zero, IntPtr.Zero) == 1;
                else if (id == IdCrisp) _settings.Crisp = SendMessage(_crisp, BM_GETCHECK, IntPtr.Zero, IntPtr.Zero) == 1;
                else if (id == IdTrained)
                {
                    // Sem o modelo treinado instalado a caixa fica desmarcada (desativada, o Windows nem desenha a caixa).
                    if (!RifeHost.HasTrained) { SendMessage(_trained, BM_SETCHECK, 0, IntPtr.Zero); return 0; }
                    _settings.TrainedModel = SendMessage(_trained, BM_GETCHECK, IntPtr.Zero, IntPtr.Zero) == 1;
                    RifeHost.Use(_settings.TrainedModel);
                }
                else if (id == IdPhotoSkin) _settings.Filter.PhotoSkin = SendMessage(_photoSkin, BM_GETCHECK, IntPtr.Zero, IntPtr.Zero) == 1;
                else if (id == IdPhotoFullRes) _settings.Filter.PhotoFullRes = SendMessage(_photoFullRes, BM_GETCHECK, IntPtr.Zero, IntPtr.Zero) == 1;
                else if (id == IdPhotoFaceClean) _settings.Filter.PhotoFaceClean = SendMessage(_photoFaceClean, BM_GETCHECK, IntPtr.Zero, IntPtr.Zero) == 1;
                else if (id == IdRtx) _settings.Filter.RtxVideo = SendMessage(_rtx, BM_GETCHECK, IntPtr.Zero, IntPtr.Zero) == 1;
                else if (id == IdCompare) _settings.Filter.Bypass = SendMessage(_compare, BM_GETCHECK, IntPtr.Zero, IntPtr.Zero) == 1;
                else break;
                _changed();
                return 0;
            }
            case WM_DRAWITEM:
            {
                var item = Marshal.PtrToStructure<DRAWITEMSTRUCT>(lParam);
                if (!_drawn.TryGetValue(item.hwndItem, out var drawn)) break;
                Paint(item.hDC, item.rcItem, drawn);
                return 1;
            }
            case WM_TIMER:
                if (Visible && _settings.PanelTab is 1 or 2 or 4) UpdateStatus();
                return 0;
            case WM_CTLCOLORSTATIC:
                if (_headers.Contains(lParam) && (int)SendMessage(lParam, 0x000E /* WM_GETTEXTLENGTH */, IntPtr.Zero, IntPtr.Zero) == 0)
                    return DividerBrush; // divisória (rótulo vazio de 1 px)
                SetTextColor(wParam, _headers.Contains(lParam) ? Secondary : _muted.Contains(lParam) ? Muted : Text);
                SetBkColor(wParam, Background);
                return BackgroundBrush;
            case WM_CLOSE:
                Native.ShowWindow(Hwnd, Native.SW_HIDE); // fechar = esconder (A abre de novo)
                return 0;
        }
        return DefWindowProcW(hwnd, msg, wParam, lParam);
    }

    public void Dispose()
    {
        lock (Panels) Panels.Remove(Hwnd);
        Native.DestroyWindow(Hwnd);
        DeleteObject(_font);
        DeleteObject(_bold);
        DeleteObject(_accentPen);
        DeleteObject(_borderPen);
    }
}
