using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Vortice.Direct3D11;
using static MKVision.Desktop.PlayerNative;

namespace MKVision.Desktop;

// Navegador dentro do player (WebView2, o motor do Edge que vem com o Windows). F2, "Navegador" no menu do topo
// ou o botão da barra alternam entre ele e o player. Em cima, três faixas: abas, barra de endereço (← → ⟳,
// endereço com a ☆ de favorito, o escudo do bloqueador, "Player") e a barra de favoritos (com pastas). As abas abertas voltam na
// próxima vez. Pop-ups que a página abre sozinha (sem clique) são bloqueados; os de clique abrem em aba nova (botão
// do meio e Ctrl+clique: atrás, sem sair da aba de agora).
//
// Os vídeos tocam no próprio site; a tecla "." leva ao player o vídeo da tela (ou a imagem sob o mouse, que abre
// no visualizador com as outras imagens grandes da página) — e "Abrir no player", no botão direito sobre uma imagem
// ou vídeo, leva aquele. ESC no player (em janela) volta ao site. Login e cookies ficam no perfil próprio do app
// (%LOCALAPPDATA%\MKVisionDesktop\browser) e são passados ao baixar o vídeo.
internal sealed partial class Player
{
    private const int TabStrip = 36, WebBar = 44, BookmarkBar = 30, WebButton = 38;
    private const string HomePage = "https://www.google.com/";
    // Configurações do navegador: página do próprio app (pasta Web\settings), mostrada como "mkvision://configurações".
    private const string SettingsHost = "configuracoes.mkvision", SettingsUrl = "https://" + SettingsHost + "/index.html",
        SettingsShown = "mkvision://configurações";
    private const string IconReload = "", IconStop = "", IconPlayer = "", IconAdd = "",
        IconClose = "", IconStar = "", IconStarFilled = "", IconGlobe = "", IconShield = "", IconSettings = "";

    private sealed class WebTab
    {
        public CoreWebView2Controller? Controller;
        public CoreWebView2? Web;
        public string Url = HomePage, Title = "Nova aba";
        public bool Loading, CanBack, CanForward, Creating;
        public bool Mobile;               // aberta como num celular (sites da lista "Modo celular")
        public bool Visited;              // já foi a aba ativa (antes disso fica muda: aba restaurada ou de fundo não toca som sozinha)
        public long NavigationStart;      // quando a navegação começou (para registrar páginas lentas)
        public bool Unresponsive;         // a página não responde (aparece no título da aba)
        public bool Audible, Muted, UserMuted; // tocando som / sem som (no player, de fundo não aberta ou pelo alto-falante) / silenciada por você
        public bool Sleeping;             // "dormindo": o motor congelou a página (acorda ao aparecer)
        public long HiddenAt;             // quando saiu da tela (para dormir depois de um tempo)
        public bool CertError;            // certificado com problema (o cadeado vira aviso)
        public double Zoom = 1;           // zoom da página (cópia para a tela; o do motor só se lê na thread da janela)
        public string? Language;          // língua que a página diz ter ("en", "pt-br"...; vazio = não diz)
        public ThumbnailCache.Thumb? Icon;
        public readonly List<CoreWebView2Frame> Frames = [];
    }

    private CoreWebView2Environment? _webEnv;
    private readonly List<WebTab> _tabs = [];  // com _lock (a tela lê)
    private WebTab? _tab;                       // aba ativa
    private readonly Stack<string> _closedTabs = new();
    private CoreWebView2Controller? _webController => _tab?.Controller;
    private CoreWebView2? _web => _tab?.Web;
    private bool _webMode, _webStarting, _webReady;
    private string _webGrabbed = "";      // último vídeo aberto pelo modo automático (não abre de novo em seguida)
    private double _webGrabbedAt;
    private List<(JsonElement info, CoreWebView2Frame? frame)>? _webCandidates; // respostas à tecla "."
    private UiCanvas? _webCanvas;
    private ID3D11ShaderResourceView? _webLayer;
    private string _webKey = "";
    private string _webTitle = "";           // título da aba ativa (para a janela)
    private const uint WS_CLIPCHILDREN = 0x02000000; // o site (janela filha) não é apagado pelo desenho do player

    // Favoritos e ícones dos sites (por domínio; também em disco, para a barra de favoritos).
    private BookmarkNode? _bookmarks;
    private readonly Dictionary<string, ThumbnailCache.Thumb?> _favicons = new(StringComparer.OrdinalIgnoreCase);
    private static readonly string FaviconFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MKVisionDesktop", "favicons");

    // Onde ficou cada coisa no último desenho (com _lock): para o mouse.
    private readonly List<(Native.RECT tab, Native.RECT close, Native.RECT audio, int index)> _tabRects = []; // index = posição em _tabs
    private readonly List<(Native.RECT rect, BookmarkNode node)> _bookmarkRects = [];
    private Native.RECT _webShieldRect, _webSettingsRect;
    private bool _addressFocusPending; // aba nova: o foco fica no endereço (não vai para o site)
    private Native.RECT _newTabRect, _webBackRect, _webForwardRect, _webReloadRect, _webAddressRect, _webStarRect,
        _webAutoRect, _webPlayerRect, _bookmarksMoreRect;
    private int _webHoverIndex = -1;
    private int _bookmarksShown;              // quantos favoritos couberam na barra (o resto vai no »)

    // Barra de endereço (controle EDIT do Windows, sobre a barra desenhada).
    private IntPtr _address, _addressFont, _addressBrush, _addressOldProc;
    private WndProcDelegate? _addressProc;
    private delegate IntPtr WndProcDelegate(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr CallWindowProc(IntPtr previous, IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern IntPtr SetFocus(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern IntPtr GetFocus();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextW(IntPtr hwnd, char[] text, int max);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateSolidBrush(uint color);
    [DllImport("gdi32.dll")] private static extern uint SetTextColor(IntPtr hdc, uint color);
    [DllImport("gdi32.dll")] private static extern uint SetBkColor(IntPtr hdc, uint color);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr SendMessageW(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
    private const uint WM_CTLCOLOREDIT = 0x0133, WM_SETFONT = 0x0030, EM_SETSEL = 0x00B1, WM_CHAR = 0x0102, EM_SETMARGINS = 0x00D3;
    private const uint WS_CHILD = 0x40000000, ES_AUTOHSCROLL = 0x0080;
    private const uint AddressBack = 0x3B3735, AddressText = 0xFFFFFF; // COLORREF (0x00BBGGRR) = #35373B
    private const uint StripColor = 0x17181B, BarColor = 0x26272B, FieldColor = 0x35373B;

    public bool WebMode { get { lock (_lock) return _webMode; } }

    // Para o autoteste.
    public bool WebReady => _webReady && _web is not null;
    public string TestWebTitle { get { lock (_lock) return _webTitle; } }
    public void TestWebNavigate(string url) => _web?.Navigate(url);
    public void TestWebScript(string js) => _ = _web?.ExecuteScriptAsync(js);
    public Task<string> TestWebEval(string js) => _web!.ExecuteScriptAsync(js);
    public int TestTabCount { get { lock (_lock) return _tabs.Count; } }
    public void TestDownload() => AskFrames(download: true);
    public void TestNewTab(string url) => _ = OpenTab(url, select: true);
    public void TestCloseTab() { if (_tab is not null) CloseTab(_tab); }
    public BookmarkNode TestBookmarks => _bookmarks ??= Bookmarks.Load();

    // ---------- Contexto da thread principal (o WebView2 só aceita chamadas dela) ----------

    private const uint WM_RUN = 0x8000 + 0x41;
    private readonly ConcurrentQueue<(SendOrPostCallback callback, object? state)> _uiQueue = new();

    private sealed class UiContext(Player player) : SynchronizationContext
    {
        public override void Post(SendOrPostCallback d, object? state)
        {
            player._uiQueue.Enqueue((d, state));
            PostMessage(player.Hwnd, WM_RUN, IntPtr.Zero, IntPtr.Zero);
        }

        public override void Send(SendOrPostCallback d, object? state) => throw new NotSupportedException();
        public override SynchronizationContext CreateCopy() => this;
    }

    private UiContext? _ui;

    // De qualquer thread: roda na thread da janela (o WebView2 só aceita chamadas dela).
    private void PostUi(Action action) => _ui?.Post(_ => action(), null);

    private void RunUiQueue()
    {
        while (_uiQueue.TryDequeue(out var item))
        {
            try { item.callback(item.state); }
            catch (Exception error) { Log.Write($"Navegador: {error.Message}"); }
        }
    }

    // ---------- Alternar player / navegador (thread principal) ----------

    public void ToggleWeb() => ShowWeb(!WebMode);
    public void ShowBrowser() => ShowWeb(true);

    // O player estava em tela cheia ao ir para o navegador: o navegador abre em janela (com as abas e os botões dela) e,
    // na volta, o player fica em tela cheia de novo.
    private bool _fullscreenBeforeWeb;

    private void ShowWeb(bool show)
    {
        bool toggleFull;
        lock (_lock)
        {
            if (_webMode == show) return;
            _webMode = show;
            toggleFull = show ? _fullscreen && !_f11 : _fullscreenBeforeWeb && !_fullscreen;
            _fullscreenBeforeWeb = show && toggleFull;
            if (show && _playing && _file is not null)
            {
                Reanchor();
                _playing = false; // o vídeo espera enquanto você navega
            }
            _listOpen = false;
            _dirty = true;
        }
        if (toggleFull) ToggleFullscreen(notify: false); // já refaz a moldura
        else RefreshFrame(); // no navegador, sem a barra de título do Windows (a faixa das abas é o topo)
        if (show) StartWeb();
        ApplyTabVisibility();
        foreach (var tab in TabsSnapshot()) ApplyMute(tab); // no player, os sites ficam mudos
        if (!show)
        {
            HideTabTip();
            HideSuggestions();
        }
        if (show) _webController?.MoveFocus(CoreWebView2MoveFocusReason.Programmatic);
        if (!show) SetFocus(Hwnd);
        LayoutWeb();
        _renderWake.Set();
        UpdateTitle();
    }

    private WebTab[] TabsSnapshot() { lock (_lock) return [.. _tabs]; }

    private async void StartWeb()
    {
        if (_webEnv is not null || _webStarting) return;
        _webStarting = true;
        try
        {
            CreateAddressBar();
            string? testProfile = Environment.GetEnvironmentVariable("MKV_WEB_PROFILE"); // autoteste: perfil separado
            if (testProfile is null) WebResolver.DeleteCookieFiles(); // sobras de uma sessão anterior
            Bookmarks.Saved += _ => PersistFavicons();
            PersistFavicons(); // ícones de sites que não são favoritos (de antes) saem do disco
            _bookmarks ??= Bookmarks.Load();
            string folder = testProfile ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MKVisionDesktop", "browser");
            var options = new CoreWebView2EnvironmentOptions { AreBrowserExtensionsEnabled = true };
            _webEnv = await CoreWebView2Environment.CreateAsync(null, folder, options);
            // Ao iniciar (Configurações): as abas da última vez, só a página inicial ou as páginas escolhidas
            // (só a ativa carrega agora; as outras, quando forem abertas).
            var urls = _prefs.WebStartup switch
            {
                1 => [HomePage],
                2 => _prefs.WebStartupPages.Where(IsWeb).ToList(),
                _ => _prefs.WebTabs.Where(IsWeb).ToList()
            };
            if (urls.Count == 0) urls.Add(_prefs.WebStartup == 0 && _prefs.WebUrl is { Length: > 0 } last && IsWeb(last) ? last : HomePage);
            lock (_lock)
                foreach (var url in urls) _tabs.Add(new WebTab { Url = url, Title = HostOf(url) });
            await SelectTab(_tabs[_prefs.WebStartup == 0 ? Math.Clamp(_prefs.WebTab, 0, _tabs.Count - 1) : 0]);
            ApplyColorScheme(); // tema que os sites veem (Configurações → Aparência)
            if (_web is not null)
            {
                await AdBlock.Install(_web.Profile); // bloqueador de anúncios (vale para todas as abas)
                await SyncAdBlockSites();            // antes de recarregar: a primeira página já vem sem bloqueio
                // A primeira página carregou antes dele: recarrega. Se ela ainda nem abriu (bloqueador pronto na
                // hora), recarregar deixaria a aba em branco: abre o endereço de novo.
                if (_web.Source == "about:blank" && _tab is not null) _web.Navigate(_tab.Url);
                else _web.Reload();
            }
            _webReady = true;
            PreloadTabs();
            if (_prefs.WebClearOnClose && _web is not null) _ = ClearPrivateData(_web.Profile); // se o app caiu, a limpeza do fechamento não aconteceu
            Log.Write($"Navegador: pronto (motor {_webEnv.BrowserVersionString}), {urls.Count} aba(s).");
        }
        catch (Exception error)
        {
            Log.Write($"Navegador: não abriu — {error.Message}");
            Notify("O navegador não abriu: " + error.Message);
            ShowWeb(false);
        }
        finally { _webStarting = false; }
    }

    // ---------- Abas ----------

    // Cria o WebView da aba (na primeira vez que ela aparece). "popup": a página pediu esta aba (window.open):
    // ela vira a janela nova da página (o site continua falando com ela, como no login do Google).
    private async Task EnsureTab(WebTab tab, CoreWebView2NewWindowRequestedEventArgs? popup = null)
    {
        if (tab.Controller is not null || tab.Creating || _webEnv is null) return;
        tab.Creating = true;
        var created = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var controller = await _webEnv.CreateCoreWebView2ControllerAsync(Hwnd);
            if (created.ElapsedMilliseconds > 1500) Log.Write($"Navegador: uma aba demorou {created.ElapsedMilliseconds} ms para ser criada.");
            bool open;
            lock (_lock) open = _tabs.Contains(tab);
            if (!open && popup is null)
            {
                controller.Close(); // a aba foi fechada enquanto o site dela nascia: não fica um site escondido tocando
                return;
            }
            var web = controller.CoreWebView2;
            controller.DefaultBackgroundColor = System.Drawing.Color.FromArgb(255, 32, 33, 36);
            web.Settings.IsPasswordAutosaveEnabled = true; // salvar senhas (como no Edge)
            web.Settings.IsGeneralAutofillEnabled = true;  // preencher formulários
            web.Settings.IsReputationCheckingRequired = _prefs.WebSmartScreen; // SmartScreen (Configurações → Privacidade)
            await web.AddScriptToExecuteOnDocumentCreatedAsync(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Web", "page.js")));
            web.SetVirtualHostNameToFolderMapping(SettingsHost, Path.Combine(AppContext.BaseDirectory, "Web", "settings"), CoreWebView2HostResourceAccessKind.Deny);
            web.SourceChanged += (_, _) => OnTabNavigated(tab);
            web.HistoryChanged += (_, _) => OnTabNavigated(tab);
            web.DocumentTitleChanged += (_, _) =>
            {
                lock (_lock) tab.Title = web.DocumentTitle is { Length: > 0 } t ? t : HostOf(web.Source);
                if (tab == _tab) lock (_lock) _webTitle = tab.Title;
                WebDirty();
                UpdateTitle();
            };
            web.NavigationStarting += (_, e) => OnNavigationStarting(tab, e);
            web.ServerCertificateErrorDetected += (_, _) => { tab.CertError = true; WebDirty(); }; // o motor mostra o aviso dele
            web.NavigationCompleted += (_, e) =>
            {
                tab.Loading = false;
                double seconds = (Environment.TickCount64 - tab.NavigationStart) / 1000.0;
                if (!e.IsSuccess && e.WebErrorStatus != CoreWebView2WebErrorStatus.OperationCanceled)
                    Log.Write($"Navegador: uma página não carregou ({e.WebErrorStatus}, {seconds:0.0} s).");
                else if (tab.NavigationStart > 0 && seconds > 6)
                    Log.Write($"Navegador: uma página demorou {seconds:0.0} s para carregar.");
                SaveSession();
                OnTabNavigated(tab);
                DetectLanguage(tab); // botão de traduzir
            };
            web.FaviconChanged += (_, _) => LoadFavicon(tab);
            web.NewWindowRequested += (_, e) => OnNewWindow(tab, e);
            web.WindowCloseRequested += (_, _) => CloseTab(tab); // a página fechou a própria janela (window.close)
            web.ContainsFullScreenElementChanged += (_, _) =>
            {
                bool full;
                lock (_lock) full = _fullscreen;
                _siteFullscreen = web.ContainsFullScreenElement;
                if (web.ContainsFullScreenElement != full && !(full && _f11)) ToggleFullscreen();
                LayoutWeb();
            };
            web.WebMessageReceived += (_, e) => OnWebMessage(e.WebMessageAsJson, null, tab);
            web.FrameCreated += (_, e) => TrackFrame(tab, e.Frame);
            controller.ZoomFactorChanged += (_, _) => OnZoomChanged(tab);
            web.ContextMenuRequested += (_, e) => OnContextMenu(tab, e);
            controller.AcceleratorKeyPressed += (_, e) =>
            {
                if (e.KeyEventKind is not (CoreWebView2KeyEventKind.KeyDown or CoreWebView2KeyEventKind.SystemKeyDown)) return;
                if (WebShortcut((int)e.VirtualKey)) e.Handled = true;
            };
            web.IsMuted = tab.Muted = !WebMode || !tab.Visited || tab.UserMuted;
            web.IsDocumentPlayingAudioChanged += (_, _) => OnAudioChanged(tab);
            web.ProcessFailed += (_, e) => OnProcessFailed(tab, e);
            (tab.Controller, tab.Web) = (controller, web);
            // Já nasce num site do modo celular: liga antes da primeira página (refazer a navegação de uma aba
            // recém-criada não funciona, e um pop-up perderia a ligação com a página que o abriu).
            if (WantsMobile(popup?.Uri ?? tab.Url)) await SetMobile(tab, true);
            if (_prefs.WebForceDark) await SetForceDark(web, true); // escurecer os sites sem tema escuro (Configurações)
            if (popup is not null) popup.NewWindow = web;
            else web.Navigate(tab.Url);
            controller.IsVisible = WebMode && tab == _tab;
            LayoutWeb();
        }
        catch (Exception error) { Log.Write($"Navegador: a aba não abriu — {error.Message}"); }
        finally { tab.Creating = false; }
    }

    // Abas restauradas da última vez: carregam em segundo plano, uma de cada vez, as mais perto da ativa primeiro
    // (sem isso, cada uma só começava a carregar ao ser clicada: ficava escura e demorava). No máximo 8; ficam mudas
    // até você entrar nelas.
    private async void PreloadTabs()
    {
        await Task.Delay(1500); // a ativa termina de carregar antes
        WebTab[] tabs = TabsSnapshot();
        int active = _tab is null ? 0 : Math.Max(0, Array.IndexOf(tabs, _tab));
        foreach (var tab in tabs.Where(t => t.Controller is null).OrderBy(t => Math.Abs(Array.IndexOf(tabs, t) - active)).Take(8))
        {
            if (_webEnv is null || !TabsSnapshot().Contains(tab)) continue;
            await EnsureTab(tab);
            await Task.Delay(400);
        }
    }

    // O processo da página (ou do navegador) caiu ou travou. Página caída fica escura: recarrega. Travada: avisa no
    // título da aba (F5 recarrega). O navegador inteiro caiu: abre de novo com as mesmas abas.
    private bool _browserRestarting;
    private void OnProcessFailed(WebTab tab, CoreWebView2ProcessFailedEventArgs e)
    {
        Log.Write($"Navegador: processo falhou numa aba — {e.ProcessFailedKind} ({e.Reason}, código {e.ExitCode}).");
        switch (e.ProcessFailedKind)
        {
            case CoreWebView2ProcessFailedKind.RenderProcessUnresponsive:
                tab.Unresponsive = true;
                lock (_lock) tab.Title = "Sem resposta (F5 recarrega) — " + tab.Title.Replace("Sem resposta (F5 recarrega) — ", "");
                WebDirty();
                break;
            case CoreWebView2ProcessFailedKind.RenderProcessExited:
            case CoreWebView2ProcessFailedKind.FrameRenderProcessExited when e.FrameInfosForFailedProcess.Count == 0:
                PostUi(() =>
                {
                    try { tab.Web?.Reload(); } catch { }
                    if (tab == _tab) WebToast("A página tinha caído e foi recarregada");
                });
                break;
            case CoreWebView2ProcessFailedKind.BrowserProcessExited:
                if (_browserRestarting) return;
                _browserRestarting = true;
                PostUi(RestartBrowser);
                break;
        }
    }

    // O navegador caiu: guarda as abas, descarta tudo e abre de novo (se estiver na tela).
    private void RestartBrowser()
    {
        SaveSession();
        WebTab[] old;
        lock (_lock)
        {
            old = [.. _tabs];
            _tabs.Clear();
            _tab = null;
            _dirty = true;
        }
        foreach (var tab in old) try { tab.Controller?.Close(); } catch { }
        _webEnv = null;
        _webReady = false;
        _browserRestarting = false;
        Notify("O navegador tinha fechado sozinho e foi aberto de novo");
        if (WebMode) StartWeb();
    }

    // Quem chama não espera a troca (_ = SelectTab): um erro aqui sumia em silêncio e a troca parava no meio — a aba
    // nova ficava escondida, com a tela preta (o site tocando por trás), e nem F5 nem um endereço novo apareciam.
    // Agora o erro vai para o log e a aba da tela é mostrada de qualquer jeito.
    private async Task SelectTab(WebTab tab)
    {
        lock (_lock)
        {
            _tab = tab;
            _webTitle = tab.Title;
            _dirty = true;
        }
        try
        {
            ApplyTabVisibility(); // a anterior some já; esta aparece assim que o site dela existir
            await EnsureTab(tab);
            if (_tab != tab) return;
            tab.Visited = true;
            ApplyMute(tab);
            ApplyTabVisibility();
            if (tab.Controller is not null && WebMode && !_addressFocusPending) tab.Controller.MoveFocus(CoreWebView2MoveFocusReason.Programmatic);
            if (_address != IntPtr.Zero) SetWindowText(_address, ShownUrl(tab.Web?.Source is { } source && source != "about:blank" ? source : tab.Url)); // a aba nova ainda não navegou
        }
        catch (Exception error) { Log.Write($"Navegador: erro ao trocar de aba — {error.Message}"); }
        LayoutWeb();
        SaveSession();
        UpdateTitle();
        _renderWake.Set();
    }

    // Só a aba da tela fica visível (com o navegador aberto); as outras, escondidas. Cada uma à parte: um erro numa
    // não impede as outras.
    private void ApplyTabVisibility()
    {
        bool web = WebMode;
        foreach (var tab in TabsSnapshot())
        {
            if (tab.Controller is not { } controller) continue;
            bool visible = web && tab == _tab;
            if (visible) (tab.Sleeping, tab.HiddenAt) = (false, 0); // aparecendo, o motor acorda a página sozinho
            else if (tab.HiddenAt == 0) tab.HiddenAt = Environment.TickCount64;
            try { controller.IsVisible = visible; }
            catch (Exception error) { Log.Write($"Navegador: não consegui {(tab == _tab ? "mostrar" : "esconder")} uma aba — {error.Message}"); }
        }
    }

    // Aba nova depois da atual — e depois das outras que você já abriu a partir dela, na ordem em que abriu (como no
    // Edge: três links abertos com o botão do meio ficam 1, 2, 3, e não 3, 2, 1). Trocando de aba, recomeça.
    private WebTab? _lastChild, _lastChildOf;

    private async Task<WebTab> OpenTab(string url, bool select, CoreWebView2NewWindowRequestedEventArgs? popup = null)
    {
        var tab = new WebTab { Url = url, Title = HostOf(url) };
        lock (_lock)
        {
            int at = _tab is null ? _tabs.Count : _tabs.IndexOf(_tab) + 1;
            if (_tab is not null && _lastChildOf == _tab && _lastChild is not null && _tabs.IndexOf(_lastChild) >= at) at = _tabs.IndexOf(_lastChild) + 1;
            _tabs.Insert(Math.Clamp(at, 0, _tabs.Count), tab);
            (_lastChild, _lastChildOf) = (tab, _tab);
            _dirty = true;
        }
        if (popup is not null) await EnsureTab(tab, popup);
        if (select) await SelectTab(tab);
        else
        {
            SaveSession();
            _renderWake.Set();
            await EnsureTab(tab); // aba de fundo (Ctrl+clique, botão do meio): já carrega, como no Chrome
        }
        return tab;
    }

    private void CloseTab(WebTab tab)
    {
        WebTab? next = null;
        bool empty;
        lock (_lock)
        {
            int index = _tabs.IndexOf(tab);
            if (index < 0) return;
            _tabs.RemoveAt(index);
            if (tab == _tab && _tabs.Count > 0) next = _tabs[Math.Min(index, _tabs.Count - 1)];
            empty = _tabs.Count == 0;
            _dirty = true;
        }
        _closedTabs.Push(tab.Web?.Source ?? tab.Url);
        // O site da aba fechada é desfeito e sai da aba: nada mais mexe nele (antes a troca para a próxima tentava
        // escondê-lo, dava erro e a próxima ficava escondida — tela preta).
        var controller = tab.Controller;
        (tab.Controller, tab.Web) = (null, null);
        tab.Frames.Clear();
        try { controller?.Close(); } catch { }
        if (empty) _ = OpenTab(HomePage, select: true);
        else if (next is not null) _ = SelectTab(next);
        else SaveSession();
        _renderWake.Set();
    }

    // Pop-ups: com clique, aba nova (é o que a página quis); sem clique (anúncio), bloqueado. Botão do meio e
    // Ctrl+clique num link: a aba fica atrás e você continua na de agora, como no Chrome (a página avisa ao apertar o
    // botão, page.js; sem o aviso, vale o Ctrl apertado). Os outros (target=_blank, window.open) vêm para a frente —
    // só depois de a abertura terminar: a página de origem fica parada esperando por ela e não é escondida no meio.
    private (string href, bool background, long at)? _linkPress;

    private async void OnNewWindow(WebTab from, CoreWebView2NewWindowRequestedEventArgs e)
    {
        if (!e.IsUserInitiated)
        {
            e.Handled = true;
            Log.Write("Navegador: pop-up bloqueado.");
            if (from == _tab) WebToast("Pop-up bloqueado");
            return;
        }
        bool background = KeyDown(VK_CONTROL) && !KeyDown(VK_SHIFT);
        if (_linkPress is { } press)
        {
            long age = Environment.TickCount64 - press.at;
            // O mesmo link, ou logo em seguida (sites que trocam o endereço do link no clique, como o Google).
            if (age < 1500 || (age < 10_000 && press.href == e.Uri)) background = press.background;
            _linkPress = null;
        }
        var deferral = e.GetDeferral();
        WebTab tab;
        try { tab = await OpenTab(e.Uri, select: false, popup: e); }
        finally { deferral.Complete(); }
        if (background) return;
        await Task.Yield(); // a página de origem recebe a janela nova antes de ir para trás
        bool open;
        lock (_lock) open = _tabs.Contains(tab);
        if (open) await SelectTab(tab);
    }

    private void SaveSession()
    {
        lock (_lock)
        {
            _prefs.WebTabs = _tabs.Select(t => t.Web?.Source ?? t.Url).Where(IsWeb).ToList();
            _prefs.WebTab = _tab is null ? 0 : Math.Max(0, _tabs.IndexOf(_tab));
            if (_tab?.Web?.Source is { } url && IsWeb(url)) _prefs.WebUrl = url;
        }
        _prefs.Save();
    }

    // Privacidade: o histórico que o motor guarda sozinho (arquivo "History", mesmo sem tela de histórico), a lista de
    // downloads dele e o cache das páginas. Cookies, logins e senhas ficam (você continua logado).
    private const CoreWebView2BrowsingDataKinds PrivateData = CoreWebView2BrowsingDataKinds.BrowsingHistory |
        CoreWebView2BrowsingDataKinds.DownloadHistory | CoreWebView2BrowsingDataKinds.DiskCache | CoreWebView2BrowsingDataKinds.CacheStorage;

    private static async Task ClearPrivateData(CoreWebView2Profile profile)
    {
        try { await profile.ClearBrowsingDataAsync(PrivateData); }
        catch (Exception error) { Log.Write($"Navegador: não consegui apagar o histórico interno ({error.Message})."); }
    }

    // Fechando a janela: com o navegador aberto, apaga primeiro (no máximo 5 s, com a janela já escondida) e só
    // depois fecha. true = a limpeza começou (a janela fecha sozinha quando terminar).
    private bool _privacyDone;
    private bool ClearBeforeClose()
    {
        if (_privacyDone || !_prefs.WebClearOnClose || _web is not { } web) return false;
        _privacyDone = true;
        Native.ShowWindow(Hwnd, Native.SW_HIDE);
        _ = Finish();
        return true;
        async Task Finish()
        {
            await Task.WhenAny(ClearPrivateData(web.Profile), Task.Delay(5000));
            PostMessage(Hwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
        }
    }

    // Sites sem bloqueio (Configurações → Bloqueador): libera os da lista no uBlock e devolve os que saíram. A
    // liberação é uma regra "de sessão" (some quando o navegador fecha, e o uBlock pode limpá-la ao atualizar as
    // listas): refeita ao abrir e, de 10 em 10 minutos no máximo, ao entrar num site da lista.
    private long _adblockSyncedAt;
    private async Task SyncAdBlockSites()
    {
        if (_webEnv is null || AdBlock.Id is null) return;
        _adblockSyncedAt = Environment.TickCount64;
        var allow = _prefs.AdBlockAllowed.ToList();
        var restore = _prefs.AdBlockApplied.Except(allow, StringComparer.OrdinalIgnoreCase).ToList();
        var modes = await AdBlock.SetSiteModes(_webEnv, Hwnd, allow, restore);
        if (modes is null) return;
        _prefs.AdBlockApplied = allow.Where(h => modes.TryGetValue(h, out int level) && level == 0).ToList();
        _prefs.Save();
        Log.Write($"Navegador: sem bloqueio de anúncios em {allow.Count} site(s)" + (restore.Count > 0 ? $"; {restore.Count} de volta ao normal" : "") + ".");
    }

    public Task TestSyncAdBlock() => SyncAdBlockSites();
    public void TestSetAdBlockLists(string[] allowed, string[] applied) { _prefs.AdBlockAllowed = [.. allowed]; _prefs.AdBlockApplied = [.. applied]; }
    public string[] TestAdBlockApplied => [.. _prefs.AdBlockApplied];

    // ---------- Modo celular (Instagram etc.): a aba se apresenta como o Chrome de um celular Android ----------
    // Identificação (User-Agent e os "client hints" que os sites leem), toque e tela de celular, pelo protocolo das
    // ferramentas de desenvolvedor do motor (vale só para aquela aba). Ao entrar num site da lista (ou sair dele) a
    // navegação é refeita já com a identificação certa.

    private const int MobileWidth = 430, MobileHeight = 932;

    private bool WantsMobile(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || !IsWeb(url)) return false;
        string host = uri.Host;
        return _prefs.WebMobileSites.Any(d => host.Equals(d, StringComparison.OrdinalIgnoreCase) || host.EndsWith("." + d, StringComparison.OrdinalIgnoreCase));
    }

    private async void OnNavigationStarting(WebTab tab, CoreWebView2NavigationStartingEventArgs e)
    {
        tab.Loading = true;
        tab.NavigationStart = Environment.TickCount64;
        tab.CertError = false;
        if (tab == _tab) HideSuggestions();
        if (tab.Unresponsive)
        {
            tab.Unresponsive = false;
            lock (_lock) tab.Title = tab.Title.Replace("Sem resposta (F5 recarrega) — ", "");
        }
        WebDirty();
        if (tab.Web is not { } web || WantsMobile(e.Uri) == tab.Mobile) return;
        e.Cancel = true; // refaz já como celular (ou como computador)
        string uri = e.Uri;
        Log.Write($"Navegador: {(tab.Mobile ? "volta ao modo computador" : "modo celular")} numa aba.");
        try
        {
            await SetMobile(tab, !tab.Mobile);
            web.Navigate(uri);
        }
        catch (Exception error) { Log.Write($"Navegador: não consegui trocar o modo celular — {error.Message}"); }
    }

    private async Task SetMobile(WebTab tab, bool mobile)
    {
        if (tab.Web is not { } web || _webEnv is null) return;
        tab.Mobile = mobile;
        if (mobile)
        {
            string full = _webEnv.BrowserVersionString.Split(' ')[0], major = full.Split('.')[0];
            var brands = new[] { new { brand = "Chromium", version = major }, new { brand = "Google Chrome", version = major }, new { brand = "Not.A/Brand", version = "99" } };
            var fullBrands = new[] { new { brand = "Chromium", version = full }, new { brand = "Google Chrome", version = full }, new { brand = "Not.A/Brand", version = "99.0.0.0" } };
            await web.CallDevToolsProtocolMethodAsync("Emulation.setUserAgentOverride", JsonSerializer.Serialize(new
            {
                userAgent = $"Mozilla/5.0 (Linux; Android 10; K) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/{major}.0.0.0 Mobile Safari/537.36",
                platform = "Linux armv8l",
                userAgentMetadata = new
                {
                    brands, fullVersionList = fullBrands, fullVersion = full, platform = "Android", platformVersion = "14.0.0",
                    architecture = "", model = "Pixel 8", mobile = true, bitness = "", wow64 = false
                }
            }));
            await web.CallDevToolsProtocolMethodAsync("Emulation.setTouchEmulationEnabled", "{\"enabled\":true,\"maxTouchPoints\":5}");
            await web.CallDevToolsProtocolMethodAsync("Emulation.setEmitTouchEventsForMouse", "{\"enabled\":true,\"configuration\":\"mobile\"}");
            await web.CallDevToolsProtocolMethodAsync("Emulation.setScrollbarsHidden", "{\"hidden\":true}"); // celular não tem a barra de rolagem
        }
        else
        {
            await web.CallDevToolsProtocolMethodAsync("Emulation.setUserAgentOverride", "{\"userAgent\":\"\"}");
            await web.CallDevToolsProtocolMethodAsync("Emulation.setTouchEmulationEnabled", "{\"enabled\":false}");
            await web.CallDevToolsProtocolMethodAsync("Emulation.setEmitTouchEventsForMouse", "{\"enabled\":false}");
            await web.CallDevToolsProtocolMethodAsync("Emulation.setScrollbarsHidden", "{\"hidden\":false}");
            if (tab.Controller is not null) tab.Controller.ZoomFactor = 1;
        }
        LayoutWeb();
        WebDirty();
    }

    // A lista mudou (Configurações): as abas abertas trocam de modo na hora.
    private async void ApplyMobileSites()
    {
        foreach (var tab in TabsSnapshot())
            if (tab.Web is { } web && WantsMobile(web.Source) != tab.Mobile)
            {
                await SetMobile(tab, !tab.Mobile);
                web.Reload();
            }
    }

    public bool TestTabMobile => _tab?.Mobile == true;
    public System.Drawing.Rectangle TestWebBounds => _tab?.Controller?.Bounds ?? default;

    private void TrackFrame(WebTab tab, CoreWebView2Frame frame)
    {
        tab.Frames.Add(frame);
        frame.WebMessageReceived += (_, e) => OnWebMessage(e.WebMessageAsJson, frame, tab);
        frame.FrameCreated += (_, e) => TrackFrame(tab, e.Frame);
        frame.Destroyed += (_, _) => tab.Frames.Remove(frame);
    }

    private void OnTabNavigated(WebTab tab)
    {
        if (tab.Web is null) return;
        ApplySiteZoom(tab);
        lock (_lock)
        {
            tab.CanBack = tab.Web.CanGoBack;
            tab.CanForward = tab.Web.CanGoForward;
            tab.Url = tab.Web.Source;
        }
        if (tab == _tab && _address != IntPtr.Zero && GetFocus() != _address) SetWindowText(_address, ShownUrl(tab.Web.Source));
        if (IsVideoPage(tab.Web.Source)) Prefetch(tab, tab.Web.Source);
        if (Environment.TickCount64 - _adblockSyncedAt > 600_000 && Uri.TryCreate(tab.Web.Source, UriKind.Absolute, out var page) &&
            _prefs.AdBlockAllowed.Any(d => page.Host.Equals(d, StringComparison.OrdinalIgnoreCase) || page.Host.EndsWith("." + d, StringComparison.OrdinalIgnoreCase)))
            _ = SyncAdBlockSites();
        WebDirty();
        UpdateTitle();
    }

    // ---------- Pré-carregamento: o endereço do vídeo é procurado assim que a página do vídeo abre ----------
    // (o yt-dlp leva alguns segundos; quando você aperta ".", já está pronto). Fica 20 min na memória.

    private readonly Dictionary<string, (Task<WebMedia> task, double at)> _prefetch = [];

    private static bool IsVideoPage(string url) =>
        System.Text.RegularExpressions.Regex.IsMatch(url,
            @"^https://([a-z0-9-]+\.)*(youtube\.com/(watch\?|shorts/)|youtu\.be/|pornhub\.(com|org)/view_video\.php\?viewkey=|xvideos\.com/video|xnxx\.com/video-|xhamster\.com/videos/|spankbang\.com/[^/]+/video/|eporner\.com/(video-|hd-porn/)|redgifs\.com/watch/|vimeo\.com/\d+|dailymotion\.com/video/)",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    private async void Prefetch(WebTab tab, string url)
    {
        double now = Environment.TickCount64 / 1000.0;
        foreach (var old in _prefetch.Where(p => now - p.Value.at > 1200).Select(p => p.Key).ToList()) _prefetch.Remove(old);
        if (_prefetch.ContainsKey(url)) return;
        _prefetch[url] = (Task.FromException<WebMedia>(new InvalidOperationException("ainda começando")), now); // marca: não repete
        await Task.Delay(800); // a página termina de pôr os cookies
        if (tab.Web is not { } web || web.Source != url) { _prefetch.Remove(url); return; }
        try
        {
            string cookies = await CookieFile(web, [url]);
            string agent = web.Settings.UserAgent, title = web.DocumentTitle;
            var task = Task.Run(() => WebResolver.ResolveWorking(url, title, agent, cookies, CancellationToken.None));
            _prefetch[url] = (task, now);
            Log.Write("Navegador: procurando o vídeo da página em segundo plano.");
        }
        catch { _prefetch.Remove(url); }
    }

    // Arquivo de cookies (para o yt-dlp) com os cookies destes endereços.
    private static async Task<string> CookieFile(CoreWebView2 web, IEnumerable<string> urls)
    {
        var all = new List<CoreWebView2Cookie>();
        foreach (var url in urls.Where(IsWeb).Distinct())
            all.AddRange(await web.CookieManager.GetCookiesAsync(url));
        var unique = all.GroupBy(c => (c.Domain, c.Path, c.Name)).Select(g => g.First()).ToList();
        return WebResolver.WriteCookies(unique.Select(c => (c.Domain, c.Path, c.IsSecure,
            c.IsSession ? 0L : new DateTimeOffset(c.Expires).ToUnixTimeSeconds(), c.Name, c.Value)));
    }

    // Endereço como o Edge mostra: com acentos e espaços legíveis ("Wikipédia" em vez de "Wikip%C3%A9dia").
    private static string ShownUrl(string url)
    {
        if (IsSettingsUrl(url)) return SettingsShown;
        try { return Uri.UnescapeDataString(url); }
        catch { return url; }
    }

        private static string HostOf(string url) => Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.Host.Replace("www.", "") : url;

    // Ícone do site (na aba e nos favoritos daquele domínio).
    private async void LoadFavicon(WebTab tab)
    {
        if (tab.Web is null) return;
        try
        {
            using var stream = await tab.Web.GetFaviconAsync(CoreWebView2FaviconImageFormat.Png);
            if (stream is null) return;
            var bytes = new MemoryStream();
            await stream.CopyToAsync(bytes);
            if (ImageFile.DecodeSmall(bytes.ToArray()) is not { } icon) return;
            string host = HostOf(tab.Web.Source);
            lock (_lock)
            {
                tab.Icon = icon;
                _favicons[host] = icon;
                _faviconBytes[host] = bytes.ToArray();
            }
            if (IsBookmarkedHost(host)) PersistFavicons(); // no disco só os ícones dos favoritos (privacidade)
            WebDirty();
        }
        catch { } // site sem ícone
    }

    // Ícones em disco = só os dos sites favoritos (a pasta não revela quais sites você visitou); os outros ficam na
    // memória e somem ao fechar. Chamado ao abrir o navegador e a cada mudança nos favoritos.
    private readonly Dictionary<string, byte[]> _faviconBytes = new(StringComparer.OrdinalIgnoreCase);

    private bool IsBookmarkedHost(string host)
    {
        _bookmarks ??= Bookmarks.Load();
        return BookmarkHosts(_bookmarks).Contains(host);
    }

    private static HashSet<string> BookmarkHosts(BookmarkNode root)
    {
        var hosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Walk(BookmarkNode node)
        {
            if (node.Url is { } url) hosts.Add(HostOf(url));
            foreach (var child in node.Children ?? []) Walk(child);
        }
        Walk(root);
        return hosts;
    }

    private void PersistFavicons()
    {
        try
        {
            _bookmarks ??= Bookmarks.Load();
            var keep = BookmarkHosts(_bookmarks).Select(SafeName).ToHashSet(StringComparer.OrdinalIgnoreCase);
            Directory.CreateDirectory(FaviconFolder);
            foreach (var file in Directory.GetFiles(FaviconFolder, "*.png"))
                if (!keep.Contains(Path.GetFileNameWithoutExtension(file))) File.Delete(file);
            Dictionary<string, byte[]> bytes;
            lock (_lock) bytes = new(_faviconBytes, StringComparer.OrdinalIgnoreCase);
            foreach (var (host, data) in bytes)
            {
                string file = Path.Combine(FaviconFolder, SafeName(host) + ".png");
                if (keep.Contains(SafeName(host)) && !File.Exists(file)) File.WriteAllBytes(file, data);
            }
        }
        catch (Exception error) { Log.Write($"Navegador: não consegui arrumar os ícones guardados ({error.Message})."); }
    }

    private static string SafeName(string host) => string.Concat(host.Select(c => char.IsLetterOrDigit(c) || c == '.' || c == '-' ? c : '_'));

    // Ícone guardado de um domínio (favoritos de sites que não estão abertos). Com _lock.
    private ThumbnailCache.Thumb? FaviconFor(string url)
    {
        string host = HostOf(url);
        if (_favicons.TryGetValue(host, out var icon)) return icon;
        try
        {
            string file = Path.Combine(FaviconFolder, SafeName(host) + ".png");
            icon = File.Exists(file) ? ImageFile.DecodeSmall(File.ReadAllBytes(file)) : null;
        }
        catch { icon = null; }
        _favicons[host] = icon;
        return icon;
    }

    // Atalhos do navegador (vêm do site, da barra de endereço ou da janela). true = usado.
    private bool WebShortcut(int key)
    {
        bool ctrl = KeyDown(VK_CONTROL), shift = KeyDown(VK_SHIFT), alt = KeyDown(0x12);
        WebTab[] tabs = TabsSnapshot();
        int index = _tab is null ? -1 : Array.IndexOf(tabs, _tab);
        switch (key)
        {
            case 0x71: ShowWeb(false); return true;                                   // F2: player
            case 'L' when ctrl: FocusAddress(); return true;
            case 'T' when ctrl && shift:
                if (_closedTabs.TryPop(out var reopen)) _ = OpenTab(reopen, select: true);
                return true;
            case 'T' when ctrl: NewTab(); return true;
            case 'W' when ctrl: if (_tab is not null) CloseTab(_tab); return true;
            case 0x09 when ctrl && tabs.Length > 1:                                   // Ctrl+Tab / Ctrl+Shift+Tab
                _ = SelectTab(tabs[((index + (shift ? -1 : 1)) % tabs.Length + tabs.Length) % tabs.Length]);
                return true;
            case >= '1' and <= '9' when ctrl && tabs.Length > 0:
                _ = SelectTab(key == '9' ? tabs[^1] : tabs[Math.Min(key - '1', tabs.Length - 1)]);
                return true;
            case 'D' when ctrl: StarMenu(); return true;
            case 'S' when ctrl: AskFrames(download: true); return true; // baixar o vídeo da tela
            case 'B' when ctrl && shift:
                _prefs.WebBookmarksBar = !_prefs.WebBookmarksBar;
                _prefs.Save();
                LayoutWeb();
                WebDirty();
                return true;
            case 0x24 when alt: _web?.Navigate(HomePage); return true;                // Alt+Home
            case 0x74 when _web is not null: _web.Reload(); return true;              // F5
            case 0x7A: ToggleWebFullscreen(); return true;                            // F11
            case 'J' when ctrl: OpenDownloads(); return true;
            case 0xBB or 0x6B when ctrl: ZoomStep(1); return true;                    // Ctrl + (teclado e numérico)
            case 0xBD or 0x6D when ctrl: ZoomStep(-1); return true;                   // Ctrl −
            case '0' or 0x60 when ctrl: ZoomStep(0); return true;                     // Ctrl 0
        }
        return false;
    }

    private void WebDirty()
    {
        lock (_lock) _dirty = true;
        _renderWake.Set();
    }

    // Um vídeo do site em tela cheia (guardado na thread da janela: o site só responde nela, e a tela desenha em outra).
    private volatile bool _siteFullscreen;

    // Altura das faixas de cima (0 em tela cheia de um vídeo do site). Com ou sem _lock, de qualquer thread.
    private int WebTop()
    {
        if (_fullscreen && (_siteFullscreen || (_f11 && !_barsPeek))) return 0; // F11: só com o mouse no topo
        return U(TabStrip) + U(WebBar) + (_prefs.WebBookmarksBar ? U(BookmarkBar) : 0);
    }

    // Com a janela mudando de tamanho, a tela cheia, a aba ou o modo.
    private void LayoutWeb()
    {
        int cw, ch;
        lock (_lock) (cw, ch) = (_clientW, _clientH);
        int top = WebTop();
        var bounds = new System.Drawing.Rectangle(0, top, Math.Max(1, cw), Math.Max(1, ch - top));
        foreach (var tab in TabsSnapshot())
        {
            if (tab.Controller is null) continue;
            if (!tab.Mobile) { tab.Controller.Bounds = bounds; continue; }
            // Modo celular: uma coluna do tamanho de um celular (430 de largura, como um iPhone/Pixel grande), no
            // centro e ampliada para ocupar a altura (a página continua vendo uma tela de celular).
            double zoom = Math.Clamp((ch - top) / (MobileHeight * _uiScale), 1, 3);
            int w = Math.Min(cw, (int)Math.Round(MobileWidth * _uiScale * zoom));
            tab.Controller.Bounds = new System.Drawing.Rectangle((cw - w) / 2, top, Math.Max(1, w), Math.Max(1, ch - top));
            if (Math.Abs(tab.Controller.ZoomFactor - zoom) > 0.01) tab.Controller.ZoomFactor = zoom;
        }
        if (_address != IntPtr.Zero)
        {
            var r = AddressRect(cw);
            int textH = U(22);
            Native.SetWindowPos(_address, IntPtr.Zero, r.Left + U(36), r.Top + (r.Height - textH) / 2, Math.Max(1, r.Width - U(36) - AddressRightReserve(_tab)), textH, 0x0004 /* SWP_NOZORDER */);
            Native.ShowWindow(_address, WebMode && top > 0 ? 5 : 0);
        }
    }

    // Faixa do meio: ← → ⟳, endereço (com a ☆ no fim), o escudo, "Player" e a engrenagem (configurações).
    private Native.RECT AddressRect(int cw)
    {
        int y = U(TabStrip);
        int left = U(8) + 3 * U(WebButton) + U(8), right = cw - U(8) - U(WebButton) - U(4) - U(104) - U(8) - U(WebButton) - U(4);
        return new Native.RECT { Left = left, Top = y + U(6), Right = Math.Max(left + U(120), right), Bottom = y + U(WebBar) - U(6) };
    }

    // ---------- Barra de endereço ----------

    private void CreateAddressBar()
    {
        if (_address != IntPtr.Zero) return;
        _address = Native.CreateWindowEx(0, "EDIT", "", WS_CHILD | ES_AUTOHSCROLL, 0, 0, 10, 10, Hwnd, IntPtr.Zero, Native.GetModuleHandle(null), IntPtr.Zero);
        _addressFont = UiCanvas.Font("Segoe UI", U(15));
        _addressBrush = CreateSolidBrush(AddressBack);
        SendMessageW(_address, WM_SETFONT, _addressFont, 1);
        SendMessageW(_address, EM_SETMARGINS, 3, IntPtr.Zero);
        _addressProc = AddressProc;
        _addressOldProc = SetWindowLongPtr(_address, -4 /* GWLP_WNDPROC */, Marshal.GetFunctionPointerForDelegate(_addressProc));
        LayoutWeb();
    }

    private IntPtr AddressProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == 0x0008) HideSuggestions(); // WM_KILLFOCUS: saiu da barra
        if (msg == 0x0201 && GetFocus() != hwnd) // WM_LBUTTONDOWN vindo de fora: o endereço inteiro selecionado (como no Edge)
        {
            FocusAddress();
            return IntPtr.Zero; // sem pôr o cursor no ponto (o próximo clique, já dentro, põe)
        }
        if (msg == 0x007B) // WM_CONTEXTMENU: o menu de editar, com o "Colar e ir"
        {
            AddressMenu();
            return IntPtr.Zero;
        }
        if (msg == WM_KEYDOWN)
        {
            int key = (int)wParam;
            bool ctrl = KeyDown(VK_CONTROL);
            bool suggesting = _suggest?.Visible == true;
            switch (key)
            {
                case 0x26 or 0x28 when suggesting: // ↑ ↓: escolhe uma sugestão
                    MoveSuggestion(key == 0x28 ? 1 : -1);
                    return IntPtr.Zero;
                case VK_RETURN when suggesting && _suggest!.Selected >= 0:
                    OpenSuggestion(_suggest.Selected, newTab: KeyDown(0x12));
                    return IntPtr.Zero;
                case VK_RETURN:
                    HideSuggestions();
                    var buffer = new char[4096];
                    int n = GetWindowTextW(hwnd, buffer, buffer.Length);
                    GoTo(new string(buffer, 0, n), newTab: KeyDown(0x12)); // Alt+Enter: em aba nova
                    return IntPtr.Zero;
                case VK_ESCAPE when suggesting: // fecha as sugestões e volta ao que foi digitado
                    HideSuggestions();
                    _addressLast = _addressTyped;
                    SetWindowText(hwnd, _addressTyped);
                    SendMessageW(hwnd, EM_SETSEL, _addressTyped.Length, _addressTyped.Length);
                    return IntPtr.Zero;
                case VK_ESCAPE:
                    if (_web is not null) SetWindowText(hwnd, ShownUrl(_web.Source));
                    _webController?.MoveFocus(CoreWebView2MoveFocusReason.Programmatic);
                    return IntPtr.Zero;
                case 'A' when ctrl: // selecionar tudo
                    SendMessageW(hwnd, EM_SETSEL, 0, -1);
                    return IntPtr.Zero;
                case VK_BACK when ctrl: // apagar a palavra antes do cursor
                    DeleteWordBefore(hwnd);
                    AddressChanged();
                    return IntPtr.Zero;
                case 'T' or 'W' or 'L' or 'D' or 'J' or 0x09 or (>= '0' and <= '9') or 0xBB or 0xBD or 0x6B or 0x6D or 0x60 when ctrl:
                case 0x71 or 0x7A:
                    if (WebShortcut(key)) return IntPtr.Zero;
                    break;
            }
        }
        // Sem o "bip" (Enter, Esc) e sem os caracteres de controle de Ctrl+A / Ctrl+Backspace / Ctrl+letra.
        if (msg == WM_CHAR && ((int)wParam == '\r' || (int)wParam == 27 || (int)wParam == 127 || ((int)wParam < 32 && (int)wParam != 8 && (int)wParam != 22 && (int)wParam != 3 && (int)wParam != 24 && (int)wParam != 26)))
            return IntPtr.Zero;
        var result = CallWindowProc(_addressOldProc, hwnd, msg, wParam, lParam);
        // Digitou, apagou, colou, recortou, desfez: as sugestões dos favoritos acompanham.
        if (msg is WM_CHAR or 0x0300 or 0x0302 or 0x0303 or 0x0304 || (msg == WM_KEYDOWN && (int)wParam == 0x2E)) AddressChanged();
        return result;
    }

    private const uint EM_GETSEL = 0x00B0, EM_REPLACESEL = 0x00C2, EM_CANUNDO = 0x00C6;

    // ---------- Botão direito na barra de endereço ----------

    // O que o menu da barra mostra (texto vazio = separador; desligado = cinza) e faz.
    private List<(string text, bool enabled, Action? run)> AddressMenuEntries()
    {
        long selection = SendMessageW(_address, EM_GETSEL, IntPtr.Zero, IntPtr.Zero).ToInt64();
        bool selected = (selection & 0xffff) != ((selection >> 16) & 0xffff);
        string clip = (ClipboardText() ?? "").Trim();
        bool link = LinkIn(clip) == clip && clip.Length > 0 || !clip.Contains(' ') && clip.Contains('.') && !clip.Contains('\n');
        void Send(uint message) => SendMessageW(_address, message, IntPtr.Zero, IntPtr.Zero);
        return
        [
            ("Desfazer\tCtrl+Z", SendMessageW(_address, EM_CANUNDO, IntPtr.Zero, IntPtr.Zero) != IntPtr.Zero, () => Send(0x0304 /* WM_UNDO */)),
            ("", false, null),
            ("Recortar\tCtrl+X", selected, () => Send(0x0300 /* WM_CUT */)),
            ("Copiar\tCtrl+C", selected, () => Send(0x0301 /* WM_COPY */)),
            ("Colar\tCtrl+V", clip.Length > 0, () => Send(0x0302 /* WM_PASTE */)),
            // O que está copiado vai direto: o endereço abre (ou, se não for um, a pesquisa), sem precisar do Enter.
            (link || clip.Length == 0 ? "Colar e ir" : "Colar e pesquisar", clip.Length > 0, () =>
            {
                HideSuggestions();
                SetWindowText(_address, clip);
                GoTo(clip);
            }),
            ("Excluir", selected, () => Send(0x0303 /* WM_CLEAR */)),
            ("", false, null),
            ("Selecionar tudo\tCtrl+A", true, () => SendMessageW(_address, EM_SETSEL, 0, -1))
        ];
    }

    private void AddressMenu()
    {
        var entries = AddressMenuEntries();
        int id = Menu(menu =>
        {
            for (int i = 0; i < entries.Count; i++)
                if (entries[i].text == "") AppendMenu(menu, MF_SEPARATOR, 0, null);
                else AppendMenu(menu, MF_STRING | (entries[i].enabled ? 0 : MF_GRAYED), i + 1, entries[i].text);
        });
        if (id > 0 && entries[id - 1] is { enabled: true, run: { } run }) run();
    }

    // O texto da área de transferência (null = nenhum).
    private string? ClipboardText()
    {
        if (TestClipboard is not null) return TestClipboard;
        if (!OpenClipboard(Hwnd)) return null;
        try
        {
            var data = GetClipboardData(13 /* CF_UNICODETEXT */);
            if (data == IntPtr.Zero) return null;
            var text = GlobalLock(data);
            try { return text == IntPtr.Zero ? null : Marshal.PtrToStringUni(text); }
            finally { GlobalUnlock(data); }
        }
        finally { CloseClipboard(); }
    }

    // O primeiro endereço (http/https ou www.) num texto; null = nenhum.
    private static string? LinkIn(string text)
    {
        var match = System.Text.RegularExpressions.Regex.Match(text, @"\b(?:https?://|www\.)[^\s<>""']+", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (!match.Success) return null;
        string link = match.Value.TrimEnd('.', ',', ';', ':', ')', ']', '!', '?');
        if (link.StartsWith("www.", StringComparison.OrdinalIgnoreCase)) link = "https://" + link;
        return Uri.TryCreate(link, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" ? link : null;
    }

    [DllImport("user32.dll")] private static extern bool OpenClipboard(IntPtr owner);
    [DllImport("user32.dll")] private static extern bool CloseClipboard();
    [DllImport("user32.dll")] private static extern IntPtr GetClipboardData(uint format);
    [DllImport("kernel32.dll")] private static extern IntPtr GlobalLock(IntPtr memory);
    [DllImport("kernel32.dll")] private static extern bool GlobalUnlock(IntPtr memory);

    // Autoteste: a área de transferência (sem mexer na de verdade), o menu da barra e a seleção dela.
    public string? TestClipboard;
    public string[] TestAddressMenu() => [.. AddressMenuEntries().Where(e => e.text != "").Select(e => e.text.Split('\t')[0] + (e.enabled ? "" : " (cinza)"))];
    public void TestAddressMenuRun(string text) { if (AddressMenuEntries().FirstOrDefault(e => e.text.Split('\t')[0] == text) is { enabled: true, run: { } run }) run(); }
    public void TestAddressClick() { SendMessageW(_address, 0x0201, 1, (IntPtr)((5 << 16) | 20)); SendMessageW(_address, 0x0202, 0, (IntPtr)((5 << 16) | 20)); }
    public void TestFocusPage() => _webController?.MoveFocus(CoreWebView2MoveFocusReason.Programmatic);

    private static void DeleteWordBefore(IntPtr hwnd)
    {
        var buffer = new char[4096];
        int n = GetWindowTextW(hwnd, buffer, buffer.Length);
        string text = new(buffer, 0, n);
        int end = Math.Clamp((int)(((long)SendMessageW(hwnd, EM_GETSEL, IntPtr.Zero, IntPtr.Zero) >> 16) & 0xffff), 0, text.Length);
        int start = end;
        while (start > 0 && char.IsWhiteSpace(text[start - 1])) start--;
        bool word = start > 0 && char.IsLetterOrDigit(text[start - 1]);
        while (start > 0 && !char.IsWhiteSpace(text[start - 1]) && char.IsLetterOrDigit(text[start - 1]) == word) start--;
        SendMessageW(hwnd, EM_SETSEL, start, end);
        SendMessage(hwnd, EM_REPLACESEL, 1, "");
    }

    private void FocusAddress()
    {
        _addressFocusPending = false;
        if (_address == IntPtr.Zero) return;
        SetFocus(_address);
        SendMessageW(_address, EM_SETSEL, 0, -1);
        _addressLast = _addressTyped = AddressValue(); // só digitando aparecem sugestões
    }

    // Aba nova (botão + ou Ctrl+T): o endereço fica com o foco e todo selecionado, pronto para digitar.
    private async void NewTab()
    {
        _addressFocusPending = true;
        FocusAddress();
        _addressFocusPending = true;
        try { await OpenTab(HomePage, select: true); }
        finally { FocusAddress(); }
    }

    public void TestNewTabButton() => NewTab();
    public string TestAddressText { get { var b = new char[4096]; return new string(b, 0, GetWindowTextW(_address, b, b.Length)); } }
    public bool TestAddressFocused => _address != IntPtr.Zero && GetFocus() == _address;
    public (int start, int end, int length) TestAddressSelection
    {
        get
        {
            long sel = (long)SendMessageW(_address, EM_GETSEL, IntPtr.Zero, IntPtr.Zero);
            return ((int)(sel & 0xffff), (int)((sel >> 16) & 0xffff), GetWindowTextW(_address, new char[4096], 4096));
        }
    }

    // Endereço digitado: com ou sem "https://"; palavras viram busca no Google.
    private void GoTo(string text, bool newTab = false)
    {
        text = text.Trim();
        if (text.Length == 0) return;
        string url;
        if (text.StartsWith("mkvision://", StringComparison.OrdinalIgnoreCase))
        {
            OpenSettings();
            return;
        }
        if (Uri.TryCreate(text, UriKind.Absolute, out var uri) && (uri.Scheme == "http" || uri.Scheme == "https")) url = uri.AbsoluteUri;
        else if (!text.Contains(' ') && text.Contains('.') && Uri.TryCreate("https://" + text, UriKind.Absolute, out var guess)) url = guess.AbsoluteUri;
        else url = "https://www.google.com/search?q=" + Uri.EscapeDataString(text);
        if (newTab || _web is null) _ = OpenTab(url, select: true);
        else
        {
            _web.Navigate(url);
            _webController?.MoveFocus(CoreWebView2MoveFocusReason.Programmatic);
        }
    }

    // WM_CTLCOLOREDIT: barra de endereço escura.
    private IntPtr AddressColors(IntPtr hdc)
    {
        SetTextColor(hdc, AddressText);
        SetBkColor(hdc, AddressBack);
        return _addressBrush;
    }

    // ---------- Faixas de cima: o que está sob o mouse (com _lock) ----------

    private UiPart WebHitTest(int x, int y)
    {
        _webHoverIndex = -1;
        int tools = U(TabStrip), books = tools + U(WebBar), end = WebTop();
        if (y >= end) return UiPart.None;
        if (y < tools)
        {
            foreach (var (tab, close, audio, index) in _tabRects)
            {
                if (!Inside(tab, x, y)) continue;
                _webHoverIndex = index;
                return Inside(close, x, y) ? UiPart.WebTabClose : Inside(audio, x, y) ? UiPart.WebTabAudio : UiPart.WebTab;
            }
            if (_tabLayout.overflow && Inside(_tabLeftRect, x, y)) return UiPart.WebTabsLeft;
            if (_tabLayout.overflow && Inside(_tabRightRect, x, y)) return UiPart.WebTabsRight;
            return Inside(_newTabRect, x, y) ? UiPart.WebNewTab : UiPart.TopBar;
        }
        if (y < books)
        {
            if (Inside(_webBackRect, x, y)) return UiPart.WebBack;
            if (Inside(_webForwardRect, x, y)) return UiPart.WebForward;
            if (Inside(_webReloadRect, x, y)) return UiPart.WebReload;
            if (Inside(_webStarRect, x, y)) return UiPart.WebStar;
            if (Inside(_webZoomRect, x, y)) return UiPart.WebZoom;
            if (Inside(_webTranslateRect, x, y)) return UiPart.WebTranslate;
            if (Inside(_siteIconRect, x, y)) return UiPart.WebSiteIcon;
            if (Inside(_webShieldRect, x, y)) return UiPart.WebShield;
            if (Inside(_webPlayerRect, x, y)) return UiPart.WebPlayer;
            if (Inside(_webSettingsRect, x, y)) return UiPart.WebSettings;
            if (Inside(_webAddressRect, x, y)) return UiPart.WebAddress;
            return UiPart.TopBar;
        }
        for (int i = 0; i < _bookmarkRects.Count; i++)
            if (Inside(_bookmarkRects[i].rect, x, y))
            {
                _webHoverIndex = i;
                return UiPart.WebBookmark;
            }
        return Inside(_bookmarksMoreRect, x, y) ? UiPart.WebBookmarksMore : UiPart.WebBookmarkBar;
    }

    private void WebClick(UiPart part, int x, int y)
    {
        int index;
        WebTab? tab = null;
        BookmarkNode? node = null;
        lock (_lock)
        {
            index = _webHoverIndex;
            if (part is UiPart.WebTab or UiPart.WebTabClose or UiPart.WebTabAudio && index >= 0 && index < _tabs.Count) tab = _tabs[index];
            if (part is UiPart.WebBookmark && index >= 0 && index < _bookmarkRects.Count) node = _bookmarkRects[index].node;
        }
        HideTabTip();
        switch (part)
        {
            case UiPart.WebTab when tab is not null:
                _ = SelectTab(tab);
                BeginTabPress(tab, x, y); // arrastando, muda a ordem
                break;
            case UiPart.WebTabClose when tab is not null: CloseTab(tab); break;
            case UiPart.WebTabAudio when tab is not null: ToggleTabMute(tab); break;
            case UiPart.WebTabsLeft: ScrollTabs(-1); break;
            case UiPart.WebTabsRight: ScrollTabs(1); break;
            case UiPart.WebNewTab: NewTab(); break;
            case UiPart.WebBack: if (_web?.CanGoBack == true) _web.GoBack(); break;
            case UiPart.WebForward: if (_web?.CanGoForward == true) _web.GoForward(); break;
            case UiPart.WebReload:
                if (_tab?.Loading == true) _web?.Stop(); else _web?.Reload();
                break;
            case UiPart.WebStar: StarMenu(); break;
            case UiPart.WebZoom: ZoomMenu(); break;
            case UiPart.WebTranslate: TranslatePage(); break;
            case UiPart.WebShield: ShieldMenu(); break;
            case UiPart.WebPlayer: ShowWeb(false); break;
            case UiPart.WebSettings: OpenSettings(); break;
            case UiPart.WebAddress: FocusAddress(); break;
            case UiPart.WebBookmark when node is not null:
                if (node.IsFolder) FolderMenu(node, x, y);
                else OpenBookmark(node.Url!, newTab: KeyDown(VK_CONTROL));
                break;
            case UiPart.WebBookmarksMore:
            {
                List<BookmarkNode> rest;
                lock (_lock) rest = _bookmarks?.Children?.Skip(_bookmarksShown).ToList() ?? [];
                FolderMenu(new BookmarkNode { Name = "", Children = rest }, x, y);
                break;
            }
        }
        WebDirty();
    }

    // Botão do meio: fecha a aba / abre o favorito em aba nova.
    private bool WebMiddleClick(int x, int y)
    {
        UiPart part;
        WebTab? tab = null;
        BookmarkNode? node = null;
        lock (_lock)
        {
            part = WebHitTest(x, y);
            if (part is UiPart.WebTab or UiPart.WebTabClose or UiPart.WebTabAudio && _webHoverIndex >= 0 && _webHoverIndex < _tabs.Count) tab = _tabs[_webHoverIndex];
            if (part is UiPart.WebBookmark && _webHoverIndex < _bookmarkRects.Count) node = _bookmarkRects[_webHoverIndex].node;
        }
        if (tab is not null) CloseTab(tab);
        else if (node is { IsFolder: false }) OpenBookmark(node.Url!, newTab: true);
        else return y < WebTop();
        return true;
    }

    private void OpenBookmark(string url, bool newTab)
    {
        if (newTab || _web is null) _ = OpenTab(url, select: !newTab || _web is null);
        else _web.Navigate(url);
    }

    // ---------- Menus (favoritos, abas) ----------

    private const uint MF_STRING = 0, MF_POPUP = 0x10, MF_SEPARATOR = 0x800, MF_GRAYED = 0x1, TPM_RETURNCMD = 0x100;

    // Mostra o menu no cursor e devolve o número escolhido (0 = nada).
    private int Menu(Action<IntPtr> build)
    {
        var menu = CreatePopupMenu();
        build(menu);
        GetCursorPos(out var point);
        int id = TrackPopupMenu(menu, TPM_RETURNCMD, point.X, point.Y, 0, Hwnd, IntPtr.Zero);
        DestroyMenu(menu);
        return id;
    }

    // Conteúdo de uma pasta como menu (subpastas viram submenus). "ids" guarda o que cada número abre.
    private static void AddFolder(IntPtr menu, BookmarkNode folder, Dictionary<int, BookmarkNode> ids)
    {
        foreach (var child in folder.Children!)
        {
            if (child.IsFolder)
            {
                var sub = CreatePopupMenu();
                AddFolder(sub, child, ids);
                AppendMenu(menu, MF_POPUP, sub, "📁 " + Short(child.Name));
            }
            else
            {
                int id = 1000 + ids.Count;
                ids[id] = child;
                AppendMenu(menu, MF_STRING, id, Short(child.Name.Length > 0 ? child.Name : child.Url!));
            }
        }
        if (folder.Children!.Count == 0) AppendMenu(menu, MF_GRAYED, 0, "(vazia)");
        else if (folder.Children.Count(c => !c.IsFolder) > 1)
        {
            AppendMenu(menu, MF_SEPARATOR, 0, null);
            int all = 1000 + ids.Count;
            ids[all] = folder;
            AppendMenu(menu, MF_STRING, all, "Abrir todos em abas novas");
        }
    }

    private static string Short(string text) => (text.Length > 60 ? text[..60] + "…" : text).Replace("&", "&&");

    private void FolderMenu(BookmarkNode folder, int x, int y)
    {
        var ids = new Dictionary<int, BookmarkNode>();
        int id = Menu(menu => AddFolder(menu, folder, ids));
        if (!ids.TryGetValue(id, out var node)) return;
        if (node.IsFolder)
            foreach (var link in node.Children!.Where(c => !c.IsFolder)) _ = OpenTab(link.Url!, select: false);
        else OpenBookmark(node.Url!, newTab: KeyDown(VK_CONTROL));
    }

    // ☆ / Ctrl+D: favoritar a página (na barra ou numa pasta), ou tirar/renomear/mover se já for favorito.
    private void StarMenu()
    {
        if (_web is null) return;
        _bookmarks ??= Bookmarks.Load();
        string url = _web.Source, title = _web.DocumentTitle is { Length: > 0 } t ? t : HostOf(url);
        var found = Bookmarks.Find(_bookmarks, url);
        var folders = Bookmarks.Folders(_bookmarks);
        int id = Menu(menu =>
        {
            if (found is { } f)
            {
                AppendMenu(menu, MF_GRAYED, 0, $"★ Nos favoritos: {Short(f.node.Name)}");
                AppendMenu(menu, MF_STRING, 1, "Renomear...");
                AppendMenu(menu, MF_STRING, 2, "Remover dos favoritos");
                var move = CreatePopupMenu();
                AppendMenu(move, MF_STRING, 100, "Barra de favoritos");
                for (int i = 0; i < folders.Count; i++) AppendMenu(move, MF_STRING, 101 + i, Short(folders[i].path));
                AppendMenu(menu, MF_POPUP, move, "Mover para");
            }
            else
            {
                AppendMenu(menu, MF_STRING, 100, "Adicionar à barra de favoritos");
                if (folders.Count > 0)
                {
                    var into = CreatePopupMenu();
                    for (int i = 0; i < folders.Count; i++) AppendMenu(into, MF_STRING, 101 + i, Short(folders[i].path));
                    AppendMenu(menu, MF_POPUP, into, "Adicionar na pasta");
                }
                AppendMenu(menu, MF_STRING, 3, "Nova pasta com esta página...");
            }
        });
        BookmarkNode Target(int choice) => choice == 100 ? _bookmarks : folders[choice - 101].folder;
        switch (id)
        {
            case 1 when found is { } f:
                if (InputBox.Show(Hwnd, "Renomear favorito", "Nome:", f.node.Name) is { } name) f.node.Name = name;
                break;
            case 2 when found is { } f:
                f.parent.Children!.Remove(f.node);
                WebToast("Removido dos favoritos");
                break;
            case 3:
                if (InputBox.Show(Hwnd, "Nova pasta", "Nome da pasta:", "") is { } folderName)
                {
                    var folder = BookmarkNode.Folder(folderName);
                    folder.Children!.Add(BookmarkNode.Link(title, url));
                    _bookmarks.Children!.Add(folder);
                    WebToast($"Salvo em {folderName}");
                }
                break;
            case >= 100 when id < 101 + folders.Count:
                var target = Target(id);
                if (found is { } existing)
                {
                    existing.parent.Children!.Remove(existing.node);
                    target.Children!.Add(existing.node);
                }
                else target.Children!.Add(BookmarkNode.Link(title, url));
                WebToast(found is null ? "Adicionado aos favoritos" : "Favorito movido");
                break;
            default:
                return;
        }
        Bookmarks.Save(_bookmarks);
        WebDirty();
    }

    // Escudo: o bloqueador de anúncios (uBlock Origin Lite).
    private async void ShieldMenu()
    {
        bool on = AdBlock.On, installed = AdBlock.Extension is not null;
        int id = Menu(menu =>
        {
            AppendMenu(menu, MF_GRAYED, 0, installed ? $"Bloqueador de anúncios: {(on ? "ligado" : "desligado")}" : "Bloqueador de anúncios: instalando...");
            AppendMenu(menu, installed ? MF_STRING : MF_GRAYED, 1, on ? "Desligar o bloqueador" : "Ligar o bloqueador");
            AppendMenu(menu, installed ? MF_STRING : MF_GRAYED, 2, "Configurações do bloqueador (listas, sites liberados)...");
        });
        if (id == 1 && AdBlock.Extension is not null)
        {
            await AdBlock.SetEnabled(!on);
            _web?.Reload();
            WebToast(on ? "Bloqueador desligado" : "Bloqueador ligado");
        }
        else if (id == 2 && AdBlock.Id is { } extensionId) _ = OpenTab($"chrome-extension://{extensionId}/dashboard.html", select: true);
        WebDirty();
    }

    // Botão direito nas faixas de cima.
    private void WebRightClick(int x, int y)
    {
        UiPart part;
        WebTab? tab = null;
        BookmarkNode? node = null;
        lock (_lock)
        {
            part = WebHitTest(x, y);
            if (part is UiPart.WebTab or UiPart.WebTabClose or UiPart.WebTabAudio && _webHoverIndex >= 0 && _webHoverIndex < _tabs.Count) tab = _tabs[_webHoverIndex];
            if (part is UiPart.WebBookmark && _webHoverIndex < _bookmarkRects.Count) node = _bookmarkRects[_webHoverIndex].node;
        }
        if (tab is not null) TabMenu(tab);
        else if (part is UiPart.WebBookmark or UiPart.WebBookmarkBar or UiPart.WebBookmarksMore) BookmarkMenu(node);
    }

    private void TabMenu(WebTab tab)
    {
        int id = Menu(menu =>
        {
            AppendMenu(menu, MF_STRING, 1, "Nova aba\tCtrl+T");
            AppendMenu(menu, MF_STRING, 2, "Duplicar");
            AppendMenu(menu, MF_STRING, 3, "Recarregar\tF5");
            AppendMenu(menu, MF_SEPARATOR, 0, null);
            AppendMenu(menu, MF_STRING, 4, "Fechar aba\tCtrl+W");
            AppendMenu(menu, MF_STRING, 5, "Fechar as outras abas");
            AppendMenu(menu, _closedTabs.Count > 0 ? MF_STRING : MF_GRAYED, 6, "Reabrir aba fechada\tCtrl+Shift+T");
        });
        switch (id)
        {
            case 1: _ = OpenTab(HomePage, select: true); break;
            case 2: _ = OpenTab(tab.Web?.Source ?? tab.Url, select: true); break;
            case 3: tab.Web?.Reload(); break;
            case 4: CloseTab(tab); break;
            case 5: foreach (var other in TabsSnapshot()) if (other != tab) CloseTab(other); break;
            case 6: if (_closedTabs.TryPop(out var url)) _ = OpenTab(url, select: true); break;
        }
    }

    private void BookmarkMenu(BookmarkNode? node)
    {
        _bookmarks ??= Bookmarks.Load();
        var parent = node is null ? _bookmarks : FindParent(_bookmarks, node) ?? _bookmarks;
        int id = Menu(menu =>
        {
            if (node is not null)
            {
                if (!node.IsFolder)
                {
                    AppendMenu(menu, MF_STRING, 1, "Abrir");
                    AppendMenu(menu, MF_STRING, 2, "Abrir em aba nova");
                }
                AppendMenu(menu, MF_STRING, 3, "Renomear...");
                AppendMenu(menu, MF_STRING, 4, node.IsFolder ? "Excluir pasta (e o que tem nela)" : "Excluir");
                AppendMenu(menu, MF_SEPARATOR, 0, null);
            }
            AppendMenu(menu, _web is not null ? MF_STRING : MF_GRAYED, 5, node is { IsFolder: true } ? "Adicionar esta página nesta pasta" : "Adicionar esta página");
            AppendMenu(menu, MF_STRING, 6, node is { IsFolder: true } ? "Nova pasta dentro desta..." : "Nova pasta...");
            AppendMenu(menu, MF_SEPARATOR, 0, null);
            AppendMenu(menu, MF_STRING, 7, "Importar favoritos do Edge");
            AppendMenu(menu, MF_STRING, 8, "Ocultar barra de favoritos\tCtrl+Shift+B");
        });
        var into = node is { IsFolder: true } ? node : parent;
        switch (id)
        {
            case 1 when node?.Url is not null: OpenBookmark(node.Url, newTab: false); return;
            case 2 when node?.Url is not null: OpenBookmark(node.Url, newTab: true); return;
            case 3 when node is not null:
                if (InputBox.Show(Hwnd, node.IsFolder ? "Renomear pasta" : "Renomear favorito", "Nome:", node.Name) is { } name) node.Name = name;
                break;
            case 4 when node is not null: Bookmarks.Remove(_bookmarks, node); break;
            case 5 when _web is not null:
                into.Children!.Add(BookmarkNode.Link(_web.DocumentTitle is { Length: > 0 } t ? t : HostOf(_web.Source), _web.Source));
                break;
            case 6:
                if (InputBox.Show(Hwnd, "Nova pasta", "Nome da pasta:", "") is { } folder) into.Children!.Add(BookmarkNode.Folder(folder));
                break;
            case 7:
                try
                {
                    int count = Bookmarks.ImportEdge(_bookmarks);
                    WebToast(count > 0 ? $"{count} favoritos trazidos do Edge" : "Os favoritos do Edge já estão aqui");
                    Log.Write($"Navegador: {count} favoritos importados do Edge.");
                }
                catch (Exception error) { WebToast("Não consegui importar: " + error.Message); }
                break;
            case 8:
                WebShortcut('B'); // mesmo que Ctrl+Shift+B
                return;
            default:
                return;
        }
        Bookmarks.Save(_bookmarks);
        WebDirty();
    }

    private static BookmarkNode? FindParent(BookmarkNode folder, BookmarkNode node)
    {
        if (folder.Children!.Contains(node)) return folder;
        foreach (var child in folder.Children)
            if (child.IsFolder && FindParent(child, node) is { } found) return found;
        return null;
    }

    // ---------- Desenho das faixas de cima (thread da tela) ----------

    private void DrawWebBar(ID3D11RenderTargetView target, int cw, UiPart hover)
    {
        int height = WebTop();
        if (height <= 0) return;
        bool showBooks = _prefs.WebBookmarksBar;
        (string title, ThumbnailCache.Thumb? icon, bool loading, bool audible, bool muted, bool userMuted, bool sleeping)[] tabs;
        int active, hoverIndex;
        bool canBack, canForward, loading, starred, dragging, settingsOpen;
        int security;
        double zoom;
        bool translate, translated;
        ThumbnailCache.Thumb? siteIcon;
        int dropIndex;
        BookmarkNode? dropFolder;
        List<BookmarkNode> books;
        lock (_lock)
        {
            tabs = _tabs.Select(t => (t.Title, t.Icon, t.Loading, t.Audible, t.Muted, t.UserMuted, t.Sleeping)).ToArray();
            active = _tab is null ? -1 : _tabs.IndexOf(_tab);
            hoverIndex = _webHoverIndex;
            (canBack, canForward, loading) = (_tab?.CanBack == true, _tab?.CanForward == true, _tab?.Loading == true);
            siteIcon = _tab?.Icon;
            (dragging, dropIndex, dropFolder) = (_dragActive, _dropIndex, _dropFolder);
            books = _bookmarks?.Children?.ToList() ?? [];
            starred = _bookmarks is not null && _tab is not null && Bookmarks.Find(_bookmarks, _tab.Url) is not null;
            settingsOpen = _tab is not null && IsSettingsUrl(_tab.Url);
            security = Security(_tab);
            (zoom, translate, translated) = (_tab?.Zoom ?? 1, ShowTranslate(_tab), _tab is not null && IsTranslated(_tab.Url));
        }
        var layout = TabLayout(cw, tabs.Length, active);
        string key = $"{cw}|{height}|{hover}|{hoverIndex}|{active}|{CaptionKey()}|{layout.first}|{canBack}{canForward}{loading}{starred}{AdBlock.On}{settingsOpen}|{siteIcon is not null}{security}|{zoom:0.00}{translate}{translated}|{dragging}{dropIndex}{dropFolder?.Name}|" +
                     string.Join("¦", tabs.Select(t => t.title + (t.icon is null ? "" : "*") + (t.loading ? "~" : "") + (t.audible ? "♪" : "") + (t.muted ? "m" : "") + (t.userMuted ? "u" : "") + (t.sleeping ? "z" : ""))) + "|" +
                     string.Join("¦", books.Select(b => b.Name + (b.IsFolder ? "/" + b.Children!.Count : "")));
        if (key != _webKey || _webLayer is null)
        {
            _webKey = key;
            var c = _webCanvas ??= new UiCanvas();
            c.Begin(cw, height);
            int strip = U(TabStrip), tools = strip + U(WebBar);
            c.Rect(0, 0, cw, strip, StripColor, 1);
            c.Rect(0, strip, cw, height - strip, BarColor, 1);
            int glyphH = c.TextHeight(_fontIcons), textH = c.TextHeight(_fontText), smallH = c.TextHeight(_fontChevron);

            // Abas: encolhem até um mínimo; depois disso rolam, com setas nas pontas (PlayerWebTabs).
            var tabRects = new List<(Native.RECT, Native.RECT, Native.RECT, int)>();
            int plusW = U(36), tw = layout.width;
            for (int i = layout.first; i < layout.first + layout.visible && i < tabs.Length; i++)
            {
                var t = tabs[i];
                int x = layout.left + (i - layout.first) * tw;
                var r = new Native.RECT { Left = x, Top = U(5), Right = x + tw - U(2), Bottom = strip };
                if (i == active) c.Rect(r.Left, r.Top, r.Width, r.Height, BarColor, 1);
                else if (hover is UiPart.WebTab or UiPart.WebTabClose or UiPart.WebTabAudio && hoverIndex == i) c.Rect(r.Left, r.Top, r.Width, r.Height, White, 0.07f);
                float fade = t.sleeping && i != active ? 0.45f : 1f; // dormindo: apagada, como no Edge
                bool showClose = i == active || hoverIndex == i || tw >= U(120);
                bool narrow = tw < U(90); // estreita (muitas abas): só o ícone, como no Edge; a ativa também com o X
                int iconSize = U(16), ix = narrow && !showClose ? r.Left + (r.Width - iconSize) / 2 : r.Left + U(10), iy = r.Top + (r.Height - iconSize) / 2;
                if (t.icon is { } icon) c.Icon(icon.Bgra, icon.Width, icon.Height, ix, iy, iconSize, iconSize);
                else c.Text(t.loading ? IconReload : IconGlobe, _fontChevron, ix + U(2), r.Top + (r.Height - smallH) / 2, Muted, fade);
                if (fade < 1) c.Rect(ix, iy, iconSize, iconSize, i == active ? BarColor : StripColor, 0.5f);
                var close = new Native.RECT { Left = r.Right - U(26), Top = r.Top + U(5), Right = r.Right - U(6), Bottom = r.Bottom - U(5) };
                int textRight = showClose ? close.Left - U(4) : r.Right - U(8);
                // Alto-falante: tocando som (ou silenciada por você); clicar silencia / devolve o som.
                var audio = default(Native.RECT);
                if ((t.audible || t.userMuted) && textRight - (ix + iconSize) > U(40))
                {
                    audio = new Native.RECT { Left = textRight - U(24), Top = r.Top + U(5), Right = textRight - U(2), Bottom = r.Bottom - U(5) };
                    if (hover == UiPart.WebTabAudio && hoverIndex == i) c.Rect(audio.Left, audio.Top, audio.Width, audio.Height, White, 0.15f);
                    string glyph = t.muted ? IconMute : IconVolume;
                    c.Text(glyph, _fontChevron, audio.Left + (audio.Width - c.TextWidth(glyph, _fontChevron)) / 2, audio.Top + (audio.Height - smallH) / 2, White, t.muted ? 0.6f : 0.9f);
                    textRight = audio.Left - U(2);
                }
                if (!narrow)
                    c.Text(Ellipsis(c, t.title, _fontText, textRight - (ix + iconSize + U(8))), _fontText, ix + iconSize + U(8), r.Top + (r.Height - textH) / 2,
                        White, (i == active ? 1f : 0.75f) * fade);
                if (showClose)
                {
                    if (hover == UiPart.WebTabClose && hoverIndex == i) c.Rect(close.Left, close.Top, close.Width, close.Height, White, 0.15f);
                    c.Text(IconClose, _fontChevron, close.Left + (close.Width - c.TextWidth(IconClose, _fontChevron)) / 2, close.Top + (close.Height - smallH) / 2, White, 0.8f);
                }
                tabRects.Add((r, showClose ? close : default, audio, i));
            }
            var tabsLeft = default(Native.RECT);
            var tabsRight = default(Native.RECT);
            int tabsEnd = layout.left + layout.visible * tw;
            if (layout.overflow)
            {
                Native.RECT Arrow(int x, string glyph, UiPart part, bool enabled)
                {
                    var r = new Native.RECT { Left = x, Top = U(6), Right = x + layout.arrow - U(2), Bottom = strip - U(4) };
                    if (hover == part && enabled) c.Rect(r.Left, r.Top, r.Width, r.Height, White, 0.12f);
                    c.Text(glyph, _fontChevron, r.Left + (r.Width - c.TextWidth(glyph, _fontChevron)) / 2, r.Top + (r.Height - smallH) / 2, White, enabled ? 0.85f : 0.25f);
                    return r;
                }
                tabsLeft = Arrow(layout.left - layout.arrow, IconChevronLeft, UiPart.WebTabsLeft, layout.first > 0);
                tabsRight = Arrow(tabsEnd, IconChevronRight, UiPart.WebTabsRight, layout.first + layout.visible < tabs.Length);
                tabsEnd += layout.arrow;
            }
            int plusX = tabsEnd;
            var plus = new Native.RECT { Left = plusX + U(4), Top = U(6), Right = plusX + U(4) + plusW - U(4), Bottom = strip - U(4) };
            if (hover == UiPart.WebNewTab) c.Rect(plus.Left, plus.Top, plus.Width, plus.Height, White, 0.12f);
            c.Text(IconAdd, _fontChevron, plus.Left + (plus.Width - c.TextWidth(IconAdd, _fontChevron)) / 2, plus.Top + (plus.Height - smallH) / 2, White, 0.85f);
            DrawCaptionButtons(c, cw);

            // Barra de endereço.
            Native.RECT Button(int x, int w, string glyph, string? label, UiPart part, bool enabled, bool on = false)
            {
                var r = new Native.RECT { Left = x, Top = strip + U(5), Right = x + w, Bottom = tools - U(5) };
                if (on) c.Rect(r.Left, r.Top, r.Width, r.Height, Accent, 0.28f);
                if (hover == part && enabled) c.Rect(r.Left, r.Top, r.Width, r.Height, White, 0.12f);
                float alpha = enabled ? 0.92f : 0.3f;
                int gy = r.Top + (r.Height - glyphH) / 2;
                if (label is null) c.Text(glyph, _fontIcons, x + (w - c.TextWidth(glyph, _fontIcons)) / 2, gy, White, alpha);
                else
                {
                    int total = c.TextWidth(glyph, _fontIcons) + U(8) + c.TextWidth(label, _fontText);
                    int gx = x + (w - total) / 2;
                    c.Text(glyph, _fontIcons, gx, gy, White, alpha);
                    c.Text(label, _fontText, gx + c.TextWidth(glyph, _fontIcons) + U(8), r.Top + (r.Height - textH) / 2, White, alpha);
                }
                return r;
            }
            int bx = U(8);
            var back = Button(bx, U(WebButton), IconBack, null, UiPart.WebBack, canBack);
            var forward = Button(bx + U(WebButton), U(WebButton), IconForward, null, UiPart.WebForward, canForward);
            var reload = Button(bx + 2 * U(WebButton), U(WebButton), loading ? IconStop : IconReload, null, UiPart.WebReload, true);
            var address = AddressRect(cw);
            c.Rect(address.Left, address.Top, address.Width, address.Height, FieldColor, 1);
            if (hover == UiPart.WebAddress) c.Rect(address.Left, address.Top, address.Width, address.Height, White, 0.05f);
            // Cadeado (clique: informações do site; arrastar: vira favorito), como no Edge (PlayerWebAddress).
            var site = new Native.RECT { Left = address.Left + U(4), Top = address.Top + U(2), Right = address.Left + U(30), Bottom = address.Bottom - U(2) };
            if (hover == UiPart.WebSiteIcon) c.Rect(site.Left, site.Top, site.Width, site.Height, White, 0.12f);
            var (lockGlyph, lockColor) = SecurityIcon(security);
            c.Text(lockGlyph, _fontChevron, site.Left + (site.Width - c.TextWidth(lockGlyph, _fontChevron)) / 2, site.Top + (site.Height - smallH) / 2, lockColor, 0.9f);
            var star = new Native.RECT { Left = address.Right - U(36), Top = address.Top + U(2), Right = address.Right - U(4), Bottom = address.Bottom - U(2) };
            // Traduzir (página em outra língua; aceso = traduzida, clique volta ao original) e o zoom fora de 100%.
            var translateRect = default(Native.RECT);
            int extrasRight = star.Left - U(2);
            if (translate)
            {
                translateRect = new Native.RECT { Left = extrasRight - U(32), Top = star.Top, Right = extrasRight, Bottom = star.Bottom };
                if (translated) c.Rect(translateRect.Left, translateRect.Top, translateRect.Width, translateRect.Height, Accent, 0.28f);
                if (hover == UiPart.WebTranslate) c.Rect(translateRect.Left, translateRect.Top, translateRect.Width, translateRect.Height, White, 0.12f);
                c.Text(IconTranslate, _fontIcons, translateRect.Left + (translateRect.Width - c.TextWidth(IconTranslate, _fontIcons)) / 2, translateRect.Top + (translateRect.Height - glyphH) / 2, White, 0.9f);
                extrasRight = translateRect.Left - U(2);
            }
            var zoomRect = default(Native.RECT);
            if (Math.Abs(zoom - 1) >= 0.01)
            {
                string label = $"{zoom * 100:0}%";
                zoomRect = new Native.RECT { Left = extrasRight - U(58), Top = star.Top, Right = extrasRight, Bottom = star.Bottom };
                if (hover == UiPart.WebZoom) c.Rect(zoomRect.Left, zoomRect.Top, zoomRect.Width, zoomRect.Height, White, 0.12f);
                int total = c.TextWidth(IconZoom, _fontChevron) + U(4) + c.TextWidth(label, _fontText), zx = zoomRect.Left + (zoomRect.Width - total) / 2;
                c.Text(IconZoom, _fontChevron, zx, zoomRect.Top + (zoomRect.Height - smallH) / 2, White, 0.8f);
                c.Text(label, _fontText, zx + c.TextWidth(IconZoom, _fontChevron) + U(4), zoomRect.Top + (zoomRect.Height - textH) / 2, White, 0.9f);
            }
            if (hover == UiPart.WebStar) c.Rect(star.Left, star.Top, star.Width, star.Height, White, 0.12f);
            string starGlyph = starred ? IconStarFilled : IconStar;
            c.Text(starGlyph, _fontIcons, star.Left + (star.Width - c.TextWidth(starGlyph, _fontIcons)) / 2, star.Top + (star.Height - glyphH) / 2, starred ? 0xF5C542 : White, 0.9f);
            var shield = Button(address.Right + U(4), U(WebButton), IconShield, null, UiPart.WebShield, true, AdBlock.On);
            var autoRect = default(Native.RECT);
            var player = Button(cw - U(8) - U(WebButton) - U(4) - U(104), U(104), IconPlayer, "Player", UiPart.WebPlayer, true);
            var gear = Button(cw - U(8) - U(WebButton), U(WebButton), IconSettings, null, UiPart.WebSettings, true, settingsOpen);

            // Favoritos.
            var bookRects = new List<(Native.RECT, BookmarkNode)>();
            var more = default(Native.RECT);
            int shown = 0;
            if (showBooks)
            {
                c.Rect(0, tools, cw, 1, White, 0.06f);
                int x = U(8), moreW = U(32);
                if (books.Count == 0)
                    c.Text("Para guardar um site aqui, clique na ☆ da barra de endereço (Ctrl+D).", _fontText, U(14), tools + (U(BookmarkBar) - textH) / 2, Muted, 0.8f);
                foreach (var book in books)
                {
                    string name = book.Name.Length > 0 ? book.Name : HostOf(book.Url ?? "");
                    string label = Ellipsis(c, name, _fontText, U(150));
                    int w = U(10) + U(16) + U(6) + c.TextWidth(label, _fontText) + U(10);
                    if (x + w > cw - moreW - U(8)) break;
                    var r = new Native.RECT { Left = x, Top = tools + U(3), Right = x + w, Bottom = tools + U(BookmarkBar) - U(3) };
                    if (hover == UiPart.WebBookmark && hoverIndex == shown) c.Rect(r.Left, r.Top, r.Width, r.Height, White, 0.12f);
                    int iconSize = U(16), iy = r.Top + (r.Height - iconSize) / 2;
                    ThumbnailCache.Thumb? favicon = null;
                    if (!book.IsFolder) lock (_lock) favicon = FaviconFor(book.Url!);
                    if (book.IsFolder) c.Text("", _fontChevron, r.Left + U(10), r.Top + (r.Height - smallH) / 2, 0xE8C15A);
                    else if (favicon is { } f) c.Icon(f.Bgra, f.Width, f.Height, r.Left + U(10), iy, iconSize, iconSize);
                    else c.Text(IconGlobe, _fontChevron, r.Left + U(10), r.Top + (r.Height - smallH) / 2, Muted);
                    c.Text(label, _fontText, r.Left + U(10) + iconSize + U(6), r.Top + (r.Height - textH) / 2, White, 0.9f);
                    bookRects.Add((r, book));
                    x += w + U(2);
                    shown++;
                }
                // Onde o arrasto vai soltar: linha entre dois favoritos, ou a pasta acesa.
                if (dragging && dropFolder is not null)
                    foreach (var (r, node) in bookRects)
                        if (node == dropFolder) c.Rect(r.Left, r.Top, r.Width, r.Height, Accent, 0.4f);
                if (dragging && dropFolder is null && dropIndex >= 0)
                {
                    int lineX = dropIndex < bookRects.Count ? bookRects[dropIndex].Item1.Left - U(2) : bookRects.Count > 0 ? bookRects[^1].Item1.Right + U(1) : U(8);
                    c.Rect(lineX, tools + U(4), U(2), U(BookmarkBar) - U(8), Accent, 1);
                }
                if (shown < books.Count)
                {
                    more = new Native.RECT { Left = cw - moreW - U(8), Top = tools + U(3), Right = cw - U(8), Bottom = tools + U(BookmarkBar) - U(3) };
                    if (hover == UiPart.WebBookmarksMore) c.Rect(more.Left, more.Top, more.Width, more.Height, White, 0.12f);
                    c.Text("»", _fontText, more.Left + (more.Width - c.TextWidth("»", _fontText)) / 2, more.Top + (more.Height - textH) / 2, White, 0.9f);
                }
            }
            lock (_lock)
            {
                _tabRects.Clear();
                _tabRects.AddRange(tabRects);
                (_tabLeftRect, _tabRightRect) = (tabsLeft, tabsRight);
                _bookmarkRects.Clear();
                _bookmarkRects.AddRange(bookRects);
                (_newTabRect, _webBackRect, _webForwardRect, _webReloadRect, _webAddressRect, _webStarRect, _webAutoRect, _webPlayerRect, _bookmarksMoreRect, _bookmarksShown) =
                    (plus, back, forward, reload, address, star, autoRect, player, more, shown);
                _webShieldRect = shield;
                (_webZoomRect, _webTranslateRect) = (zoomRect, translateRect);
                _webSettingsRect = gear;
                _siteIconRect = site;
            }
            _webLayer = c.Upload(_gpu);
        }
        _gpu.DrawOverlay(target, 0, 0, cw, height, _webLayer);
    }

    // ---------- Mensagens das páginas ----------

    private void OnWebMessage(string json, CoreWebView2Frame? frame, WebTab tab)
    {
        if (tab != _tab) return; // abas de fundo não abrem nada sozinhas
        JsonElement info;
        try { info = JsonDocument.Parse(json).RootElement.Clone(); }
        catch { return; }
        string type = Str(info, "type");
        if (type.StartsWith("settings-"))
        {
            // Só a página de configurações do app (no quadro principal) muda os ajustes; um site não.
            if (frame is null && tab.Web is { } web && IsSettingsUrl(web.Source)) OnSettingsMessage(type, info, web);
            return;
        }
        switch (type)
        {
            case "dot":
                DotPressed();
                break;
            case "candidate":
                _webCandidates?.Add((info, frame));
                break;
            case "image":
                GrabImage(info);
                break;
            case "open-video": // o botão "Abrir no player" sobre o vídeo (page.js): o mesmo que o "." com aquele vídeo
                Grab(info, frame);
                break;
            case "link-press": // botão do meio / Ctrl+clique num link (a aba nova vem logo depois)
                _linkPress = (Str(info, "href"), Flag(info, "background"), Environment.TickCount64);
                break;
        }
    }

    // ---------- Botão direito numa imagem ou vídeo: "Abrir no player" (o mesmo que o ".", com o que foi clicado) ----------

    private CoreWebView2ContextMenuItem? _menuPlayer;
    private (WebTab tab, JsonElement? media, CoreWebView2Frame? frame, CoreWebView2ContextMenuTargetKind kind, string src, string page)? _menuTarget;

    private async void OnContextMenu(WebTab tab, CoreWebView2ContextMenuRequestedEventArgs e)
    {
        if (tab != _tab || tab.Web is not { } web || _webEnv is null) return;
        var target = e.ContextMenuTarget;
        var kind = target.Kind;
        string src = target.HasSourceUri ? target.SourceUri : "", page = target.FrameUri;
        var deferral = e.GetDeferral(); // o menu espera a página dizer o que foi clicado (alguns milissegundos)
        try
        {
            var (media, frame) = await MenuMedia(tab, web);
            int at = 0;
            // Imagem/vídeo no ponto (inclusive por baixo de camadas): abrir no player.
            if (media is not null || (kind == CoreWebView2ContextMenuTargetKind.Image && IsWeb(src)) || kind == CoreWebView2ContextMenuTargetKind.Video)
            {
                _menuTarget = (tab, media, frame, kind, src, page);
                if (_menuPlayer is null)
                {
                    _menuPlayer = _webEnv.CreateContextMenuItem("Abrir no player", null, CoreWebView2ContextMenuItemKind.Command);
                    _menuPlayer.CustomItemSelected += (_, _) => PostUi(OpenMenuTarget); // depois que o menu fecha
                }
                e.MenuItems.Insert(at++, _menuPlayer);
            }
            // Link (ou um endereço no texto selecionado): abrir em uma nova aba — atrás, ao lado desta (como o botão do meio).
            string? link = target.HasLinkUri && IsWeb(target.LinkUri) ? target.LinkUri : target.HasSelection ? LinkIn(target.SelectionText) : null;
            _menuLink = link;
            if (link is not null)
            {
                var open = _webEnv.CreateContextMenuItem("Abrir link em uma nova aba", null, CoreWebView2ContextMenuItemKind.Command);
                open.CustomItemSelected += (_, _) => PostUi(() => _ = OpenTab(link, select: false));
                e.MenuItems.Insert(at++, open);
            }
            // Texto selecionado: pesquisar no Google (aba nova, na frente).
            if (target.HasSelection && target.SelectionText.Trim() is { Length: > 0 } selection)
            {
                string shown = selection.Length > 32 ? selection[..32].TrimEnd() + "…" : selection;
                var search = _webEnv.CreateContextMenuItem($"Pesquisar no Google por \"{shown.Replace('\n', ' ')}\"", null, CoreWebView2ContextMenuItemKind.Command);
                search.CustomItemSelected += (_, _) => PostUi(() => _ = OpenTab("https://www.google.com/search?q=" + Uri.EscapeDataString(selection), select: true));
                e.MenuItems.Insert(at++, search);
            }
            // Página (sem texto, imagem ou vídeo): traduzir.
            else if (kind == CoreWebView2ContextMenuTargetKind.Page && media is null && IsWeb(web.Source) && !IsSettingsUrl(web.Source))
            {
                bool translated = IsTranslated(web.Source);
                var translate = _webEnv.CreateContextMenuItem(translated ? "Ver a página original" : "Traduzir para o português", null, CoreWebView2ContextMenuItemKind.Command);
                translate.CustomItemSelected += (_, _) => PostUi(TranslatePage);
                e.MenuItems.Insert(at++, translate);
            }
            if (at > 0) e.MenuItems.Insert(at, _webEnv.CreateContextMenuItem("", null, CoreWebView2ContextMenuItemKind.Separator));
            if (TestSuppressMenu)
            {
                TestMenuLabels = [.. e.MenuItems.Select(i => i.Label)];
                e.Handled = true; // autoteste: sem o menu na tela
            }
        }
        catch (Exception error) { Log.Write($"Navegador: menu do botão direito — {error.Message}"); }
        finally { deferral.Complete(); }
    }

    // A imagem/vídeo do último botão direito, perguntada à página e a cada quadro (iframe): fica a resposta do quadro
    // onde o clique foi (o mais recente). Até 400 ms.
    private static async Task<(JsonElement? media, CoreWebView2Frame? frame)> MenuMedia(WebTab tab, CoreWebView2 web)
    {
        const string script = "window.__mkvMenuMedia ? JSON.stringify(window.__mkvMenuMedia()) : 'null'";
        var asks = new List<(Task<string> task, CoreWebView2Frame? frame)>();
        try { asks.Add((web.ExecuteScriptAsync(script), null)); } catch { }
        foreach (var frame in tab.Frames.ToArray())
            try { asks.Add((frame.ExecuteScriptAsync(script), frame)); } catch { } // quadro que acabou de sumir
        await Task.WhenAny(Task.WhenAll(asks.Select(a => (Task)a.task)), Task.Delay(400));
        JsonElement? best = null;
        CoreWebView2Frame? bestFrame = null;
        foreach (var (task, frame) in asks)
        {
            if (!task.IsCompletedSuccessfully) continue;
            try
            {
                if (JsonSerializer.Deserialize<string>(task.Result) is not { } inner || inner == "null") continue;
                var info = JsonDocument.Parse(inner).RootElement.Clone();
                if (best is null || Num(info, "at") > Num(best.Value, "at")) (best, bestFrame) = (info, frame);
            }
            catch { }
        }
        return (best, bestFrame);
    }

    private void OpenMenuTarget()
    {
        if (_menuTarget is not { } m || m.tab != _tab) return;
        _menuTarget = null;
        if (m.media is { } info)
        {
            if (Str(info, "type") == "image") GrabImage(info);
            else Grab(info, m.frame);
        }
        else if (m.kind == CoreWebView2ContextMenuTargetKind.Image)
            GrabImage(JsonSerializer.SerializeToElement(new { type = "image", src = m.src, gallery = new[] { m.src }, frame = m.page }));
        else AskFrames(); // vídeo que a página não descreveu: o da tela, como o "."
    }

    // Autoteste: itens do último menu (sem mostrá-lo) e escolher "Abrir no player".
    public bool TestSuppressMenu;
    public string[] TestMenuLabels = [];
    public void TestPickMenuPlayer() => OpenMenuTarget();

    private static bool IsSettingsUrl(string url) => url.StartsWith("https://" + SettingsHost + "/", StringComparison.OrdinalIgnoreCase);

    // Engrenagem: a aba de configurações (se já está aberta, vai para ela).
    private void OpenSettings()
    {
        WebTab? open;
        lock (_lock) open = _tabs.FirstOrDefault(t => IsSettingsUrl(t.Web?.Source ?? t.Url));
        if (open is not null) _ = SelectTab(open);
        else _ = OpenTab(SettingsUrl, select: true);
    }

    private static string DownloadsFolder() => Environment.GetEnvironmentVariable("MKV_DOWNLOADS") is { Length: > 0 } test ? test // autoteste
        : Path.Combine(VideoDownload.DownloadsFolder(), "MK Vision");

    private void PostSettingsState(CoreWebView2 web) => web.PostWebMessageAsJson(JsonSerializer.Serialize(new
    {
        type = "state", startup = _prefs.WebStartup, pages = _prefs.WebStartupPages, bookmarksBar = _prefs.WebBookmarksBar,
        adblock = AdBlock.On, adblockInstalled = AdBlock.Extension is not null, downloads = DownloadsFolder(),
        mobileSites = _prefs.WebMobileSites, adblockAllowed = _prefs.AdBlockAllowed,
        clearOnClose = _prefs.WebClearOnClose, smartScreen = _prefs.WebSmartScreen,
        colorScheme = _prefs.WebColorScheme, forceDark = _prefs.WebForceDark, sleepMinutes = _prefs.WebSleepMinutes
    }));

    private async void OnSettingsMessage(string type, JsonElement info, CoreWebView2 web)
    {
        void Toast(string text) => web.PostWebMessageAsJson(JsonSerializer.Serialize(new { type = "toast", text }));
        switch (type)
        {
            case "settings-get":
                break;
            case "settings-set":
                if (info.TryGetProperty("startup", out var startup) && startup.ValueKind == JsonValueKind.Number)
                    _prefs.WebStartup = Math.Clamp(startup.GetInt32(), 0, 2);
                if (info.TryGetProperty("pages", out var pages) && pages.ValueKind == JsonValueKind.Array)
                    _prefs.WebStartupPages = pages.EnumerateArray().Select(p => p.GetString() ?? "").Where(IsWeb).Take(50).ToList();
                if (info.TryGetProperty("mobileSites", out var sites) && sites.ValueKind == JsonValueKind.Array)
                {
                    _prefs.WebMobileSites = sites.EnumerateArray().Select(p => (p.GetString() ?? "").Trim().ToLowerInvariant())
                        .Where(d => d.Length > 0 && d.Contains('.') && Uri.CheckHostName(d) == UriHostNameType.Dns).Distinct().Take(50).ToList();
                    ApplyMobileSites();
                }
                if (info.TryGetProperty("colorScheme", out var scheme) && scheme.ValueKind == JsonValueKind.Number)
                {
                    _prefs.WebColorScheme = Math.Clamp(scheme.GetInt32(), 0, 2);
                    ApplyColorScheme();
                }
                if (info.TryGetProperty("forceDark", out var dark) && dark.ValueKind is JsonValueKind.True or JsonValueKind.False)
                {
                    _prefs.WebForceDark = dark.GetBoolean();
                    ApplyForceDark();
                }
                if (info.TryGetProperty("sleepMinutes", out var sleep) && sleep.ValueKind == JsonValueKind.Number)
                    _prefs.WebSleepMinutes = Math.Clamp(sleep.GetInt32(), 0, 24 * 60);
                if (info.TryGetProperty("clearOnClose", out var clear) && clear.ValueKind is JsonValueKind.True or JsonValueKind.False)
                    _prefs.WebClearOnClose = clear.GetBoolean();
                if (info.TryGetProperty("smartScreen", out var screen) && screen.ValueKind is JsonValueKind.True or JsonValueKind.False)
                {
                    _prefs.WebSmartScreen = screen.GetBoolean();
                    foreach (var open in TabsSnapshot())
                        if (open.Web is not null) open.Web.Settings.IsReputationCheckingRequired = _prefs.WebSmartScreen;
                }
                if (info.TryGetProperty("adblockAllowed", out var allowed) && allowed.ValueKind == JsonValueKind.Array)
                {
                    _prefs.AdBlockAllowed = allowed.EnumerateArray().Select(p => (p.GetString() ?? "").Trim().ToLowerInvariant())
                        .Where(d => d.Length > 0 && d.Contains('.') && Uri.CheckHostName(d) == UriHostNameType.Dns).Distinct().Take(50).ToList();
                    await SyncAdBlockSites();
                    Toast("Pronto (vale ao recarregar as páginas)");
                }
                if (info.TryGetProperty("bookmarksBar", out var bar) && bar.ValueKind is JsonValueKind.True or JsonValueKind.False && bar.GetBoolean() != _prefs.WebBookmarksBar)
                {
                    _prefs.WebBookmarksBar = bar.GetBoolean();
                    LayoutWeb();
                    WebDirty();
                }
                if (info.TryGetProperty("adblock", out var block) && block.ValueKind is JsonValueKind.True or JsonValueKind.False && AdBlock.Extension is not null && block.GetBoolean() != AdBlock.On)
                {
                    await AdBlock.SetEnabled(block.GetBoolean());
                    Toast(AdBlock.On ? "Bloqueador ligado (vale ao recarregar as páginas)" : "Bloqueador desligado (vale ao recarregar as páginas)");
                    WebDirty();
                }
                _prefs.Save();
                break;
            case "settings-current-tabs":
            {
                List<string> urls;
                lock (_lock) urls = _tabs.Select(t => t.Web?.Source ?? t.Url).Where(u => IsWeb(u) && !IsSettingsUrl(u)).Distinct().ToList();
                web.PostWebMessageAsJson(JsonSerializer.Serialize(new { type = "tabs", urls }));
                return;
            }
            case "settings-import-edge":
                try
                {
                    _bookmarks ??= Bookmarks.Load();
                    int count = Bookmarks.ImportEdge(_bookmarks);
                    Bookmarks.Save(_bookmarks);
                    Toast(count > 0 ? $"{count} favoritos trazidos do Edge" : "Os favoritos do Edge já estão aqui");
                    Log.Write($"Navegador: {count} favoritos importados do Edge.");
                    WebDirty();
                }
                catch (Exception error) { Toast("Não consegui importar: " + error.Message); }
                break;
            case "settings-adblock-dashboard":
                if (AdBlock.Id is { } extensionId) _ = OpenTab($"chrome-extension://{extensionId}/dashboard.html", select: true);
                else Toast("O bloqueador ainda está instalando");
                return;
            case "settings-downloads-list":
                OpenDownloads();
                return;
            case "settings-open-downloads":
                Directory.CreateDirectory(DownloadsFolder());
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{DownloadsFolder()}\"") { UseShellExecute = true });
                return;
            default:
                return;
        }
        PostSettingsState(web);
    }

    public string TestSettingsUrl => SettingsUrl;
    public void TestOpenBackground(string url) => _ = OpenTab(url, select: false);
    // O "Abrir link em uma nova aba" do último botão direito (como se escolhido).
    private string? _menuLink;
    public void TestPickMenuLink() { if (_menuLink is { } link) _ = OpenTab(link, select: false); }
    // Abas: endereço carregado (vazio = ainda não criada), muda e ativa.
    public (string url, bool muted, bool active)[] TestTabs
    {
        get { lock (_lock) return _tabs.Select(t => (t.Web?.Source ?? "", t.Web?.IsMuted ?? true, t == _tab)).ToArray(); }
    }
    public Task<string> TestCrashPage() => _web!.CallDevToolsProtocolMethodAsync("Page.crash", "{}");
    public async Task<string> TestCookieFile(params string[] urls) => File.ReadAllText(await CookieFile(_web!, urls));
    public void TestSelectTab(int index) { WebTab tab; lock (_lock) tab = _tabs[index]; _ = SelectTab(tab); }
    public Task<string> TestCdp(string method, string json) => _web!.CallDevToolsProtocolMethodAsync(method, json);
    public System.Drawing.Rectangle[] TestTabBounds { get { lock (_lock) return _tabs.Select(t => t.Controller?.Bounds ?? default).ToArray(); } }
    public bool[] TestTabVisible { get { lock (_lock) return _tabs.Select(t => t.Controller?.IsVisible == true).ToArray(); } }
    public void TestSetSessionTabs(params string[] urls) { _prefs.WebTabs = [.. urls]; _prefs.WebTab = 0; _prefs.WebStartup = 0; }
    public void TestOpenSettings() => OpenSettings();
    public (int startup, string[] pages, bool bar) TestStartup => (_prefs.WebStartup, [.. _prefs.WebStartupPages], _prefs.WebBookmarksBar);

    private static string Str(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
    private static double Num(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0;
    private static bool Flag(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    // ".": primeiro a imagem sob o cursor (por baixo das camadas que alguns sites põem por cima das fotos, como o
    // Instagram); se não houver, o vídeo da tela.
    private async void DotPressed()
    {
        if (CursorInPage() is { } point && await ImageAt(point.x, point.y) is { } image)
        {
            GrabImage(image);
            return;
        }
        AskFrames();
    }

    // Cursor em coordenadas da página (CSS), ou null se ele está fora dela.
    private (double x, double y)? CursorInPage()
    {
        if (_webController is not { } controller) return null;
        GetCursorPos(out var cursor);
        ScreenToClient(Hwnd, ref cursor);
        var b = controller.Bounds;
        if (cursor.X < b.Left || cursor.X >= b.Right || cursor.Y < b.Top || cursor.Y >= b.Bottom) return null;
        double scale = controller.RasterizationScale * controller.ZoomFactor;
        return ((cursor.X - b.Left) / scale, (cursor.Y - b.Top) / scale);
    }

    private async Task<JsonElement?> ImageAt(double x, double y)
    {
        if (_web is not { } web) return null;
        try
        {
            string json = await web.ExecuteScriptAsync(FormattableString.Invariant($"window.__mkvImageAt ? JSON.stringify(window.__mkvImageAt({x:0.#}, {y:0.#})) : 'null'"));
            string? inner = JsonSerializer.Deserialize<string>(json);
            if (inner is null or "null") return null;
            return JsonDocument.Parse(inner).RootElement.Clone();
        }
        catch { return null; }
    }

    public async Task<bool> TestImageAt(double x, double y)
    {
        if (await ImageAt(x, y) is not { } image) return false;
        GrabImage(image);
        return true;
    }

    // ".": pergunta a cada quadro da página qual é o seu melhor vídeo e fica com o que está tocando/maior.
    private async void AskFrames(bool download = false)
    {
        if (_web is null || _tab is null) return;
        var candidates = new List<(JsonElement info, CoreWebView2Frame? frame)>();
        _webCandidates = candidates;
        try { await _web.ExecuteScriptAsync("window.__mkvReport && window.__mkvReport()"); } catch { }
        foreach (var frame in _tab.Frames.ToArray())
            try { _ = frame.ExecuteScriptAsync("window.__mkvReport && window.__mkvReport()"); } catch { }
        await Task.Delay(350);
        _webCandidates = null;
        var best = candidates.Where(c => Num(c.info, "score") >= 0).OrderByDescending(c => Num(c.info, "score")).FirstOrDefault();
        if (best.info.ValueKind == JsonValueKind.Undefined)
        {
            WebToast("Nenhum vídeo nesta página (passe o mouse numa imagem para abrir a imagem)");
            return;
        }
        Grab(best.info, best.frame, download);
    }

    private void WebToast(string text)
    {
        try { _ = _web?.ExecuteScriptAsync($"window.__mkvToast && window.__mkvToast({JsonSerializer.Serialize(text)})"); } catch { }
    }

    // Sites que o yt-dlp conhece bem: ele acha a melhor qualidade (o vídeo da página costuma ser em pedaços).
    private static bool PreferYtDlp(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) &&
        System.Text.RegularExpressions.Regex.IsMatch(u.Host, @"(^|\.)(youtube\.com|youtu\.be|pornhub\.com|xvideos\.com|xnxx\.com|xhamster\.com|spankbang\.com|eporner\.com|vimeo\.com|dailymotion\.com|twitter\.com|x\.com|reddit\.com|tiktok\.com|instagram\.com)$");

    // Abre o vídeo no player: pausa o do site, volta para o player e descobre o endereço (direto ou pelo yt-dlp).
    // "download": Ctrl+S — o mesmo vídeo que o "." abriria é salvo em Downloads\MK Vision (o site continua).
    private async void Grab(JsonElement info, CoreWebView2Frame? frame, bool download = false)
    {
        if (_web is null) return;
        var web = _web;
        string src = Str(info, "src"), frameUrl = Str(info, "frame"), page = web.Source;
        string title = web.DocumentTitle is { Length: > 0 } t ? t : "vídeo";
        (_webGrabbed, _webGrabbedAt) = (frameUrl + "|" + src, Environment.TickCount64 / 1000.0);
        if (download) WebToast("Procurando o vídeo para baixar...");
        else
        {
            try
            {
                if (frame is null) await web.ExecuteScriptAsync("window.__mkvPause && window.__mkvPause()");
                else await frame.ExecuteScriptAsync("window.__mkvPause && window.__mkvPause()");
            }
            catch { }
            ShowWeb(false);
            Notify($"Abrindo: {title}");
            lock (_lock) _osdUntil = Now + 30; // fica até o vídeo abrir
        }
        string agent = web.Settings.UserAgent;
        string? cookies = null;
        try
        {
            cookies = await CookieFile(web, [page, frameUrl, src]);
            // Cabeçalho "Cookie" para o arquivo direto (cookies do domínio dele).
            var forSrc = IsWeb(src) ? await web.CookieManager.GetCookiesAsync(src) : [];
            string cookieHeader = string.Join("; ", forSrc.Select(c => $"{c.Name}={c.Value}"));
            string pageForTools = Flag(info, "top") || !IsWeb(frameUrl) ? page : frameUrl;
            // RedGIFs: o vídeo do feed vem em pedaços (blob); pelo identificador do bloco chegamos ao MP4 em HD
            // (media.redgifs.com/Nome.mp4) e, se não der, à página do vídeo, que o yt-dlp conhece.
            string item = Str(info, "item");
            if (item.Length > 0 && Uri.TryCreate(pageForTools, UriKind.Absolute, out var feed) && feed.Host.EndsWith("redgifs.com"))
            {
                pageForTools = $"https://www.redgifs.com/watch/{Uri.EscapeDataString(item)}";
                var m = System.Text.RegularExpressions.Regex.Match(Str(info, "itemMedia"),
                    @"^(https://media\.redgifs\.com/[^/?#]+?)(-mobile|-silent)?\.(m4s|mp4)([?#].*)?$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                if (m.Success) src = m.Groups[1].Value + ".mp4";
                if (title.Length == 0 || title.Contains("RedGIFs", StringComparison.OrdinalIgnoreCase)) title = item;
            }

            // A duração e o tamanho do vídeo da tela: num post com vários itens (carrossel), o yt-dlp acha o certo.
            var hint = new MediaHint(Num(info, "duration"), (int)Num(info, "width"), (int)Num(info, "height"));
            // Story do Instagram: pela própria página, com o login (o yt-dlp não consegue a lista; PlayerWebInstagram).
            _instagramLimited = false;
            WebMedia? media = await InstagramStory(web, pageForTools, agent, hint);
            if (IsStory(pageForTools))
                Log.Write($"Navegador: vídeo do story na página: {(src.StartsWith("blob:") ? "em pedaços (blob)" : IsWeb(src) ? "arquivo direto" : "sem endereço")}.");
            // No story, o vídeo da tela direto também vale (se for um arquivo comum), antes do yt-dlp.
            if (media is null && IsWeb(src) && (!PreferYtDlp(pageForTools) || IsStory(pageForTools)))
            {
                string direct = await BetterFile(src);
                var headers = new Dictionary<string, string> { ["User-Agent"] = agent, ["Referer"] = frameUrl };
                if (cookieHeader.Length > 0) headers["Cookie"] = cookieHeader;
                try
                {
                    await Task.Run(() => new HttpRangeSource(direct, headers).Dispose()); // o servidor entrega por trechos?
                    string ext = System.Text.RegularExpressions.Regex.Match(direct, @"\.(mp4|m4v|webm|mov)(\?|#|$)", System.Text.RegularExpressions.RegexOptions.IgnoreCase) is { Success: true } m
                        ? "." + m.Groups[1].Value.ToLowerInvariant() : ".mp4";
                    media = new WebMedia(title, frameUrl, direct, headers, null, null, false, null, Num(info, "duration"), 0, ext);
                }
                catch (Exception error) { Log.Write($"Navegador: arquivo direto não deu ({error.Message}); tentando pelo yt-dlp."); }
            }
            // O Instagram já disse "pedidos demais": o yt-dlp (mais 3 pedidos) só pioraria. Avisa (no catch).
            if (media is null && IsStory(pageForTools) && _instagramLimited) throw new InvalidOperationException("429");
            if (media is null && _prefetch.TryGetValue(pageForTools, out var early) && Environment.TickCount64 / 1000.0 - early.at < 1200)
            {
                try
                {
                    bool ready = early.task.IsCompletedSuccessfully;
                    media = await early.task;
                    Log.Write($"Navegador: vídeo {(ready ? "já estava pronto" : "estava sendo procurado")} (pré-carregado).");
                }
                catch { _prefetch.Remove(pageForTools); } // o pré-carregamento falhou: procura de novo agora
            }
            media ??= await Task.Run(() => WebResolver.ResolveWorking(pageForTools, title, agent, cookies, CancellationToken.None, hint));
            if (download)
            {
                SaveMedia(web, media, item.Length > 0 && title != item ? $"{title} ({item})" : title);
                return;
            }
            Log.Write($"Navegador: abrindo o vídeo no player ({(media.Hls ? "HLS" : media.AudioUrl is not null ? "imagem e som separados" : "arquivo")}).");
            OpenWeb(media, agent, cookies);
        }
        catch (Exception error)
        {
            Log.Write($"Navegador: não consegui {(download ? "baixar" : "abrir")} o vídeo — {error.Message}");
            string reason = _instagramLimited ? "o Instagram está limitando os pedidos agora (429). Espere alguns minutos e tente de novo." : error.Message;
            if (download) WebToast("Não consegui baixar: " + reason);
            else Notify("Não consegui abrir este vídeo: " + reason);
        }
    }

    // Ctrl+S: salva o vídeo em Downloads\MK Vision. Arquivo direto (RedGIFs etc.): baixa mostrando o andamento na
    // página. HLS / YouTube: o yt-dlp baixa (só a imagem quando o som vem separado — sem ffmpeg para juntar).
    private async void SaveMedia(CoreWebView2 web, WebMedia media, string name)
    {
        string folder = Environment.GetEnvironmentVariable("MKV_DOWNLOADS") is { Length: > 0 } test ? test // autoteste
            : DownloadsFolder();
        Directory.CreateDirectory(folder);
        string safe = string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)).Trim();
        if (safe.Length > 120) safe = safe[..120];
        string ext = media.Hls ? ".ts" : media.Extension;
        string path = Path.Combine(folder, safe + ext);
        for (int i = 2; File.Exists(path); i++) path = Path.Combine(folder, $"{safe} ({i}){ext}");
        void Toast(string text)
        {
            try { _ = web.ExecuteScriptAsync($"window.__mkvToast && window.__mkvToast({JsonSerializer.Serialize(text)})"); } catch { }
        }
        Log.Write("Navegador: baixando um vídeo.");
        try
        {
            if (!media.Hls && !media.Download && media.AudioUrl is null)
            {
                // Arquivo direto: baixa com os cabeçalhos do site, mostrando quanto já foi.
                string target = path;
                await Task.Run(async () =>
                {
                    using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
                    using var request = new HttpRequestMessage(HttpMethod.Get, media.VideoUrl);
                    foreach (var (key, value) in media.VideoHeaders) request.Headers.TryAddWithoutValidation(key, value);
                    using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
                    response.EnsureSuccessStatusCode();
                    long total = response.Content.Headers.ContentLength ?? 0, got = 0;
                    await using var source = await response.Content.ReadAsStreamAsync();
                    await using (var file = File.Create(target + ".part"))
                    {
                        var buffer = new byte[1 << 20];
                        var shown = System.Diagnostics.Stopwatch.StartNew();
                        int n;
                        while ((n = await source.ReadAsync(buffer)) > 0)
                        {
                            await file.WriteAsync(buffer.AsMemory(0, n));
                            got += n;
                            if (shown.ElapsedMilliseconds > 700 && total > 0)
                            {
                                shown.Restart();
                                int percent = (int)(got * 100 / total);
                                PostUi(() => Toast($"Baixando... {percent}%"));
                            }
                        }
                    }
                    File.Move(target + ".part", target);
                });
            }
            else
            {
                Toast("Baixando (pelo yt-dlp)...");
                string raw = media.Hls ? path + ".raw.ts" : path;
                await new VideoDownload().DownloadFormat(media.Page, media.FormatId, raw, media.ToolAgent, media.ToolCookies, CancellationToken.None, media.Item);
                if (media.Hls)
                {
                    // HLS do YouTube vem sem relógio (PCR): a cópia final já sai consertada (as outras passam iguais).
                    await TsFixer.Run(raw, path, Task.CompletedTask, CancellationToken.None);
                    File.Delete(raw);
                }
            }
            long size = new FileInfo(path).Length;
            Toast($"Baixado: {Path.GetFileName(path)} ({size / 1048576.0:0.0} MB) — em Downloads\\MK Vision");
            Log.Write($"Navegador: download terminado ({size / 1048576.0:0.0} MB)."); 
        }
        catch (Exception error)
        {
            try { if (File.Exists(path + ".part")) File.Delete(path + ".part"); } catch { }
            Log.Write($"Navegador: o download falhou — {error.Message}");
            Toast("O download falhou: " + error.Message);
        }
    }

    // RedGIFs entrega a versão leve ("-mobile") na página; a HD tem o mesmo nome sem o sufixo.
    private static async Task<string> BetterFile(string url)
    {
        var sd = System.Text.RegularExpressions.Regex.Match(url, @"^(https://[^/?#]*redgifs\.com/[^?#]+?)-mobile(\.mp4)([?#].*)?$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (!sd.Success) return url;
        string hd = sd.Groups[1].Value + sd.Groups[2].Value + sd.Groups[3].Value;
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            using var response = await http.SendAsync(new HttpRequestMessage(HttpMethod.Head, hd));
            if (response.IsSuccessStatusCode) return hd;
        }
        catch { }
        return url;
    }

    // ".": imagem sob o mouse — abre no visualizador; as outras imagens grandes da página vêm junto (Page Up/Down).
    private async void GrabImage(JsonElement info)
    {
        if (_web is null) return;
        var web = _web;
        string src = Str(info, "src"), frameUrl = Str(info, "frame");
        if (!IsWeb(src)) return;
        var gallery = info.TryGetProperty("gallery", out var g) && g.ValueKind == JsonValueKind.Array
            ? g.EnumerateArray().Select(e => e.GetString() ?? "").Where(IsWeb).ToArray() : [src];
        if (gallery.Length == 0) gallery = [src];
        int index = Math.Max(0, Array.IndexOf(gallery, src));
        var headers = new Dictionary<string, string> { ["User-Agent"] = web.Settings.UserAgent, ["Referer"] = frameUrl };
        try
        {
            var cookies = await web.CookieManager.GetCookiesAsync(src);
            if (cookies.Count > 0) headers["Cookie"] = string.Join("; ", cookies.Select(c => $"{c.Name}={c.Value}"));
        }
        catch { }
        ShowWeb(false);
        OpenWebGallery(gallery, index, headers);
    }

    private void DisposeWeb()
    {
        WebResolver.DeleteCookieFiles();
        if (_tabs.Count > 0) SaveSession();
        foreach (var tab in TabsSnapshot())
            try { tab.Controller?.Close(); } catch { }
        _webCanvas?.Dispose();
        _tabTip?.Dispose();
        _suggest?.Dispose();
        if (_addressFont != IntPtr.Zero) UiCanvas.DeleteFont(_addressFont);
        if (_addressBrush != IntPtr.Zero) DeleteObject(_addressBrush);
        _webDownload?.Cancel();
    }

    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
}
