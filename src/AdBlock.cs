using System.IO.Compression;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;

namespace MKVision.Desktop;

// Bloqueio de anúncios do navegador: o uBlock Origin Lite (extensão de código aberto, versão para o Edge), baixado
// da página oficial dele no GitHub e instalado no perfil do navegador do app. Confere versão nova a cada 7 dias.
// Fica em %LOCALAPPDATA%\MKVisionDesktop\extensions\ubol.
internal static class AdBlock
{
    private const string Releases = "https://api.github.com/repos/uBlockOrigin/uBOL-home/releases/latest";
    private static readonly string Root = Environment.GetEnvironmentVariable("MKV_EXTENSIONS") is { Length: > 0 } test ? test // autoteste
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MKVisionDesktop", "extensions");
    private static readonly string Folder = Path.Combine(Root, "ubol");
    private static readonly string Stamp = Path.Combine(Root, "ubol.version");

    public static string? Id { get; private set; } // para abrir o painel dele (chrome-extension://Id/dashboard.html)
    public static CoreWebView2BrowserExtension? Extension { get; private set; }
    // Ligado? (guardado aqui: a extensão só pode ser consultada na thread da janela, e a tela desenha em outra)
    public static bool On => _on;
    private static volatile bool _on;

    // Thread da janela.
    public static async Task SetEnabled(bool on)
    {
        if (Extension is null) return;
        await Extension.EnableAsync(on);
        _on = Extension.IsEnabled;
    }

    // Instala (ou atualiza) e liga. Erros só vão para o log: o navegador funciona sem ele.
    public static async Task Install(CoreWebView2Profile profile)
    {
        try
        {
            string? version = await Ensure();
            if (version is null) return;
            var installed = await profile.GetBrowserExtensionsAsync();
            var current = installed.FirstOrDefault(e => e.Name.Contains("uBlock", StringComparison.OrdinalIgnoreCase));
            string marker = Path.Combine(Root, "ubol.installed");
            bool same = current is not null && File.Exists(marker) && File.ReadAllText(marker) == version;
            if (current is not null && !same)
            {
                await current.RemoveAsync();
                current = null;
            }
            if (current is null)
            {
                current = await profile.AddBrowserExtensionAsync(Folder);
                File.WriteAllText(marker, version);
                Log.Write($"Navegador: bloqueador de anúncios instalado (uBlock Origin Lite {version}, {current.Id}).");
            }
            if (!current.IsEnabled) await current.EnableAsync(true);
            Id = current.Id;
            Extension = current;
            _on = current.IsEnabled;
        }
        catch (Exception error)
        {
            Log.Write($"Navegador: bloqueador de anúncios indisponível — {error.Message}");
            if (Environment.GetEnvironmentVariable("MKV_LOG") is not null) Log.Write(error.ToString()); // autoteste: detalhe
        }
    }

    // Sites sem bloqueio: "allow" ficam livres, "restore" voltam ao modo padrão. Duas partes, pela página do painel do
    // uBlock aberta escondida (só uma página da extensão pode pedir isso a ela): o modo "sem filtragem" do próprio
    // uBlock (tira os scripts e o visual dele do site) e uma regra de sessão nossa que libera os pedidos de rede — a
    // regra do modo dele é "dinâmica", e o motor embutido (WebView2) não aceita regras dinâmicas ("erro interno").
    // Devolve o modo de cada site depois (0 = sem bloqueio), ou null se não deu.
    public static async Task<Dictionary<string, int>?> SetSiteModes(CoreWebView2Environment env, IntPtr parent, IReadOnlyList<string> allow, IReadOnlyList<string> restore)
    {
        if (Id is null || (allow.Count == 0 && restore.Count == 0)) return null;
        CoreWebView2Controller? controller = null;
        try
        {
            controller = await env.CreateCoreWebView2ControllerAsync(parent);
            controller.IsVisible = false;
            var web = controller.CoreWebView2;
            var loaded = new TaskCompletionSource<bool>();
            web.NavigationCompleted += (_, e) => loaded.TrySetResult(e.IsSuccess);
            web.Navigate($"chrome-extension://{Id}/dashboard.html");
            if (await Task.WhenAny(loaded.Task, Task.Delay(10000)) != loaded.Task || !loaded.Task.Result) throw new TimeoutException("o painel do bloqueador não abriu");
            string script = $$"""
                (async () => {
                    const send = m => chrome.runtime.sendMessage(m);
                    const out = {};
                    for (const h of {{JsonSerializer.Serialize(allow)}}) out[h] = await send({ what: 'setFilteringMode', hostname: h, level: 0 });
                    const standard = await send({ what: 'getDefaultFilteringMode' });
                    for (const h of {{JsonSerializer.Serialize(restore)}}) out[h] = await send({ what: 'setFilteringMode', hostname: h, level: standard });
                    // Regras próprias de liberação (o modo "sem filtragem" do uBlock não chega a criar a dele no motor
                    // embutido): "permitir tudo" na página do site e "permitir" o que ela pede. Números entre os do
                    // uBlock (ele mantém os de 5.000.000 para cima), prioridade acima de todas as dele.
                    const sites = {{JsonSerializer.Serialize(allow)}};
                    const rules = sites.length === 0 ? [] : [
                        { id: 7900000, priority: 3000000, action: { type: 'allowAllRequests' }, condition: { requestDomains: sites, resourceTypes: ['main_frame', 'sub_frame'] } },
                        { id: 7900001, priority: 3000000, action: { type: 'allow' }, condition: { initiatorDomains: sites } },
                    ];
                    try {
                        await chrome.declarativeNetRequest.updateSessionRules({ removeRuleIds: [7900000, 7900001], addRules: rules });
                    } catch (e) { out.rulesError = String(e); }
                    window.__mkvModes = JSON.stringify(out);
                })().catch(e => window.__mkvModes = JSON.stringify({ error: String(e) }));
                """;
            await web.ExecuteScriptAsync(script);
            for (int i = 0; i < 50; i++)
            {
                await Task.Delay(100);
                string result = await web.ExecuteScriptAsync("window.__mkvModes || ''");
                string? json = JsonSerializer.Deserialize<string>(result);
                if (string.IsNullOrEmpty(json)) continue;
                if (json.Contains("\"error\"")) throw new InvalidOperationException(json);
                var modes = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!;
                if (modes.TryGetValue("rulesError", out var rulesError)) throw new InvalidOperationException("regras: " + rulesError.GetString());
                return modes.ToDictionary(m => m.Key, m => m.Value.GetInt32());
            }
            throw new TimeoutException("o bloqueador não respondeu");
        }
        catch (Exception error)
        {
            Log.Write($"Navegador: não consegui liberar sites no bloqueador — {error.Message}");
            return null;
        }
        finally { controller?.Close(); }
    }

    // Garante a extensão na pasta; devolve a versão (null = não tem e não deu para baixar).
    private static async Task<string?> Ensure()
    {
        string? have = File.Exists(Stamp) && File.Exists(Path.Combine(Folder, "manifest.json")) ? File.ReadAllText(Stamp).Trim() : null;
        if (have is not null && DateTime.UtcNow - File.GetLastWriteTimeUtc(Stamp) < TimeSpan.FromDays(7)) return have;
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("MKVisionDesktop");
            using var release = JsonDocument.Parse(await http.GetStringAsync(Releases));
            string tag = release.RootElement.GetProperty("tag_name").GetString()!;
            if (tag == have)
            {
                File.SetLastWriteTimeUtc(Stamp, DateTime.UtcNow); // confere de novo daqui a 7 dias
                return have;
            }
            string url = release.RootElement.GetProperty("assets").EnumerateArray()
                .Select(a => a.GetProperty("browser_download_url").GetString()!)
                .First(u => u.EndsWith(".edge.zip", StringComparison.OrdinalIgnoreCase));
            if (!url.StartsWith("https://github.com/uBlockOrigin/uBOL-home/releases/download/", StringComparison.Ordinal))
                throw new InvalidOperationException("endereço de download inesperado");
            Directory.CreateDirectory(Root);
            string zip = Path.Combine(Root, "ubol.zip"), fresh = Folder + ".new";
            await using (var source = await http.GetStreamAsync(url))
            await using (var file = File.Create(zip))
                await source.CopyToAsync(file);
            if (Directory.Exists(fresh)) Directory.Delete(fresh, true);
            ZipFile.ExtractToDirectory(zip, fresh);
            File.Delete(zip);
            // O manifest às vezes fica numa subpasta do zip.
            string manifestDir = Directory.GetFiles(fresh, "manifest.json", SearchOption.AllDirectories).OrderBy(p => p.Length).First();
            string content = Path.GetDirectoryName(manifestDir)!;
            if (Directory.Exists(Folder)) Directory.Delete(Folder, true);
            Directory.Move(content, Folder);
            if (Directory.Exists(fresh)) Directory.Delete(fresh, true);
            File.WriteAllText(Stamp, tag);
            Log.Write($"Navegador: uBlock Origin Lite {tag} baixado.");
            return tag;
        }
        catch (Exception error)
        {
            Log.Write($"Navegador: não consegui baixar o bloqueador de anúncios ({error.Message}).");
            return have;
        }
    }
}
