using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using static MKVision.Desktop.PlayerNative;

namespace MKVision.Desktop;

// Facilidades da página, como no Edge:
// - zoom (Ctrl + / Ctrl − / Ctrl 0, ou Ctrl + roda) lembrado por site; fora de 100% aparece "125%" na barra de
//   endereço (clique: aumentar, diminuir, voltar a 100%);
// - F11: tela cheia do navegador (as faixas de cima aparecem ao levar o mouse ao topo da tela);
// - traduzir: página em outra língua mostra o botão "A字" na barra, que abre a tradução do Google Tradutor na mesma
//   aba (de novo: volta ao original); também no botão direito;
// - tema dos sites (Configurações → Aparência): o do Windows, escuro ou claro; e escurecer também os sites sem tema
//   escuro (o "modo escuro automático" do motor, aba por aba);
// - Ctrl+J: a lista de downloads do motor.
internal sealed partial class Player
{
    private const string IconTranslate = "", IconZoom = "";
    private static readonly double[] ZoomSteps = [0.25, 0.33, 0.5, 0.67, 0.75, 0.8, 0.9, 1, 1.1, 1.25, 1.5, 1.75, 2, 2.5, 3, 4, 5];

    private Native.RECT _webZoomRect, _webTranslateRect; // com _lock: onde ficaram no último desenho

    // ---------- Zoom por site ----------

    private bool _applyingZoom;

    private static string ZoomKey(string url) => Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.Host.ToLowerInvariant().Replace("www.", "") : "";

    // O zoom da aba mudou (atalho, Ctrl + roda, menu): guarda para o site.
    private void OnZoomChanged(WebTab tab)
    {
        if (tab.Controller is not { } controller || tab.Web is not { } web) return;
        double zoom = controller.ZoomFactor;
        if (tab.Mobile) return; // no modo celular o zoom é o que encaixa a coluna na tela (não é do site)
        tab.Zoom = zoom;
        // Carregando outra página, quem muda o zoom é o motor (volta a 100% ao trocar de site): não é escolha sua. O zoom
        // do site novo vem quando ela termina de carregar (ApplySiteZoom).
        if (!_applyingZoom && !tab.Loading) RememberZoom(web, zoom);
        if (tab == _tab) LayoutWeb();
        WebDirty();
    }

    private void RememberZoom(CoreWebView2 web, double zoom)
    {
        if (ZoomKey(web.Source) is not { Length: > 0 } site || !IsWeb(web.Source)) return;
        if (Math.Abs(zoom - 1) < 0.01) _prefs.WebZoom.Remove(site);
        else _prefs.WebZoom[site] = Math.Round(zoom, 2);
        _prefs.Save();
    }

    // Entrou num site: o zoom guardado dele (ou 100%).
    private void ApplySiteZoom(WebTab tab)
    {
        if (tab.Controller is not { } controller || tab.Web is not { } web || tab.Mobile) return;
        double want = _prefs.WebZoom.TryGetValue(ZoomKey(web.Source), out double z) ? z : 1;
        if (Math.Abs(controller.ZoomFactor - want) < 0.01) return;
        _applyingZoom = true;
        try { controller.ZoomFactor = want; }
        finally { _applyingZoom = false; }
        tab.Zoom = want;
    }

    // Ctrl + / Ctrl − (próximo degrau, como no Edge) e Ctrl 0 (100%).
    private void ZoomStep(int direction)
    {
        if (_tab is not { Controller: { } controller } tab) return;
        if (tab.Mobile)
        {
            WebToast("No modo celular o tamanho é automático");
            return;
        }
        double now = controller.ZoomFactor, next = direction == 0 ? 1
            : direction > 0 ? ZoomSteps.FirstOrDefault(s => s > now + 0.001, ZoomSteps[^1])
            : ZoomSteps.LastOrDefault(s => s < now - 0.001, ZoomSteps[0]);
        controller.ZoomFactor = next;
        tab.Zoom = next;
        if (tab.Web is { } web) RememberZoom(web, next); // o motor só avisa das mudanças dele (Ctrl + roda)
        LayoutWeb();
        WebDirty();
        WebToast($"Zoom {next * 100:0}%");
    }

    private void ZoomMenu()
    {
        if (_tab is null) return;
        int id = Menu(menu =>
        {
            AppendMenu(menu, MF_GRAYED, 0, $"Zoom deste site: {_tab.Zoom * 100:0}%");
            AppendMenu(menu, MF_SEPARATOR, 0, null);
            AppendMenu(menu, MF_STRING, 1, "Aumentar\tCtrl +");
            AppendMenu(menu, MF_STRING, 2, "Diminuir\tCtrl −");
            AppendMenu(menu, MF_STRING, 3, "Voltar a 100%\tCtrl 0");
        });
        if (id is >= 1 and <= 3) ZoomStep(id switch { 1 => 1, 2 => -1, _ => 0 });
    }

    // ---------- F11: tela cheia do navegador ----------

    private bool _f11, _barsPeek; // tela cheia pelo F11 / faixas de cima à mostra (mouse no topo)

    private void ToggleWebFullscreen()
    {
        bool full;
        lock (_lock) full = _fullscreen;
        (_f11, _barsPeek) = (!full, false);
        ToggleFullscreen();
        LayoutWeb();
        WebDirty();
        if (_f11) WebToast("Tela cheia — F11 para sair (leve o mouse ao topo para ver as abas)");
    }

    // ~10x por segundo: em F11, o mouse no topo da tela mostra as faixas; saindo delas, somem.
    private void PeekBars()
    {
        bool full;
        lock (_lock) full = _fullscreen;
        if (!_f11) return;
        if (!full || !WebMode)
        {
            (_f11, _barsPeek) = (false, false); // saiu da tela cheia por outro caminho
            return;
        }
        GetCursorPos(out var cursor);
        ScreenToClient(Hwnd, ref cursor);
        int bars = U(TabStrip) + U(WebBar) + (_prefs.WebBookmarksBar ? U(BookmarkBar) : 0);
        bool peek = cursor.Y >= 0 && cursor.Y < (_barsPeek ? bars + U(8) : U(3)) || (_barsPeek && GetFocus() == _address);
        if (peek == _barsPeek) return;
        _barsPeek = peek;
        LayoutWeb();
        WebDirty();
    }

    // ---------- Traduzir ----------

    private static bool IsTranslated(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Host.EndsWith(".translate.goog", StringComparison.OrdinalIgnoreCase);

    // Página carregada: em que língua ela diz estar (o botão de traduzir aparece se não for português).
    private async void DetectLanguage(WebTab tab)
    {
        if (tab.Web is not { } web) return;
        string lang = "";
        try
        {
            if (IsWeb(web.Source) && !IsSettingsUrl(web.Source))
                lang = JsonSerializer.Deserialize<string>(await web.ExecuteScriptAsync("(document.documentElement.lang || '').trim().toLowerCase()")) ?? "";
        }
        catch { }
        tab.Language = lang;
        if (tab == _tab) LayoutWeb();
        WebDirty();
    }

    // Com ou sem _lock: mostra o botão de traduzir? (página em outra língua, ou já traduzida — aí ele volta ao original)
    private static bool ShowTranslate(WebTab? tab) =>
        tab is not null && (IsTranslated(tab.Url) || (tab.Language is { Length: >= 2 } lang && !lang.StartsWith("pt") && IsWeb(tab.Url) && !IsSettingsUrl(tab.Url)));

    private void TranslatePage()
    {
        if (_web is not { } web) return;
        if (IsTranslated(web.Source))
        {
            if (web.CanGoBack) web.GoBack(); // de volta ao original
            return;
        }
        if (!IsWeb(web.Source)) return;
        web.Navigate("https://translate.google.com/translate?sl=auto&tl=pt&hl=pt-BR&u=" + Uri.EscapeDataString(web.Source));
        Log.Write("Navegador: traduzindo uma página (Google Tradutor).");
    }

    // ---------- Tema dos sites ----------

    private void ApplyColorScheme()
    {
        if (_web is not { } web) return;
        try
        {
            web.Profile.PreferredColorScheme = _prefs.WebColorScheme switch
            {
                1 => CoreWebView2PreferredColorScheme.Dark,
                2 => CoreWebView2PreferredColorScheme.Light,
                _ => CoreWebView2PreferredColorScheme.Auto
            };
        }
        catch (Exception error) { Log.Write($"Navegador: não consegui mudar o tema dos sites — {error.Message}"); }
    }

    // Escurecer os sites sem tema escuro (o motor inverte as cores claras da página, como o "modo escuro" do Edge).
    private static async Task SetForceDark(CoreWebView2 web, bool on)
    {
        try { await web.CallDevToolsProtocolMethodAsync("Emulation.setAutoDarkModeOverride", on ? "{\"enabled\":true}" : "{}"); }
        catch (Exception error) { Log.Write($"Navegador: não consegui {(on ? "escurecer" : "clarear")} uma aba — {error.Message}"); }
    }

    private async void ApplyForceDark()
    {
        foreach (var tab in TabsSnapshot())
            if (tab.Web is { } web) await SetForceDark(web, _prefs.WebForceDark);
    }

    // ---------- Ctrl+J ----------

    private void OpenDownloads()
    {
        try { _web?.OpenDefaultDownloadDialog(); }
        catch (Exception error) { Log.Write($"Navegador: a lista de downloads não abriu — {error.Message}"); }
    }

    // Largura reservada à direita do endereço (a ☆, o "125%" e o "A字"). Com ou sem _lock.
    private int AddressRightReserve(WebTab? tab) =>
        U(40) + (tab is { Zoom: var z } && Math.Abs(z - 1) >= 0.01 ? U(60) : 0) + (ShowTranslate(tab) ? U(34) : 0);

    public double TestZoom => _tab?.Controller?.ZoomFactor ?? 0;
    public void TestZoomStep(int direction) => ZoomStep(direction);
    public bool TestShowTranslate { get { lock (_lock) return ShowTranslate(_tab); } }
    public void TestTranslate() => TranslatePage();
    public bool TestF11 => _f11;
    public void TestToggleF11() => ToggleWebFullscreen();
    public int TestWebTop => WebTop();
    public void TestPeek(bool on) { _barsPeek = on; LayoutWeb(); WebDirty(); }
    public bool TestDownloadsOpen => _web?.IsDefaultDownloadDialogOpen == true;
    public void TestDownloadsKey() => OpenDownloads(); // o que o Ctrl+J chama
    public string[] TestSavedZoom => [.. _prefs.WebZoom.Select(z => $"{z.Key}={z.Value}")];
}
