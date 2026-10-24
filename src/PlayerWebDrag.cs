using System.Runtime.InteropServices;

namespace MKVision.Desktop;

// Arrastar para a barra de favoritos, como no Edge: o ícone do site no começo da barra de endereço (vira um
// favorito novo) ou um favorito da barra (muda de lugar). Soltar entre dois favoritos põe ali; em cima de uma
// pasta, põe dentro dela. Clique sem arrastar: no ícone, seleciona o endereço; no favorito, abre.
internal sealed partial class Player
{
    private string? _dragUrl, _dragTitle;      // arrastando o site da aba (do ícone do endereço)
    private BookmarkNode? _pressNode;           // favorito pressionado (vira arrasto se o mouse andar)
    private bool _dragActive;
    private int _dragStartX, _dragStartY, _linkDragX, _linkDragY;
    private int _dropIndex = -1;                // posição na barra onde vai entrar (-1 = lugar nenhum)
    private BookmarkNode? _dropFolder;          // ou: pasta onde vai entrar
    private Native.RECT _siteIconRect;

    [DllImport("user32.dll")] private static extern IntPtr SetCursor(IntPtr cursor);
    private const int IDC_HAND = 32649, IDC_NO = 32648;

    // Para o autoteste: centro do ícone do site e de um favorito da barra.
    public (int x, int y) TestSiteIconPoint { get { lock (_lock) return ((_siteIconRect.Left + _siteIconRect.Right) / 2, (_siteIconRect.Top + _siteIconRect.Bottom) / 2); } }
    public (int x, int y) TestBookmarkPoint(int i) { lock (_lock) { var r = _bookmarkRects[i].rect; return ((r.Left + r.Right) / 2, (r.Top + r.Bottom) / 2); } }

    // Com _lock.
    private bool Dragging => _dragUrl is not null || _pressNode is not null || _pressTab is not null;

    private void BeginLinkDrag(int x, int y)
    {
        if (_web is null) return;
        _dragUrl = _web.Source;
        _dragTitle = _web.DocumentTitle is { Length: > 0 } t ? t : HostOf(_dragUrl);
        StartPress(x, y);
    }

    private void BeginBookmarkPress(int x, int y)
    {
        lock (_lock)
        {
            if (_webHoverIndex < 0 || _webHoverIndex >= _bookmarkRects.Count) return;
            _pressNode = _bookmarkRects[_webHoverIndex].node;
        }
        StartPress(x, y);
    }

    private void StartPress(int x, int y)
    {
        (_dragStartX, _dragStartY, _linkDragX, _linkDragY, _dragActive) = (x, y, x, y, false);
        PlayerNative.SetCapture(Hwnd);
    }

    // true = o mouse está arrastando (a interface não reage a ele enquanto isso).
    private bool WebDragMove(int x, int y)
    {
        lock (_lock)
        {
            if (!Dragging) return false;
            (_linkDragX, _linkDragY) = (x, y);
            if (!_dragActive && (Math.Abs(x - _dragStartX) > 4 || Math.Abs(y - _dragStartY) > 4)) _dragActive = true;
            if (!_dragActive) return true;
            if (_pressTab is not null)
            {
                MoveDraggedTab(x); // aba: muda de lugar na faixa (PlayerWebTabs)
                return true;
            }
            (_dropIndex, _dropFolder) = DropTarget(x, y);
            _dirty = true;
        }
        SetCursor(PlayerNative.LoadCursor(IntPtr.Zero, (IntPtr)(_dropIndex >= 0 || _dropFolder is not null ? IDC_HAND : IDC_NO)));
        _renderWake.Set();
        return true;
    }

    // Com _lock: onde o favorito entraria se soltasse aqui.
    private (int index, BookmarkNode? folder) DropTarget(int x, int y)
    {
        int top = U(TabStrip) + U(WebBar);
        if (!_prefs.WebBookmarksBar || y < top - U(10) || y > top + U(BookmarkBar) + U(10)) return (-1, null);
        for (int i = 0; i < _bookmarkRects.Count; i++)
        {
            var (r, node) = _bookmarkRects[i];
            if (x < r.Left) return (i, null);
            if (x >= r.Right) continue;
            int quarter = r.Width / 4;
            if (node.IsFolder && node != _pressNode && x >= r.Left + quarter && x < r.Right - quarter) return (-1, node);
            return (x < r.Left + r.Width / 2 ? i : i + 1, null);
        }
        return (_bookmarkRects.Count, null);
    }

    // Soltou o botão: solta o arrasto (ou trata como clique). true = era arrasto/clique dele.
    private bool WebDragEnd()
    {
        string? url, title;
        BookmarkNode? pressed, folder;
        bool active;
        int index;
        lock (_lock)
        {
            if (!Dragging) return false;
            if (_pressTab is not null)
            {
                active = _dragActive;
                (_pressTab, _dragActive) = (null, false);
                _dirty = true;
                PlayerNative.ReleaseCapture();
                if (active) SaveSession(); // a ordem nova volta da próxima vez
                _renderWake.Set();
                return true;
            }
            (url, title, pressed, active, index, folder) = (_dragUrl, _dragTitle, _pressNode, _dragActive, _dropIndex, _dropFolder);
            (_dragUrl, _dragTitle, _pressNode, _dragActive, _dropIndex, _dropFolder) = (null, null, null, false, -1, null);
            _dirty = true;
        }
        PlayerNative.ReleaseCapture();
        if (!active)
        {
            if (pressed is not null)
            {
                if (pressed.IsFolder) FolderMenu(pressed, _linkDragX, _linkDragY);
                else OpenBookmark(pressed.Url!, newTab: PlayerNative.KeyDown(PlayerNative.VK_CONTROL));
            }
            else SiteInfoMenu(); // clique no cadeado: informações do site
        }
        else if (index >= 0 || folder is not null)
        {
            _bookmarks ??= Bookmarks.Load();
            var root = _bookmarks;
            BookmarkNode node;
            if (pressed is not null)
            {
                if (folder == pressed) return true;
                // Tirar do lugar antigo (se estava antes na mesma barra, a posição nova anda uma casa).
                int old = root.Children!.IndexOf(pressed);
                if (old >= 0 && folder is null && old < index) index--;
                Bookmarks.Remove(root, pressed);
                node = pressed;
            }
            else
            {
                if (url is null || !IsWeb(url)) return true;
                node = BookmarkNode.Link(title ?? HostOf(url), url);
            }
            if (folder is not null) folder.Children!.Add(node);
            else root.Children!.Insert(Math.Clamp(index, 0, root.Children!.Count), node);
            Bookmarks.Save(root);
            if (pressed is null) WebToast(folder is null ? "Adicionado aos favoritos" : $"Adicionado em {folder.Name}");
        }
        WebDirty();
        return true;
    }
}
