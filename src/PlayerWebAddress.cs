using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Web.WebView2.Core;
using static MKVision.Desktop.PlayerNative;

namespace MKVision.Desktop;

// Barra de endereço, como no Edge:
// - digitando, aparecem sugestões — SÓ dos favoritos (nome ou endereço; sem acento e sem diferença de maiúsculas).
//   ↑/↓ escolhem (o endereço aparece na barra), Enter abre, Esc fecha; clique também abre;
// - o cadeado no começo da barra (no lugar do ícone do site; continua dando para arrastá-lo para os favoritos): clique
//   mostra se a conexão é segura e as permissões do site (câmera, microfone, localização...), liga/desliga o
//   bloqueador e o modo celular só para ele e apaga os cookies e dados dele.
internal sealed partial class Player
{
    private const string IconLock = "", IconWarning = "", IconInfo = "";

    // ---------- Sugestões dos favoritos ----------

    private WebPopup? _suggest;
    private List<BookmarkNode> _suggestItems = [];
    private string _addressTyped = "", _addressLast = "";

    // Texto da barra (o que está digitado).
    private string AddressValue()
    {
        var buffer = new char[4096];
        int n = GetWindowTextW(_address, buffer, buffer.Length);
        return new string(buffer, 0, n);
    }

    // Depois de cada tecla/colar na barra: se o texto mudou, refaz as sugestões.
    private void AddressChanged()
    {
        string text = AddressValue();
        if (text == _addressLast) return;
        _addressLast = _addressTyped = text;
        ShowSuggestions(text);
    }

    private void ShowSuggestions(string text)
    {
        _bookmarks ??= Bookmarks.Load();
        _suggestItems = SuggestFavorites(_bookmarks, text, 8);
        if (_suggestItems.Count == 0 || GetFocus() != _address)
        {
            HideSuggestions();
            return;
        }
        Native.RECT address;
        lock (_lock) address = _webAddressRect;
        var corner = new Native.POINT { X = address.Left, Y = address.Bottom + U(4) };
        ClientToScreen(Hwnd, ref corner);
        var rows = new List<WebPopup.Row>();
        foreach (var node in _suggestItems)
        {
            ThumbnailCache.Thumb? icon;
            lock (_lock) icon = FaviconFor(node.Url!);
            rows.Add(new WebPopup.Row(node.Name.Length > 0 ? node.Name : HostOf(node.Url!), ShownUrl(node.Url!), icon));
        }
        if (_suggest is null)
        {
            _suggest = new WebPopup(Hwnd, list: true);
            _suggest.Picked += index => OpenSuggestion(index, newTab: false);
        }
        _suggest.Show(corner.X, corner.Y, address.Right - address.Left, rows);
    }

    private void HideSuggestions()
    {
        _suggest?.Hide();
        _suggestItems = [];
    }

    // ↑/↓ na barra com as sugestões abertas: a escolhida aparece na barra (↑ no topo volta ao que foi digitado).
    private void MoveSuggestion(int delta)
    {
        if (_suggest is not { Visible: true } popup) return;
        int next = popup.Selected + delta;
        if (next < -1) next = popup.Count - 1;
        if (next >= popup.Count) next = -1;
        popup.Selected = next;
        string text = next < 0 ? _addressTyped : ShownUrl(_suggestItems[next].Url!);
        _addressLast = text;
        SetWindowText(_address, text);
        SendMessageW(_address, EM_SETSEL, text.Length, text.Length);
    }

    private void OpenSuggestion(int index, bool newTab)
    {
        if (index < 0 || index >= _suggestItems.Count) return;
        string url = _suggestItems[index].Url!;
        HideSuggestions();
        _addressLast = ShownUrl(url);
        SetWindowText(_address, _addressLast);
        if (newTab || _web is null) _ = OpenTab(url, select: true);
        else
        {
            _web.Navigate(url);
            _webController?.MoveFocus(CoreWebView2MoveFocusReason.Programmatic);
        }
    }

    // Favoritos que combinam com o texto: todas as palavras têm de aparecer no nome ou no endereço. Primeiro os que
    // começam pelo texto (nome, depois site), depois os que só contêm. Até "max".
    internal static List<BookmarkNode> SuggestFavorites(BookmarkNode root, string text, int max)
    {
        string query = Fold(text.Trim());
        if (query.Length == 0) return [];
        var words = query.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var found = new List<(BookmarkNode node, int score)>();
        void Walk(BookmarkNode folder)
        {
            foreach (var node in folder.Children ?? [])
            {
                if (node.IsFolder) { Walk(node); continue; }
                if (node.Url is not { } url) continue;
                string name = Fold(node.Name), address = Fold(Uri.UnescapeDataString(url));
                string bare = System.Text.RegularExpressions.Regex.Replace(address, @"^[a-z]+://(www\.)?", "");
                if (!words.All(w => name.Contains(w) || bare.Contains(w))) continue;
                int score = name.StartsWith(query) ? 0
                    : bare.StartsWith(query) ? 1
                    : name.Split(' ', StringSplitOptions.RemoveEmptyEntries).Any(w => w.StartsWith(words[0])) ? 2
                    : name.Contains(query) ? 3 : 4;
                if (!found.Any(f => f.node.Url == url)) found.Add((node, score)); // o mesmo site em duas pastas: uma vez só
            }
        }
        Walk(root);
        return found.OrderBy(f => f.score).ThenBy(f => f.node.Name.Length).Take(max).Select(f => f.node).ToList();
    }

    // Sem acentos e em minúsculas ("Vídeos" acha "videos").
    private static string Fold(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (char c in text.ToLowerInvariant().Normalize(NormalizationForm.FormD))
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark) builder.Append(c);
        return builder.ToString().Normalize(NormalizationForm.FormC);
    }

    // ---------- Cadeado: segurança e informações do site ----------

    // Como está a conexão da aba: segura (https sem erro de certificado), não segura (http ou certificado com erro) ou
    // página do próprio app/arquivo. Com ou sem _lock.
    private static int Security(WebTab? tab)
    {
        if (tab is null) return 2;
        string url = tab.Url;
        if (IsSettingsUrl(url) || !IsWeb(url)) return 2;
        return url.StartsWith("https://", StringComparison.OrdinalIgnoreCase) && !tab.CertError ? 0 : 1;
    }

    private static (string glyph, uint color) SecurityIcon(int security) => security switch
    {
        0 => (IconLock, 0xFFFFFF),
        1 => (IconWarning, 0xF2B84B), // âmbar: não seguro
        _ => (IconInfo, 0xB8BCC2)
    };

    private static readonly (CoreWebView2PermissionKind kind, string name)[] SitePermissions =
    [
        (CoreWebView2PermissionKind.Camera, "Câmera"),
        (CoreWebView2PermissionKind.Microphone, "Microfone"),
        (CoreWebView2PermissionKind.Geolocation, "Localização"),
        (CoreWebView2PermissionKind.Notifications, "Notificações"),
        (CoreWebView2PermissionKind.ClipboardRead, "Área de transferência"),
        (CoreWebView2PermissionKind.Autoplay, "Tocar sozinho (com som)"),
        (CoreWebView2PermissionKind.MultipleAutomaticDownloads, "Vários downloads"),
    ];

    private async void SiteInfoMenu()
    {
        if (_web is not { } web || _tab is not { } tab || !Uri.TryCreate(web.Source, UriKind.Absolute, out var uri)) return;
        int security = Security(tab);
        bool site = IsWeb(web.Source) && !IsSettingsUrl(web.Source);
        string origin = uri.GetLeftPart(UriPartial.Authority), host = HostOf(web.Source);
        string domain = uri.Host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? uri.Host[4..] : uri.Host;
        var states = new Dictionary<CoreWebView2PermissionKind, CoreWebView2PermissionState>();
        if (site)
            try
            {
                foreach (var setting in await web.Profile.GetNonDefaultPermissionSettingsAsync())
                    if (string.Equals(setting.PermissionOrigin.TrimEnd('/'), origin, StringComparison.OrdinalIgnoreCase))
                        states[setting.PermissionKind] = setting.PermissionState;
            }
            catch (Exception error) { Log.Write($"Navegador: não consegui ler as permissões do site — {error.Message}"); }
        bool adblockOff = _prefs.AdBlockAllowed.Any(d => HostMatches(uri.Host, d));
        bool mobile = _prefs.WebMobileSites.Any(d => HostMatches(uri.Host, d));
        const int Permission = 1000; // 1000 + permissão * 10 + estado
        int id = Menu(menu =>
        {
            AppendMenu(menu, MF_GRAYED, 0, security switch
            {
                0 => $"🔒 Conexão segura — {host}",
                1 => tab.CertError ? $"⚠ Certificado com problema — {host}" : $"⚠ Conexão não segura (http) — não digite senhas aqui",
                _ => IsSettingsUrl(web.Source) ? "Página do MK Vision" : "Página local"
            });
            if (!site) return;
            AppendMenu(menu, MF_SEPARATOR, 0, null);
            var permissions = CreatePopupMenu();
            for (int i = 0; i < SitePermissions.Length; i++)
            {
                var (kind, name) = SitePermissions[i];
                var state = states.TryGetValue(kind, out var s) ? s : CoreWebView2PermissionState.Default;
                var choices = CreatePopupMenu();
                AppendMenu(choices, MF_STRING | (state == CoreWebView2PermissionState.Allow ? MF_CHECKED : 0), Permission + i * 10 + 1, "Permitir");
                AppendMenu(choices, MF_STRING | (state == CoreWebView2PermissionState.Deny ? MF_CHECKED : 0), Permission + i * 10 + 2, "Bloquear");
                AppendMenu(choices, MF_STRING | (state == CoreWebView2PermissionState.Default ? MF_CHECKED : 0), Permission + i * 10 + 0, "Perguntar (padrão)");
                string label = state switch { CoreWebView2PermissionState.Allow => "permitido", CoreWebView2PermissionState.Deny => "bloqueado", _ => "perguntar" };
                AppendMenu(permissions, MF_POPUP, choices, $"{name}: {label}");
            }
            AppendMenu(menu, MF_POPUP, permissions, "Permissões deste site");
            AppendMenu(menu, MF_STRING | (adblockOff ? 0 : MF_CHECKED), 1, $"Bloqueador de anúncios em {Short(domain)}");
            AppendMenu(menu, MF_STRING | (mobile ? MF_CHECKED : 0), 2, $"Modo celular em {Short(domain)}");
            AppendMenu(menu, MF_SEPARATOR, 0, null);
            AppendMenu(menu, MF_STRING, 3, $"Apagar cookies e dados de {Short(domain)}...");
        });
        if (id >= Permission)
        {
            var (kind, name) = SitePermissions[(id - Permission) / 10];
            var state = ((id - Permission) % 10) switch { 1 => CoreWebView2PermissionState.Allow, 2 => CoreWebView2PermissionState.Deny, _ => CoreWebView2PermissionState.Default };
            try
            {
                await web.Profile.SetPermissionStateAsync(kind, origin, state);
                WebToast($"{name}: {(state switch { CoreWebView2PermissionState.Allow => "permitido", CoreWebView2PermissionState.Deny => "bloqueado", _ => "vai perguntar" })} neste site");
            }
            catch (Exception error) { WebToast("Não consegui mudar a permissão: " + error.Message); }
        }
        else if (id == 1)
        {
            var list = _prefs.AdBlockAllowed.Where(d => !HostMatches(uri.Host, d)).ToList();
            if (!adblockOff) list.Add(domain);
            _prefs.AdBlockAllowed = list;
            _prefs.Save();
            await SyncAdBlockSites();
            web.Reload();
            WebToast(adblockOff ? "Bloqueador ligado neste site" : "Bloqueador desligado neste site");
        }
        else if (id == 2)
        {
            var list = _prefs.WebMobileSites.Where(d => !HostMatches(uri.Host, d)).ToList();
            if (!mobile) list.Add(domain);
            _prefs.WebMobileSites = list;
            _prefs.Save();
            ApplyMobileSites(); // a aba troca na hora
        }
        else if (id == 3)
        {
            if (MessageBoxW(Hwnd, $"Apagar os cookies e os dados guardados de {domain}?\n\nVocê sai da conta neste site.", "MK Vision", 0x4 | 0x30 /* SIM/NÃO, ATENÇÃO */) != 6) return;
            await ClearSiteData(web, origin);
            web.Reload();
            WebToast($"Cookies e dados de {domain} apagados");
        }
    }

    private static bool HostMatches(string host, string domain) =>
        host.Equals(domain, StringComparison.OrdinalIgnoreCase) || host.EndsWith("." + domain, StringComparison.OrdinalIgnoreCase);

    // Cookies do site (inclusive os do domínio de cima que ele usa) e o que ele guardou (armazenamento, cache, service workers).
    private static async Task ClearSiteData(CoreWebView2 web, string origin)
    {
        try
        {
            foreach (var cookie in await web.CookieManager.GetCookiesAsync(origin)) web.CookieManager.DeleteCookie(cookie);
            await web.CallDevToolsProtocolMethodAsync("Storage.clearDataForOrigin", System.Text.Json.JsonSerializer.Serialize(new { origin, storageTypes = "all" }));
            Log.Write("Navegador: cookies e dados de um site apagados.");
        }
        catch (Exception error) { Log.Write($"Navegador: não consegui apagar os dados do site — {error.Message}"); }
    }

    private const uint MF_CHECKED = 0x8;
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int MessageBoxW(IntPtr owner, string text, string caption, uint type);

    public void TestTypeAddress(string text) { FocusAddress(); SetWindowText(_address, text); AddressChanged(); }
    public string[] TestSuggestions => [.. _suggestItems.Select(b => b.Name)];
    public bool TestSuggestVisible => _suggest?.Visible == true;
    public IntPtr TestSuggestHandle => _suggest?.Handle ?? IntPtr.Zero;
    public IntPtr TestTipHandle => _tabTip?.Handle ?? IntPtr.Zero;
    public void TestAddressKey(int key) => SendMessageW(_address, WM_KEYDOWN, key, IntPtr.Zero);
    public int TestSecurity { get { lock (_lock) return Security(_tab); } }
    // Autoteste: a permissão do site aberto, mudada como o menu do cadeado faz e lida de volta.
    public async Task<string> TestSitePermission(CoreWebView2PermissionKind kind, CoreWebView2PermissionState state)
    {
        var web = _web!;
        string origin = new Uri(web.Source).GetLeftPart(UriPartial.Authority);
        await web.Profile.SetPermissionStateAsync(kind, origin, state);
        var found = (await web.Profile.GetNonDefaultPermissionSettingsAsync()).FirstOrDefault(p => p.PermissionKind == kind && p.PermissionOrigin.TrimEnd('/') == origin);
        return found is null ? "Default" : found.PermissionState.ToString();
    }
}
