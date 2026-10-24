using System.Runtime.InteropServices;
using Vortice.Direct3D11;
using static MKVision.Desktop.PlayerNative;

namespace MKVision.Desktop;

// Interface do player por cima do vídeo:
//   • barra de baixo (só com o mouse em cima dela; some na hora em que ele sai): linha de progresso (clicar ou
//     arrastar = pular), play/pause, volume (passar o mouse abre 0–100; clicar = mudo), tempo "00:37 / 02:41"
//     e tela cheia;
//   • menu do topo (só com o mouse em cima): Arquivo (Abrir vídeo, Sair) e Ajustes (painel);
//   • lista dos vídeos da pasta (mouse no meio da borda direita; some ao sair dela): clicar abre, roda rola;
//     Ctrl+Shift+1…4 = tamanho dos ícones, como no Explorer (extra grandes, grandes, médios, só os nomes);
//     à esquerda dela, a árvore de pastas do computador (PlayerBrowser.cs) para ver os vídeos de outra pasta;
//   • clicar no vídeo = play/pause; arrastar com zoom = mover; duplo clique = tela cheia;
//   • sem vídeo: um aviso no meio de como abrir;
//   • aviso rápido no canto superior esquerdo a cada comando ("Velocidade 0.3x", "Girado 45°"...), ~1,5 s.
// O mouse é tratado na thread principal; o desenho (UiCanvas → camada sobre o vídeo), na thread da tela.
internal sealed partial class Player
{
    private enum UiPart { None, Video, Seek, Play, Volume, VolumeSlider, Fullscreen, BottomBar, TopBar, MenuFile, MenuSettings, MenuWeb, List, ListItem, TreeItem, NavBack, NavForward, NavUp,
        WebBack, WebForward, WebReload, WebAddress, WebAuto, WebPlayer, WebTab, WebTabClose, WebNewTab, WebStar,
        WebBookmark, WebBookmarksMore, WebBookmarkBar, WebShield, WebSiteIcon, WebSettings, WebTabAudio, WebTabsLeft, WebTabsRight, WebZoom, WebTranslate }

    // Medidas a 96 dpi (multiplicadas pela escala da tela).
    private const int BarHeight = 84, SeekY = 18, RowY = 56, TopHeight = 40, SideMargin = 16;
    private const uint Accent = 0x6B9BFF, White = 0xFFFFFF, Muted = 0xB8BCC2;
    private const string IconPlay = "", IconPause = "", IconVolume = "", IconMute = "",
        IconFullscreen = "", IconWindow = "";
    private const uint WM_MOUSELEAVE = 0x02A3;

    [StructLayout(LayoutKind.Sequential)]
    private struct TRACKMOUSEEVENT { public int cbSize; public uint dwFlags; public IntPtr hwndTrack; public uint dwHoverTime; }

    [DllImport("user32.dll")] private static extern bool TrackMouseEvent(ref TRACKMOUSEEVENT track);
    [DllImport("user32.dll")] private static extern IntPtr CreatePopupMenu();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool AppendMenu(IntPtr menu, uint flags, IntPtr id, string? text);
    [DllImport("user32.dll")] private static extern int TrackPopupMenu(IntPtr menu, uint flags, int x, int y, int reserved, IntPtr hwnd, IntPtr rect);
    [DllImport("user32.dll")] private static extern bool DestroyMenu(IntPtr menu);

    // Para o autoteste.
    public (bool bottom, bool top) BarsVisible { get { lock (_lock) return (BottomVisible(), TopVisible()); } }
    public bool Playing { get { lock (_lock) return _playing; } }
    public int UiScaled(double value) => U(value);
    // O autoteste simula o mouse sem mover o cursor de verdade (que está fora da janela): ignora o "saiu".
    public bool IgnoreMouseLeave;
    public (string[] files, int index, bool listOpen, string name, int rotation) FolderState
    {
        get { lock (_lock) return (_folderFiles, _folderIndex, _listOpen, _fileName, _rotation); }
    }
    public void TestListMode(int mode) => SetListMode(mode);
    public int ListModeForTest => _prefs.ListMode;
    public string TestTitle { get { UpdateTitle(); return _lastTitle; } }
    public bool HasAudioForTest => _audio?.HasAudio == true;
    public float TestVolume => _prefs.Volume;
    public bool TestVolumeOpen { get { lock (_lock) return _volumeOpen; } }
    public double[] TestQueueTimes()
    {
        lock (_lock) return _queue.Select(f => f.FileTime).ToArray();
    }

    public void TestSeek(double fileTime) => SeekTo(fileTime);

    public void TestEnsemble(bool on)
    {
        _prefs.Smooth = true; // outro autoteste pode ter desligado e guardado
        _prefs.Ensemble = on;
        OnSettingsChanged();
    }

    public void TestPerformanceMode(int mode)
    {
        _prefs.Performance = mode;
        OnSettingsChanged();
    }

    private double _uiScale = 1;
    private int U(double value) => (int)Math.Round(value * _uiScale);

    // Estado da interface: a thread principal escreve (com _lock); a da tela lê.
    private int _mouseX = -1, _mouseY = -1;
    private bool _mouseInside, _trackingMouse;
    private UiPart _hover;
    private bool _seekDragging, _volumeDragging, _volumeOpen, _pressedOnVideo, _moved;
    // Clique que trouxe a janela para a frente (estava em outra, com Alt+Tab): só ativa, não dá play/pausa.
    private long _clickActivatedAt = long.MinValue / 2;
    private bool _activationPress;
    private int _pressX, _pressY;
    private double _seekPreview;
    private long _lastSeekRequest;
    private Native.RECT _menuFileRect, _menuSettingsRect, _menuWebRect; // calculados ao desenhar o topo
    private bool _listOpen;
    private int _listScroll = -1;   // primeira linha visível (-1 = posicionar no vídeo atual ao abrir)
    private ThumbnailCache? _thumbs;
    private int _listHover = -1;
    private const int ListItemHeight = 30, ListHeader = 44, NavButton = 30;
    private const string IconBack = "\uE72B", IconForward = "\uE72A", IconUp = "\uE74A";

    // Desenho da interface (thread da tela).
    private UiCanvas? _bottomCanvas, _topCanvas, _hintCanvas, _listCanvas;
    private ID3D11ShaderResourceView? _bottomLayer, _topLayer, _hintLayer, _listLayer;
    private string _bottomKey = "", _topKey = "", _hintKey = "", _listKey = "";
    private IntPtr _fontText, _fontIcons, _fontMenu, _fontHint;

    // Aviso rápido do último comando (canto superior esquerdo).
    private const double OsdSeconds = 1.5, OsdFade = 0.35;
    private string _osdText = "";
    private double _osdUntil;
    private UiCanvas? _osdCanvas;
    private ID3D11ShaderResourceView? _osdLayer;
    private string _osdKey = "";
    private IntPtr _fontOsd;

    // Qualquer thread: mostra o aviso (substitui o anterior).
    public void Notify(string text)
    {
        lock (_lock)
        {
            _osdText = text;
            _osdUntil = Now + OsdSeconds;
            _dirty = true;
        }
        _renderWake.Set();
    }

    // Com _lock: ainda há aviso na tela (a tela continua redesenhando até ele sumir, mesmo pausado).
    private bool OsdActive() => _osdText != "" && Now < _osdUntil;

    // Som.
    private PlayerAudio? _audio;
    private string? _audioOpen;

    // ---------- Geometria e o que está sob o mouse (com _lock) ----------

    private (int x0, int x1, int y) SeekGeometry() => (U(SideMargin), _clientW - U(SideMargin), _clientH - U(BarHeight) + U(SeekY));
    private int RowCenter() => _clientH - U(BarHeight) + U(RowY);
    private (int x0, int x1) VolumeSlider() => (U(104), U(104) + U(84));

    // Tamanho dos ícones (_prefs.ListMode): colunas e largura da miniatura (0 = só os nomes).
    private (int columns, int thumb) ListGrid() => _prefs.ListMode switch
    {
        1 => (2, 280),
        2 => (2, 200),
        3 => (3, 132),
        _ => (1, 0)
    };

    private int CellWidth() => ListGrid().thumb == 0 ? 0 : ListGrid().thumb + 12;
    private int CellHeight() => ListGrid().thumb == 0 ? ListItemHeight : ListGrid().thumb * 9 / 16 + 34;
    private int ListRows() => (_browseFiles.Length + ListGrid().columns - 1) / ListGrid().columns;

    // Painel da lista (com _lock): à direita, no meio da altura, 80% dela; a árvore de pastas à esquerda e os
    // vídeos da pasta à direita.
    private Native.RECT ListRect()
    {
        var (columns, thumb) = ListGrid();
        int files = thumb == 0 ? U(380) : U(columns * CellWidth() + 16);
        int width = Math.Min(TreeW() + files, _clientW - U(40));
        int height = (int)(_clientH * 0.8);
        int x = _clientW - width - U(12), y = (_clientH - height) / 2;
        return new Native.RECT { Left = x, Top = y, Right = x + width, Bottom = y + height };
    }

    // Largura da árvore (janela estreita: encolhe, mas deixa espaço para os vídeos).
    private int TreeW() => Math.Max(0, Math.Min(U(TreeWidth), (_clientW - U(40)) / 2));

    private int ListVisible() => Math.Max(1, (ListRect().Height - U(ListHeader) - U(8)) / U(CellHeight())); // linhas visíveis

    // Faixa que abre a lista: o último quarto da largura, na altura do meio (30% a 70%).
    private bool InListTrigger(int x, int y) => x >= _clientW * 3 / 4 && Math.Abs(y - _clientH / 2) <= _clientH * 0.2;

    private void ClampListScroll()
    {
        int max = Math.Max(0, ListRows() - ListVisible());
        if (_listScroll < 0) _listScroll = Math.Clamp(BrowseCurrent() / ListGrid().columns - ListVisible() / 2, 0, max); // centraliza o atual
        _listScroll = Math.Clamp(_listScroll, 0, max);
    }

    // Ctrl+Shift+1…4.
    private void SetListMode(int mode)
    {
        lock (_lock)
        {
            int first = _listScroll * ListGrid().columns; // mantém o mesmo trecho da lista à vista
            _prefs.ListMode = mode;
            _listScroll = first / ListGrid().columns;
            ClampListScroll();
            _dirty = true;
        }
        _prefs.Save();
        _renderWake.Set();
        Notify(mode switch { 1 => "Ícones extra grandes", 2 => "Ícones grandes", 3 => "Ícones médios", _ => "Lista: só os nomes" });
    }

    private UiPart HitTest(int x, int y)
    {
        if (!_mouseInside || x < 0 || y < 0 || x >= _clientW || y >= _clientH) return UiPart.None;
        if (_webMode) return WebHitTest(x, y);
        if (_listOpen && Inside(ListRect(), x, y))
        {
            var rect = ListRect();
            int treeW = TreeW();
            if (x < rect.Left + treeW)
            {
                _listHover = -1;
                int treeRow = (y - rect.Top - U(8)) / U(TreeRow), treeIndex = _treeScroll + treeRow;
                bool valid = y >= rect.Top + U(8) && treeRow < TreeVisible() && treeIndex < _treeRows.Length && !_treeRows[treeIndex].Separator;
                _treeHover = valid ? treeIndex : -1;
                _treeHoverChevron = valid && x < rect.Left + U(8) + _treeRows[treeIndex].Depth * U(16) + U(16);
                return valid ? UiPart.TreeItem : UiPart.List;
            }
            _treeHover = -1;
            var (columns, thumb) = ListGrid();
            int left = rect.Left + treeW;
            if (y < rect.Top + U(ListHeader) - U(4) && x < left + U(8) + 3 * U(NavButton))
            {
                _listHover = -1;
                int button = (x - left - U(8)) / U(NavButton);
                return button switch { 0 => UiPart.NavBack, 1 => UiPart.NavForward, 2 => UiPart.NavUp, _ => UiPart.List };
            }
            int row = (y - rect.Top - U(ListHeader)) / U(CellHeight());
            int column = thumb == 0 ? 0 : (x - left - U(8)) / U(CellWidth());
            int index = (_listScroll + row) * columns + column;
            _listHover = y >= rect.Top + U(ListHeader) && row < ListVisible() && column >= 0 && column < columns && index < _browseFiles.Length ? index : -1;
            return _listHover >= 0 ? UiPart.ListItem : UiPart.List;
        }
        _listHover = -1;
        _treeHover = -1;
        if (_file is not null && y >= _clientH - U(BarHeight))
        {
            var (x0, x1, sy) = SeekGeometry();
            if (Math.Abs(y - sy) <= U(10) && x >= x0 - U(6) && x <= x1 + U(6)) return UiPart.Seek;
            int row = RowCenter();
            if (_volumeOpen && Math.Abs(y - row) <= U(26) && x >= U(56))
            {
                // Volume aberto: o alto-falante e a barra são uma área só (antes, o vão entre os dois fechava a
                // barra no caminho do mouse).
                var (s0, s1) = VolumeSlider();
                if (x < s0 - U(4)) return UiPart.Volume;
                if (x <= s1 + U(12)) return UiPart.VolumeSlider;
            }
            if (Math.Abs(y - row) <= U(20))
            {
                if (x >= U(10) && x < U(52)) return UiPart.Play;
                if (x >= U(56) && x < U(98)) return UiPart.Volume;
                var (v0, v1) = VolumeSlider();
                if (_volumeOpen && x >= v0 - U(4) && x <= v1 + U(8)) return UiPart.VolumeSlider;
                if (x >= _clientW - U(54) && x < _clientW - U(10)) return UiPart.Fullscreen;
            }
            return UiPart.BottomBar;
        }
        if (y < U(TopHeight))
        {
            if (Inside(_menuFileRect, x, y)) return UiPart.MenuFile;
            if (Inside(_menuWebRect, x, y)) return UiPart.MenuWeb;
            if (Inside(_menuSettingsRect, x, y)) return UiPart.MenuSettings;
            return UiPart.TopBar;
        }
        return UiPart.Video;
    }

    private static bool Inside(Native.RECT r, int x, int y) => x >= r.Left && x < r.Right && y >= r.Top && y < r.Bottom;

    private bool BottomVisible() => _file is not null && !_cropping && (_seekDragging || _volumeDragging || (_mouseInside && _mouseY >= _clientH - U(BarHeight)));
    private bool TopVisible() => _mouseInside && !_cropping && !_seekDragging && !_volumeDragging && _mouseY >= 0 && _mouseY < U(TopHeight);

    // ---------- Mouse (thread principal) ----------

    private void OnMouseMove(int x, int y)
    {
        if (WebDragMove(x, y)) return; // arrastando um site/favorito para a barra de favoritos
        if (CropMouseMove(x, y)) return; // cortando (F4, PlayerCrop)
        if (AnnotateMouseMove(x, y)) return; // desenhando (F3, PlayerAnnotate)
        if (!_trackingMouse)
        {
            var track = new TRACKMOUSEEVENT { cbSize = Marshal.SizeOf<TRACKMOUSEEVENT>(), dwFlags = 2 /* TME_LEAVE */, hwndTrack = Hwnd };
            _trackingMouse = TrackMouseEvent(ref track);
        }
        bool seekNow = false;
        double seekTo = 0;
        lock (_lock)
        {
            (_mouseX, _mouseY, _mouseInside) = (x, y, true);
            if (_seekDragging)
            {
                _seekPreview = SeekTimeAt(x);
                if (Environment.TickCount64 - _lastSeekRequest > 150) // arrastando: pula no máximo ~7x por segundo
                {
                    (seekNow, seekTo) = (true, _seekPreview);
                    _lastSeekRequest = Environment.TickCount64;
                }
            }
            else if (_volumeDragging)
            {
                SetVolumeAt(x);
                _osdText = _prefs.Muted ? "Mudo" : $"Volume {_prefs.Volume * 100:0}%";
                _osdUntil = Now + OsdSeconds;
            }
            // As faixas abrem ao ENTRAR nelas (são grandes: fechado pela tecla com o mouse dentro, só reabre ao sair e voltar).
            // Com a lista aberta, a parte dela que cai na faixa dos ajustes não conta (escolhendo um vídeo lá em cima,
            // os ajustes não abrem por cima): a entrada vale quando o mouse sai da lista ainda dentro da faixa.
            bool inPanelZone = InPanelTrigger(x, y) && !(_listOpen && Inside(ListRect(), x, y)), inListZone = InListTrigger(x, y);
            bool enterPanel = inPanelZone && !_inPanelZone, enterList = inListZone && !_inListZone;
            (_inPanelZone, _inListZone) = (inPanelZone, inListZone);
            // Com o painel de anotações aberto, a lista e os ajustes não abrem pelas bordas (o mouse está desenhando).
            if (!_webMode && !_annotating && !_seekDragging && !_volumeDragging && enterPanel) _panelWanted = true; // abre na thread principal, abaixo
            if (!_webMode && !_annotating && !_listOpen && !_seekDragging && !_volumeDragging && enterList)
            {
                _listOpen = true;
                _listByClick = false;
                SyncTree();
                ClampListScroll();
            }
            else if (_listOpen && !Inside(ListRect(), x, y) && !InListTrigger(x, y) && !(_listByClick && !_listEntered))
            {
                _listOpen = false; // saiu da lista: some na hora
            }
            if (_listOpen && Inside(ListRect(), x, y)) _listEntered = true;
            _hover = HitTest(x, y);
            _volumeOpen = _volumeDragging || _hover is UiPart.Volume or UiPart.VolumeSlider;
            _dirty = true;
        }
        if (_pressedOnVideo && _dragging)
        {
            if (Math.Abs(x - _pressX) > 4 || Math.Abs(y - _pressY) > 4) _moved = true; // virou arrastar, não clique
            Pan(x - _dragX, y - _dragY);
            (_dragX, _dragY) = (x, y);
        }
        if (seekNow) SeekTo(seekTo);
        if (_panelWanted && _panel is { Visible: false })
        {
            _panel.Show();
            _panelByHover = true;
            _panelEntered = false;
        }
        _panelWanted = false;
        _renderWake.Set();
    }

    // Ajustes: o canto de cima à direita abre; sai do painel (e do canto), ele some — como a lista e as barras.
    // Aberto pelo menu ou pela tecla A, some quando o mouse entrar e depois sair dele.
    private bool _panelWanted, _panelByHover, _panelEntered, _inPanelZone, _inListZone;

    // Faixa que abre os ajustes: o último quarto da largura, no alto (até 20% da altura; a da lista começa em 30%).
    private bool InPanelTrigger(int x, int y) => x >= _clientW * 3 / 4 && y < Math.Max(U(36), _clientH / 5);

    [DllImport("user32.dll")] private static extern bool GetCursorPos(out Native.POINT point);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);

    // WM_TIMER (~10x por segundo).
    private void AutoHidePanel()
    {
        if (_panel is not { Visible: true }) { _panelByHover = _panelEntered = false; return; }
        if (IgnoreMouseLeave) return; // autoteste: o cursor de verdade está fora da janela
        if ((GetAsyncKeyState(0x01) & 0x8000) != 0) return; // arrastando um controle
        GetCursorPos(out var cursor);
        GetWindowRect(_panel.Hwnd, out var panel);
        bool inPanel = cursor.X >= panel.Left - 8 && cursor.X < panel.Right + 8 && cursor.Y >= panel.Top - 8 && cursor.Y < panel.Bottom + 8;
        var local = cursor;
        ScreenToClient(Hwnd, ref local);
        bool inCorner;
        lock (_lock) inCorner = !_webMode && InPanelTrigger(local.X, local.Y) && local.X < _clientW && local.Y >= 0;
        // Entre o canto e o painel há uma faixa: conta como dentro.
        bool between = cursor.X >= panel.Left && cursor.Y < panel.Top && local.Y >= 0 && local.X < _clientW;
        if (inPanel) _panelEntered = true;
        if (!inPanel && !inCorner && !between && (_panelEntered || _panelByHover))
        {
            _panel.Hide();
            _panelByHover = _panelEntered = false;
        }
    }

    private void OnMouseLeave()
    {
        _trackingMouse = false;
        if (IgnoreMouseLeave) return;
        lock (_lock)
        {
            if (_seekDragging || _volumeDragging) return; // arrastando com o mouse fora: continua
            _mouseInside = false;
            _inPanelZone = _inListZone = false;
            _hover = UiPart.None;
            _volumeOpen = false;
            _listOpen = false;
            _dirty = true;
        }
        _renderWake.Set();
    }

    private void OnMouseDown(int x, int y, bool doubleClick)
    {
        UiPart part;
        lock (_lock)
        {
            (_mouseX, _mouseY, _mouseInside) = (x, y, true);
            part = HitTest(x, y);
        }
        if (CropMouseDown(x, y) || AnnotateMouseDown(x, y, part)) // F4: a moldura do corte; F3: painel de anotações ou um traço
        {
            lock (_lock) _dirty = true;
            _renderWake.Set();
            return;
        }
        switch (part)
        {
            case UiPart.Seek:
                lock (_lock)
                {
                    _seekDragging = true;
                    _seekPreview = SeekTimeAt(x);
                    _lastSeekRequest = Environment.TickCount64;
                }
                SetCapture(Hwnd);
                SeekTo(SeekTimeAt(x));
                break;
            case UiPart.Play:
                TogglePlay();
                break;
            case UiPart.Volume:
                _prefs.Muted = !_prefs.Muted;
                _prefs.Save();
                Notify(_prefs.Muted ? "Mudo" : $"Volume {_prefs.Volume * 100:0}%");
                break;
            case UiPart.VolumeSlider:
                lock (_lock)
                {
                    _volumeDragging = true;
                    SetVolumeAt(x);
                }
                SetCapture(Hwnd);
                break;
            case UiPart.Fullscreen:
                ToggleFullscreen();
                break;
            case UiPart.MenuFile:
                ShowFileMenu();
                break;
            case UiPart.MenuSettings:
                _panel?.Toggle();
                break;
            case UiPart.MenuWeb:
                ShowWeb(true);
                break;
            case UiPart.WebBack or UiPart.WebForward or UiPart.WebReload or UiPart.WebAddress or UiPart.WebAuto or UiPart.WebPlayer
                or UiPart.WebTab or UiPart.WebTabClose or UiPart.WebNewTab or UiPart.WebStar or UiPart.WebBookmarksMore
                or UiPart.WebShield or UiPart.WebSettings or UiPart.WebTabAudio or UiPart.WebTabsLeft or UiPart.WebTabsRight or UiPart.WebZoom or UiPart.WebTranslate:
                WebClick(part, x, y);
                break;
            case UiPart.WebBookmark: // abre ao soltar (se não virar arrasto)
                BeginBookmarkPress(x, y);
                break;
            case UiPart.WebSiteIcon:
                BeginLinkDrag(x, y);
                break;
            case UiPart.ListItem:
            {
                string? file;
                bool isFolder;
                lock (_lock)
                {
                    file = _listHover >= 0 && _listHover < _browseFiles.Length ? _browseFiles[_listHover] : null;
                    isFolder = file is not null && _listHover < _browseDirs;
                    if (isFolder)
                    {
                        ShowFolder(file!, record: true); // pasta: entra nela (← volta)
                        _dirty = true;
                    }
                }
                if (isFolder) _renderWake.Set();
                else if (file is not null)
                {
                    Open(file);
                    lock (_lock) _browseSyncedTo = file; // a árvore já está nesta pasta
                }
                break;
            }
            case UiPart.TreeItem:
                TreeClick(doubleClick);
                break;
            case UiPart.NavBack:
                Navigate(-1);
                break;
            case UiPart.NavForward:
                Navigate(1);
                break;
            case UiPart.NavUp:
                Navigate(0);
                break;
            case UiPart.Video:
                if (doubleClick) ToggleFullscreen();
                // Clique = play/pause (ao soltar, se não arrastou); arrastar com zoom = mover a imagem.
                // No duplo clique os dois cliques pausam e retomam (como nos players da web).
                _pressedOnVideo = true;
                _activationPress = Environment.TickCount64 - _clickActivatedAt < 500;
                _moved = false;
                (_pressX, _pressY) = (x, y);
                _dragging = true;
                (_dragX, _dragY) = (x, y);
                SetCapture(Hwnd);
                break;
        }
        lock (_lock) _dirty = true;
        _renderWake.Set();
        UpdateTitle();
    }

    private void OnMouseUp()
    {
        if (WebDragEnd()) return;
        if (CropMouseUp() || AnnotateMouseUp()) return;
        bool seekEnd, volumeEnd, click;
        double seekTo;
        lock (_lock)
        {
            (seekEnd, volumeEnd, seekTo) = (_seekDragging, _volumeDragging, _seekPreview);
            _seekDragging = _volumeDragging = false;
            _volumeOpen = _hover is UiPart.Volume or UiPart.VolumeSlider;
            _dirty = true;
        }
        click = _pressedOnVideo && !_moved;
        _pressedOnVideo = false;
        _dragging = false;
        ReleaseCapture();
        if (seekEnd) SeekTo(seekTo);
        if (volumeEnd)
        {
            _prefs.Save();
            Notify(_prefs.Muted ? "Mudo" : $"Volume {_prefs.Volume * 100:0}%");
        }
        if (click && _activationPress) { } // só trouxe a janela para a frente
        else if (click && !HasMedia()) ToggleListByClick(); // tela vazia: a lista de pastas, para escolher o que abrir
        else if (click && StillGuard()) Notify("Quadro da IA na tela — Espaço para voltar ao vídeo");
        else if (click) TogglePlay();
        _renderWake.Set();
        UpdateTitle();
    }

    // Clique na tela vazia (nada aberto): abre a lista de pastas — a mesma da borda direita — e ela fica aberta até o
    // mouse entrar nela e sair (aberta pela borda, some ao sair; aqui o mouse ainda está no meio da tela). Outro
    // clique fora dela fecha.
    private bool _listByClick, _listEntered;
    public bool TestListOpen { get { lock (_lock) return _listOpen; } }

    private bool HasMedia() { lock (_lock) return _file is not null || _image || _pendingOpen is not null; }

    private void ToggleListByClick()
    {
        lock (_lock)
        {
            if (_listOpen) _listOpen = false;
            else
            {
                (_listOpen, _listByClick, _listEntered) = (true, true, false);
                SyncTree();
                ClampListScroll();
            }
            _hover = HitTest(_mouseX, _mouseY);
            _dirty = true;
        }
        _renderWake.Set();
    }

    // true = a roda foi usada pela lista.
    private bool OnListWheel(int x, int y, int delta)
    {
        lock (_lock)
        {
            if (!_listOpen || !Inside(ListRect(), x, y)) return false;
            if (x < ListRect().Left + TreeW())
                _treeScroll = Math.Clamp(_treeScroll - Math.Sign(delta) * 3, 0, Math.Max(0, _treeRows.Length - TreeVisible()));
            else
            {
                _listScroll -= Math.Sign(delta) * (ListGrid().thumb == 0 ? 3 : 1); // nomes: 3 linhas; ícones: 1
                ClampListScroll();
            }
            _hover = HitTest(x, y);
            _dirty = true;
        }
        _renderWake.Set();
        return true;
    }

    // Com _lock.
    private double SeekTimeAt(int x)
    {
        var (x0, x1, _) = SeekGeometry();
        return Math.Clamp((double)(x - x0) / Math.Max(1, x1 - x0), 0, 1) * _duration;
    }

    // Com _lock.
    private void SetVolumeAt(int x)
    {
        var (v0, v1) = VolumeSlider();
        _prefs.Volume = (float)Math.Clamp((double)(x - v0) / (v1 - v0), 0, 1);
        _prefs.Muted = _prefs.Volume <= 0;
    }

    private void ShowFileMenu()
    {
        var menu = CreatePopupMenu();
        AppendMenu(menu, 0, 1, "Abrir vídeo...\tCtrl+O");
        AppendMenu(menu, 0x800 /* MF_SEPARATOR */, 0, null);
        bool registered = FileAssociation.IsRegistered();
        AppendMenu(menu, 0, 3, registered ? "Escolher o MK Vision como player padrão..." : "Tornar o MK Vision o player de vídeos padrão...");
        if (registered) AppendMenu(menu, 0, 4, "Remover o MK Vision dos players de vídeo");
        AppendMenu(menu, 0x800 /* MF_SEPARATOR */, 0, null);
        if (UpdateMenuText() is { } update) AppendMenu(menu, 0, 5, update);
        AppendMenu(menu, 0x1 | 0x2 /* MF_GRAYED | MF_DISABLED */, 0, $"MK Vision {Updater.Current}");
        AppendMenu(menu, 0x800 /* MF_SEPARATOR */, 0, null);
        AppendMenu(menu, 0, 2, "Sair");
        var point = new Native.POINT { X = _menuFileRect.Left, Y = _menuFileRect.Bottom };
        Native.ClientToScreen(Hwnd, ref point);
        int command = TrackPopupMenu(menu, 0x100 /* TPM_RETURNCMD */, point.X, point.Y, 0, Hwnd, IntPtr.Zero);
        DestroyMenu(menu);
        if (command == 1) ShowOpenDialog();
        else if (command == 2) PostMessage(Hwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
        else if (command == 5) StartUpdate();
        else if (command == 3)
        {
            try
            {
                FileAssociation.Register();
                FileAssociation.OpenDefaultAppsSettings(); // a escolha final é da pessoa, na tela do Windows
                Notify("Na tela do Windows, escolha o MK Vision como padrão");
            }
            catch (Exception error)
            {
                Notify($"Não consegui registrar: {error.Message}");
            }
        }
        else if (command == 4)
        {
            try
            {
                FileAssociation.Unregister();
                Notify("MK Vision removido dos players de vídeo");
            }
            catch (Exception error)
            {
                Notify($"Não consegui remover: {error.Message}");
            }
        }
    }

    // ---------- Som (thread principal, ~10x por segundo) ----------

    private void SyncAudio()
    {
        if (_audio is null) return;
        string? open;
        double fileTime, speed;
        bool playing;
        lock (_lock)
        {
            open = _audioOpen;
            _audioOpen = null;
            (fileTime, speed, playing) = (_shownFileTime, _speed, _playing && _file is not null && !_seekDragging);
        }
        if (open is not null) _audio.Open(open);
        // Som de um vídeo que ainda baixa: perto do fim do que o tocador conhece, reabre (o arquivo já cresceu).
        else if (_webAudioGrows && _webAudioUrl is { } url)
        {
            double now = Environment.TickCount64 / 1000.0;
            double known = _audio.Duration;
            if (known > 0 && fileTime > known - 3 && known < _duration - 1 && now - _webAudioReopenedAt > 2)
            {
                _webAudioReopenedAt = now;
                _audio.Open(url);
            }
        }
        _audio.Sync(fileTime, playing, speed, _prefs.Volume, _prefs.Muted);
    }

    // ---------- Desenho da interface (thread da tela, com o lock da GPU) ----------

    private void DrawInterface(ID3D11RenderTargetView target, int cw, int ch)
    {
        if (_fontText == IntPtr.Zero)
        {
            _uiScale = Math.Max(1, GetDpiForWindow(Hwnd) / 96.0);
            _fontText = UiCanvas.Font("Segoe UI", U(15));
            _fontMenu = UiCanvas.Font("Segoe UI", U(15));
            _fontHint = UiCanvas.Font("Segoe UI", U(17));
            _fontIcons = UiCanvas.Font("Segoe MDL2 Assets", U(20));
            _fontOsd = UiCanvas.Font("Segoe UI", U(20), 600);
            _fontChevron = UiCanvas.Font("Segoe MDL2 Assets", U(11));
        }
        bool bottom, top, hasFile, playing, seekHover, volumeOpen;
        UiPart hover;
        double position, duration;
        bool web;
        lock (_lock) (web, hover) = (_webMode, _hover);
        if (web)
        {
            // Navegador: só a barra dele (o site fica por cima do resto) e os avisos. A barra some só quando o espaço dela
            // some (vídeo do site em tela cheia, F11 sem o mouse no topo) — antes, qualquer tela cheia a apagava e o
            // espaço ficava preto.
            bool bars = WebTop() > 0;
            if (bars) DrawWebBar(target, cw, hover);
            DrawOsd(target, bars);
            return;
        }
        lock (_lock)
        {
            hasFile = _file is not null || _image;
            bottom = BottomVisible();
            top = TopVisible();
            hover = _hover;
            seekHover = _hover == UiPart.Seek || _seekDragging;
            volumeOpen = _volumeOpen;
            playing = _playing;
            position = _seekDragging ? _seekPreview : _shownFileTime;
            duration = _duration;
        }

        bool list;
        Native.RECT listRect;
        lock (_lock)
        {
            list = _listOpen;
            listRect = ListRect();
        }
        if (!hasFile && !list) DrawHint(target, cw, ch); // com a lista aberta o texto ficaria embaralhado por baixo dela
        if (list) DrawFileList(target, listRect);
        if (bottom) DrawBottomBar(target, cw, ch, hover, seekHover, volumeOpen, playing, position, duration);
        if (top) DrawTopBar(target, cw, hover);
        DrawOsd(target, top);
    }

    private void DrawBottomBar(ID3D11RenderTargetView target, int cw, int ch, UiPart hover, bool seekHover, bool volumeOpen,
        bool playing, double position, double duration)
    {
        int height = U(BarHeight);
        double fraction = duration > 0 ? Math.Clamp(position / duration, 0, 1) : 0;
        int x0 = U(SideMargin), x1 = cw - U(SideMargin);
        float played = (float)(x0 + (x1 - x0) * fraction);
        string time = $"{ClockText(position)} / {ClockText(duration)}";
        float volume = _prefs.Muted ? 0 : _prefs.Volume;
        bool hasAudio = _audio?.HasAudio == true;
        string key = $"{cw}|{hover}|{seekHover}|{volumeOpen}|{playing}|{_fullscreen}|{(int)played}|{time}|{volume:0.00}|{hasAudio}";
        if (key != _bottomKey || _bottomLayer is null)
        {
            _bottomKey = key;
            var c = _bottomCanvas ??= new UiCanvas();
            c.Begin(cw, height);
            c.Gradient(0f, 0.8f);
            // Linha de progresso.
            float sy = U(SeekY), thickness = seekHover ? U(6) : U(4);
            c.Rect(x0, sy - thickness / 2, x1 - x0, thickness, White, 0.3f);
            c.Rect(x0, sy - thickness / 2, played - x0, thickness, Accent, 1);
            if (seekHover) c.Circle(played, sy, U(7), Accent, 1);
            // Botões.
            int row = U(RowY), iconTop = row - c.TextHeight(_fontIcons) / 2;
            void Button(UiPart part, int center, string icon, float alpha = 1)
            {
                if (hover == part) c.Circle(center, row, U(19), White, 0.15f);
                c.Text(icon, _fontIcons, center - c.TextWidth(icon, _fontIcons) / 2, iconTop, White, alpha);
            }
            Button(UiPart.Play, U(31), playing ? IconPause : IconPlay);
            Button(UiPart.Volume, U(77), _prefs.Muted || _prefs.Volume <= 0 || !hasAudio ? IconMute : IconVolume, hasAudio ? 1 : 0.5f);
            int timeX = U(108);
            if (volumeOpen)
            {
                var (v0, v1) = VolumeSlider();
                c.Rect(v0, row - U(2), v1 - v0, U(4), White, 0.3f);
                c.Rect(v0, row - U(2), (v1 - v0) * volume, U(4), White, 1);
                c.Circle(v0 + (v1 - v0) * volume, row, U(6), White, 1);
                timeX = v1 + U(18);
            }
            c.Text(time, _fontText, timeX, row - c.TextHeight(_fontText) / 2, White);
            Button(UiPart.Fullscreen, cw - U(32), _fullscreen ? IconWindow : IconFullscreen);
            _bottomLayer = c.Upload(_gpu);
        }
        _gpu.DrawOverlay(target, 0, ch - height, cw, height, _bottomLayer);
    }

    private void DrawTopBar(ID3D11RenderTargetView target, int cw, UiPart hover)
    {
        int height = U(TopHeight);
        string key = $"{cw}|{hover}";
        if (key != _topKey || _topLayer is null)
        {
            _topKey = key;
            var c = _topCanvas ??= new UiCanvas();
            c.Begin(cw, height);
            c.Gradient(0.8f, 0f);
            int textTop = (height - c.TextHeight(_fontMenu)) / 2, x = U(14);
            Native.RECT Item(string text, UiPart part)
            {
                int w = c.TextWidth(text, _fontMenu);
                var rect = new Native.RECT { Left = x - U(8), Top = U(5), Right = x + w + U(8), Bottom = height - U(5) };
                if (hover == part) c.Rect(rect.Left, rect.Top, rect.Width, rect.Height, White, 0.15f);
                c.Text(text, _fontMenu, x, textTop, White);
                x += w + U(24);
                return rect;
            }
            var file = Item("Arquivo", UiPart.MenuFile);
            var settings = Item("Ajustes", UiPart.MenuSettings);
            var web = Item("Navegador (F2)", UiPart.MenuWeb);
            lock (_lock) (_menuFileRect, _menuSettingsRect, _menuWebRect) = (file, settings, web);
            _topLayer = c.Upload(_gpu);
        }
        _gpu.DrawOverlay(target, 0, 0, cw, height, _topLayer);
    }

    private void DrawHint(ID3D11RenderTargetView target, int cw, int ch)
    {
        const string line1 = "Clique aqui para abrir uma imagem/vídeo.", line2 = "(F2 alternar para navegador)";
        int width = Math.Min(cw, U(560)), height = U(70);
        string key = $"{width}";
        if (key != _hintKey || _hintLayer is null)
        {
            _hintKey = key;
            var c = _hintCanvas ??= new UiCanvas();
            c.Begin(width, height);
            int lineHeight = c.TextHeight(_fontHint);
            c.Text(line1, _fontHint, (width - c.TextWidth(line1, _fontHint)) / 2, U(8), White, 0.9f);
            c.Text(line2, _fontText, (width - c.TextWidth(line2, _fontText)) / 2, U(14) + lineHeight, Muted, 0.9f);
            _hintLayer = c.Upload(_gpu);
        }
        _gpu.DrawOverlay(target, (cw - width) / 2, (ch - height) / 2, width, height, _hintLayer);
    }

    private void DrawOsd(ID3D11RenderTargetView target, bool topVisible)
    {
        string text;
        double left;
        lock (_lock) (text, left) = (_osdText, _osdUntil - Now);
        if (text == "" || left <= 0) return;
        float alpha = (float)Math.Clamp(left / OsdFade, 0, 1); // some suavemente no fim
        string key = $"{text}|{(int)(alpha * 20)}";
        var c = _osdCanvas ??= new UiCanvas();
        if (key != _osdKey || _osdLayer is null)
        {
            _osdKey = key;
            int w = c.TextWidth(text, _fontOsd) + U(28), h = c.TextHeight(_fontOsd) + U(14);
            c.Begin(w, h);
            float radius = h / 2f;
            c.Rect(radius, 0, w - 2 * radius, h, 0x000000, 0.7f * alpha); // pílula escura
            c.Circle(radius, radius, radius, 0x000000, 0.7f * alpha);
            c.Circle(w - radius, radius, radius, 0x000000, 0.7f * alpha);
            c.Text(text, _fontOsd, U(14), U(7), White, alpha);
            _osdLayer = c.Upload(_gpu);
        }
        _gpu.DrawOverlay(target, U(16), U(16) + (topVisible ? U(TopHeight) : 0), c.Width, c.Height, _osdLayer);
    }

    private void DrawFileList(ID3D11RenderTargetView target, Native.RECT rect)
    {
        string[] files;
        int current, scroll, hover, visible, columns, thumb, cellW, cellH, treeW, treeScroll, treeHover, dirs;
        TreeNode[] treeRows;
        string? browse;
        bool canBack, canForward, canUp;
        int navHover;
        lock (_lock)
        {
            ClampListScroll();
            (files, current, scroll, hover, visible, dirs) = (_browseFiles, BrowseCurrent(), _listScroll, _listHover, ListVisible(), _browseDirs);
            (columns, thumb) = ListGrid();
            (cellW, cellH) = (U(CellWidth()), U(CellHeight()));
            (treeW, treeRows, treeScroll, treeHover, browse) = (TreeW(), _treeRows, _treeScroll, _treeHover, _browseFolder);
            (canBack, canForward, canUp, navHover) = (CanBack(), CanForward(), ParentFolder() is not null,
                _hover is UiPart.NavBack ? 0 : _hover is UiPart.NavForward ? 1 : _hover is UiPart.NavUp ? 2 : -1);
        }
        _thumbs ??= CreateThumbnails();
        int fullW = rect.Width, h = rect.Height;
        string key = $"{fullW}x{h}|{browse}|{files.Length}|{current}|{scroll}|{hover}|{columns}x{thumb}|{_thumbs.Version}|" +
                     $"{treeRows.Length}|{treeRows.GetHashCode()}|{treeScroll}|{treeHover}|{canBack}{canForward}{canUp}{navHover}";
        if (key != _listKey || _listLayer is null)
        {
            _listKey = key;
            var c = _listCanvas ??= new UiCanvas();
            c.Begin(fullW, h);
            c.Rect(0, 0, fullW, h, 0x202124, 0.92f);
            c.Rect(0, 0, fullW, 1, White, 0.12f);
            DrawTree(c, treeW, h, treeRows, treeScroll, treeHover, browse);
            c.OffsetX = treeW; // os vídeos à direita da árvore
            int w = fullW - treeW;
            // Cabeçalho: pasta e posição.
            string folder = FolderTitle(browse);
            int media = files.Length - dirs;
            string count = current >= 0 ? $"{current - dirs + 1} / {media}"
                : media > 0 || dirs == 0 ? $"{media} arquivo{(media == 1 ? "" : "s")}" : $"{dirs} pasta{(dirs == 1 ? "" : "s")}";
            int countWidth = c.TextWidth(count, _fontText);
            // ← → ↑ (voltar, avançar, pasta de cima), como no Explorer.
            int navY = (U(ListHeader) - U(4) - U(NavButton)) / 2, glyphH = c.TextHeight(_fontChevron);
            string[] glyphs = [IconBack, IconForward, IconUp];
            bool[] enabled = [canBack, canForward, canUp];
            for (int i = 0; i < 3; i++)
            {
                int bx = U(8) + i * U(NavButton);
                if (i == navHover && enabled[i]) c.Rect(bx, navY, U(NavButton), U(NavButton), White, 0.14f);
                string glyph = glyphs[i];
                c.Text(glyph, _fontChevron, bx + (U(NavButton) - c.TextWidth(glyph, _fontChevron)) / 2, navY + (U(NavButton) - glyphH) / 2,
                    White, enabled[i] ? 0.9f : 0.3f);
            }
            int titleX = U(8) + 3 * U(NavButton) + U(8);
            c.Text(Ellipsis(c, folder, _fontMenu, w - countWidth - titleX - U(24)), _fontMenu, titleX, U(12), White);
            c.Text(count, _fontText, w - countWidth - U(14), U(12), Muted);
            c.Rect(U(10), U(ListHeader) - U(4), w - U(20), 1, White, 0.15f);
            int textHeight = c.TextHeight(_fontText);
            if (files.Length == 0) c.Text("Nenhuma pasta, vídeo ou foto aqui", _fontText, U(18), U(ListHeader) + U(10), Muted);
            int rows = (files.Length + columns - 1) / columns;
            for (int row = 0; row < visible && scroll + row < rows; row++)
            for (int column = 0; column < columns; column++)
            {
                int index = (scroll + row) * columns + column;
                if (index >= files.Length) break;
                bool isDir = index < dirs;
                string name = isDir ? Path.GetFileName(files[index]) : Path.GetFileNameWithoutExtension(files[index]);
                uint color = index == current ? Accent : White;
                if (thumb == 0)
                {
                    // Só os nomes.
                    int top = U(ListHeader) + row * cellH;
                    if (index == hover) c.Rect(U(6), top, w - U(12), cellH, White, 0.12f);
                    if (index == current)
                    {
                        c.Rect(U(6), top, w - U(12), cellH, Accent, 0.18f);
                        c.Rect(U(6), top + U(5), U(3), cellH - U(10), Accent, 1);
                    }
                    int nameX = U(18);
                    if (isDir)
                    {
                        int iconSize = Math.Min(U(20), cellH - U(4));
                        if (_thumbs.GetIcon(files[index]) is { } small) c.Icon(small.Bgra, small.Width, small.Height, nameX, top + (cellH - iconSize) / 2, iconSize, iconSize);
                        else c.Text(IconFolder, _fontIcons, nameX, top + (cellH - c.TextHeight(_fontIcons)) / 2, 0xE8C15A);
                        nameX += iconSize + U(8);
                    }
                    c.Text(Ellipsis(c, name, _fontText, w - nameX - U(22)), _fontText, nameX, top + (cellH - textHeight) / 2, color);
                    continue;
                }
                // Ícone: miniatura (como no Explorer) e o nome embaixo.
                int x = U(8) + column * cellW, y = U(ListHeader) + row * cellH;
                if (index == hover) c.Rect(x, y, cellW, cellH, White, 0.12f);
                if (index == current) c.Rect(x, y, cellW, cellH, Accent, 0.22f);
                int tw = cellW - U(12), th = tw * 9 / 16, tx = x + U(6), ty = y + U(6);
                if (isDir)
                {
                    // Pasta: o ícone grande do Windows, sem o fundo preto das miniaturas.
                    if (_thumbs.GetFolder(files[index]) is { } folderIcon) c.Icon(folderIcon.Bgra, folderIcon.Width, folderIcon.Height, tx, ty, tw, th);
                    else c.Text(IconFolder, _fontIcons, tx + (tw - c.TextWidth(IconFolder, _fontIcons)) / 2, ty + (th - c.TextHeight(_fontIcons)) / 2, 0xE8C15A);
                }
                else
                {
                    c.Rect(tx, ty, tw, th, 0x000000, 0.6f);
                    if (_thumbs.Get(files[index]) is { } image) c.Image(image.Bgra, image.Width, image.Height, tx, ty, tw, th);
                }
                string label = Ellipsis(c, name, _fontText, tw);
                c.Text(label, _fontText, tx + (tw - c.TextWidth(label, _fontText)) / 2, ty + th + U(5), color);
            }
            // Barra de rolagem.
            if (rows > visible)
            {
                float trackTop = U(ListHeader), trackHeight = h - U(ListHeader) - U(8);
                float bar = Math.Max(U(20), trackHeight * visible / rows);
                float y = trackTop + (trackHeight - bar) * scroll / Math.Max(1, rows - visible);
                c.Rect(w - U(6), y, U(3), bar, White, 0.35f);
            }
            c.OffsetX = 0;
            _listLayer = c.Upload(_gpu);
        }
        _gpu.DrawOverlay(target, rect.Left, rect.Top, fullW, h, _listLayer);
    }

    private ThumbnailCache CreateThumbnails()
    {
        var thumbs = new ThumbnailCache();
        thumbs.Loaded += () =>
        {
            lock (_lock) _dirty = true; // miniatura nova: redesenha a lista
            _renderWake.Set();
        };
        return thumbs;
    }

    // Corta o texto com "…" para caber na largura.
    private static string Ellipsis(UiCanvas c, string text, IntPtr font, int maxWidth)
    {
        if (c.TextWidth(text, font) <= maxWidth) return text;
        int low = 0, high = text.Length;
        while (low < high)
        {
            int mid = (low + high + 1) / 2;
            if (c.TextWidth(text[..mid] + "…", font) <= maxWidth) low = mid; else high = mid - 1;
        }
        return text[..low] + "…";
    }

    // "00:37" / "02:41" (com hora: "1:02:41").
    private static string ClockText(double seconds)
    {
        var t = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"mm\:ss");
    }

    private void DisposeInterface()
    {
        _bottomCanvas?.Dispose();
        _topCanvas?.Dispose();
        _hintCanvas?.Dispose();
        _listCanvas?.Dispose();
        _osdCanvas?.Dispose();
        if (_fontOsd != IntPtr.Zero) UiCanvas.DeleteFont(_fontOsd);
        if (_fontChevron != IntPtr.Zero) UiCanvas.DeleteFont(_fontChevron);
        _thumbs?.Dispose();
        foreach (var font in new[] { _fontText, _fontMenu, _fontHint, _fontIcons })
            if (font != IntPtr.Zero) UiCanvas.DeleteFont(font);
        _audio?.Dispose();
    }
}
