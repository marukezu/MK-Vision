using System.Text.Json;
using System.Text.Json.Serialization;

namespace MKVision.Desktop;

// Favoritos do navegador do app: a barra de favoritos é a pasta raiz; pastas podem ter pastas. Guardados em
// %LOCALAPPDATA%\MKVisionDesktop\bookmarks.json. Dá para trazer os do Edge (arquivo "Bookmarks" do perfil dele).
internal sealed class BookmarkNode
{
    public string Name { get; set; } = "";
    public string? Url { get; set; }                       // null = pasta
    public List<BookmarkNode>? Children { get; set; }      // só pastas

    [JsonIgnore] public bool IsFolder => Url is null;

    public static BookmarkNode Folder(string name) => new() { Name = name, Children = [] };
    public static BookmarkNode Link(string name, string url) => new() { Name = name, Url = url };
}

internal static class Bookmarks
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MKVisionDesktop", "bookmarks.json");

    public static BookmarkNode Load()
    {
        try
        {
            if (File.Exists(FilePath) && JsonSerializer.Deserialize<BookmarkNode>(File.ReadAllText(FilePath)) is { Children: not null } root)
                return root;
        }
        catch (Exception error) { Log.Write($"Navegador: não consegui ler os favoritos ({error.Message})."); }
        return BookmarkNode.Folder("Barra de favoritos");
    }

    // Avisado depois de guardar (os ícones dos sites favoritos vão para o disco; os outros saem).
    public static event Action<BookmarkNode>? Saved;

    public static void Save(BookmarkNode root)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(root, new JsonSerializerOptions
            {
                WriteIndented = true,
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
            }));
        }
        catch (Exception error) { Log.Write($"Navegador: não consegui guardar os favoritos ({error.Message})."); }
        Saved?.Invoke(root);
    }

    // Procura o favorito com este endereço (e a pasta onde ele está).
    public static (BookmarkNode node, BookmarkNode parent)? Find(BookmarkNode folder, string url)
    {
        foreach (var child in folder.Children!)
        {
            if (child.Url is not null && SameUrl(child.Url, url)) return (child, folder);
            if (child.IsFolder && Find(child, url) is { } found) return found;
        }
        return null;
    }

    public static bool SameUrl(string a, string b) => string.Equals(a.TrimEnd('/'), b.TrimEnd('/'), StringComparison.OrdinalIgnoreCase);

    // Todas as pastas (com o caminho para mostrar: "Vídeos › Japão").
    public static List<(BookmarkNode folder, string path)> Folders(BookmarkNode root)
    {
        var list = new List<(BookmarkNode, string)>();
        void Walk(BookmarkNode folder, string path)
        {
            foreach (var child in folder.Children!)
                if (child.IsFolder)
                {
                    string p = path.Length == 0 ? child.Name : path + " › " + child.Name;
                    list.Add((child, p));
                    Walk(child, p);
                }
        }
        Walk(root, "");
        return list;
    }

    public static bool Remove(BookmarkNode folder, BookmarkNode node)
    {
        if (folder.Children!.Remove(node)) return true;
        foreach (var child in folder.Children)
            if (child.IsFolder && Remove(child, node)) return true;
        return false;
    }

    // Favoritos do Edge (perfil padrão): a barra vai para a raiz; "Outros favoritos" vira uma pasta. O que já
    // existe (mesmo endereço) não é duplicado. Devolve quantos foram trazidos.
    public static int ImportEdge(BookmarkNode root)
    {
        string file = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Microsoft", "Edge", "User Data", "Default", "Bookmarks");
        if (!File.Exists(file)) throw new FileNotFoundException("não achei os favoritos do Edge neste computador");
        using var doc = JsonDocument.Parse(File.ReadAllText(file));
        var roots = doc.RootElement.GetProperty("roots");
        int count = 0;
        void Copy(JsonElement source, BookmarkNode target)
        {
            if (!source.TryGetProperty("children", out var children)) return;
            foreach (var item in children.EnumerateArray())
            {
                string name = item.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                if (item.TryGetProperty("type", out var t) && t.GetString() == "folder")
                {
                    var folder = target.Children!.FirstOrDefault(c => c.IsFolder && c.Name == name);
                    if (folder is null) target.Children!.Add(folder = BookmarkNode.Folder(name));
                    Copy(item, folder);
                }
                else if (item.TryGetProperty("url", out var u) && u.GetString() is { } url && Find(root, url) is null)
                {
                    target.Children!.Add(BookmarkNode.Link(name, url));
                    count++;
                }
            }
        }
        if (roots.TryGetProperty("bookmark_bar", out var bar)) Copy(bar, root);
        if (roots.TryGetProperty("other", out var other) && other.TryGetProperty("children", out var oc) && oc.GetArrayLength() > 0)
        {
            var folder = root.Children!.FirstOrDefault(c => c.IsFolder && c.Name == "Outros favoritos");
            if (folder is null) root.Children!.Add(folder = BookmarkNode.Folder("Outros favoritos"));
            Copy(other, folder);
        }
        return count;
    }
}
