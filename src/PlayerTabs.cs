using Vortice.Direct3D11;

namespace MKVision.Desktop;

// Abas do player: cada imagem ou vídeo aberto fica na sua aba, com a sua sessão — a posição do vídeo, a velocidade, o
// zoom, o giro e o espelho, o corte (F4), as anotações (F3), as versões da IA (I, U, Y) e a resposta do ComfyUI (T).
// Trocar de aba guarda a que sai e restaura a que entra como estava. Abrir algo que já está numa aba vai para ela;
// senão, abre em aba nova (Page Up/Down, o carrossel e a pasta trocam o conteúdo da própria aba). Só a aba da tela
// usa a placa: o vídeo de uma aba de fundo é fechado (volta na mesma posição) e a foto fica na memória (volta na hora).
// A faixa das abas aparece com o mouse no topo (no lugar do antigo menu Arquivo/Ajustes/Navegador).
internal sealed partial class Player
{
    private sealed class PlayerTab
    {
        public string Path = "";     // o que está aberto (arquivo, endereço de imagem ou "web:N")
        public string Identity = ""; // para "já está aberto?": caminho completo, endereço, página + item do site
        public string Title = "";
        public bool Video;
        public TabState? Saved;      // o estado enquanto a aba não está na tela
        public ComfyPending? Comfy;  // a resposta do ComfyUI que chegou com a aba fora da tela
        public bool ComfyDone;       // "pronto" na aba, até ela aparecer
    }

    // A resposta do ComfyUI esperando a aba (ou o quadro) aparecer: a imagem, de que arquivo e de que momento do vídeo.
    private sealed record ComfyPending(byte[] Pixels, int W, int H, string Path, double Time, bool Video);

    // Uma aba fora da tela.
    private sealed class TabState
    {
        public string[] FolderFiles = [];
        public int FolderIndex = -1;
        public double FileTime, Speed = 1, Zoom = 1, ZoomX, ZoomY;
        public bool Playing, FlipH, FlipV, FromWeb;
        public int Rotation;
        public (double x, double y, double w, double h) Crop = FullCrop, CropBaked = FullCrop;
        public List<AnnoStroke> Strokes = [];
        public string AnnoKey = "";
        public string WebAgent = "";
        public string? WebCookies;
        public IReadOnlyDictionary<string, string> WebHeaders = new Dictionary<string, string>();
        // Foto: ela mesma (a textura fica viva) e o que a tela mostrava dela.
        public Frame? Photo;
        public string PhotoPath = "", FileName = "";
        // Versões da imagem (do quadro do momento StillTime, no vídeo).
        public Frame? ComfyLayer, AiOriginal, AiComfy;
        public int AiOriginalModel, AiComfyModel;
        public bool ShowComfy, ShowAi, ShowTone;
        public Dictionary<Frame, (Frame layer, float exposure)> Tone = [];
        public double StillTime;
        public bool HasLayers => ComfyLayer is not null || AiOriginal is not null || AiComfy is not null || Tone.Count > 0;
    }

    // Com _lock (as listas e a aba da tela).
    private readonly List<PlayerTab> _mediaTabs = [];
    private PlayerTab? _activeTab;
    private TabState? _restore;       // a aba que entra: aplicado quando o arquivo abrir (OpenNow, OpenImage)
    private TabState? _layersRestore; // vídeo: as versões voltam quando o quadro delas aparecer (Draw)
    private ComfyPending? _comfyRestore; // a resposta do ComfyUI esperando o quadro dela aparecer (Draw)
    private int _tabHover = -1;
    private readonly List<(Native.RECT tab, Native.RECT close)> _mediaTabRects = [];

    // Thread da tela.
    private UiCanvas? _tabsCanvas;
    private ID3D11ShaderResourceView? _tabsLayer;
    private string _tabsKey = "";

    private const string IconPhoto = "", IconVideo = "", IconTabClose = "";

    // "Já está aberto?": o caminho completo (sem diferença de maiúsculas), o endereço, ou a página e o item do site.
    private static string IdentityOf(string path)
    {
        if (IsWeb(path) || IsWebKey(path)) return path;
        try { return System.IO.Path.GetFullPath(path).ToLowerInvariant(); } catch { return path.ToLowerInvariant(); }
    }

    private static string WebIdentity(WebMedia media) => $"web|{media.Page}|{media.Item}";

    // ---------- Abrir em abas (thread da janela) ----------

    // Abre em aba nova — ou vai para a aba onde já está aberto. "after": a nova fica logo depois dela (senão, depois da
    // aba da tela).
    public void OpenTab(string path) => OpenTab(path, null, null);

    private void OpenTab(string path, string? identity, PlayerTab? after)
    {
        if (!PrepareTab(identity ?? IdentityOf(path), path, after)) return;
        Open(path);
    }

    // Antes de abrir algo novo: a aba onde já está (vai para ela; false = não abrir de novo) ou uma aba nova, vazia, na
    // tela (true). Thread da janela.
    private bool PrepareTab(string identity, string path, PlayerTab? after = null)
    {
        PlayerTab? existing;
        lock (_lock) existing = _mediaTabs.FirstOrDefault(t => t.Identity == identity);
        if (existing is not null)
        {
            SwitchTab(existing);
            return false;
        }
        var tab = new PlayerTab { Path = path, Identity = identity, Title = TabTitle(path) };
        SaveActive();
        lock (_gpuLock)
        lock (_lock)
        {
            int at = after is not null && _mediaTabs.IndexOf(after) is int a and >= 0 ? a + 1 :
                     _activeTab is not null && _mediaTabs.IndexOf(_activeTab) is int b and >= 0 ? b + 1 : _mediaTabs.Count;
            _mediaTabs.Insert(at, tab);
            _activeTab = tab;
            ResetSession();
            _dirty = true;
        }
        _renderWake.Set();
        return true;
    }

    private static string TabTitle(string path) => IsWebKey(path) ? "Vídeo" : IsWeb(path) ? WebName(path) : System.IO.Path.GetFileName(path);

    // Com _lock: uma aba nova começa sem nada da anterior (giro, espelho, velocidade, anotações).
    private void ResetSession()
    {
        (_rotation, _flipH, _flipV, _speed) = (0, false, false, 1);
        _strokes.Clear();
        (_annoKey, _annoFull, _annotating, _cropping) = ("", true, false, false);
        _layersRestore = null;
        _comfyRestore = null;
        _restore = null;
    }

    // Open (qualquer thread) numa aba: a aba da tela passa a ter este arquivo (sem aba: cria uma). Com _lock.
    private void TrackOpen(string path)
    {
        if (_activeTab is null)
        {
            _activeTab = new PlayerTab { Path = path, Identity = IdentityOf(path), Title = TabTitle(path) };
            _mediaTabs.Add(_activeTab);
        }
        else if (_activeTab.Path != path)
        {
            _activeTab.Path = path;
            _activeTab.Identity = IdentityOf(path);
            _activeTab.Title = TabTitle(path);
        }
    }

    // Com _lock: o nome da aba da tela (o arquivo aberto de verdade: o título do vídeo do site, o nome da foto).
    private void TabOpened(string path, string name, bool video)
    {
        if (_activeTab is not { } tab || tab.Path != path) return;
        tab.Title = name;
        tab.Video = video;
    }

    // ---------- Trocar de aba ----------

    public void SwitchTab(int index)
    {
        PlayerTab? tab;
        lock (_lock) tab = index >= 0 && index < _mediaTabs.Count ? _mediaTabs[index] : null;
        if (tab is not null) SwitchTab(tab);
    }

    private void SwitchTab(PlayerTab target)
    {
        lock (_lock)
            if (target == _activeTab || !_mediaTabs.Contains(target)) return;
        SaveActive();
        Activate(target);
    }

    // A aba da tela guarda o seu estado e sai da tela (a foto e as versões ficam vivas nela; o vídeo é fechado pela
    // próxima a abrir). Thread da janela.
    private void SaveActive()
    {
        _photoJob?.Cancel(); // a IA de fotos desta imagem para (a resposta iria para outra)
        lock (_gpuLock)
        lock (_lock)
        {
            if (_activeTab is not { } tab || !_mediaTabs.Contains(tab)) return;
            var s = new TabState
            {
                FolderFiles = _folderFiles, FolderIndex = _folderIndex, FileTime = _shownFileTime, Speed = _speed,
                Zoom = _zoom, ZoomX = _zoomX, ZoomY = _zoomY, Playing = _playing && _file is not null, FlipH = _flipH, FlipV = _flipV,
                FromWeb = _fromWeb, Rotation = _rotation, Crop = _crop, CropBaked = _cropBaked, Strokes = [.. _strokes], AnnoKey = _annoKey,
                WebAgent = _webUserAgent, WebCookies = _webCookies, WebHeaders = _webImageHeaders, PhotoPath = _photoPath, FileName = _fileName
            };
            if (_image && _queue.FirstOrDefault(f => f.Image) is { } photo)
            {
                s.Photo = photo;
                _queue.Remove(photo); // fica viva na aba (não volta para o reaproveitamento)
            }
            // As versões vão junto; a luz e cor do quadro de um vídeo (que é reaproveitado) não dá para guardar.
            (s.ComfyLayer, s.AiOriginal, s.AiComfy, s.AiOriginalModel, s.AiComfyModel) = (_comfyLayer, _aiOriginal, _aiComfy, _aiOriginalModel, _aiComfyModel);
            (s.ShowComfy, s.ShowAi, s.ShowTone, s.StillTime) = (_showComfy, _showAi, _showTone, _stillTime);
            foreach (var (source, toned) in _tone)
                if (source == s.Photo || source == s.ComfyLayer || source == s.AiOriginal || source == s.AiComfy) s.Tone[source] = toned;
                else DisposeFrame(toned.layer);
            _tone.Clear();
            (_comfyLayer, _aiOriginal, _aiComfy, _still) = (null, null, null, null);
            (_stillShowAi, _showComfy, _showAi, _showTone, _stillSerial) = (false, false, false, false, -1);
            // Uma restauração que ainda não aconteceu (trocou de aba antes do quadro aparecer) continua guardada.
            if (_layersRestore is { } pending && !s.HasLayers)
                (s.ComfyLayer, s.AiOriginal, s.AiComfy, s.ShowComfy, s.ShowAi, s.StillTime, s.Tone) =
                    (pending.ComfyLayer, pending.AiOriginal, pending.AiComfy, pending.ShowComfy, pending.ShowAi, pending.StillTime, pending.Tone);
            if (_comfyRestore is { } waiting) tab.Comfy ??= waiting;
            (_layersRestore, _comfyRestore, _restore) = (null, null, null);
            _strokes.Clear();
            (_annotating, _cropping) = (false, false);
            tab.Saved = s;
        }
    }

    // A aba entra na tela. Foto: volta na hora (a textura dela ficou viva). Vídeo: reabre na posição em que estava.
    private void Activate(PlayerTab target)
    {
        VideoFile? old = null;
        lock (_gpuLock)
        lock (_lock)
        {
            _activeTab = target;
            var s = target.Saved ?? new TabState();
            target.Saved = null;
            target.ComfyDone = false;
            ResetSession();
            (_folderFiles, _folderIndex, _fromWeb) = (s.FolderFiles, s.FolderIndex, s.FromWeb);
            (_webUserAgent, _webCookies, _webImageHeaders) = (s.WebAgent, s.WebCookies, s.WebHeaders);
            _listScroll = -1;
            if (s.Photo is { } photo)
            {
                old = _file;
                _file = null;
                RecycleQueue();
                _image = true;
                _queue.Add(photo);
                (_fileName, _photoPath, _error) = (s.FileName, s.PhotoPath, "");
                (_videoW, _videoH) = (photo.Width, photo.Height);
                (_duration, _anchorMedia, _anchorWall, _loopOffset, _lastQueuedTime, _shownFileTime) = (0, 0, Now, 0, 0, 0);
                (_ended, _playing, _pendingOpen, _seekRequest) = (false, false, null, null);
                _seekGeneration++;
                ApplySession(s);
                if (s.HasLayers)
                {
                    RestoreLayers(s);
                    (_stillSerial, _stillTime) = (photo.Serial, photo.FileTime);
                    ShowLayers();
                }
                if (target.Comfy is { } comfy && comfy.Path == target.Path)
                {
                    ApplyComfy(photo, comfy.Pixels, comfy.W, comfy.H);
                    target.Comfy = null;
                }
            }
            else
            {
                // Vídeo (ou foto que precisa abrir de novo): o arquivo abre e o estado volta (OpenNow/OpenImage).
                _restore = s;
                _pendingOpen = target.Path;
                if (s.HasLayers) _layersRestore = s;
                if (target.Comfy is { } comfy && comfy.Path == target.Path) _comfyRestore = comfy;
                target.Comfy = null;
            }
            _dirty = true;
        }
        old?.Dispose();
        _decodeWake.Set();
        _renderWake.Set();
        UpdateTitle();
    }

    // Com _lock: o que a aba guardou de "como ver" (zoom, giro, espelho, corte, velocidade, anotações).
    private void ApplySession(TabState s)
    {
        (_zoom, _zoomX, _zoomY, _rotation, _flipH, _flipV) = (s.Zoom, s.ZoomX, s.ZoomY, s.Rotation, s.FlipH, s.FlipV);
        (_crop, _cropBaked, _speed) = (s.Crop, s.CropBaked, s.Speed);
        _strokes.Clear();
        _strokes.AddRange(s.Strokes);
        (_annoKey, _annoFull) = (s.AnnoKey, true);
    }

    // Com _lock e _gpuLock: as versões guardadas voltam (de quem é o quadro: quem chama acerta _stillSerial).
    private void RestoreLayers(TabState s)
    {
        (_comfyLayer, _aiOriginal, _aiComfy, _aiOriginalModel, _aiComfyModel) = (s.ComfyLayer, s.AiOriginal, s.AiComfy, s.AiOriginalModel, s.AiComfyModel);
        (_showComfy, _showAi, _showTone) = (s.ShowComfy, s.ShowAi, s.ShowTone);
        foreach (var (source, toned) in s.Tone) _tone[source] = toned;
        (s.ComfyLayer, s.AiOriginal, s.AiComfy) = (null, null, null);
        s.Tone = [];
    }

    // Com _lock (decodificação, ao abrir o arquivo da aba que entrou): o estado dela volta; no vídeo, a posição.
    private void ApplyRestore(string path, bool video)
    {
        if (_restore is not { } s || _activeTab?.Path != path) return;
        _restore = null;
        Log.Write($"Abas: \"{_activeTab.Title}\" volta ({(video ? $"vídeo em {s.FileTime:0.00} s, {(s.Playing ? "tocando" : "parado")}" : "foto")}).");
        ApplySession(s);
        if (!video) return;
        if (s.FileTime > 0.05)
        {
            _seekRequest = s.FileTime;
            (_anchorMedia, _shownFileTime) = (s.FileTime, s.FileTime);
            _seekGeneration++;
        }
        _playing = s.Playing && !s.HasLayers; // com versões (ComfyUI, IA) o quadro fica parado nelas
    }

    // Com _lock e _gpuLock (Draw, com o quadro da tela): as versões e a resposta do ComfyUI esperando este quadro.
    private void AttachPending(Frame frame)
    {
        double tolerance = Math.Max(_frameDuration * 0.6, 0.02);
        if (_layersRestore is { } s)
        {
            if (_playing) // deu play antes do quadro voltar: as versões eram do quadro parado
            {
                DisposeSaved(s);
                _layersRestore = null;
            }
            else if (_file is null || Math.Abs(frame.FileTime - s.StillTime) <= tolerance)
            {
                RestoreLayers(s);
                (_stillSerial, _stillTime) = (frame.Serial, frame.FileTime);
                _layersRestore = null;
                ShowLayers();
            }
        }
        if (_comfyRestore is { } c && _layersRestore is null)
        {
            if (_playing && c.Video) _comfyRestore = null;
            else if (!c.Video || Math.Abs(frame.FileTime - c.Time) <= tolerance)
            {
                ApplyComfy(frame, c.Pixels, c.W, c.H);
                _comfyRestore = null;
            }
        }
    }

    // Com _lock e _gpuLock: a resposta do ComfyUI vira a versão "ComfyUI" deste quadro.
    private void ApplyComfy(Frame frame, byte[] pixels, int w, int h)
    {
        var layer = NewLayer(pixels, w, h, baked: true); // veio da imagem como a tela mostrava
        LayersFor(frame);
        DisposeLayer(ref _comfyLayer);
        DisposeLayer(ref _aiComfy);
        layer.Serial = ++_nextSerial;
        (_comfyLayer, _showComfy, _showAi) = (layer, true, false);
        _cropBaked = FullCrop; // a resposta já vem cortada (foi feita da parte cortada)
        ShowLayers();
    }

    // Com _gpuLock: o que uma aba fechada guardava (a foto e as versões).
    private static void DisposeSaved(TabState s)
    {
        foreach (var (_, toned) in s.Tone) DisposeFrame(toned.layer);
        DisposeFrame(s.ComfyLayer);
        DisposeFrame(s.AiOriginal);
        DisposeFrame(s.AiComfy);
        DisposeFrame(s.Photo);
        (s.Photo, s.ComfyLayer, s.AiOriginal, s.AiComfy) = (null, null, null, null);
        s.Tone = [];
    }

    // ---------- Fechar ----------

    public void CloseTab(int index)
    {
        PlayerTab? tab;
        lock (_lock) tab = index >= 0 && index < _mediaTabs.Count ? _mediaTabs[index] : null;
        if (tab is not null) CloseTab(tab);
    }

    // Fecha a aba (e tudo dela no ComfyUI: na fila sai, gerando é interrompido). A da tela: a do lado entra.
    private void CloseTab(PlayerTab tab)
    {
        CancelComfyFor(tab);
        PlayerTab? next = null;
        bool active;
        lock (_gpuLock)
        lock (_lock)
        {
            int index = _mediaTabs.IndexOf(tab);
            if (index < 0) return;
            active = tab == _activeTab;
            _mediaTabs.RemoveAt(index);
            if (tab.Saved is { } saved) DisposeSaved(saved);
            if (active)
            {
                // Some da tela: as versões e o estado dela vão embora (a foto sai no RecycleQueue de quem abrir).
                DisposeStill();
                if (_layersRestore is { } pending) DisposeSaved(pending);
                ResetSession();
                _activeTab = null;
                next = _mediaTabs.Count == 0 ? null : _mediaTabs[Math.Min(index, _mediaTabs.Count - 1)];
            }
            _tabHover = -1;
            _dirty = true;
        }
        if (active)
        {
            if (next is not null) Activate(next);
            else CloseMedia();
        }
        _renderWake.Set();
        UpdateTitle();
    }

    // Sem abas: a tela vazia ("Clique aqui para abrir...").
    private void CloseMedia()
    {
        VideoFile? old;
        lock (_gpuLock)
        lock (_lock)
        {
            old = _file;
            _file = null;
            _image = false;
            RecycleQueue();
            DisposeStill();
            (_fileName, _photoPath, _error, _pendingOpen, _seekRequest) = ("", "", "", null, null);
            (_folderFiles, _folderIndex, _fromWeb) = ([], -1, false);
            (_playing, _ended, _duration, _shownFileTime) = (false, false, 0, 0);
            (_zoom, _zoomX, _zoomY) = (1, 0, 0);
            (_crop, _cropBaked) = (FullCrop, FullCrop);
            _seekGeneration++;
            _dirty = true;
        }
        old?.Dispose();
        _renderWake.Set();
    }

    // ---------- Faixa das abas (thread da tela) ----------

    private const int MediaTabWidth = 230, MediaTabMin = 120, MediaTabGap = 4;

    // Com _lock: os retângulos das abas e do X de cada uma.
    private void LayoutTabs(int cw)
    {
        _mediaTabRects.Clear();
        int n = _mediaTabs.Count;
        if (n == 0) return;
        int left = U(8), room = cw - left * 2;
        int w = Math.Max(U(MediaTabMin), Math.Min(U(MediaTabWidth), (room - (n - 1) * U(MediaTabGap)) / n));
        int top = U(4), bottom = U(TopHeight) - U(4);
        for (int i = 0, x = left; i < n; i++, x += w + U(MediaTabGap))
        {
            var tab = new Native.RECT { Left = x, Top = top, Right = x + w, Bottom = bottom };
            var close = new Native.RECT { Left = x + w - U(28), Top = top + U(4), Right = x + w - U(6), Bottom = bottom - U(4) };
            _mediaTabRects.Add((tab, close));
        }
    }

    // Com _lock: aba (e o X) no ponto; -1 = nenhuma.
    private (int index, bool close) TabAt(int x, int y)
    {
        LayoutTabs(_clientW);
        for (int i = 0; i < _mediaTabRects.Count; i++)
            if (Inside(_mediaTabRects[i].tab, x, y)) return (i, Inside(_mediaTabRects[i].close, x, y));
        return (-1, false);
    }

    // O selo da aba: o ComfyUI dela (gerando, na fila, pronto).
    private string TabBadge(PlayerTab tab)
    {
        if (ComfyJobOf(tab) is { } job) return job.Running ? (job.Fraction > 0 ? $"{job.Fraction * 100:0}%" : "gerando") : $"fila {QueuePosition(job)}º";
        return tab.ComfyDone || tab.Comfy is not null ? "pronto ✓" : "";
    }

    private void DrawTabStrip(ID3D11RenderTargetView target, int cw)
    {
        int height = U(TopHeight);
        PlayerTab[] tabs;
        PlayerTab? active;
        int hover;
        (Native.RECT tab, Native.RECT close)[] rects;
        lock (_lock)
        {
            LayoutTabs(cw);
            (tabs, active, hover, rects) = ([.. _mediaTabs], _activeTab, _tabHover, [.. _mediaTabRects]);
        }
        if (tabs.Length == 0) return;
        var badges = tabs.Select(TabBadge).ToArray();
        string key = $"{cw}|{hover}|{Array.IndexOf(tabs, active)}|{string.Join("|", tabs.Select((t, i) => t.Title + (t.Video ? "v" : "f") + badges[i]))}|{_hover}";
        if (key != _tabsKey || _tabsLayer is null)
        {
            _tabsKey = key;
            var c = _tabsCanvas ??= new UiCanvas();
            c.Begin(cw, height);
            c.Gradient(0.85f, 0.35f);
            for (int i = 0; i < tabs.Length && i < rects.Length; i++)
            {
                var (r, close) = rects[i];
                var tab = tabs[i];
                bool on = tab == active, over = i == hover;
                c.Rect(r.Left, r.Top, r.Width, r.Height, on ? Accent : White, on ? 0.38f : over ? 0.16f : 0.08f);
                int textTop = r.Top + (r.Height - c.TextHeight(_fontMenu)) / 2, x = r.Left + U(10);
                string icon = tab.Video ? IconVideo : IconPhoto;
                c.Text(icon, _fontChevron, x, r.Top + (r.Height - c.TextHeight(_fontChevron)) / 2, White, 0.85f);
                x += c.TextWidth(icon, _fontChevron) + U(8);
                // O selo (ComfyUI) à direita, antes do X; o nome ocupa o resto (cortado com "…").
                int right = close.Left - U(6);
                if (badges[i].Length > 0)
                {
                    int bw = c.TextWidth(badges[i], _fontChevron) + U(10);
                    c.Rect(right - bw, r.Top + U(7), bw, r.Height - U(14), badges[i].StartsWith("pronto") ? 0x34C759u : Accent, 0.85f);
                    c.Text(badges[i], _fontChevron, right - bw + U(5), r.Top + (r.Height - c.TextHeight(_fontChevron)) / 2, White, 1);
                    right -= bw + U(6);
                }
                c.Text(Ellipsis(c, tab.Title, _fontMenu, Math.Max(0, right - x)), _fontMenu, x, textTop, White, on ? 1 : 0.85f);
                bool closeHover = over && _hover == UiPart.PlayerTabClose;
                if (closeHover) c.Rect(close.Left, close.Top, close.Width, close.Height, White, 0.2f);
                c.Text(IconTabClose, _fontChevron, close.Left + (close.Width - c.TextWidth(IconTabClose, _fontChevron)) / 2,
                    close.Top + (close.Height - c.TextHeight(_fontChevron)) / 2, White, over || on ? 0.95f : 0.55f);
            }
            _tabsLayer = c.Upload(_gpu);
        }
        _gpu.DrawOverlay(target, 0, 0, cw, height, _tabsLayer);
    }

    // Botão do meio numa aba: fecha. true = era uma aba.
    private bool TabMiddleClick(int x, int y)
    {
        int index;
        lock (_lock)
        {
            if (y >= U(TopHeight) || _mediaTabs.Count == 0) return false;
            index = TabAt(x, y).index;
        }
        if (index < 0) return false;
        CloseTab(index);
        return true;
    }

    // Ctrl+Tab / Ctrl+Shift+Tab.
    private void NextTab(int direction)
    {
        PlayerTab? target;
        lock (_lock)
        {
            if (_mediaTabs.Count < 2 || _activeTab is null) return;
            int index = _mediaTabs.IndexOf(_activeTab);
            target = _mediaTabs[((index + direction) % _mediaTabs.Count + _mediaTabs.Count) % _mediaTabs.Count];
        }
        SwitchTab(target);
    }

    // Autoteste.
    public string[] TestMediaTabs { get { lock (_lock) return [.. _mediaTabs.Select(t => (t == _activeTab ? "*" : "") + t.Title + (TabBadge(t) is { Length: > 0 } b ? $" [{b}]" : ""))]; } }
    public string TestMouse { get { lock (_lock) return $"{_mouseX},{_mouseY} dentro {_mouseInside} parte {_hover} aba {_tabHover} topo {TopVisible()} abas {_mediaTabs.Count}"; } }
    public void TestNextTab(int direction) => NextTab(direction);
    public void TestSwitchTab(int index) => SwitchTab(index);
    public void TestCloseTab(int index) => CloseTab(index);
    public static string TestStepText(int stage, int stages, int step, int steps, double elapsed, double perStep, double remaining) =>
        StepText(new ComfyClient.StepInfo(stage, stages, step, steps, elapsed, perStep, remaining), 0);
    public int TestActiveTab { get { lock (_lock) return _activeTab is null ? -1 : _mediaTabs.IndexOf(_activeTab); } }
    public (int x, int y) TestTabCenter(int index, bool close = false)
    {
        lock (_lock)
        {
            LayoutTabs(_clientW);
            var r = close ? _mediaTabRects[index].close : _mediaTabRects[index].tab;
            return ((r.Left + r.Right) / 2, (r.Top + r.Bottom) / 2);
        }
    }
}
