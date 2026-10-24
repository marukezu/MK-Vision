using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Web.WebView2.Core;

namespace MKVision.Desktop;

// Stories do Instagram. O yt-dlp abre a página do story com o login, mas a lista dos itens vem de uma API do
// Instagram (feed/reels_media) que não responde a ele — e aí ele diz "You need to log in", mesmo logado. O app pede
// a mesma API de dentro da própria página aberta (com o seu login e a identificação do navegador, como o site faz) e
// toca o arquivo direto (vídeo com som). Todos os stories da pessoa (ou do destaque) vêm juntos: Page Up/Down passa
// por eles. O número da conta vem da própria página do story (o endereço de perfil da API é muito limitado: 429,
// "pedidos demais"; fica de reserva). Se não der, o vídeo da tela direto (se for um arquivo comum) e, por último,
// o yt-dlp.
internal sealed partial class Player
{
    private bool _instagramLimited; // a última tentativa ouviu 429 do Instagram ("pedidos demais")
    private static bool IsStory(string url) => StoryUrl.IsMatch(url);
    private static readonly Regex StoryUrl = new(@"^https://(www\.)?instagram\.com/stories/(?<user>[^/?#]+)(/(?<id>\d+))?", RegexOptions.IgnoreCase);
    private const string InstagramAppId = "936619743392459"; // o do site (o mesmo que o yt-dlp usa)

    // Story da página (ou null: não é story, ou a API não respondeu).
    private async Task<WebMedia?> InstagramStory(CoreWebView2 web, string page, string agent, MediaHint hint)
    {
        var match = StoryUrl.Match(page);
        if (!match.Success) return null;
        string user = match.Groups["user"].Value, id = match.Groups["id"].Value;
        if (user == "highlights" && id.Length == 0) return null;
        try
        {
            var items = await StoryItems(web, user, id);
            var media = StoryMedia(items, id, hint, page, agent, user == "highlights" ? "Destaque" : $"Story de @{user}");
            if (media is null) Log.Write("Navegador: o story não tem vídeo (só fotos).");
            else Log.Write($"Navegador: story pelo próprio Instagram ({items.Count} item(ns)).");
            return media;
        }
        catch (Exception error)
        {
            _instagramLimited = error.Message.Contains(" 429");
            Log.Write($"Navegador: não consegui o story pelo Instagram ({error.Message}); tentando de outro jeito.");
            return null;
        }
    }

    internal sealed record StoryItem(string Pk, double Duration, int Width, int Height, string? Video, string? Image);

    // Pede à API do Instagram, de dentro da página (mesmo login e identificação do site), os itens do story: os da
    // pessoa ou os do destaque. Cada item: o vídeo (o maior arquivo) e/ou a foto (a maior).
    private static async Task<List<StoryItem>> StoryItems(CoreWebView2 web, string user, string id)
    {
        string script = $$"""
            (async () => {
                const headers = { 'X-IG-App-ID': '{{InstagramAppId}}', 'X-ASBD-ID': '359341', 'X-Requested-With': 'XMLHttpRequest',
                                  'X-IG-WWW-Claim': sessionStorage.getItem('www-claim-v2') || '0' };
                const get = async path => {
                    const r = await fetch(path, { headers, credentials: 'include' });
                    if (!r.ok) throw new Error(path.split('?')[0] + ' respondeu ' + r.status);
                    return r.json();
                };
                const user = {{JsonSerializer.Serialize(user)}}, id = {{JsonSerializer.Serialize(id)}};
                // Número da conta: na página do story (como o yt-dlp acha); o endereço de perfil só de reserva.
                const owner = async () => {
                    try {
                        const r = await fetch(location.pathname, { credentials: 'include' });
                        const html = r.ok ? await r.text() : '';
                        const m = html.match(/"user":\{"pk":"(\d+)"/) || html.match(/"user":\{[^{}]*?"(?:pk|id)":"(\d+)"/) ||
                                  html.match(/"owner":\{[^{}]*?"id":"(\d+)"/) || html.match(/"profile_id":"?(\d+)/);
                        if (m) return m[1];
                    } catch { }
                    return String((await get('/api/v1/users/web_profile_info/?username=' + encodeURIComponent(user))).data.user.id);
                };
                const reel = user === 'highlights' ? 'highlight:' + id : await owner();
                const data = await get('/api/v1/feed/reels_media/?reel_ids=' + encodeURIComponent(reel));
                const items = (data.reels && data.reels[reel] && data.reels[reel].items) || (data.reels_media && data.reels_media[0] && data.reels_media[0].items) || [];
                const biggest = list => (list || []).slice().sort((a, b) => (b.width * b.height) - (a.width * a.height))[0];
                return items.map(i => ({
                    pk: String(i.pk || String(i.id || '').split('_')[0]),
                    duration: i.video_duration || 0,
                    width: i.original_width || 0, height: i.original_height || 0,
                    video: (biggest(i.video_versions) || {}).url || null,
                    image: (biggest(i.image_versions2 && i.image_versions2.candidates) || {}).url || null
                }));
            })()
            """;
        string reply = await web.CallDevToolsProtocolMethodAsync("Runtime.evaluate",
            JsonSerializer.Serialize(new { expression = script, awaitPromise = true, returnByValue = true }));
        using var doc = JsonDocument.Parse(reply);
        var root = doc.RootElement;
        if (root.TryGetProperty("exceptionDetails", out var failure))
            throw new InvalidOperationException(failure.TryGetProperty("exception", out var e) && e.TryGetProperty("description", out var d) ? d.GetString()?.Split('\n')[0] : "erro na página");
        var list = new List<StoryItem>();
        foreach (var item in root.GetProperty("result").GetProperty("value").EnumerateArray())
        {
            string? Text(string name) => item.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            double Number(string name) => item.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0;
            list.Add(new StoryItem(Text("pk") ?? "", Number("duration"), (int)Number("width"), (int)Number("height"), Text("video"), Text("image")));
        }
        return list;
    }

    // O story da tela (o do endereço; num destaque, o que bate com a duração do vídeo da tela) e todos os da pessoa
    // na "pasta". null = nenhum vídeo.
    internal static WebMedia? StoryMedia(IReadOnlyList<StoryItem> items, string id, MediaHint hint, string page, string agent, string title)
    {
        var headers = new Dictionary<string, string> { ["User-Agent"] = agent, ["Referer"] = "https://www.instagram.com/" };
        var post = new List<WebPostItem>();
        for (int i = 0; i < items.Count; i++)
        {
            var item = items[i];
            if (item.Video is { } video)
                post.Add(new WebPostItem(i + 1, new WebMedia(title, page, video, headers, null, null, false, null, item.Duration, 0, ".mp4", Height: item.Height, Item: i + 1), null));
            else if (item.Image is { } image) post.Add(new WebPostItem(i + 1, null, image));
        }
        var videos = post.Where(p => p.Video is not null).ToList();
        if (videos.Count == 0) return null;
        int at = id.Length > 0 ? items.ToList().FindIndex(i => i.Pk == id) : -1;
        var chosen = at >= 0 && items[at].Video is not null ? post.First(p => p.Index == at + 1)
            : videos.OrderBy(p => hint.Duration > 0 ? Math.Abs(p.Video!.Duration - hint.Duration) : 0).ThenBy(p => p.Index).First();
        return chosen.Video! with { Post = post.Count > 1 ? post : null };
    }

    public Task<List<StoryItem>> TestStoryItems(string user, string id) => StoryItems(_web!, user, id);
}
