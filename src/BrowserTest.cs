namespace MKVision.Desktop;

// --browsertest <vídeo> <pasta>: abre a lista da borda direita (com a árvore de pastas), clica em "Este
// Computador" e numa pasta, e salva fotos da tela em PNG.
internal static class BrowserTest
{
    // --idleclicktest <pasta de saída>: sem nada aberto, clicar na tela abre a lista de pastas, que fica aberta com o
    // mouse no meio; entrar nela e sair fecha; outro clique fora também. Com um vídeo aberto, o clique só pausa.
    public static int IdleClick(string output)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        Directory.CreateDirectory(output);
        using var gpu = new Gpu();
        var player = new Player(gpu, new object()) { IgnoreMouseLeave = true };
        void Pump(double seconds)
        {
            var until = DateTime.UtcNow.AddSeconds(seconds);
            while (DateTime.UtcNow < until)
            {
                while (Native.PeekMessage(out var msg, IntPtr.Zero, 0, 0, 1)) { Native.TranslateMessage(ref msg); Native.DispatchMessage(ref msg); }
                Thread.Sleep(5);
            }
        }
        void Mouse(uint message, int x, int y) => PlayerNative.PostMessage(player.Hwnd, message, IntPtr.Zero, (IntPtr)((y << 16) | (x & 0xffff)));
        void Click(int x, int y) { Mouse(PlayerNative.WM_MOUSEMOVE, x, y); Mouse(PlayerNative.WM_LBUTTONDOWN, x, y); Mouse(PlayerNative.WM_LBUTTONUP, x, y); Pump(0.5); }
        void Save(string name)
        {
            if (player.Snapshot() is { } s) InterpTest.Png(Path.Combine(output, name), s.pixels, s.width, 0, 0, s.width, s.height);
        }
        int fails = 0;
        void Check(string what, bool ok, string detail = "") { Console.WriteLine($"{(ok ? "OK    " : "FALHOU")} {what} {detail}"); if (!ok) fails++; }
        Pump(1.5);
        Native.GetClientRect(player.Hwnd, out var client);
        int w = client.Width, h = client.Height;
        Mouse(PlayerNative.WM_MOUSEMOVE, w / 3, h / 2);
        Pump(0.5);
        Save("vazia.png");
        Check("tela vazia: lista fechada", !player.TestListOpen);
        Click(w / 3, h / 2);
        Check("clique abre a lista", player.TestListOpen, $"pasta: {player.TreeState.browse}");
        for (int i = 0; i < 5; i++) Mouse(PlayerNative.WM_MOUSEMOVE, w / 3 + i * 10, h / 2 + i * 5);
        Pump(0.5);
        Check("com o mouse no meio, continua aberta", player.TestListOpen);
        Save("lista.png");
        Mouse(PlayerNative.WM_MOUSEMOVE, w - 60, h / 2);
        Pump(0.3);
        Mouse(PlayerNative.WM_MOUSEMOVE, w / 10, h / 2);
        Pump(0.5);
        Check("entrou e saiu: fecha", !player.TestListOpen);
        Click(w / 3, h / 2);
        Check("clique de novo abre", player.TestListOpen);
        Click(w / 10, h / 2);
        Check("clique fora dela fecha", !player.TestListOpen);
        string video = Path.Combine(output, "clip.mp4");
        Vortice.MediaFoundation.MediaFactory.MFStartup(true).CheckError();
        if (!File.Exists(video)) PlayerTest.WriteClip(video);
        player.Open(video);
        Pump(2);
        bool before = player.State.playing;
        Click(w / 3, h / 2);
        Check("com vídeo: clique pausa/toca, sem lista", !player.TestListOpen && player.State.playing != before, $"tocando {before} → {player.State.playing}");
        player.Dispose();
        Console.WriteLine(fails == 0 ? "tudo certo" : $"{fails} falha(s)");
        return fails == 0 ? 0 : 1;
    }

    // --listfolderstest <pasta de saída>: cria ali uma pasta com 2 subpastas e um vídeo, abre o vídeo e confere que a
    // lista mostra as subpastas antes dos vídeos e que clicar numa entra nela (fotos em PNG, ícones e só nomes).
    public static int Folders(string output)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        string root = Path.Combine(output, "pasta-teste");
        Directory.CreateDirectory(Path.Combine(root, "Sub A"));
        Directory.CreateDirectory(Path.Combine(root, "Sub B"));
        string video = Path.Combine(root, "clip.mp4");
        Vortice.MediaFoundation.MediaFactory.MFStartup(true).CheckError();
        if (!File.Exists(video)) PlayerTest.WriteClip(video);
        using var gpu = new Gpu();
        var player = new Player(gpu, new object()) { IgnoreMouseLeave = true };
        void Pump(double seconds)
        {
            var until = DateTime.UtcNow.AddSeconds(seconds);
            while (DateTime.UtcNow < until)
            {
                while (Native.PeekMessage(out var msg, IntPtr.Zero, 0, 0, 1))
                {
                    Native.TranslateMessage(ref msg);
                    Native.DispatchMessage(ref msg);
                }
                Thread.Sleep(5);
            }
        }
        void Mouse(uint message, int x, int y) => PlayerNative.PostMessage(player.Hwnd, message, IntPtr.Zero, (IntPtr)((y << 16) | (x & 0xffff)));
        void Save(string name)
        {
            if (player.Snapshot() is not { } s) { Console.WriteLine($"{name}: sem foto"); return; }
            InterpTest.Png(Path.Combine(output, name), s.pixels, s.width, 0, 0, s.width, s.height);
        }
        int fails = 0;
        void Check(string what, bool ok, string detail = "") { Console.WriteLine($"{(ok ? "ok  " : "FALHOU")} {what} {detail}"); if (!ok) fails++; }
        player.Open(video);
        Pump(1.5);
        PlayerNative.PostMessage(player.Hwnd, PlayerNative.WM_KEYDOWN, PlayerNative.VK_SPACE, IntPtr.Zero); // pausa
        Native.GetClientRect(player.Hwnd, out var client);
        int w = client.Width, h = client.Height;
        Mouse(PlayerNative.WM_MOUSEMOVE, w / 2, h / 2);
        Mouse(PlayerNative.WM_MOUSEMOVE, w - 40, h / 2);
        Pump(2.5); // ícones do Shell chegam
        var dirs = player.BrowseDirsForTest.Select(Path.GetFileName).ToArray();
        Check("subpastas na lista, antes do vídeo", dirs.SequenceEqual(["Sub A", "Sub B"]), string.Join(", ", dirs));
        Check("contagem de vídeos sem as pastas", player.TreeState.files == 1, $"{player.TreeState.files}");
        int mode = player.ListModeForTest;
        player.TestListMode(2);
        Pump(2.5); // ícones grandes das pastas chegam
        Save("pastas-icones.png");
        player.TestListMode(4);
        Pump(1);
        Save("pastas-nomes.png");
        player.TestListMode(mode);
        Pump(0.5);
        var (x, y) = player.ListItemPoint(1);
        Mouse(PlayerNative.WM_MOUSEMOVE, x, y);
        Mouse(PlayerNative.WM_LBUTTONDOWN, x, y);
        Mouse(PlayerNative.WM_LBUTTONUP, x, y);
        Pump(1);
        Check("clicar na pasta entra nela", string.Equals(player.TreeState.browse, Path.Combine(root, "Sub B"), StringComparison.OrdinalIgnoreCase), player.TreeState.browse ?? "");
        Check("o vídeo continua aberto", player.FolderState.name.Contains("clip"), player.FolderState.name);
        Save("pastas-entrou.png");
        player.Dispose();
        Console.WriteLine(fails == 0 ? "tudo certo" : $"{fails} falha(s)");
        return fails == 0 ? 0 : 1;
    }

    public static int Run(string video, string folder)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        Directory.CreateDirectory(folder);
        using var gpu = new Gpu();
        var player = new Player(gpu, new object());
        void Pump(double seconds)
        {
            var until = DateTime.UtcNow.AddSeconds(seconds);
            while (DateTime.UtcNow < until)
            {
                while (Native.PeekMessage(out var msg, IntPtr.Zero, 0, 0, 1))
                {
                    Native.TranslateMessage(ref msg);
                    Native.DispatchMessage(ref msg);
                }
                Thread.Sleep(5);
            }
        }
        void Mouse(uint message, int x, int y) => PlayerNative.PostMessage(player.Hwnd, message, IntPtr.Zero, (IntPtr)((y << 16) | (x & 0xffff)));
        void Save(string name)
        {
            if (player.Snapshot() is not { } s) { Console.WriteLine($"{name}: sem foto"); return; }
            InterpTest.Png(Path.Combine(folder, name), s.pixels, s.width, 0, 0, s.width, s.height);
        }
        player.IgnoreMouseLeave = true;
        player.Open(video);
        Pump(1.5);
        PlayerNative.PostMessage(player.Hwnd, PlayerNative.WM_KEYDOWN, PlayerNative.VK_SPACE, IntPtr.Zero); // pausa
        Native.GetClientRect(player.Hwnd, out var client);
        int w = client.Width, h = client.Height;
        Mouse(PlayerNative.WM_MOUSEMOVE, w - 4, h / 2);
        Pump(3); // ícones do Shell chegam
        Mouse(PlayerNative.WM_MOUSEMOVE, w - 200, h / 2);
        Pump(1);
        Save("1-lista.png");
        var tree = player.TreeState;
        Console.WriteLine($"árvore: {tree.rows.Length} linhas; pasta: {tree.browse}; vídeos: {tree.files}");
        foreach (var row in tree.rows) Console.WriteLine("  " + row);
        // Clica no primeiro disco que não é o do vídeo.
        int driveRow = Array.FindIndex(tree.rows, r => r.EndsWith(":)") && !(tree.browse ?? "").StartsWith(r[(r.LastIndexOf('(') + 1)..^1]));
        if (driveRow >= 0)
        {
            // Roda do mouse sobre a árvore até o topo.
            var (tx, ty) = player.TreeRowPoint(0, chevron: false);
            var screen = new Native.POINT { X = tx, Y = h / 2 };
            Native.ClientToScreen(player.Hwnd, ref screen);
            for (int i = 0; i < 20; i++)
                PlayerNative.PostMessage(player.Hwnd, PlayerNative.WM_MOUSEWHEEL, (IntPtr)(120 << 16), (IntPtr)((screen.Y << 16) | (screen.X & 0xffff)));
            Pump(0.5);
            var (x, y) = player.TreeRowPoint(driveRow, chevron: true);
            Mouse(PlayerNative.WM_MOUSEMOVE, x, y);
            Mouse(PlayerNative.WM_LBUTTONDOWN, x, y);
            Mouse(PlayerNative.WM_LBUTTONUP, x, y);
            Pump(1.5);
            Save("2-disco-aberto.png");
            tree = player.TreeState;
            Console.WriteLine($"depois de abrir o disco: {tree.rows.Length} linhas");
            var (nx, ny) = player.TreeRowPoint(driveRow + 1, chevron: false);
            Mouse(PlayerNative.WM_MOUSEMOVE, nx, ny);
            Mouse(PlayerNative.WM_LBUTTONDOWN, nx, ny);
            Mouse(PlayerNative.WM_LBUTTONUP, nx, ny);
            Pump(1.5);
            tree = player.TreeState;
            Console.WriteLine($"clicou na pasta: {tree.browse}, {tree.files} vídeos");
            Save("3-pasta.png");
            string? Click(int button)
            {
                var (bx, by) = player.NavPoint(button);
                Mouse(PlayerNative.WM_MOUSEMOVE, bx, by);
                Mouse(PlayerNative.WM_LBUTTONDOWN, bx, by);
                Mouse(PlayerNative.WM_LBUTTONUP, bx, by);
                Pump(0.6);
                return player.TreeState.browse;
            }
            Console.WriteLine($"← {Click(0)}");
            Console.WriteLine($"→ {Click(1)}");
            Console.WriteLine($"↑ {Click(2)}");
            Console.WriteLine($"↑ {Click(2)}");
            Console.WriteLine($"← {Click(0)}");
            Save("4-navegar.png");
        }
        player.Dispose();
        return 0;
    }
}
