using System.Runtime.InteropServices;
using Vortice.Direct3D11;
using Vortice.Mathematics;
using Vortice.MediaFoundation;

namespace MKVision.Desktop;

// "--playertest": grava um vídeo H.264 de teste (bloco branco andando 6 px por quadro, 30 quadros/s, 3 s) com o
// codificador do Windows e lê de volta pelo mesmo caminho do player (VideoFile): confere tamanho, taxa,
// duração, o tempo de cada quadro, a posição do bloco na imagem decodificada e o pulo para um ponto.
internal static class PlayerTest
{
    private const int W = 640, H = 360, Frames = 90, Block = 80, X0 = 40, Step = 6;

    public static int Run()
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        string path = Path.Combine(Path.GetTempPath(), "mkvision-player-test.mp4");
        MediaFactory.MFStartup(true).CheckError();
        Write(path);
        Console.WriteLine($"vídeo de teste gravado: {new FileInfo(path).Length / 1024} KB");

        using var gpu = new Gpu();
        using var file = new VideoFile(path, gpu.Device);
        int bad = 0;
        void Check(string name, bool ok, string detail)
        {
            Console.WriteLine($"{(ok ? "OK    " : "FALHOU")} {name}: {detail}");
            if (!ok) bad++;
        }
        Check("tamanho", file.Width == W && file.Height == H, $"{file.Width}x{file.Height}");
        Check("quadros por segundo", Math.Abs(1 / file.FrameDuration - 30) < 0.1, $"{1 / file.FrameDuration:0.##}");
        Check("duração", Math.Abs(file.Duration - Frames / 30.0) < 0.05, $"{file.Duration:0.000} s");

        var times = new List<double>();
        var positions = new Dictionary<int, int>();
        ID3D11Texture2D? staging = null;
        int index = 0;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (file.ReadFrame((source, sub) =>
        {
            if (index % 15 != 0) return;
            var d = source.Description;
            staging ??= gpu.Device.CreateTexture2D(new Texture2DDescription(d.Format, W, H, 1, 1, BindFlags.None, ResourceUsage.Staging, CpuAccessFlags.Read));
            gpu.Context.CopySubresourceRegion(staging, 0, 0, 0, 0, source, sub, new Box(0, 0, 0, W, H, 1));
            positions[index] = BlockLeft(gpu, staging);
        }, out double time))
        {
            times.Add(time);
            index++;
        }
        double decodeMs = clock.Elapsed.TotalMilliseconds;
        Check("quadros lidos", times.Count == Frames, $"{times.Count} em {decodeMs:0} ms ({times.Count / decodeMs * 1000:0} quadros/s)");
        bool even = times.Zip(times.Skip(1)).All(p => Math.Abs(p.Second - p.First - 1 / 30.0) < 0.002);
        Check("tempo de cada quadro", even, $"de {times.FirstOrDefault():0.000} a {times.LastOrDefault():0.000} s, sempre 1/30 s");
        foreach (var (k, left) in positions.OrderBy(p => p.Key))
            Check($"bloco no quadro {k}", Math.Abs(left - (X0 + Step * k)) <= 3, $"borda em {left} (esperado {X0 + Step * k})");

        file.Seek(2.0);
        double first = -1;
        for (int i = 0; i < Frames && file.ReadFrame((_, _) => { }, out double t); i++)
        {
            if (t >= 2.0 - 1 / 60.0) { first = t; break; } // o player descarta até o alvo, como aqui
        }
        Check("pular para 2,0 s", Math.Abs(first - 2.0) < 0.02, $"primeiro quadro em {first:0.000} s");

        staging?.Dispose();
        Console.WriteLine(bad == 0 ? "\nTudo certo." : $"\n{bad} falha(s).");
        return bad == 0 ? 0 : 2;
    }

    // "--playeruitest": abre a janela do player com o vídeo de teste e simula o uso (teclas e roda do mouse),
    // conferindo se ele desenha e responde.
    public static int RunWindow()
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        string path = Path.Combine(Path.GetTempPath(), "mkvision-player-test.mp4");
        MediaFactory.MFStartup(true).CheckError();
        Write(path);
        using var gpu = new Gpu();
        var player = new Player(gpu, new object());
        player.Open(path);
        int bad = 0;
        void Check(string name, bool ok, string detail)
        {
            Console.WriteLine($"{(ok ? "OK    " : "FALHOU")} {name}: {detail}");
            if (!ok) bad++;
        }
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
        void Key(int vk) => PlayerNative.PostMessage(player.Hwnd, PlayerNative.WM_KEYDOWN, vk, IntPtr.Zero);

        Pump(1.5);
        long drawn = player.FramesDrawn;
        var s = player.State;
        Check("tocando", drawn > 60 && s.playing && s.fileTime > 0.5, $"{drawn} quadros de tela em 1,5 s, vídeo em {s.fileTime:0.00} s");
        Key(PlayerNative.VK_SPACE);
        Pump(0.5);
        double paused = player.State.fileTime;
        Pump(0.5);
        Check("pausa", !player.State.playing && Math.Abs(player.State.fileTime - paused) < 0.001, $"parado em {paused:0.00} s");
        Key(PlayerNative.VK_SPACE);
        Key(PlayerNative.VK_DOWN); Key(PlayerNative.VK_DOWN); Key(PlayerNative.VK_DOWN);
        Pump(0.3);
        Check("velocidade", Math.Abs(player.State.speed - 0.7) < 0.01, $"{player.State.speed:0.0}x");
        Key(PlayerNative.VK_HOME);
        Key(0x23); // End
        Pump(0.3);
        Check("Home/End espelham", player.TestFlip == (true, true), $"{player.TestFlip}");
        Key(PlayerNative.VK_HOME);
        Key(0x23);
        Pump(0.3);
        Check("Home/End de novo: sem espelho", player.TestFlip == (false, false), $"{player.TestFlip}");
        player.TestSeek(0); // Ctrl+Home (o Ctrl não dá para simular sem mexer no teclado de verdade)
        Pump(0.5);
        Check("voltar ao início", player.State.fileTime < 0.5, $"vídeo em {player.State.fileTime:0.00} s");
        Native.GetClientRect(player.Hwnd, out var client);
        var center = new Native.POINT { X = client.Width / 2, Y = client.Height / 2 };
        Native.ClientToScreen(player.Hwnd, ref center);
        for (int i = 0; i < 4; i++)
            PlayerNative.PostMessage(player.Hwnd, PlayerNative.WM_MOUSEWHEEL, (IntPtr)(120 << 16), (IntPtr)((center.Y << 16) | (center.X & 0xffff)));
        Pump(0.3);
        Check("zoom", Math.Abs(player.State.zoom - Math.Pow(1.2, 4)) < 0.01, $"{player.State.zoom:0.00}x");
        Key('F');
        Pump(0.5);
        Native.GetClientRect(player.Hwnd, out var full);
        Check("tela cheia", player.State.fullscreen && full.Width >= client.Width, $"{full.Width}x{full.Height}");
        Key(PlayerNative.VK_ESCAPE);
        Pump(0.5);

        // Mouse: clique no vídeo, barra de baixo, botões, menu do topo.
        Native.GetClientRect(player.Hwnd, out var area);
        IntPtr At(int x, int y) => (IntPtr)((y << 16) | (x & 0xffff));
        void Move(int x, int y) { PlayerNative.PostMessage(player.Hwnd, PlayerNative.WM_MOUSEMOVE, IntPtr.Zero, At(x, y)); Pump(0.15); }
        void Click(int x, int y)
        {
            PlayerNative.PostMessage(player.Hwnd, PlayerNative.WM_MOUSEMOVE, IntPtr.Zero, At(x, y));
            PlayerNative.PostMessage(player.Hwnd, PlayerNative.WM_LBUTTONDOWN, 1, At(x, y));
            PlayerNative.PostMessage(player.Hwnd, PlayerNative.WM_LBUTTONUP, IntPtr.Zero, At(x, y));
            Pump(0.3);
        }
        player.IgnoreMouseLeave = true;
        bool wasPlaying = player.Playing;
        Click(area.Width / 2, area.Height / 2);
        Check("clique no vídeo pausa", player.Playing != wasPlaying, player.Playing ? "tocando" : "pausado");
        Click(area.Width / 2, area.Height / 2);
        Check("clique no vídeo retoma", player.Playing == wasPlaying, player.Playing ? "tocando" : "pausado");
        Move(area.Width / 2, area.Height / 2);
        Check("barra escondida no meio", !player.BarsVisible.bottom && !player.BarsVisible.top, "nenhuma barra");
        Move(area.Width / 2, area.Height - player.UiScaled(10));
        Check("barra de baixo com o mouse em cima", player.BarsVisible.bottom, "aparece");
        int row = area.Height - player.UiScaled(84) + player.UiScaled(56);
        Click(player.UiScaled(31), row);
        Check("botão play/pause", !player.Playing, "pausado pelo botão");
        Click(player.UiScaled(31), row);
        // Volume: passar no alto-falante e deslizar até a barra (devagar, passando pelo vão entre os dois).
        float volumeBefore = player.TestVolume;
        for (int x = player.UiScaled(70); x <= player.UiScaled(104) + player.UiScaled(21); x += player.UiScaled(3)) Move(x, row);
        Check("barra de volume continua aberta", player.TestVolumeOpen, player.TestVolumeOpen ? "aberta" : "fechou no caminho");
        Click(player.UiScaled(104) + player.UiScaled(21), row);
        Check("ajustar o volume na barra", Math.Abs(player.TestVolume - 0.25f) < 0.05f, $"{volumeBefore * 100:0}% → {player.TestVolume * 100:0}%");
        Click(player.UiScaled(104) + player.UiScaled(84), row); // volta para 100%
        Move(area.Width / 2, area.Height / 2);
        Check("barra some ao sair", !player.BarsVisible.bottom, "sumiu");
        Move(area.Width / 2, player.UiScaled(15));
        Check("menu do topo com o mouse em cima", player.BarsVisible.top, "aparece");
        Move(area.Width / 2, area.Height / 2);
        Click(area.Width - player.UiScaled(32), area.Height - player.UiScaled(84) + player.UiScaled(56));
        Pump(0.3);
        Check("botão de tela cheia", player.State.fullscreen, "tela cheia");
        Key(PlayerNative.VK_ESCAPE);
        Pump(0.3);

        Key('A');
        Pump(0.3);
        Check("painel de ajustes", player.PanelVisible, "aberto com A");
        string before = player.ProfileName;
        Key('P');
        Pump(0.3);
        Check("trocar perfil", player.ProfileName != before, $"{before} → {player.ProfileName}");
        Key('A');
        Pump(0.3);
        Check("fechar painel", !player.PanelVisible, "fechado com A");
        drawn = player.FramesDrawn;
        Pump(1.0);
        Check("continua desenhando", player.FramesDrawn - drawn > 40, $"{player.FramesDrawn - drawn} quadros de tela em 1 s");
        player.Dispose();
        Console.WriteLine(bad == 0 ? "\nTudo certo." : $"\n{bad} falha(s).");
        return bad == 0 ? 0 : 2;
    }

    // "--playerslowtest": câmera lenta no player. Toca o vídeo de teste em 0.1x e fotografa a tela várias vezes:
    // com a IA, o bloco aparece na posição exata do momento (também entre os quadros reais); sem ela, ficaria
    // parado e pulando 6 px do vídeo a cada quadro real.
    public static int RunSlow(bool ensemble = false)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        string path = Path.Combine(Path.GetTempPath(), "mkvision-player-test.mp4");
        MediaFactory.MFStartup(true).CheckError();
        Write(path);
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
        RifeHost.Start(); // o player só carrega a IA ao abrir um vídeo; o teste espera ela pronta antes
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (RifeHost.Instance is null && clock.Elapsed.TotalSeconds < 240) Pump(0.2);
        if (RifeHost.Instance is null) { Console.WriteLine("IA não carregou."); player.Dispose(); return 1; }
        Console.WriteLine($"IA pronta em {clock.Elapsed.TotalSeconds:0.0} s");

        player.TestEnsemble(ensemble);
        player.Open(path);
        for (int i = 0; i < 9; i++) PlayerNative.PostMessage(player.Hwnd, PlayerNative.WM_KEYDOWN, PlayerNative.VK_DOWN, IntPtr.Zero); // 1x → 0.1x
        Pump(2.0); // a IA começa a analisar à frente
        player.TakeSlowStats();

        int bad = 0, samples = 0;
        double worst = 0;
        var lines = new List<string>();
        for (int i = 0; i < 40; i++)
        {
            Pump(0.06);
            var shot = player.Snapshot();
            if (shot is not { } snap) continue;
            // Onde o vídeo (640x360) está na janela e a linha do meio do bloco.
            double scale = Math.Min((double)snap.width / W, (double)snap.height / H);
            double rx = (snap.width - W * scale) / 2, ry = (snap.height - H * scale) / 2;
            int row = (int)(ry + (140 + Block / 2) * scale);
            int left = -1;
            for (int x = 0; x < snap.width; x++)
                if (snap.pixels[(row * snap.width + x) * 4 + 1] > 180) { left = x; break; }
            // Esperado: o bloco anda Step px por quadro (30 quadros/s), contínuo no tempo do vídeo.
            double fileTime = snap.media % (Frames / 30.0);
            double expected = rx + (X0 + Step * 30 * fileTime) * scale;
            double error = Math.Abs(left - expected);
            worst = Math.Max(worst, error);
            samples++;
            if (error > 4) bad++; // 4 px na tela = 2 px do vídeo (o intermediário é criado no tamanho da tela)
            if (i % 5 == 0) lines.Add($"   vídeo em {fileTime:0.000} s: borda em {left} (esperado {expected:0.0})");
        }
        var stats = player.TakeSlowStats();
        lines.ForEach(Console.WriteLine);
        Console.WriteLine($"IA: {stats.info} | quadros de tela sem análise pronta: {stats.noMotion} de {stats.slowFrames}");
        Console.WriteLine($"Posição do bloco: maior desvio {worst:0.0} px (sem a IA seria até {Step * Math.Min(1264.0 / W, 761.0 / H):0} px) | fora: {bad} de {samples}");
        player.Dispose();
        bool ok = samples >= 30 && bad == 0 && stats.noMotion * 20 <= Math.Max(1, stats.slowFrames);
        Console.WriteLine(ok ? "\nTudo certo." : "\nFalhou.");
        return ok ? 0 : 2;
    }

    // "--playershot <pasta>": fotos da tela do player com a barra de baixo e com o menu do topo (conferir o visual).
    public static int PlayerShot(string folder)
    {
        string path = Path.Combine(Path.GetTempPath(), "mkvision-player-test.mp4");
        MediaFactory.MFStartup(true).CheckError();
        Write(path);
        using var gpu = new Gpu();
        var player = new Player(gpu, new object());
        void Pump(double seconds)
        {
            var until = DateTime.UtcNow.AddSeconds(seconds);
            while (DateTime.UtcNow < until)
            {
                while (Native.PeekMessage(out var msg, IntPtr.Zero, 0, 0, 1)) { Native.TranslateMessage(ref msg); Native.DispatchMessage(ref msg); }
                Thread.Sleep(5);
            }
        }
        void Save(string name)
        {
            if (player.Snapshot() is not { } s) return;
            using var file = new BinaryWriter(File.Create(Path.Combine(folder, name)));
            file.Write((ushort)0x4D42); file.Write(54 + s.pixels.Length); file.Write(0); file.Write(54);
            file.Write(40); file.Write(s.width); file.Write(-s.height); file.Write((ushort)1); file.Write((ushort)32);
            file.Write(0); file.Write(s.pixels.Length); file.Write(2835); file.Write(2835); file.Write(0); file.Write(0);
            file.Write(s.pixels);
        }
        Pump(0.8);
        Save("player-empty.bmp");
        player.Open(path);
        Pump(1.2);
        PlayerNative.PostMessage(player.Hwnd, PlayerNative.WM_KEYDOWN, PlayerNative.VK_DOWN, IntPtr.Zero);
        PlayerNative.PostMessage(player.Hwnd, PlayerNative.WM_KEYDOWN, PlayerNative.VK_DOWN, IntPtr.Zero);
        Pump(0.4);
        Save("player-osd.bmp");
        Pump(1.8);
        Save("player-osd-gone.bmp");
        // Câmera lenta com giro e zoom: o intermediário é criado na tela, já girado e ampliado.
        var waitAi = System.Diagnostics.Stopwatch.StartNew();
        while (RifeHost.Instance is null && waitAi.Elapsed.TotalSeconds < 240) Pump(0.2);
        PlayerNative.PostMessage(player.Hwnd, PlayerNative.WM_KEYDOWN, 0x2E, IntPtr.Zero); // Delete: 45°
        for (int i = 0; i < 9; i++) PlayerNative.PostMessage(player.Hwnd, PlayerNative.WM_KEYDOWN, PlayerNative.VK_DOWN, IntPtr.Zero);
        Native.GetClientRect(player.Hwnd, out var zoomArea);
        var middle = new Native.POINT { X = zoomArea.Width / 2, Y = zoomArea.Height / 2 };
        Native.ClientToScreen(player.Hwnd, ref middle);
        for (int i = 0; i < 3; i++)
            PlayerNative.PostMessage(player.Hwnd, PlayerNative.WM_MOUSEWHEEL, (IntPtr)(120 << 16), (IntPtr)((middle.Y << 16) | (middle.X & 0xffff)));
        Pump(3.0);
        Save("slow-rotated-a.bmp");
        Pump(0.13);
        Save("slow-rotated-b.bmp");
        PlayerNative.PostMessage(player.Hwnd, PlayerNative.WM_KEYDOWN, 0x2D, IntPtr.Zero); // Insert: volta a 0°
        PlayerNative.PostMessage(player.Hwnd, PlayerNative.WM_KEYDOWN, PlayerNative.VK_BACK, IntPtr.Zero); // 1x
        Pump(0.5);
        player.IgnoreMouseLeave = true;
        Native.GetClientRect(player.Hwnd, out var area);
        PlayerNative.PostMessage(player.Hwnd, PlayerNative.WM_MOUSEMOVE, IntPtr.Zero, (IntPtr)(((area.Height - player.UiScaled(26)) << 16) | player.UiScaled(77)));
        Pump(0.5);
        Save("player-bottom.bmp");
        PlayerNative.PostMessage(player.Hwnd, PlayerNative.WM_MOUSEMOVE, IntPtr.Zero, (IntPtr)((player.UiScaled(15) << 16) | player.UiScaled(30)));
        Pump(0.5);
        Save("player-top.bmp");
        player.Dispose();
        Console.WriteLine("fotos salvas");
        return 0;
    }

    // "--foldertest <pasta das fotos>": pasta com vários vídeos — ordem do Explorer, Page Down, lista da borda
    // direita (fotos nos 4 tamanhos de ícone) e giro de 45° (foto).
    public static int RunFolder(string shots)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        string folder = Path.Combine(Path.GetTempPath(), "mkvision-folder-test");
        if (Directory.Exists(folder)) Directory.Delete(folder, true);
        Directory.CreateDirectory(folder);
        string first = Path.Combine(folder, "clip 1.mp4");
        MediaFactory.MFStartup(true).CheckError();
        Write(first);
        foreach (var n in new[] { 2, 3, 4, 5, 9, 10 }) File.Copy(first, Path.Combine(folder, $"clip {n}.mp4"));
        using var gpu = new Gpu();
        var player = new Player(gpu, new object());
        player.IgnoreMouseLeave = true;
        int bad = 0;
        void Check(string name, bool ok, string detail)
        {
            Console.WriteLine($"{(ok ? "OK    " : "FALHOU")} {name}: {detail}");
            if (!ok) bad++;
        }
        void Pump(double seconds)
        {
            var until = DateTime.UtcNow.AddSeconds(seconds);
            while (DateTime.UtcNow < until)
            {
                while (Native.PeekMessage(out var msg, IntPtr.Zero, 0, 0, 1)) { Native.TranslateMessage(ref msg); Native.DispatchMessage(ref msg); }
                Thread.Sleep(5);
            }
        }
        void Save(string name)
        {
            if (player.Snapshot() is not { } s) return;
            using var file = new BinaryWriter(File.Create(Path.Combine(shots, name)));
            file.Write((ushort)0x4D42); file.Write(54 + s.pixels.Length); file.Write(0); file.Write(54);
            file.Write(40); file.Write(s.width); file.Write(-s.height); file.Write((ushort)1); file.Write((ushort)32);
            file.Write(0); file.Write(s.pixels.Length); file.Write(2835); file.Write(2835); file.Write(0); file.Write(0);
            file.Write(s.pixels);
        }
        player.Open(Path.Combine(folder, "clip 2.mp4"));
        Pump(1.0);
        var state = player.FolderState;
        string order = string.Join(", ", state.files.Select(Path.GetFileNameWithoutExtension));
        Check("ordem do Explorer", order == "clip 1, clip 2, clip 3, clip 4, clip 5, clip 9, clip 10", order);
        Check("vídeo atual na pasta", state.index == 1, $"{state.index + 1} de {state.files.Length}");
        PlayerNative.PostMessage(player.Hwnd, PlayerNative.WM_KEYDOWN, 0x22, IntPtr.Zero); // Page Down
        Pump(0.8);
        Check("Page Down = próximo", player.FolderState.name == "clip 3.mp4", player.FolderState.name);
        PlayerNative.PostMessage(player.Hwnd, PlayerNative.WM_KEYDOWN, 0x21, IntPtr.Zero); // Page Up
        PlayerNative.PostMessage(player.Hwnd, PlayerNative.WM_KEYDOWN, 0x21, IntPtr.Zero);
        PlayerNative.PostMessage(player.Hwnd, PlayerNative.WM_KEYDOWN, 0x21, IntPtr.Zero);
        Pump(0.8);
        Check("Page Up dá a volta", player.FolderState.name == "clip 10.mp4", player.FolderState.name);
        Native.GetClientRect(player.Hwnd, out var area);
        IntPtr At(int x, int y) => (IntPtr)((y << 16) | (x & 0xffff));
        PlayerNative.PostMessage(player.Hwnd, PlayerNative.WM_MOUSEMOVE, IntPtr.Zero, At(area.Width - 4, area.Height / 2));
        Pump(0.3);
        Check("lista abre na borda direita", player.FolderState.listOpen, "aberta");
        PlayerNative.PostMessage(player.Hwnd, PlayerNative.WM_MOUSEMOVE, IntPtr.Zero, At(area.Width - player.UiScaled(60), area.Height / 2));
        Pump(3.0); // miniaturas
        Check("lista continua com o mouse dentro", player.FolderState.listOpen, "aberta");
        foreach (var mode in new[] { 2, 1, 3, 4 })
        {
            player.TestListMode(mode);
            Pump(1.0);
            Save($"list-mode{mode}.bmp");
        }
        PlayerNative.PostMessage(player.Hwnd, PlayerNative.WM_MOUSEMOVE, IntPtr.Zero, At(area.Width / 3, area.Height / 2));
        Pump(0.3);
        Check("lista some ao sair", !player.FolderState.listOpen, "fechada");
        PlayerNative.PostMessage(player.Hwnd, PlayerNative.WM_KEYDOWN, 0x2E, IntPtr.Zero); // Delete: +45°
        Pump(0.6);
        Check("Delete gira 45°", player.FolderState.rotation == 45, $"{player.FolderState.rotation}°");
        Save("rotated45.bmp");
        PlayerNative.PostMessage(player.Hwnd, PlayerNative.WM_KEYDOWN, 0x2D, IntPtr.Zero); // Insert: −45°
        PlayerNative.PostMessage(player.Hwnd, PlayerNative.WM_KEYDOWN, 0x2D, IntPtr.Zero);
        PlayerNative.PostMessage(player.Hwnd, PlayerNative.WM_KEYDOWN, 0x2D, IntPtr.Zero);
        Pump(0.6);
        Check("Insert gira para o outro lado", player.FolderState.rotation == 270, $"{player.FolderState.rotation}° (= −90°)");
        Save("rotated-90.bmp");
        player.Dispose();
        Console.WriteLine(bad == 0 ? "\nTudo certo." : $"\n{bad} falha(s).");
        return bad == 0 ? 0 : 2;
    }

    // "--assoctest": registra o MK Vision como player de vídeos, confere e desfaz (sem abrir a tela do Windows).
    public static int RunAssociation()
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        bool before = FileAssociation.IsRegistered();
        FileAssociation.Register();
        bool registered = FileAssociation.IsRegistered();
        using var openWith = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Classes\.mkv\OpenWithProgids");
        bool inOpenWith = openWith?.GetValueNames().Contains("MKVision.Video") == true;
        using var caps = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\MKVision\Capabilities\FileAssociations");
        int types = caps?.GetValueNames().Length ?? 0;
        FileAssociation.Unregister();
        bool after = FileAssociation.IsRegistered();
        Console.WriteLine($"antes: {before} | registrado: {registered} | no \"Abrir com\" do .mkv: {inOpenWith} | formatos: {types} | depois de desfazer: {after}");
        bool ok = registered && inOpenWith && types == FileAssociation.Extensions.Length && !after;
        Console.WriteLine(ok ? "Tudo certo." : "Falhou.");
        return ok ? 0 : 2;
    }

    // "--playerperf [modo]": uso da placa (nvidia-smi) com um vídeo 1080p 60 fps em tela cheia: 1x e câmera lenta 0.1x.
    public static int RunPerf(int mode)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        string path = Path.Combine(Path.GetTempPath(), "mkvision-perf-1080p60.mp4");
        MediaFactory.MFStartup(true).CheckError();
        if (!File.Exists(path)) WriteBig(path, 1920, 1080, 60, 300);
        using var gpu = new Gpu();
        var player = new Player(gpu, new object());
        if (mode >= 0) player.TestPerformanceMode(mode);
        void Pump(double seconds)
        {
            var until = DateTime.UtcNow.AddSeconds(seconds);
            while (DateTime.UtcNow < until)
            {
                while (Native.PeekMessage(out var msg, IntPtr.Zero, 0, 0, 1)) { Native.TranslateMessage(ref msg); Native.DispatchMessage(ref msg); }
                Thread.Sleep(5);
            }
        }
        (double gpuUse, double fps) Measure(double seconds)
        {
            var samples = new List<double>();
            var smi = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("nvidia-smi",
                "--query-gpu=utilization.gpu --format=csv,noheader,nounits -lms 250") { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true })!;
            smi.OutputDataReceived += (_, e) => { if (double.TryParse(e.Data?.Trim(), out double v)) lock (samples) samples.Add(v); };
            smi.BeginOutputReadLine();
            long drawn = player.FramesDrawn;
            Pump(seconds);
            double fps = (player.FramesDrawn - drawn) / seconds;
            smi.Kill();
            lock (samples) return (samples.Count > 0 ? samples.Average() : -1, fps);
        }
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (RifeHost.Instance is null && clock.Elapsed.TotalSeconds < 240) Pump(0.2);
        Pump(1);
        var idle = Measure(3);
        player.Open(path);
        PlayerNative.PostMessage(player.Hwnd, PlayerNative.WM_KEYDOWN, 'F', IntPtr.Zero); // tela cheia
        Pump(3);
        var normal = Measure(6);
        // Quanto o vídeo andou em 6 s de relógio (o arquivo de teste tem 5 s e repete).
        double progress = 0, previous = player.State.fileTime;
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (watch.Elapsed.TotalSeconds < 6)
        {
            Pump(0.1);
            double now = player.State.fileTime;
            progress += now >= previous ? now - previous : now + 5 - previous;
            previous = now;
        }
        Console.WriteLine($"1x: o vídeo andou {progress:0.00} s em {watch.Elapsed.TotalSeconds:0.00} s de relógio");
        for (int i = 0; i < 9; i++) PlayerNative.PostMessage(player.Hwnd, PlayerNative.WM_KEYDOWN, PlayerNative.VK_DOWN, IntPtr.Zero); // 0.1x
        Pump(4);
        var slow = Measure(8);
        var stats = player.TakeSlowStats();
        player.Dispose();
        Console.WriteLine($"placa sem o player tocando: {idle.gpuUse:0}%");
        Console.WriteLine($"1x:   placa {normal.gpuUse:0}% | {normal.fps:0} quadros de tela/s");
        Console.WriteLine($"0.1x: placa {slow.gpuUse:0}% | {slow.fps:0} quadros de tela/s | IA: {stats.info}");
        return 0;
    }

    // Vídeo de teste grande (só imagem): fundo em degradê e círculos andando.
    internal static void WriteBig(string path, int w, int h, int fps, int frames)
    {
        using var writer = MediaFactory.MFCreateSinkWriterFromURL(path, null!, null!);
        using var output = MediaFactory.MFCreateMediaType();
        output.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video).CheckError();
        output.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.H264).CheckError();
        output.Set(MediaTypeAttributeKeys.AvgBitrate, 12_000_000u).CheckError();
        output.Set(MediaTypeAttributeKeys.InterlaceMode, 2u).CheckError();
        output.Set(MediaTypeAttributeKeys.FrameSize, ((ulong)w << 32) | (uint)h).CheckError();
        output.Set(MediaTypeAttributeKeys.FrameRate, ((ulong)fps << 32) | 1).CheckError();
        output.Set(MediaTypeAttributeKeys.PixelAspectRatio, (1ul << 32) | 1).CheckError();
        int stream = writer.AddStream(output);
        using var input = MediaFactory.MFCreateMediaType();
        input.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video).CheckError();
        input.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.Rgb32).CheckError();
        input.Set(MediaTypeAttributeKeys.InterlaceMode, 2u).CheckError();
        input.Set(MediaTypeAttributeKeys.FrameSize, ((ulong)w << 32) | (uint)h).CheckError();
        input.Set(MediaTypeAttributeKeys.FrameRate, ((ulong)fps << 32) | 1).CheckError();
        input.Set(MediaTypeAttributeKeys.PixelAspectRatio, (1ul << 32) | 1).CheckError();
        input.Set(MediaTypeAttributeKeys.DefaultStride, (uint)(w * 4)).CheckError();
        writer.SetInputMediaType(stream, input, null!);
        writer.BeginWriting();
        var pixels = new byte[w * h * 4];
        for (int k = 0; k < frames; k++)
        {
            double t = (double)k / fps;
            Parallel.For(0, h, y =>
            {
                for (int x = 0; x < w; x++)
                {
                    double v = 40 + 60.0 * x / w + 40.0 * y / h;
                    for (int c = 0; c < 3; c++)
                    {
                        double cx = w * (0.2 + 0.3 * c) + 200 * Math.Sin(t * 2 + c), cy = h * 0.5 + 200 * Math.Cos(t * 1.5 + c);
                        if ((x - cx) * (x - cx) + (y - cy) * (y - cy) < 120 * 120) v = 200 + c * 15;
                    }
                    int i = (y * w + x) * 4;
                    pixels[i] = pixels[i + 1] = pixels[i + 2] = (byte)v;
                    pixels[i + 3] = 255;
                }
            });
            using var buffer = MediaFactory.MFCreateMemoryBuffer(pixels.Length);
            buffer.Lock(out IntPtr data, out _, out _);
            Marshal.Copy(pixels, 0, data, pixels.Length);
            buffer.Unlock();
            buffer.CurrentLength = pixels.Length;
            using var sample = MediaFactory.MFCreateSample();
            sample.AddBuffer(buffer);
            sample.SampleTime = k * 10_000_000L / fps;
            sample.SampleDuration = 10_000_000L / fps;
            writer.WriteSample(stream, sample);
        }
        writer.Finalize();
    }

    // "--panelshot <arquivo.bmp>": abre o player com o painel de ajustes e salva uma foto de cada aba (conferir o
    // visual): <arquivo>-0.bmp imagem, -0a com "Avançado" aberto, -1 câmera lenta, -2 zoom. Volta para a aba que estava.
    public static int PanelShot(string output)
    {
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
                Thread.Sleep(10);
            }
        }
        PlayerNative.PostMessage(player.Hwnd, PlayerNative.WM_KEYDOWN, 'A', IntPtr.Zero);
        Pump(2.5);
        var panel = player.Panel!;
        int before = player.PanelTabForTest;
        string stem = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(output))!, Path.GetFileNameWithoutExtension(output));
        foreach (var (tab, advanced, suffix) in new[] { (0, false, "0"), (0, true, "0a"), (3, false, "3"), (1, false, "1"), (2, false, "2"), (4, false, "4") })
        {
            panel.SelectTab(tab, advanced);
            Pump(1.2);
            var (w, h) = Shot(panel.Hwnd, $"{stem}-{suffix}.bmp");
            Console.WriteLine($"painel aba {suffix}: {w}x{h} salvo em {stem}-{suffix}.bmp");
        }
        panel.SelectTab(before);
        player.Dispose();
        return 0;
    }

    // "--hovertest": confere as faixas que abrem a lista (último quarto, meio da altura) e os ajustes (último quarto,
    // no alto) e que só reabrem ao entrar de novo na faixa.
    public static int HoverTest()
    {
        using var gpu = new Gpu();
        var player = new Player(gpu, new object()) { IgnoreMouseLeave = true };
        void Pump(int ms)
        {
            var until = DateTime.UtcNow.AddMilliseconds(ms);
            do
            {
                while (Native.PeekMessage(out var msg, IntPtr.Zero, 0, 0, 1))
                {
                    Native.TranslateMessage(ref msg);
                    Native.DispatchMessage(ref msg);
                }
                Thread.Sleep(5);
            } while (DateTime.UtcNow < until);
        }
        void Move(double fx, double fy)
        {
            Native.GetClientRect(player.Hwnd, out var client);
            int x = (int)(client.Width * fx), y = (int)(client.Height * fy);
            PlayerNative.PostMessage(player.Hwnd, PlayerNative.WM_MOUSEMOVE, IntPtr.Zero, (IntPtr)((y << 16) | (x & 0xffff)));
            Pump(15);
        }
        Pump(1500);
        int fails = 0;
        void Check(string what, bool ok) { Console.WriteLine($"{(ok ? "ok  " : "FALHOU")} {what}"); if (!ok) fails++; }
        Move(0.5, 0.5); Check("centro: nada aberto", !player.FolderState.listOpen && !player.PanelVisible);
        Move(0.7, 0.5); Check("70% da largura: lista fechada", !player.FolderState.listOpen);
        Move(0.78, 0.5); Check("78% da largura, meio: lista abre", player.FolderState.listOpen);
        Move(0.1, 0.5); Check("vai para a esquerda: lista fecha", !player.FolderState.listOpen);
        Move(0.5, 0.1); Check("alto, no meio: ajustes fechados", !player.PanelVisible);
        Move(0.8, 0.1); Check("80% da largura, alto: ajustes abrem", player.PanelVisible);
        player.Panel!.Hide();
        Move(0.82, 0.12); Check("fechado com o mouse na faixa: não reabre", !player.PanelVisible);
        Move(0.1, 0.5);
        Move(0.8, 0.1); Check("saiu e voltou: reabre", player.PanelVisible);
        player.Panel!.Hide();
        // Lista aberta e o mouse sobe pela lista até o canto: os ajustes abrem ao sair dela pelo alto.
        Move(0.1, 0.5);
        Move(0.85, 0.5); Check("lista aberta", player.FolderState.listOpen);
        Move(0.85, 0.3); Move(0.85, 0.15);
        Check("subindo pela lista: ajustes ainda fechados", !player.PanelVisible);
        Move(0.85, 0.05); Check("saiu da lista pelo alto: ajustes abrem", player.PanelVisible);
        player.Panel!.Hide();
        player.Dispose();
        Console.WriteLine(fails == 0 ? "tudo certo" : $"{fails} falha(s)");
        return fails == 0 ? 0 : 1;
    }

    private static (int w, int h) Shot(IntPtr hwnd, string output)
    {
        PlayerNative.GetWindowRect(hwnd, out var rect);
        int w = rect.Width, h = rect.Height;
        IntPtr screen = GetDC(IntPtr.Zero), memory = CreateCompatibleDC(screen), bitmap = CreateCompatibleBitmap(screen, w, h);
        var old = SelectObject(memory, bitmap);
        PrintWindow(hwnd, memory, 2); // PW_RENDERFULLCONTENT
        var header = new BITMAPINFOHEADER { biSize = 40, biWidth = w, biHeight = -h, biPlanes = 1, biBitCount = 32 };
        var pixels = new byte[w * h * 4];
        GetDIBits(memory, bitmap, 0, (uint)h, pixels, ref header, 0);
        SelectObject(memory, old);
        DeleteDC(memory);
        PlayerNative.DeleteObject(bitmap);
        ReleaseDC(IntPtr.Zero, screen);
        using var file = new BinaryWriter(File.Create(output));
        file.Write((ushort)0x4D42);
        file.Write(54 + pixels.Length);
        file.Write(0);
        file.Write(54);
        file.Write(40); file.Write(w); file.Write(-h); file.Write((ushort)1); file.Write((ushort)32);
        file.Write(0); file.Write(pixels.Length); file.Write(2835); file.Write(2835); file.Write(0); file.Write(0);
        file.Write(pixels);
        return (w, h);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public int biSize, biWidth, biHeight;
        public short biPlanes, biBitCount;
        public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant;
    }

    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
    [DllImport("user32.dll")] private static extern bool PrintWindow(IntPtr hwnd, IntPtr dc, uint flags);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleBitmap(IntPtr dc, int w, int h);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern int GetDIBits(IntPtr dc, IntPtr bitmap, uint start, uint lines, byte[] bits, ref BITMAPINFOHEADER info, uint usage);

    // "--audiotest": som do player (no mudo, para não fazer barulho): grava um vídeo de teste com um tom contínuo
    // e confere que o som anda junto com a imagem, para quando pausa e pula junto.
    public static int RunAudio()
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        string path = Path.Combine(Path.GetTempPath(), "mkvision-player-audio-test.mp4");
        MediaFactory.MFStartup(true).CheckError();
        Write(path, withAudio: true);
        using var audio = new PlayerAudio();
        audio.Open(path);
        int bad = 0;
        void Check(string name, bool ok, string detail)
        {
            Console.WriteLine($"{(ok ? "OK    " : "FALHOU")} {name}: {detail}");
            if (!ok) bad++;
        }
        var clock = System.Diagnostics.Stopwatch.StartNew();
        double fileTime = 0;
        while (clock.Elapsed.TotalSeconds < 2.0) // "tocando" em 1x por 2 s
        {
            fileTime = clock.Elapsed.TotalSeconds;
            audio.Sync(fileTime, playing: true, speed: 1, volume: 0, muted: true);
            Thread.Sleep(100);
        }
        Check("tem som", audio.HasAudio, "faixa de áudio encontrada");
        Check("som anda junto", Math.Abs(audio.Position - fileTime) < 0.25, $"som em {audio.Position:0.00} s, imagem em {fileTime:0.00} s");
        audio.Sync(fileTime, playing: false, speed: 1, volume: 0, muted: true);
        Thread.Sleep(400);
        double paused = audio.Position;
        Thread.Sleep(400);
        Check("pausa junto", Math.Abs(audio.Position - paused) < 0.02, $"parado em {paused:0.00} s");
        for (int i = 0; i < 10; i++) { audio.Sync(0.5, playing: true, speed: 1, volume: 0, muted: true); Thread.Sleep(30); }
        Thread.Sleep(200);
        Check("pula junto", audio.Position < 1.2, $"som em {audio.Position:0.00} s depois de pular para 0,5 s");
        Console.WriteLine(bad == 0 ? "\nTudo certo." : $"\n{bad} falha(s).");
        return bad == 0 ? 0 : 2;
    }

    // Grava o vídeo de teste (RGB → H.264 em MP4) com o codificador do Windows; com áudio: tom de 440 Hz (AAC).
    public static void WriteClip(string path) => Write(path, withAudio: true);

    private static void Write(string path, bool withAudio = false)
    {
        if (File.Exists(path)) File.Delete(path);
        using var writer = MediaFactory.MFCreateSinkWriterFromURL(path, null!, null!);
        using var output = MediaFactory.MFCreateMediaType();
        output.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video).CheckError();
        output.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.H264).CheckError();
        output.Set(MediaTypeAttributeKeys.AvgBitrate, 4_000_000u).CheckError();
        output.Set(MediaTypeAttributeKeys.InterlaceMode, 2u).CheckError(); // progressivo
        output.Set(MediaTypeAttributeKeys.FrameSize, ((ulong)W << 32) | H).CheckError();
        output.Set(MediaTypeAttributeKeys.FrameRate, (30ul << 32) | 1).CheckError();
        output.Set(MediaTypeAttributeKeys.PixelAspectRatio, (1ul << 32) | 1).CheckError();
        int stream = writer.AddStream(output);
        using var input = MediaFactory.MFCreateMediaType();
        input.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video).CheckError();
        input.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.Rgb32).CheckError();
        input.Set(MediaTypeAttributeKeys.InterlaceMode, 2u).CheckError();
        input.Set(MediaTypeAttributeKeys.FrameSize, ((ulong)W << 32) | H).CheckError();
        input.Set(MediaTypeAttributeKeys.FrameRate, (30ul << 32) | 1).CheckError();
        input.Set(MediaTypeAttributeKeys.PixelAspectRatio, (1ul << 32) | 1).CheckError();
        input.Set(MediaTypeAttributeKeys.DefaultStride, (uint)(W * 4)).CheckError(); // linhas de cima para baixo
        writer.SetInputMediaType(stream, input, null!);
        int audioStream = -1;
        const int rate = 48000;
        if (withAudio)
        {
            using var aac = MediaFactory.MFCreateMediaType();
            aac.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Audio).CheckError();
            aac.Set(MediaTypeAttributeKeys.Subtype, AudioFormatGuids.Aac).CheckError();
            aac.Set(MediaTypeAttributeKeys.AudioBitsPerSample, 16u).CheckError();
            aac.Set(MediaTypeAttributeKeys.AudioSamplesPerSecond, (uint)rate).CheckError();
            aac.Set(MediaTypeAttributeKeys.AudioNumChannels, 2u).CheckError();
            aac.Set(MediaTypeAttributeKeys.AudioAvgBytesPerSecond, 24000u).CheckError();
            audioStream = writer.AddStream(aac);
            using var pcm = MediaFactory.MFCreateMediaType();
            pcm.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Audio).CheckError();
            pcm.Set(MediaTypeAttributeKeys.Subtype, AudioFormatGuids.Pcm).CheckError();
            pcm.Set(MediaTypeAttributeKeys.AudioBitsPerSample, 16u).CheckError();
            pcm.Set(MediaTypeAttributeKeys.AudioSamplesPerSecond, (uint)rate).CheckError();
            pcm.Set(MediaTypeAttributeKeys.AudioNumChannels, 2u).CheckError();
            pcm.Set(MediaTypeAttributeKeys.AudioBlockAlignment, 4u).CheckError();
            pcm.Set(MediaTypeAttributeKeys.AudioAvgBytesPerSecond, (uint)(rate * 4)).CheckError();
            writer.SetInputMediaType(audioStream, pcm, null!);
        }
        writer.BeginWriting();
        var tone = new byte[rate / 30 * 4];

        var pixels = new byte[W * H * 4];
        for (int k = 0; k < Frames; k++)
        {
            int bx = X0 + Step * k;
            for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
            {
                int i = (y * W + x) * 4;
                bool inside = x >= bx && x < bx + Block && y >= 140 && y < 140 + Block;
                byte v = inside ? (byte)235 : (byte)(30 + (x + y) / 16);
                pixels[i] = pixels[i + 1] = pixels[i + 2] = v;
                pixels[i + 3] = 255;
            }
            using var buffer = MediaFactory.MFCreateMemoryBuffer(pixels.Length);
            buffer.Lock(out IntPtr data, out _, out _);
            Marshal.Copy(pixels, 0, data, pixels.Length);
            buffer.Unlock();
            buffer.CurrentLength = pixels.Length;
            using var sample = MediaFactory.MFCreateSample();
            sample.AddBuffer(buffer);
            sample.SampleTime = k * 10_000_000L / 30;
            sample.SampleDuration = 10_000_000L / 30;
            writer.WriteSample(stream, sample);
            if (audioStream >= 0)
            {
                for (int n = 0; n < rate / 30; n++)
                {
                    short value = (short)(Math.Sin(2 * Math.PI * 440 * (k * (rate / 30) + n) / rate) * 8000);
                    BitConverter.TryWriteBytes(tone.AsSpan(n * 4), value);
                    BitConverter.TryWriteBytes(tone.AsSpan(n * 4 + 2), value);
                }
                using var audioBuffer = MediaFactory.MFCreateMemoryBuffer(tone.Length);
                audioBuffer.Lock(out IntPtr audioData, out _, out _);
                Marshal.Copy(tone, 0, audioData, tone.Length);
                audioBuffer.Unlock();
                audioBuffer.CurrentLength = tone.Length;
                using var audioSample = MediaFactory.MFCreateSample();
                audioSample.AddBuffer(audioBuffer);
                audioSample.SampleTime = k * 10_000_000L / 30;
                audioSample.SampleDuration = 10_000_000L / 30;
                writer.WriteSample(audioStream, audioSample);
            }
        }
        writer.Finalize();
    }

    // Borda esquerda do bloco branco na linha do meio dele.
    private static int BlockLeft(Gpu gpu, ID3D11Texture2D staging)
    {
        var map = gpu.Context.Map(staging, 0, MapMode.Read);
        try
        {
            nint row = map.DataPointer + (nint)((140 + Block / 2) * map.RowPitch);
            for (int x = 0; x < W; x++)
                if (Marshal.ReadByte(row, x * 4 + 1) > 180) return x;
            return -1;
        }
        finally { gpu.Context.Unmap(staging, 0); }
    }
}
