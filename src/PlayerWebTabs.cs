using System.Runtime.InteropServices;
using Microsoft.Web.WebView2.Core;

namespace MKVision.Desktop;

// Faixa das abas, como no Edge:
// - muitas abas encolhem até um mínimo e depois rolam (setas nas pontas, roda do mouse sobre a faixa); a aba ativa
//   fica sempre à vista;
// - arrastar uma aba muda a ordem;
// - aba tocando som mostra o alto-falante; clicar nele silencia (ou devolve o som);
// - parar o mouse sobre uma aba mostra o título inteiro e o site;
// - aba parada há um tempo (Configurações → Desempenho) "dorme": o motor congela a página e libera memória e placa;
//   ela acorda sozinha ao voltar para ela.
internal sealed partial class Player
{
    private const int TabMin = 56, TabMax = 230, TabArrow = 28;
    private const string IconChevronLeft = "";

    private int _tabFirst, _tabActiveSeen = -1;                                    // com _lock
    private (int first, int visible, int left, int width, bool overflow) _tabLayout; // com _lock: último desenho
    private Native.RECT _tabLeftRect, _tabRightRect;                                // setas (com _lock)

    [DllImport("user32.dll")] private static extern bool ClientToScreen(IntPtr hwnd, ref Native.POINT point);

    // Onde cada aba fica: a primeira à vista, quantas cabem, onde começam e a largura. Thread da tela.
    private (int first, int visible, int left, int width, int arrow, bool overflow) TabLayout(int cw, int count, int active)
    {
        int start = U(8), end = cw - CaptionWidth() - U(8) - U(36), min = U(TabMin), avail = end - start, arrow = 0;
        bool overflow = count * min > avail;
        if (overflow)
        {
            arrow = U(TabArrow);
            avail -= 2 * arrow;
        }
        int visible = overflow ? Math.Max(1, avail / min) : count;
        int width = count == 0 ? 0 : overflow ? avail / visible : Math.Clamp(avail / count, min, U(TabMax));
        lock (_lock)
        {
            if (active != _tabActiveSeen)
            {
                // Outra aba ficou ativa (clique, Ctrl+Tab, aba nova): ela vem para a vista. Rolar depois não a traz de volta.
                _tabActiveSeen = active;
                if (active >= 0 && active < _tabFirst) _tabFirst = active;
                else if (active >= _tabFirst + visible) _tabFirst = active - visible + 1;
            }
            _tabFirst = Math.Clamp(_tabFirst, 0, Math.Max(0, count - visible));
            _tabLayout = (_tabFirst, visible, start + arrow, width, overflow);
            return (_tabFirst, visible, start + arrow, width, arrow, overflow);
        }
    }

    // Rola a faixa das abas (setas e roda do mouse).
    private void ScrollTabs(int delta)
    {
        lock (_lock) _tabFirst = Math.Max(0, _tabFirst + delta); // o desenho limita o fim
        WebDirty();
    }

    // Roda do mouse sobre as faixas de cima: sobre as abas, rola (quando não cabem). true = usada.
    private bool WebWheel(int x, int y, int delta)
    {
        if (!WebMode || y < 0 || y >= WebTop()) return false;
        bool overflow;
        lock (_lock) overflow = _tabLayout.overflow;
        if (y < U(TabStrip) && overflow) ScrollTabs(delta > 0 ? -1 : 1);
        return true; // nas faixas de cima a roda não faz zoom no player
    }

    // Som da aba: o motor avisa quando ela toca; o app silencia as de fundo que você ainda não abriu e todas no
    // player. "UserMuted" é o alto-falante clicado.
    private void ApplyMute(WebTab tab)
    {
        if (tab.Web is not { } web) return;
        bool muted = !WebMode || !tab.Visited || tab.UserMuted;
        try { web.IsMuted = muted; } catch { }
        tab.Muted = muted;
    }

    private void ToggleTabMute(WebTab tab)
    {
        if (tab.Muted) (tab.UserMuted, tab.Visited) = (false, true); // inclusive a de fundo ainda não aberta
        else tab.UserMuted = true;
        ApplyMute(tab);
        WebDirty();
    }

    private void OnAudioChanged(WebTab tab)
    {
        if (tab.Web is not { } web) return;
        tab.Audible = web.IsDocumentPlayingAudio;
        WebDirty();
    }

    // ---------- Arrastar uma aba (muda a ordem) ----------

    private WebTab? _pressTab; // aba pressionada (vira arrasto se o mouse andar)

    private void BeginTabPress(WebTab tab, int x, int y)
    {
        lock (_lock) _pressTab = tab;
        StartPress(x, y);
    }

    // Com _lock, arrastando: a aba vai para a posição sob o mouse.
    private void MoveDraggedTab(int x)
    {
        if (_pressTab is not { } tab) return;
        var (first, visible, left, width, _) = _tabLayout;
        if (width <= 0) return;
        int target = Math.Clamp(first + (x - left) / width, 0, _tabs.Count - 1);
        target = Math.Clamp(target, first, first + visible - 1);
        int from = _tabs.IndexOf(tab);
        if (from < 0 || target == from) return;
        _tabs.RemoveAt(from);
        _tabs.Insert(Math.Min(target, _tabs.Count), tab);
        _tabActiveSeen = _tab is null ? -1 : _tabs.IndexOf(_tab); // a ativa mudou de lugar, mas não "ficou ativa"
        _dirty = true;
    }

    // ---------- Dica com o título inteiro ----------

    private WebPopup? _tabTip;
    private int _tipIndex = -1;
    private long _tipSince;

    // ~10x por segundo (WM_TIMER): a dica aparece depois de o mouse parar 0,6 s numa aba.
    private void UpdateTabTip()
    {
        int index = -1;
        lock (_lock)
            if (_webMode && !Dragging && _hover is UiPart.WebTab or UiPart.WebTabClose or UiPart.WebTabAudio) index = _webHoverIndex;
        long now = Environment.TickCount64;
        if (index != _tipIndex)
        {
            (_tipIndex, _tipSince) = (index, now);
            _tabTip?.Hide();
            return;
        }
        if (index < 0 || now - _tipSince < 600 || _tabTip?.Visible == true) return;
        WebTab tab;
        Native.RECT rect;
        lock (_lock)
        {
            if (index >= _tabs.Count) return;
            tab = _tabs[index];
            rect = _tabRects.FirstOrDefault(r => r.index == index).tab;
        }
        var point = new Native.POINT { X = rect.Left, Y = U(TabStrip) + U(2) };
        ClientToScreen(Hwnd, ref point);
        string site = IsSettingsUrl(tab.Url) ? SettingsShown : HostOf(tab.Web?.Source ?? tab.Url);
        if (tab.Sleeping) site += " · dormindo (libera memória)";
        else if (tab.Audible && !tab.Muted) site += " · tocando som";
        _tabTip ??= new WebPopup(Hwnd, list: false);
        _tabTip.Show(point.X, point.Y, 0, [new WebPopup.Row(tab.Title, site)]);
    }

    // Clique, arrasto, troca de modo: a dica some e só volta ao parar de novo numa aba.
    private void HideTabTip()
    {
        _tabTip?.Hide();
        (_tipIndex, _tipSince) = (-2, Environment.TickCount64);
    }

    // ---------- Abas dormindo ----------

    private long _sleepCheckedAt;

    // De 30 em 30 s: aba escondida há mais que o tempo escolhido (e sem som) dorme. No player, todas dormem com o tempo.
    private void SleepTabs()
    {
        long now = Environment.TickCount64;
        if (now - _sleepCheckedAt < 30_000) return;
        _sleepCheckedAt = now;
        int minutes = _prefs.WebSleepMinutes;
        if (minutes <= 0) return;
        foreach (var tab in TabsSnapshot())
        {
            if (tab.Web is not { } web || tab.Controller is not { } controller || tab.Sleeping || tab.Audible) continue;
            bool visible;
            try { visible = controller.IsVisible; } catch { continue; }
            if (visible) continue;
            if (tab.HiddenAt == 0) tab.HiddenAt = now;
            if (now - tab.HiddenAt >= minutes * 60_000L) Suspend(tab, web);
        }
    }

    private async void Suspend(WebTab tab, CoreWebView2 web)
    {
        try
        {
            if (!await web.TrySuspendAsync()) return; // tocando, em tela cheia... o motor recusa
            tab.Sleeping = true;
            WebDirty();
        }
        catch { } // a aba fechou no meio
    }

    // Chamado ~10x por segundo (WM_TIMER).
    private void WebTimer()
    {
        if (_webEnv is null) return;
        UpdateTabTip();
        SleepTabs();
        PeekBars(); // F11: faixas de cima com o mouse no topo (PlayerWebPage)
    }

    public void TestSleepNow(int minutes) { _prefs.WebSleepMinutes = minutes; _sleepCheckedAt = 0; foreach (var t in TabsSnapshot()) t.HiddenAt = 1; SleepTabs(); } // autoteste: como se estivessem escondidas há muito tempo
    public (bool audible, bool muted, bool sleeping)[] TestTabStates { get { lock (_lock) return _tabs.Select(t => (t.Audible, t.Muted, t.Sleeping)).ToArray(); } }
    public void TestToggleMute(int index) { WebTab tab; lock (_lock) tab = _tabs[index]; ToggleTabMute(tab); }
    public (int first, int visible, bool overflow) TestTabLayout { get { lock (_lock) return (_tabLayout.first, _tabLayout.visible, _tabLayout.overflow); } }
    public (int x, int y) TestTabPoint(int index) { lock (_lock) { var r = _tabRects.First(t => t.index == index).tab; return ((r.Left + r.Right) / 2, (r.Top + r.Bottom) / 2); } }
    public bool TestTipVisible => _tabTip?.Visible == true;
    public void TestWheel(int x, int y, int delta) => WebWheel(x, y, delta);
}
