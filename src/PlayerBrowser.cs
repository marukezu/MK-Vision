using System.Runtime.InteropServices;

namespace MKVision.Desktop;

// Árvore de pastas na lista da borda direita, como o painel de navegação do Explorer: Acesso rápido (as pastas
// fixadas no Windows), Este Computador e os discos; ▸ abre/fecha, clicar no nome mostra os vídeos da pasta ao
// lado. A pasta do vídeo aberto já aparece aberta e selecionada.
// A árvore muda na thread principal (com _lock); a tela só lê.
internal sealed partial class Player
{
    private sealed class TreeNode
    {
        public required string Name;
        public string? Path;              // null = "Este Computador" (só abre/fecha)
        public required string Icon;      // nome para o Shell (caminho ou ::{CLSID})
        public int Depth;
        public bool Expanded, Loaded, Separator;
        public bool HasChildren = true;   // até abrir não sabemos: mostra a seta
        public List<TreeNode> Children = [];
    }

    private const int TreeWidth = 260, TreeRow = 28;
    private const string ThisPc = "::{20D04FE0-3AEA-1069-A2D8-08002B30309D}";
    private const string IconChevronRight = "", IconChevronDown = "", IconFolder = "";

    private readonly List<TreeNode> _treeRoots = [];
    private TreeNode[] _treeRows = [];      // linhas visíveis (árvore achatada)
    private TreeNode? _thisPc;
    private int _treeScroll, _treeHover = -1;
    private bool _treeHoverChevron;
    private string? _browseFolder;          // pasta mostrada ao lado da árvore
    private string[] _browseFiles = [];     // subpastas primeiro (as _browseDirs primeiras), depois vídeos e fotos
    private int _browseDirs;
    private string? _browseSyncedTo;        // vídeo para o qual a árvore já foi posicionada
    private readonly List<string> _history = []; // pastas mostradas (← e →, como no Explorer)
    private int _historyIndex = -1;
    private IntPtr _fontChevron;

    // Para o autoteste: linhas da árvore (recuo = nível), pasta mostrada e quantos vídeos; ponto de uma linha.
    public (string[] rows, string? browse, int files) TreeState
    {
        get { lock (_lock) return (_treeRows.Select(r => new string(' ', r.Depth * 4) + (r.Separator ? "──" : r.Name)).ToArray(), _browseFolder, _browseFiles.Length - _browseDirs); }
    }

    // Autoteste: subpastas mostradas na lista (vêm antes dos vídeos) e o centro do item "index" na janela.
    public string[] BrowseDirsForTest { get { lock (_lock) return _browseFiles[.._browseDirs]; } }

    public (int x, int y) ListItemPoint(int index)
    {
        lock (_lock)
        {
            var rect = ListRect();
            var (columns, thumb) = ListGrid();
            int row = index / columns - Math.Max(0, _listScroll), column = thumb == 0 ? 0 : index % columns;
            int x = rect.Left + TreeW() + U(8) + column * U(CellWidth()) + U(CellWidth()) / 2;
            if (thumb == 0) x = rect.Left + TreeW() + U(40);
            return (x, rect.Top + U(ListHeader) + row * U(CellHeight()) + U(CellHeight()) / 2);
        }
    }

    public (int x, int y) TreeRowPoint(int row, bool chevron)
    {
        lock (_lock)
        {
            var rect = ListRect();
            int y = rect.Top + U(8) + (row - _treeScroll) * U(TreeRow) + U(TreeRow) / 2;
            int x = rect.Left + U(8) + _treeRows[row].Depth * U(16) + (chevron ? U(6) : U(60));
            return (x, y);
        }
    }

    // Autoteste: centro do botão ← (0), → (1) ou ↑ (2).
    public (int x, int y) NavPoint(int button)
    {
        lock (_lock)
        {
            var rect = ListRect();
            return (rect.Left + TreeW() + U(8) + button * U(NavButton) + U(NavButton) / 2, rect.Top + (U(ListHeader) - U(4)) / 2);
        }
    }

    // Thread principal, ao criar o player.
    private void BuildTree()
    {
        _thisPc = new TreeNode { Name = "Este Computador", Icon = ThisPc, Expanded = true, Loaded = true };
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (!drive.IsReady) continue;
                string letter = drive.Name.TrimEnd('\\');
                string label = drive.VolumeLabel is { Length: > 0 } l ? l : drive.DriveType == DriveType.Removable ? "Unidade USB" : "Disco Local";
                _thisPc.Children.Add(new TreeNode { Name = $"{label} ({letter})", Path = drive.Name, Icon = drive.Name, Depth = 1 });
            }
            catch { } // disco que sumiu ou não responde
        }
        lock (_lock)
        {
            _treeRoots.Clear();
            foreach (var (name, path) in KnownFolders()) _treeRoots.Add(new TreeNode { Name = name, Path = path, Icon = path });
            _treeRoots.Add(new TreeNode { Name = "", Icon = "", Separator = true });
            _treeRoots.Add(_thisPc);
            FlattenTree();
        }
        // As pastas fixadas do Acesso rápido vêm do Shell (pode demorar um pouco): numa thread STA.
        var thread = new Thread(() =>
        {
            var pinned = PinnedFolders();
            if (pinned.Count == 0) return;
            lock (_lock)
            {
                int separator = _treeRoots.FindIndex(n => n.Separator);
                _treeRoots.RemoveRange(0, separator);
                _treeRoots.InsertRange(0, pinned.Select(p => new TreeNode { Name = p.name, Path = p.path, Icon = p.path }));
                FlattenTree();
                _dirty = true;
            }
            _renderWake.Set();
        }) { IsBackground = true, Name = "Player: acesso rápido" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHGetKnownFolderPath(ref Guid id, uint flags, IntPtr token, out IntPtr path);

    private static string? KnownFolder(string guid)
    {
        var id = new Guid(guid);
        if (SHGetKnownFolderPath(ref id, 0, IntPtr.Zero, out var p) != 0) return null;
        try { return Marshal.PtrToStringUni(p); }
        finally { Marshal.FreeCoTaskMem(p); }
    }

    // Enquanto o Acesso rápido não chega (ou se o Windows não responder): as pastas padrão.
    private static List<(string name, string path)> KnownFolders()
    {
        var list = new List<(string, string)>();
        void Add(string name, string? path)
        {
            if (!string.IsNullOrEmpty(path) && Directory.Exists(path)) list.Add((name, path));
        }
        Add("Área de Trabalho", Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory));
        Add("Downloads", KnownFolder("374DE290-123F-4565-9164-39C4925E467B"));
        Add("Documentos", Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments));
        Add("Imagens", Environment.GetFolderPath(Environment.SpecialFolder.MyPictures));
        Add("Músicas", Environment.GetFolderPath(Environment.SpecialFolder.MyMusic));
        Add("Vídeos", Environment.GetFolderPath(Environment.SpecialFolder.MyVideos));
        return list;
    }

    // Pastas fixadas no Acesso rápido do Explorer, na mesma ordem (thread STA).
    private static List<(string name, string path)> PinnedFolders()
    {
        var list = new List<(string, string)>();
        try
        {
            var type = Type.GetTypeFromProgID("Shell.Application");
            if (type is null) return list;
            dynamic shell = Activator.CreateInstance(type)!;
            dynamic? folder = shell.NameSpace("shell:::{679f85cb-0220-4080-b29b-5540cc05aab6}");
            if (folder is null) return list;
            var all = new List<(string, string, bool?)>();
            foreach (dynamic item in folder.Items())
            {
                string path = item.Path;
                if (!(bool)item.IsFolder || !Directory.Exists(path)) continue;
                bool? pinned = null;
                try { if (item.ExtendedProperty("System.Home.IsPinned") is bool b) pinned = b; } catch { }
                all.Add(((string)item.Name, path, pinned));
            }
            bool knowsPinned = all.Any(a => a.Item3 is not null);
            foreach (var (name, path, pinned) in all)
                if (!knowsPinned || pinned == true) list.Add((name, path));
        }
        catch (Exception error)
        {
            Log.Write($"Player: não consegui ler o Acesso rápido ({error.Message}); usando as pastas padrão.");
        }
        return list;
    }

    // Com _lock: subpastas (sem as ocultas e de sistema), na ordem do Explorer.
    private static void LoadChildren(TreeNode node)
    {
        if (node.Loaded || node.Path is null) return;
        node.Loaded = true;
        try
        {
            var dirs = new DirectoryInfo(node.Path).EnumerateDirectories()
                .Where(d => (d.Attributes & (FileAttributes.Hidden | FileAttributes.System)) == 0)
                .Select(d => d.FullName).ToArray();
            Array.Sort(dirs, (a, b) => StrCmpLogicalW(System.IO.Path.GetFileName(a), System.IO.Path.GetFileName(b)));
            node.Children = dirs.Select(d => new TreeNode { Name = System.IO.Path.GetFileName(d), Path = d, Icon = d, Depth = node.Depth + 1 }).ToList();
        }
        catch { node.Children = []; } // sem permissão
        node.HasChildren = node.Children.Count > 0;
    }

    // Com _lock.
    private void FlattenTree()
    {
        var rows = new List<TreeNode>();
        void Add(TreeNode node)
        {
            rows.Add(node);
            if (node.Expanded) foreach (var child in node.Children) Add(child);
        }
        foreach (var root in _treeRoots) Add(root);
        _treeRows = [.. rows];
        _treeScroll = Math.Clamp(_treeScroll, 0, Math.Max(0, _treeRows.Length - TreeVisible()));
    }

    // Com _lock.
    private int TreeVisible() => Math.Max(1, (ListRect().Height - U(16)) / U(TreeRow));

    // Com _lock: mostra os vídeos de "folder" ao lado da árvore; "record" = entra no histórico (← e →).
    private void Browse(string folder, bool record = true)
    {
        if (record && !(_historyIndex >= 0 && string.Equals(_history[_historyIndex], folder, StringComparison.OrdinalIgnoreCase)))
        {
            _history.RemoveRange(_historyIndex + 1, _history.Count - _historyIndex - 1); // voltou e foi para outra: perde o "avançar"
            _history.Add(folder);
            _historyIndex = _history.Count - 1;
        }
        string[] files, dirs;
        try
        {
            files = Directory.GetFiles(folder).Where(IsMedia).ToArray();
            Array.Sort(files, (a, b) => StrCmpLogicalW(System.IO.Path.GetFileName(a), System.IO.Path.GetFileName(b)));
        }
        catch { files = []; }
        try
        {
            dirs = new DirectoryInfo(folder).EnumerateDirectories()
                .Where(d => (d.Attributes & (FileAttributes.Hidden | FileAttributes.System)) == 0)
                .Select(d => d.FullName).ToArray();
            Array.Sort(dirs, (a, b) => StrCmpLogicalW(System.IO.Path.GetFileName(a), System.IO.Path.GetFileName(b)));
        }
        catch { dirs = []; } // sem permissão
        _browseFolder = folder;
        RememberFolder(folder);
        _browseFiles = [.. dirs, .. files];
        _browseDirs = dirs.Length;
        _listScroll = -1; // centraliza o vídeo aberto, se estiver nesta pasta
        _listHover = -1;
    }

    // Nome da pasta para o cabeçalho ("Vídeos"; disco: "E:\").
    private static string FolderTitle(string? folder) =>
        folder is null ? "" : System.IO.Path.GetFileName(folder.TrimEnd('\\')) is { Length: > 0 } name ? name : folder;

    // Com _lock: o vídeo aberto, se houver.
    private string? CurrentFile() => _folderIndex >= 0 && _folderIndex < _folderFiles.Length ? _folderFiles[_folderIndex] : null;

    // Com _lock: posição do vídeo aberto na pasta mostrada (-1 = ele não está nela).
    private int BrowseCurrent()
    {
        string? current = CurrentFile();
        return current is null ? -1 : Array.FindIndex(_browseFiles, f => string.Equals(f, current, StringComparison.OrdinalIgnoreCase));
    }

    // A última pasta aberta (de onde abriu um arquivo ou por onde andou na lista) fica guardada: a lista e o "Abrir"
    // começam nela, também depois de fechar o app. Com _lock (de qualquer thread: grava na da janela).
    private void RememberFolder(string? folder)
    {
        if (string.IsNullOrEmpty(folder) || string.Equals(_prefs.LastBrowseFolder, folder, StringComparison.OrdinalIgnoreCase)) return;
        _prefs.LastBrowseFolder = folder;
        PostUi(() => _prefs.Save());
    }

    // Onde a lista e o "Abrir" começam: a última pasta aberta (se ainda existe), senão Vídeos.
    private string StartFolder() =>
        _prefs.LastBrowseFolder is { Length: > 0 } last && Directory.Exists(last) ? last : Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);

    // Com _lock, ao abrir a lista: a pasta do vídeo aberto fica aberta na árvore, selecionada e à vista.
    private void SyncTree()
    {
        string? current = CurrentFile();
        if (current == _browseSyncedTo && _browseFolder is not null) return;
        _browseSyncedTo = current;
        string? folder = current is not null ? System.IO.Path.GetDirectoryName(current) : null;
        folder ??= _browseFolder ?? StartFolder(); // nada aberto (ou da internet): a última pasta aberta
        ShowFolder(folder, record: true);
    }

    // Com _lock: mostra a pasta e a deixa aberta, selecionada e à vista na árvore.
    private void ShowFolder(string folder, bool record)
    {
        Browse(folder, record);
        TreeNode? node = _thisPc is null ? null : ExpandTo(folder);
        FlattenTree();
        if (node is not null)
        {
            int row = Array.IndexOf(_treeRows, node);
            if (row >= 0 && (row < _treeScroll || row >= _treeScroll + TreeVisible()))
                _treeScroll = Math.Clamp(row - TreeVisible() / 2, 0, Math.Max(0, _treeRows.Length - TreeVisible()));
        }
    }

    // Com _lock: abre os nós de "Este Computador" até a pasta (devolve o nó dela).
    private TreeNode? ExpandTo(string folder)
    {
        string full;
        try { full = System.IO.Path.GetFullPath(folder).TrimEnd('\\'); }
        catch { return null; }
        TreeNode? node = _thisPc!.Children.FirstOrDefault(d => full.StartsWith(d.Path!.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase));
        if (node is null) return null;
        _thisPc.Expanded = true;
        while (!string.Equals(node.Path!.TrimEnd('\\'), full, StringComparison.OrdinalIgnoreCase))
        {
            LoadChildren(node);
            node.Expanded = true;
            var next = node.Children.FirstOrDefault(c =>
                full.StartsWith(c.Path! + "\\", StringComparison.OrdinalIgnoreCase) || string.Equals(c.Path, full, StringComparison.OrdinalIgnoreCase));
            if (next is null) break; // pasta oculta no caminho: para no último nível visível
            node = next;
        }
        return node;
    }

    // As pastas do Acesso rápido não abrem na árvore (como no Explorer); "Este Computador", os discos e as
    // pastas deles abrem.
    private static bool Expandable(TreeNode node) => node.Depth > 0 || node.Path is null;

    // Thread principal: clique numa linha da árvore.
    private void TreeClick(bool doubleClick)
    {
        lock (_lock)
        {
            if (_treeHover < 0 || _treeHover >= _treeRows.Length) return;
            var node = _treeRows[_treeHover];
            if (node.Separator) return;
            if (Expandable(node) && (_treeHoverChevron || node.Path is null || doubleClick))
            {
                LoadChildren(node);
                node.Expanded = !node.Expanded && node.HasChildren;
                FlattenTree();
            }
            if (node.Path is not null && !_treeHoverChevron) Browse(node.Path);
            _dirty = true;
        }
        _renderWake.Set();
    }

    // Botões do cabeçalho dos vídeos: voltar, avançar, pasta de cima (com _lock: disponíveis?).
    private bool CanBack() => _historyIndex > 0;
    private bool CanForward() => _historyIndex >= 0 && _historyIndex < _history.Count - 1;
    private string? ParentFolder() => _browseFolder is null ? null : System.IO.Path.GetDirectoryName(_browseFolder.TrimEnd('\\') is { Length: 2 } d && d[1] == ':' ? d + "\\" : _browseFolder);

    // Qualquer thread que não segura _lock: -1 = voltar, +1 = avançar, 0 = pasta de cima.
    private void Navigate(int direction)
    {
        lock (_lock)
        {
            if (direction < 0 && CanBack()) ShowFolder(_history[--_historyIndex], record: false);
            else if (direction > 0 && CanForward()) ShowFolder(_history[++_historyIndex], record: false);
            else if (direction == 0 && ParentFolder() is { } parent) ShowFolder(parent, record: true);
            else return;
            _dirty = true;
        }
        _renderWake.Set();
    }

    // Desenha a árvore na coluna da esquerda do painel (thread da tela).
    private void DrawTree(UiCanvas c, int width, int height, TreeNode[] rows, int scroll, int hover, string? selected)
    {
        c.Rect(0, 0, width, height, 0x000000, 0.18f);
        int rowH = U(TreeRow), top = U(8), visible = Math.Max(1, (height - U(16)) / rowH);
        int textHeight = c.TextHeight(_fontText), chevronHeight = c.TextHeight(_fontChevron);
        for (int i = 0; i < visible && scroll + i < rows.Length; i++)
        {
            var node = rows[scroll + i];
            int y = top + i * rowH;
            if (node.Separator)
            {
                c.Rect(U(12), y + rowH / 2, width - U(24), 1, White, 0.15f);
                continue;
            }
            bool isSelected = node.Path is not null && string.Equals(node.Path.TrimEnd('\\'), selected?.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
            if (scroll + i == hover) c.Rect(U(4), y, width - U(8), rowH, White, 0.10f);
            if (isSelected) c.Rect(U(4), y, width - U(8), rowH, White, 0.16f);
            int x = U(8) + node.Depth * U(16);
            if (Expandable(node) && node.HasChildren)
                c.Text(node.Expanded ? IconChevronDown : IconChevronRight, _fontChevron, x + U(2), y + (rowH - chevronHeight) / 2, Muted);
            int iconX = x + U(18), iconSize = U(20);
            if (_thumbs!.GetIcon(node.Icon) is { } icon) c.Icon(icon.Bgra, icon.Width, icon.Height, iconX, y + (rowH - iconSize) / 2, iconSize, iconSize);
            else c.Text(IconFolder, _fontIcons, iconX, y + (rowH - c.TextHeight(_fontIcons)) / 2, 0xE8C15A);
            int textX = iconX + iconSize + U(8);
            c.Text(Ellipsis(c, node.Name, _fontText, width - textX - U(8)), _fontText, textX, y + (rowH - textHeight) / 2, White);
        }
        if (rows.Length > visible)
        {
            float trackHeight = height - U(16);
            float bar = Math.Max(U(20), trackHeight * visible / rows.Length);
            float y = top + (trackHeight - bar) * scroll / Math.Max(1, rows.Length - visible);
            c.Rect(width - U(5), y, U(3), bar, White, 0.3f);
        }
        c.Rect(width - 1, 0, 1, height, White, 0.12f);
    }
}
