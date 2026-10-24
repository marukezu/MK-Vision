using Vortice.MediaFoundation;

namespace MKVision.Desktop;

// --webtest <pasta> [página real]: abre o navegador do app numa página de teste (vídeo servido pelo próprio app,
// com trechos), aperta "." e confere que o vídeo vai para o player e toca; depois, se der uma página real
// (ex.: YouTube), faz o mesmo nela (o yt-dlp acha o vídeo). Salva fotos da barra e da página.
internal static class WebBrowserTest
{
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hwnd);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
    [System.Runtime.InteropServices.DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [System.Runtime.InteropServices.DllImport("gdi32.dll")] private static extern IntPtr CreateDIBSection(IntPtr dc, ref BITMAPINFOHEADER info, uint usage, out IntPtr bits, IntPtr section, uint offset);
    [System.Runtime.InteropServices.DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [System.Runtime.InteropServices.DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
    [System.Runtime.InteropServices.DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER { public int biSize, biWidth, biHeight; public short biPlanes, biBitCount; public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant; }

    // Pixels (BGRA) da área do app como o Windows a compõe (com o site, que é uma janela filha).
    private static (byte[] pixels, int width, int height) ClientPixels(IntPtr hwnd)
    {
        Native.GetClientRect(hwnd, out var r);
        int w = r.Width, h = r.Height;
        var screen = GetDC(IntPtr.Zero);
        var dc = CreateCompatibleDC(screen);
        var info = new BITMAPINFOHEADER { biSize = 40, biWidth = w, biHeight = -h, biPlanes = 1, biBitCount = 32 };
        var bitmap = CreateDIBSection(dc, ref info, 0, out var bits, IntPtr.Zero, 0);
        var old = SelectObject(dc, bitmap);
        PrintWindow(hwnd, dc, 1 | 2);
        var pixels = new byte[w * h * 4];
        System.Runtime.InteropServices.Marshal.Copy(bits, pixels, 0, pixels.Length);
        SelectObject(dc, old);
        DeleteObject(bitmap);
        DeleteDC(dc);
        ReleaseDC(IntPtr.Zero, screen);
        return (pixels, w, h);
    }

    // Foto da janela inteira como o Windows a compõe (com o site, que é uma janela filha).
    private static void WindowShot(IntPtr hwnd, string path)
    {
        Native.GetClientRect(hwnd, out var r);
        int w = r.Width, h = r.Height;
        var screen = GetDC(IntPtr.Zero);
        var dc = CreateCompatibleDC(screen);
        var info = new BITMAPINFOHEADER { biSize = 40, biWidth = w, biHeight = -h, biPlanes = 1, biBitCount = 32 };
        var bitmap = CreateDIBSection(dc, ref info, 0, out var bits, IntPtr.Zero, 0);
        var old = SelectObject(dc, bitmap);
        PrintWindow(hwnd, dc, 1 | 2); // só a área do cliente, com o conteúdo DirectX/WebView
        var pixels = new byte[w * h * 4];
        System.Runtime.InteropServices.Marshal.Copy(bits, pixels, 0, pixels.Length);
        SelectObject(dc, old);
        DeleteObject(bitmap);
        DeleteDC(dc);
        ReleaseDC(IntPtr.Zero, screen);
        InterpTest.Png(path, pixels, w, 0, 0, w, h);
    }

    // --webinspect <página> <script>: abre a página no navegador do app, espera carregar e imprime o resultado do
    // script (para entender como um site monta os vídeos).
    public static int Inspect(string page, string script)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
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
        PlayerNative.PostMessage(player.Hwnd, PlayerNative.WM_KEYDOWN, 0x71, IntPtr.Zero);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (!player.WebReady && clock.Elapsed.TotalSeconds < 30) Pump(0.1);
        if (page.StartsWith("ubol:")) page = $"chrome-extension://{AdBlock.Id}/{page[5..]}";
        player.TestWebNavigate(page);
        Pump(10);
        var task = player.TestWebEval(script);
        clock.Restart();
        while (!task.IsCompleted && clock.Elapsed.TotalSeconds < 10) Pump(0.1);
        string result = task.IsCompletedSuccessfully ? task.Result : "\"sem resposta\"";
        if (!result.StartsWith('"'))
        {
            // Script assíncrono: ele guarda a resposta em window.__r.
            Pump(5);
            task = player.TestWebEval("String(window.__r)");
            clock.Restart();
            while (!task.IsCompleted && clock.Elapsed.TotalSeconds < 10) Pump(0.1);
            result = task.IsCompletedSuccessfully ? task.Result : "\"sem resposta\"";
        }
        Console.WriteLine(System.Text.Json.JsonSerializer.Deserialize<string>(result) ?? result);
        player.Dispose();
        return 0;
    }

    // --webtabstest <pasta>: abas (abrir, trocar, fechar) e barra de favoritos com pasta; salva uma foto.
    // --settingstest <pasta>: aba nova com o endereço em foco e selecionado; engrenagem → Configurações (painéis);
    // "Ao iniciar" com páginas escolhidas e o navegador abrindo com elas. Usar com MKV_SETTINGS e MKV_WEB_PROFILE
    // apontando para a pasta de teste (não mexe nos ajustes, abas e cookies do MK Vision em uso).
    public static int Settings(string folder)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        Directory.CreateDirectory(folder);
        using var gpu = new Gpu();
        int failures = 0;
        void Check(string what, bool ok, string detail)
        {
            Console.WriteLine($"{(ok ? "OK    " : "FALHOU")} {what}: {detail}");
            if (!ok) failures++;
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
        string Eval(Player p, string js)
        {
            var task = p.TestWebEval(js);
            var clock = System.Diagnostics.Stopwatch.StartNew();
            while (!task.IsCompleted && clock.Elapsed.TotalSeconds < 5) Pump(0.05);
            return task.IsCompletedSuccessfully ? task.Result : "(sem resposta)";
        }
        Player Start()
        {
            var p = new Player(gpu, new object());
            PlayerNative.PostMessage(p.Hwnd, PlayerNative.WM_KEYDOWN, 0x71, IntPtr.Zero);
            var clock = System.Diagnostics.Stopwatch.StartNew();
            while (!p.WebReady && clock.Elapsed.TotalSeconds < 40) Pump(0.1);
            Pump(1.5);
            return p;
        }
        var player = Start();
        // Aba nova: endereço com o foco e todo selecionado, mesmo depois de a página carregar.
        player.TestNewTabButton();
        Pump(4);
        var sel = player.TestAddressSelection;
        Check("aba nova: foco no endereço", player.TestAddressFocused, player.TestAddressText);
        Check("aba nova: endereço todo selecionado", sel.start == 0 && sel.end == sel.length && sel.length > 0, $"{sel.start}–{sel.end} de {sel.length}");
        // Engrenagem: abre as configurações (e não duplica a aba).
        player.TestOpenSettings();
        Pump(3);
        int tabs = player.TestTabCount;
        player.TestOpenSettings();
        Pump(1);
        Check("configurações abertas", player.TestWebTitle == "Configurações", player.TestWebTitle);
        Check("endereço mostrado", player.TestAddressText == "mkvision://configurações", player.TestAddressText);
        Check("engrenagem de novo não abre outra aba", player.TestTabCount == tabs, $"{tabs} → {player.TestTabCount}");
        WindowShot(player.Hwnd, Path.Combine(folder, "config-inicio.png"));
        // Ao iniciar: páginas específicas, adicionando pela tela.
        Eval(player, "document.querySelector('input[name=startup][value=\"2\"]').click()");
        foreach (var page in new[] { "youtube.com", "google.com" })
        {
            Eval(player, "[...document.querySelectorAll('#pages button')].find(b => b.textContent === 'Adicionar uma página').click()");
            Pump(0.3);
            Eval(player, $"(() => {{ const i = document.querySelector('#pages input'); i.value = '{page}'; [...document.querySelectorAll('#pages button')].find(b => b.textContent === 'Adicionar').click(); }})()");
            Pump(0.5);
        }
        Eval(player, "[...document.querySelectorAll('#pages button')].find(b => b.textContent === 'Adicionar uma página').click()");
        Pump(0.3);
        Eval(player, "(() => { const i = document.querySelector('#pages input'); i.value = 'isso não é site'; [...document.querySelectorAll('#pages button')].find(b => b.textContent === 'Adicionar').click(); })()");
        Pump(0.5);
        string error = Eval(player, "(document.querySelector('#pages .error') || {}).textContent || ''");
        Check("endereço inválido avisa", error.Contains("inválido"), error);
        WindowShot(player.Hwnd, Path.Combine(folder, "config-paginas.png"));
        Eval(player, "[...document.querySelectorAll('#pages button')].find(b => b.textContent === 'Cancelar').click()");
        var startup = player.TestStartup;
        Check("páginas guardadas", startup.startup == 2 && startup.pages.SequenceEqual(["https://youtube.com/", "https://google.com/"]), $"{startup.startup}: {string.Join(", ", startup.pages)}");
        foreach (var panel in new[] { "favoritos", "bloqueador", "downloads", "privacidade", "atalhos" })
        {
            Eval(player, $"document.querySelector('nav a[data-go={panel}]').click()");
            Pump(0.4);
            WindowShot(player.Hwnd, Path.Combine(folder, $"config-{panel}.png"));
        }
        // Um site não consegue mudar os ajustes.
        player.TestNewTab("https://example.com/");
        Pump(4);
        Eval(player, "window.chrome.webview.postMessage({ type: 'settings-set', startup: 0 })");
        Pump(0.5);
        Check("site não muda os ajustes", player.TestStartup.startup == 2, $"{player.TestStartup.startup}");
        player.Dispose();
        Pump(1);
        // Abre de novo: começa com as páginas escolhidas.
        player = Start();
        Pump(2);
        Check("ao iniciar abre as páginas escolhidas", player.TestTabCount == 2, $"{player.TestTabCount} abas");
        Check("endereço da primeira página", player.TestAddressText.Contains("youtube"), $"{player.TestAddressText} (página: {Eval(player, "location.href")}, foco no endereço: {player.TestAddressFocused})");
        player.Dispose();
        Console.WriteLine(failures == 0 ? "tudo certo" : $"{failures} falha(s)");
        return failures == 0 ? 0 : 1;
    }

    // --mobiletest <pasta>: instagram.com abre como num celular (identificação, toque, coluna no centro) e outro site
    // na mesma aba volta ao computador. Usar com MKV_SETTINGS e MKV_WEB_PROFILE na pasta de teste.
    public static int Mobile(string folder)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        Directory.CreateDirectory(folder);
        using var gpu = new Gpu();
        int failures = 0;
        void Check(string what, bool ok, string detail)
        {
            Console.WriteLine($"{(ok ? "OK    " : "FALHOU")} {what}: {detail}");
            if (!ok) failures++;
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
        var player = new Player(gpu, new object());
        string Eval(string js)
        {
            var task = player.TestWebEval(js);
            var clock = System.Diagnostics.Stopwatch.StartNew();
            while (!task.IsCompleted && clock.Elapsed.TotalSeconds < 5) Pump(0.05);
            if (!task.IsCompletedSuccessfully) return "(sem resposta)";
            try { return System.Text.Json.JsonSerializer.Deserialize<string>(task.Result) ?? task.Result; } catch { return task.Result; }
        }
        PlayerNative.PostMessage(player.Hwnd, PlayerNative.WM_KEYDOWN, 0x71, IntPtr.Zero);
        var start = System.Diagnostics.Stopwatch.StartNew();
        while (!player.WebReady && start.Elapsed.TotalSeconds < 40) Pump(0.1);
        Pump(1);
        Native.GetClientRect(player.Hwnd, out var client);
        player.TestNewTab("https://www.instagram.com/");
        for (int i = 0; i < 8; i++) { Pump(1); Console.WriteLine($"  {i + 1}s: {Eval("location.href")}"); }
        string probe = "JSON.stringify({ua: navigator.userAgent, mobile: navigator.userAgentData && navigator.userAgentData.mobile, " +
                       "platform: navigator.userAgentData && navigator.userAgentData.platform, touch: navigator.maxTouchPoints, width: innerWidth, url: location.href})";
        string info = Eval(probe);
        Console.WriteLine("instagram: " + info);
        var bounds = player.TestWebBounds;
        Check("instagram em modo celular", player.TestTabMobile && info.Contains("Android") && info.Contains("\"mobile\":true"), info);
        Check("tela de celular (~430)", info.Contains("\"width\":43"), $"coluna {bounds.Width}px de {client.Width}px");
        WindowShot(player.Hwnd, Path.Combine(folder, "instagram.png"));
        player.TestWebNavigate("https://www.google.com/");
        Pump(5);
        info = Eval(probe);
        Console.WriteLine("google: " + info);
        Check("outro site volta ao computador", !player.TestTabMobile && info.Contains("Windows") && player.TestWebBounds.Width == client.Width, info);
        player.TestWebNavigate("https://www.instagram.com/explore/");
        Pump(6);
        Check("volta ao instagram: celular de novo", player.TestTabMobile && Eval(probe).Contains("Android"), Eval("location.href"));
        // Pesquisa: Enter numa caixa "Pesquisar" leva à página de posts e reels do assunto (sem login a página
        // manda para o login, com o destino no endereço).
        Eval("(() => { const i = document.createElement('input'); i.placeholder = 'Pesquisar'; document.body.prepend(i); i.focus(); i.value = 'futebol brasil';" +
             " i.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', bubbles: true })); })()");
        Pump(5);
        string after = Uri.UnescapeDataString(Eval("location.href")).Replace("+", " ");
        Check("Enter na pesquisa vai aos posts do assunto", after.Contains("/explore/search/keyword/?q=futebol brasil"), after);
        player.Dispose();
        Console.WriteLine(failures == 0 ? "tudo certo" : $"{failures} falha(s)");
        return failures == 0 ? 0 : 1;
    }

    // --starttest: o app sem vídeo abre no navegador (como no início). Usar com MKV_SETTINGS e MKV_WEB_PROFILE.
    public static int Start()
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        using var gpu = new Gpu();
        using var overlay = new Overlay(gpu);
        using var app = new App(gpu, overlay);
        app.OpenPlayer(null, browser: true);
        var player = app.PlayerForTest!;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (!player.WebReady && clock.Elapsed.TotalSeconds < 40)
        {
            while (Native.PeekMessage(out var msg, IntPtr.Zero, 0, 0, 1)) { Native.TranslateMessage(ref msg); Native.DispatchMessage(ref msg); }
            Thread.Sleep(5);
        }
        bool ok = player.WebMode && player.WebReady;
        Console.WriteLine($"{(ok ? "OK    " : "FALHOU")} abre no navegador: modo navegador {player.WebMode}, pronto {player.WebReady} ({clock.Elapsed.TotalSeconds:0.0} s), título \"{player.TestTitle}\"");
        player.Dispose();
        return ok ? 0 : 1;
    }

    // --robusttest: abas restauradas pré-carregam (mudas), aba de fundo já carrega, página derrubada volta sozinha
    // e a IA de câmera lenta não carrega só por abrir o navegador. Usar com MKV_SETTINGS, MKV_WEB_PROFILE e MKV_LOG.
    public static int Robust()
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        using var gpu = new Gpu();
        int failures = 0;
        void Check(string what, bool ok, string detail)
        {
            Console.WriteLine($"{(ok ? "OK    " : "FALHOU")} {what}: {detail}");
            if (!ok) failures++;
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
        var player = new Player(gpu, new object());
        player.TestSetSessionTabs("https://example.com/", "https://www.wikipedia.org/", "https://www.google.com/");
        player.ShowBrowser();
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (!player.WebReady && clock.Elapsed.TotalSeconds < 40) Pump(0.1);
        Pump(7);
        string Tabs() => string.Join(" | ", player.TestTabs.Select(t => $"{(t.active ? "*" : "")}{(t.url.Length > 0 ? new Uri(t.url).Host : "(não criada)")}{(t.muted ? " mudo" : "")}"));
        var tabs = player.TestTabs;
        Check("abas restauradas carregaram em segundo plano", tabs.All(t => t.url.StartsWith("https://")), Tabs());
        Check("as de fundo ficam mudas, a ativa não", tabs.All(t => t.active ? !t.muted : t.muted), Tabs());
        Check("IA de câmera lenta não carregou só por abrir o navegador", !RifeHost.Started, $"carregada: {RifeHost.Started}");
        player.TestOpenBackground("https://www.iana.org/");
        Pump(5);
        tabs = player.TestTabs;
        Check("aba de fundo já carregou", tabs.Any(t => !t.active && t.url.Contains("iana.org")), Tabs());
        Check("a ativa continua a mesma", tabs.First(t => t.active).url.Contains("example.com"), Tabs());
        // Derruba a página da aba ativa: tem que voltar sozinha.
        try { player.TestCrashPage(); } catch { }
        Pump(6);
        string alive = "";
        var eval = player.TestWebEval("location.host");
        var wait = System.Diagnostics.Stopwatch.StartNew();
        while (!eval.IsCompleted && wait.Elapsed.TotalSeconds < 5) Pump(0.05);
        if (eval.IsCompletedSuccessfully) alive = eval.Result;
        Check("página derrubada voltou sozinha", alive.Contains("example.com"), alive);
        player.Dispose();
        string log = File.Exists(Log.FilePath) ? File.ReadAllText(Log.FilePath) : "";
        Check("o log registrou a queda", log.Contains("processo falhou"), log.Split('\n').FirstOrDefault(l => l.Contains("processo falhou"))?.Trim() ?? "(nada)");
        Console.WriteLine(failures == 0 ? "tudo certo" : $"{failures} falha(s)");
        return failures == 0 ? 0 : 1;
    }

    // --adblocksitetest: no Instagram, o registro do que você assiste (/ajax/bz) é barrado com o bloqueio e passa
    // com o site na lista "sem bloqueio". Usa o perfil de verdade (o bloqueador não instala num perfil novo de
    // teste) com MKV_SETTINGS e MKV_LOG separados; termina com o Instagram liberado (o padrão).
    public static int AdBlockSites()
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        using var gpu = new Gpu();
        int failures = 0;
        void Check(string what, bool ok, string detail)
        {
            Console.WriteLine($"{(ok ? "OK    " : "FALHOU")} {what}: {detail}");
            if (!ok) failures++;
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
        var player = new Player(gpu, new object());
        string Eval(string js)
        {
            var task = player.TestWebEval(js);
            var clock = System.Diagnostics.Stopwatch.StartNew();
            while (!task.IsCompleted && clock.Elapsed.TotalSeconds < 5) Pump(0.05);
            if (!task.IsCompletedSuccessfully) return "(sem resposta)";
            try { return System.Text.Json.JsonSerializer.Deserialize<string>(task.Result) ?? ""; } catch { return task.Result; }
        }
        string Probe()
        {
            Eval("window.__probe = ''; fetch('/ajax/bz', { method: 'POST', body: '' }).then(r => window.__probe = 'passou (resposta ' + r.status + ')').catch(e => window.__probe = 'barrado (' + e.message + ')')");
            for (int i = 0; i < 50; i++)
            {
                Pump(0.1);
                string r = Eval("window.__probe || ''");
                if (r.Length > 0) return r;
            }
            return "(sem resposta)";
        }
        // Começa com o Instagram COM bloqueio (devolvido ao normal).
        player.TestSetAdBlockLists([], ["instagram.com"]);
        player.TestSetSessionTabs("https://www.instagram.com/");
        player.ShowBrowser();
        var start = System.Diagnostics.Stopwatch.StartNew();
        while ((!player.WebReady || AdBlock.Id is null) && start.Elapsed.TotalSeconds < 40) Pump(0.1);
        Pump(5);
        Check("bloqueador instalado", AdBlock.Id is not null, AdBlock.Id ?? "(não)");
        player.TestWebNavigate("https://www.instagram.com/");
        Pump(5);
        string blocked = Probe();
        Check("com bloqueio, o registro do que se assiste é barrado", blocked.StartsWith("barrado"), blocked);
        player.TestSetAdBlockLists(["instagram.com"], []);
        var sync = player.TestSyncAdBlock();
        while (!sync.IsCompleted) Pump(0.1);
        Check("instagram.com na lista sem bloqueio do uBlock", player.TestAdBlockApplied.Contains("instagram.com"), string.Join(", ", player.TestAdBlockApplied));
        string free = "";
        for (int attempt = 0; attempt < 4 && !free.StartsWith("passou"); attempt++)
        {
            Pump(attempt * 3);
            player.TestWebNavigate("https://www.instagram.com/");
            Pump(5);
            free = Probe();
            Console.WriteLine($"  tentativa {attempt + 1}: {free}");
        }
        Check("sem bloqueio, o registro chega ao Instagram", free.StartsWith("passou"), free);
        if (!free.StartsWith("passou"))
        {
            // Diagnóstico: com o bloqueador inteiro desligado, passa? (se não, quem barra é outra coisa)
            var off = AdBlock.SetEnabled(false);
            while (!off.IsCompleted) Pump(0.1);
            player.TestWebNavigate("https://www.instagram.com/");
            Pump(5);
            Console.WriteLine($"  com o bloqueador desligado: {Probe()}");
            var on = AdBlock.SetEnabled(true);
            while (!on.IsCompleted) Pump(0.1);
        }
        player.Dispose();
        Console.WriteLine(failures == 0 ? "tudo certo" : $"{failures} falha(s)");
        return failures == 0 ? 0 : 1;
    }

    // --privacytest: abre páginas e fecha a janela (com o navegador aberto): o histórico interno é apagado antes de
    // fechar; o log não guarda endereços nem caminhos. O histórico (arquivo History do perfil de teste) é conferido
    // por fora depois. Usar com MKV_SETTINGS, MKV_WEB_PROFILE e MKV_LOG.
    public static int Privacy()
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        using var gpu = new Gpu();
        void Pump(double seconds)
        {
            var until = DateTime.UtcNow.AddSeconds(seconds);
            while (DateTime.UtcNow < until)
            {
                while (Native.PeekMessage(out var msg, IntPtr.Zero, 0, 0, 1)) { Native.TranslateMessage(ref msg); Native.DispatchMessage(ref msg); }
                Thread.Sleep(5);
            }
        }
        Log.Write(@"teste: https://exemplo.com/video?id=123 e C:\Users\Fulano\Vídeos\meu vídeo.mp4 'D:\x y\z.mp4' fim");
        var player = new Player(gpu, new object());
        player.TestSetSessionTabs("https://example.com/");
        player.ShowBrowser();
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (!player.WebReady && clock.Elapsed.TotalSeconds < 40) Pump(0.1);
        Pump(3);
        player.TestWebNavigate("https://www.iana.org/domains");
        Pump(4);
        player.TestWebNavigate("https://www.wikipedia.org/");
        Pump(4);
        PlayerNative.PostMessage(player.Hwnd, 0x0010 /* WM_CLOSE */, IntPtr.Zero, IntPtr.Zero);
        clock.Restart();
        while (player.TestWindowAlive && clock.Elapsed.TotalSeconds < 10) Pump(0.1);
        Console.WriteLine($"janela fechou em {clock.Elapsed.TotalSeconds:0.0} s (fechada: {!player.TestWindowAlive})");
        string log = File.ReadAllText(Log.FilePath);
        bool clean = !log.Contains("exemplo.com") && !log.Contains("Fulano") && !log.Contains("meu vídeo") && !log.Contains("x y") && log.Contains("[endereço]") && log.Contains("[arquivo]");
        Console.WriteLine($"{(clean ? "OK    " : "FALHOU")} log sem endereços nem caminhos: {log.Split('\n').FirstOrDefault(l => l.Contains("teste:"))?.Trim()}");
        return clean ? 0 : 1;
    }

    // --igimagetest [endereço]: numa página do Instagram, a maior foto visível; confere que há uma camada por cima e
    // que o "." no ponto dela abre a foto no player. Usar com MKV_SETTINGS, MKV_WEB_PROFILE e MKV_LOG.
    public static int InstagramImage(string url)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        using var gpu = new Gpu();
        void Pump(double seconds)
        {
            var until = DateTime.UtcNow.AddSeconds(seconds);
            while (DateTime.UtcNow < until)
            {
                while (Native.PeekMessage(out var msg, IntPtr.Zero, 0, 0, 1)) { Native.TranslateMessage(ref msg); Native.DispatchMessage(ref msg); }
                Thread.Sleep(5);
            }
        }
        var player = new Player(gpu, new object());
        string Eval(string js)
        {
            var task = player.TestWebEval(js);
            var clock = System.Diagnostics.Stopwatch.StartNew();
            while (!task.IsCompleted && clock.Elapsed.TotalSeconds < 5) Pump(0.05);
            if (!task.IsCompletedSuccessfully) return "(sem resposta)";
            try { return System.Text.Json.JsonSerializer.Deserialize<string>(task.Result) ?? ""; } catch { return task.Result; }
        }
        player.TestSetSessionTabs(url);
        player.ShowBrowser();
        var start = System.Diagnostics.Stopwatch.StartNew();
        while (!player.WebReady && start.Elapsed.TotalSeconds < 40) Pump(0.1);
        Pump(8);
        string found = Eval("(() => { let best = null, area = 0; for (const img of document.images) { const r = img.getBoundingClientRect();" +
            " if (img.naturalWidth < 120 || r.width * r.height <= area || r.bottom < 0 || r.top > innerHeight) continue; best = img; area = r.width * r.height; }" +
            " if (!best) return ''; const r = best.getBoundingClientRect(); const x = Math.round(r.left + r.width / 2), y = Math.round(Math.min(r.top + r.height / 2, innerHeight - 5));" +
            " const top = document.elementFromPoint(x, y); return x + ',' + y + ',' + (top === best ? 'sem camada' : 'coberta por ' + top.tagName + '.' + (top.className || '')); })()");
        Console.WriteLine($"foto na página: {(found.Length > 0 ? found : "(nenhuma)")}  [{Eval("location.href")}]");
        if (found.Length == 0) { player.Dispose(); return 1; }
        var parts = found.Split(',');
        var task = player.TestImageAt(double.Parse(parts[0]), double.Parse(parts[1]));
        while (!task.IsCompleted) Pump(0.1);
        Pump(5);
        bool ok = task.Result && !player.WebMode && player.TestTitle.Contains("foto");
        Console.WriteLine($"{(ok ? "OK    " : "FALHOU")} \".\" na foto abre no player: achou {task.Result}, título \"{player.TestTitle}\"");
        player.Dispose();
        return ok ? 0 : 1;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool EnumChildWindows(IntPtr parent, EnumProc callback, IntPtr param);
    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)] private static extern int GetClassName(IntPtr hwnd, System.Text.StringBuilder name, int max);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    private delegate bool EnumProc(IntPtr hwnd, IntPtr param);

    // Janelas filhas (classe, visível) — para ver como as abas estão por baixo.
    private static string ChildWindows(IntPtr parent)
    {
        var list = new List<string>();
        EnumChildWindows(parent, (h, _) =>
        {
            var name = new System.Text.StringBuilder(256);
            GetClassName(h, name, 256);
            list.Add($"{name}{(IsWindowVisible(h) ? "" : " (oculta)")}");
            return true;
        }, IntPtr.Zero);
        return string.Join(", ", list);
    }

    // Janela que recebe o mouse do site visível (a mais funda, como o Windows escolhe sob o cursor).
    private static IntPtr InputWindow(IntPtr parent)
    {
        IntPtr legacy = IntPtr.Zero, widget = IntPtr.Zero;
        EnumChildWindows(parent, (h, _) =>
        {
            var name = new System.Text.StringBuilder(256);
            GetClassName(h, name, 256);
            if (IsWindowVisible(h) && name.ToString() == "Chrome_RenderWidgetHostHWND") legacy = h;
            if (IsWindowVisible(h) && name.ToString() == "Chrome_WidgetWin_1") widget = h;
            return true;
        }, IntPtr.Zero);
        return legacy != IntPtr.Zero ? legacy : widget;
    }

    // --newtabtest <pasta>: abas abertas pelos links da página. Botão do meio e Ctrl+clique abrem a aba atrás (a
    // atual continua na tela); target=_blank abre na frente. Depois de cada uma, a aba de antes continua respondendo
    // (clique, rolagem, animação, script). Usar com MKV_SETTINGS, MKV_WEB_PROFILE e MKV_LOG.
    public static int NewTabs(string folder)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        Directory.CreateDirectory(folder);
        // Imagem e vídeo servidos pelo app (para o "Abrir no player" do botão direito).
        string png = Path.Combine(folder, "foto.png"), clip = Path.Combine(Path.GetTempPath(), "mkvision-web-test.mp4");
        var photo = new byte[400 * 300 * 4];
        for (int i = 0; i < 400 * 300; i++) (photo[i * 4], photo[i * 4 + 1], photo[i * 4 + 2], photo[i * 4 + 3]) = ((byte)(i % 400 / 2), (byte)(i / 400), 160, 255);
        InterpTest.Png(png, photo, 400, 0, 0, 400, 300);
        MediaFactory.MFStartup(true).CheckError();
        PlayerTest.WriteClip(clip);
        string imageUrl = LocalMediaServer.Register(() => File.OpenRead(png), "image/png", ".png");
        string videoUrl = LocalMediaServer.Register(() => File.OpenRead(clip), "video/mp4", ".mp4");
        string pageFile = Path.Combine(folder, "links.html");
        File.WriteAllText(pageFile, "<!doctype html><meta charset=utf-8><title>Links de teste</title>" +
            "<body style='margin:0;height:4000px;font:20px Segoe UI;background:#223;color:#fff'>" +
            "<p><a id=plain href='https://example.com/?meio' style='display:inline-block;padding:20px;color:#9cf'>link comum</a></p>" +
            "<p><a id=blank href='https://example.com/?blank' target=_blank style='display:inline-block;padding:20px;color:#9cf'>link target=_blank</a></p>" +
            "<p><a id=ctrl href='https://example.com/?ctrl' style='display:inline-block;padding:20px;color:#9cf'>link para Ctrl+clique</a></p>" +
            "<p><button id=b style='font-size:20px;padding:20px' onclick='window.clicks=(window.clicks||0)+1'>contar</button> <span id=texto>texto comum</span></p>" +
            $"<p><img id=img src='{imageUrl}' width=200 height=150> <video id=v src='{videoUrl}' width=320 height=180></video></p>" +
            "<script>window.raf=0;window.gap=0;let last=performance.now();(function f(t){window.raf++;if(t-last>window.gap)window.gap=t-last;last=t;requestAnimationFrame(f)})(last);</script>");
        using var gpu = new Gpu();
        int failures = 0;
        void Check(string what, bool ok, string detail)
        {
            Console.WriteLine($"{(ok ? "OK    " : "FALHOU")} {what}: {detail}");
            if (!ok) failures++;
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
        var player = new Player(gpu, new object()) { IgnoreMouseLeave = true };
        bool osInput = Environment.GetEnvironmentVariable("MKV_OSINPUT") == "1";
        Console.WriteLine(osInput ? "cliques pelas mensagens do Windows" : "cliques pelo motor (DevTools)");
        T Await<T>(Task<T> task, double seconds, T fallback)
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            while (!task.IsCompleted && clock.Elapsed.TotalSeconds < seconds) Pump(0.02);
            return task.IsCompletedSuccessfully ? task.Result : fallback;
        }
        string Eval(string js)
        {
            string raw = Await(player.TestWebEval(js), 3, "(sem resposta)");
            try { return System.Text.Json.JsonSerializer.Deserialize<string>(raw) ?? raw; } catch { return raw; }
        }
        void Cdp(string method, object args) => Await(player.TestCdp(method, System.Text.Json.JsonSerializer.Serialize(args)), 3, "");
        // Clique como o de um mouse de verdade (o motor recebe o botão e as teclas): 0 = esquerdo, 1 = meio.
        void Click(string id, string button, int modifiers = 0)
        {
            var p = Eval($"(() => {{ const r = document.getElementById('{id}').getBoundingClientRect(); return Math.round(r.left + r.width / 2) + ',' + Math.round(r.top + r.height / 2); }})()").Split(',');
            if (p.Length != 2 || !int.TryParse(p[0], out int x) || !int.TryParse(p[1], out int y)) return;
            if (osInput && modifiers == 0)
            {
                // Pelas mensagens do Windows, na janela do site (o caminho de um mouse de verdade).
                double dpr = double.TryParse(Eval("String(devicePixelRatio)"), System.Globalization.CultureInfo.InvariantCulture, out double d) ? d : 1;
                var target = InputWindow(player.Hwnd);
                int px = (int)(x * dpr), py = (int)(y * dpr);
                IntPtr at = (IntPtr)((py << 16) | (px & 0xffff));
                (uint down, uint up, int flag) = button == "middle" ? (0x0207u, 0x0208u, 0x10) : (0x0201u, 0x0202u, 0x1);
                PlayerNative.PostMessage(target, PlayerNative.WM_MOUSEMOVE, IntPtr.Zero, at);
                Pump(0.05);
                PlayerNative.PostMessage(target, down, flag, at);
                Pump(0.08);
                PlayerNative.PostMessage(target, up, IntPtr.Zero, at);
                return;
            }
            Cdp("Input.dispatchMouseEvent", new { type = "mouseMoved", x, y, modifiers });
            Cdp("Input.dispatchMouseEvent", new { type = "mousePressed", x, y, button, buttons = button == "middle" ? 4 : 1, clickCount = 1, modifiers });
            Cdp("Input.dispatchMouseEvent", new { type = "mouseReleased", x, y, button, buttons = 0, clickCount = 1, modifiers });
        }
        string Tabs() => string.Join(" | ", player.TestTabs.Select(t => $"{(t.active ? "*" : "")}{(t.url.StartsWith("file:") ? "links" : t.url.Length > 0 ? new Uri(t.url).PathAndQuery : "(não criada)")}"));
        // A aba ativa responde? Animação (quadros em 1 s), clique no botão, rolagem e tempo de resposta de um script.
        void Alive(string when)
        {
            // Como você faria: volta para a aba dos links, se a tela não está nela.
            var now = player.TestTabs;
            if (!now.First(t => t.active).url.StartsWith("file:"))
            {
                player.TestSelectTab(Array.FindIndex(now, t => t.url.StartsWith("file:")));
                Pump(1.5);
            }
            var clock = System.Diagnostics.Stopwatch.StartNew();
            string title = Eval("document.title");
            double ms = clock.Elapsed.TotalMilliseconds;
            int raf0 = int.TryParse(Eval("String(window.raf)"), out int r0) ? r0 : -1;
            Pump(1);
            int raf1 = int.TryParse(Eval("String(window.raf)"), out int r1) ? r1 : -1;
            int clicks0 = int.TryParse(Eval("String(window.clicks||0)"), out int c0) ? c0 : -1;
            Eval("window.scrollTo(0, 0)");
            Click("b", "left");
            Pump(0.3);
            int clicks1 = int.TryParse(Eval("String(window.clicks||0)"), out int c1) ? c1 : -1;
            Cdp("Input.dispatchMouseEvent", new { type = "mouseWheel", x = 200, y = 200, deltaX = 0, deltaY = 400 });
            Pump(0.6);
            string scroll = Eval("String(Math.round(scrollY))");
            Eval("window.scrollTo(0, 0)");
            bool ok = title == "Links de teste" && raf1 - raf0 >= 20 && clicks1 == clicks0 + 1 && scroll != "0";
            Check($"aba de antes responde ({when})", ok,
                $"título \"{title}\" em {ms:0} ms, {raf1 - raf0} quadros/s, clique {clicks0}→{clicks1}, rolagem {scroll} px");
        }

        player.TestSetSessionTabs("https://example.com/");
        player.ShowBrowser();
        var start = System.Diagnostics.Stopwatch.StartNew();
        while (!player.WebReady && start.Elapsed.TotalSeconds < 40) Pump(0.1);
        Pump(2);
        player.TestWebNavigate(new Uri(pageFile).AbsoluteUri);
        start.Restart();
        while (player.TestWebTitle != "Links de teste" && start.Elapsed.TotalSeconds < 15) Pump(0.1);
        Pump(1);
        Console.WriteLine($"janelas: {ChildWindows(player.Hwnd)}");
        Alive("no começo");

        // Botão do meio: aba nova atrás, já carregando; a de agora continua na tela.
        int count = player.TestTabCount;
        Eval("window.gap = 0");
        Click("plain", "middle");
        Pump(4);
        Console.WriteLine($"maior parada da página enquanto a aba abria: {Eval("String(Math.round(window.gap))")} ms");
        Check("botão do meio abriu uma aba", player.TestTabCount == count + 1, Tabs());
        Check("botão do meio: continua na aba de antes", player.TestTabs.First(t => t.active).url.StartsWith("file:"), Tabs());
        Check("botão do meio: a aba nova carregou atrás", player.TestTabs.Any(t => !t.active && t.url.Contains("?meio")), Tabs());
        Console.WriteLine($"janelas: {ChildWindows(player.Hwnd)}");
        WindowShot(player.Hwnd, Path.Combine(folder, "meio.png"));
        Alive("depois do botão do meio");

        // Ctrl+clique: igual ao botão do meio (como no Chrome).
        count = player.TestTabCount;
        Click("ctrl", "left", modifiers: 2);
        Pump(4);
        Check("Ctrl+clique abriu uma aba atrás", player.TestTabCount == count + 1 && player.TestTabs.First(t => t.active).url.StartsWith("file:"), Tabs());
        Alive("depois do Ctrl+clique");

        // target=_blank: abre na frente; voltando, a de antes responde.
        count = player.TestTabCount;
        Click("blank", "left");
        Pump(4);
        var tabs = player.TestTabs;
        Check("target=_blank abriu na frente", player.TestTabCount == count + 1 && tabs.First(t => t.active).url.Contains("?blank"), Tabs());
        WindowShot(player.Hwnd, Path.Combine(folder, "blank.png"));
        player.TestSelectTab(Array.FindIndex(tabs, t => t.url.StartsWith("file:")));
        Pump(1.5);
        Console.WriteLine($"janelas: {ChildWindows(player.Hwnd)}; visíveis: {string.Join(",", player.TestTabVisible)}; tamanhos: {string.Join(" ", player.TestTabBounds.Select(b => $"{b.X},{b.Y},{b.Width}x{b.Height}"))}");
        WindowShot(player.Hwnd, Path.Combine(folder, "volta.png"));
        Alive("voltando depois do target=_blank");

        // Fechar a aba da tela (Ctrl+W, X): a do lado aparece, com o endereço dela, e responde.
        Click("blank", "left");
        Pump(4);
        count = player.TestTabCount;
        player.TestCloseTab();
        Pump(1.5);
        tabs = player.TestTabs;
        int active = Array.FindIndex(tabs, t => t.active);
        Check("fechar a aba da tela: a do lado aparece", player.TestTabCount == count - 1 && active >= 0 && player.TestTabVisible[active],
            $"{Tabs()}; visíveis: {string.Join(",", player.TestTabVisible)}");
        Check("fechar a aba da tela: endereço da que apareceu", player.TestAddressText == Uri.UnescapeDataString(tabs[active].url), $"{player.TestAddressText} (aba: {tabs[active].url})");
        WindowShot(player.Hwnd, Path.Combine(folder, "fechou.png"));

        // Botão direito: "Abrir no player" na imagem e no vídeo (o mesmo que o "."); no texto, o menu de sempre.
        player.TestSelectTab(Array.FindIndex(player.TestTabs, t => t.url.StartsWith("file:")));
        Pump(1.5);
        player.TestSuppressMenu = true;
        void RightClick(string id)
        {
            player.TestMenuLabels = [];
            var p = Eval($"(() => {{ const e = document.getElementById('{id}'); e.scrollIntoView({{ block: 'center' }}); const r = e.getBoundingClientRect(); return Math.round(r.left + r.width / 2) + ',' + Math.round(r.top + r.height / 2); }})()").Split(',');
            if (p.Length != 2 || !int.TryParse(p[0], out int x) || !int.TryParse(p[1], out int y)) return;
            Cdp("Input.dispatchMouseEvent", new { type = "mouseMoved", x, y });
            Cdp("Input.dispatchMouseEvent", new { type = "mousePressed", x, y, button = "right", buttons = 2, clickCount = 1 });
            Cdp("Input.dispatchMouseEvent", new { type = "mouseReleased", x, y, button = "right", buttons = 0, clickCount = 1 });
            Pump(1.2);
        }
        RightClick("texto");
        Check("botão direito no texto: menu normal", player.TestMenuLabels.Length > 0 && !player.TestMenuLabels.Contains("Abrir no player"), string.Join(" · ", player.TestMenuLabels));
        RightClick("img");
        Check("botão direito na imagem: \"Abrir no player\" no topo", player.TestMenuLabels.FirstOrDefault() == "Abrir no player", string.Join(" · ", player.TestMenuLabels));
        player.TestPickMenuPlayer();
        bool Wait(Func<bool> condition, double seconds)
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            while (!condition() && clock.Elapsed.TotalSeconds < seconds) Pump(0.1);
            return condition();
        }
        Check("imagem abriu no player", Wait(() => !player.WebMode && player.TestTitle.Contains("foto"), 10), $"modo {(player.WebMode ? "navegador" : "player")}, título \"{player.TestTitle}\"");
        player.ShowBrowser();
        Pump(1.5);
        RightClick("v");
        Check("botão direito no vídeo: \"Abrir no player\" no topo", player.TestMenuLabels.FirstOrDefault() == "Abrir no player", string.Join(" · ", player.TestMenuLabels));
        player.TestPickMenuPlayer();
        Check("vídeo abriu e toca no player", Wait(() => !player.WebMode && player.State.fileTime > 0.3, 20), $"modo {(player.WebMode ? "navegador" : "player")}, vídeo em {player.State.fileTime:0.00} s — {player.TestTitle}");
        player.Dispose();
        Console.WriteLine(failures == 0 ? "tudo certo" : $"{failures} falha(s)");
        return failures == 0 ? 0 : 1;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool ClientToScreen(IntPtr hwnd, ref Native.POINT point);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hwnd);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool IsZoomed(IntPtr hwnd);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern IntPtr SendMessageW(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    // Foto do que está na tela no lugar da janela (para as janelinhas, que ficam por cima de tudo).
    private static void ScreenShot(IntPtr hwnd, string path)
    {
        if (hwnd == IntPtr.Zero) return;
        PlayerNative.GetWindowRect(hwnd, out var r);
        int w = Math.Max(1, r.Width), h = Math.Max(1, r.Height);
        var screen = GetDC(IntPtr.Zero);
        var dc = CreateCompatibleDC(screen);
        var info = new BITMAPINFOHEADER { biSize = 40, biWidth = w, biHeight = -h, biPlanes = 1, biBitCount = 32 };
        var bitmap = CreateDIBSection(dc, ref info, 0, out var bits, IntPtr.Zero, 0);
        var old = SelectObject(dc, bitmap);
        BitBlt(dc, 0, 0, w, h, screen, r.Left, r.Top, 0x00CC0020);
        var pixels = new byte[w * h * 4];
        System.Runtime.InteropServices.Marshal.Copy(bits, pixels, 0, pixels.Length);
        SelectObject(dc, old);
        DeleteObject(bitmap);
        DeleteDC(dc);
        ReleaseDC(IntPtr.Zero, screen);
        InterpTest.Png(path, pixels, w, 0, 0, w, h);
    }
    [System.Runtime.InteropServices.DllImport("gdi32.dll")] private static extern bool BitBlt(IntPtr dest, int x, int y, int w, int h, IntPtr src, int sx, int sy, uint rop);

    // Foto da janela inteira, com a moldura (o que o Windows mostra).
    private static void FullWindowShot(IntPtr hwnd, string path)
    {
        PlayerNative.GetWindowRect(hwnd, out var r);
        int w = r.Width, h = r.Height;
        var screen = GetDC(IntPtr.Zero);
        var dc = CreateCompatibleDC(screen);
        var info = new BITMAPINFOHEADER { biSize = 40, biWidth = w, biHeight = -h, biPlanes = 1, biBitCount = 32 };
        var bitmap = CreateDIBSection(dc, ref info, 0, out var bits, IntPtr.Zero, 0);
        var old = SelectObject(dc, bitmap);
        PrintWindow(hwnd, dc, 2); // a janela toda, com o conteúdo DirectX/WebView
        var pixels = new byte[w * h * 4];
        System.Runtime.InteropServices.Marshal.Copy(bits, pixels, 0, pixels.Length);
        SelectObject(dc, old);
        DeleteObject(bitmap);
        DeleteDC(dc);
        ReleaseDC(IntPtr.Zero, screen);
        InterpTest.Png(path, pixels, w, 0, 0, w, h);
    }

    // --webframetest <pasta>: no navegador a janela não tem barra de título (as abas são o topo): o espaço vazio da
    // faixa arrasta, os botões minimizar/maximizar/fechar respondem, a borda de cima redimensiona, maximizada as abas não
    // ficam cortadas; no player e em tela cheia a moldura volta ao normal. Usar com MKV_SETTINGS, MKV_WEB_PROFILE e MKV_LOG.
    public static int Frame(string folder)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        Directory.CreateDirectory(folder);
        using var gpu = new Gpu();
        int failures = 0;
        void Check(string what, bool ok, string detail)
        {
            Console.WriteLine($"{(ok ? "OK    " : "FALHOU")} {what}: {detail}");
            if (!ok) failures++;
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
        var player = new Player(gpu, new object()) { IgnoreMouseLeave = true };
        IntPtr hwnd = player.Hwnd;
        int U(int v) => player.UiScaled(v);
        // Quanto a área do app começa abaixo do topo da janela (0 = sem barra de título).
        int Caption()
        {
            PlayerNative.GetWindowRect(hwnd, out var w);
            var p = new Native.POINT();
            ClientToScreen(hwnd, ref p);
            return p.Y - w.Top;
        }
        int Hit(int x, int y)
        {
            var p = new Native.POINT { X = x, Y = y };
            ClientToScreen(hwnd, ref p);
            return (int)SendMessageW(hwnd, 0x0084, IntPtr.Zero, (IntPtr)((p.Y << 16) | (p.X & 0xffff)));
        }
        void Press(int hit)
        {
            PlayerNative.PostMessage(hwnd, 0x00A1, (IntPtr)hit, IntPtr.Zero);
            PlayerNative.PostMessage(hwnd, 0x00A2, (IntPtr)hit, IntPtr.Zero);
            Pump(1);
        }
        int captionPlayer = Caption();
        player.TestSetSessionTabs("https://example.com/");
        player.ShowBrowser();
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (!player.WebReady && clock.Elapsed.TotalSeconds < 40) Pump(0.1);
        Pump(2);
        Check("player com barra de título", captionPlayer > 20, $"{captionPlayer} px");
        Check("navegador sem barra de título", Caption() == 0, $"{Caption()} px");
        FullWindowShot(hwnd, Path.Combine(folder, "janela.png"));
        var (min, max, close, strip) = player.TestCaptionPoints;
        Native.GetClientRect(hwnd, out var client);
        string Hits() => $"vazio {Hit(client.Width / 2, strip / 2)}, aba {Hit(U(60), strip / 2)}, mín {Hit(min, strip / 2)}, máx {Hit(max, strip / 2)}, fechar {Hit(close, strip / 2)}, borda {Hit(client.Width / 2, 2)}, endereço {Hit(client.Width / 2, strip + 20)}";
        Check("o que cada ponto faz", Hit(client.Width / 2, strip / 2) == 2 && Hit(U(60), strip / 2) == 1 && Hit(min, strip / 2) == 8 && Hit(max, strip / 2) == 9 &&
            Hit(close, strip / 2) == 20 && Hit(client.Width / 2, 2) == 12 && Hit(client.Width / 2, strip + 20) == 1, Hits());
        // Maximizar pelo botão: as abas ficam inteiras na tela.
        Press(9);
        var p0 = new Native.POINT();
        ClientToScreen(hwnd, ref p0);
        var info = new PlayerNative.MONITORINFO { cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<PlayerNative.MONITORINFO>() };
        PlayerNative.GetMonitorInfo(PlayerNative.MonitorFromWindow(hwnd, PlayerNative.MONITOR_DEFAULTTONEAREST), ref info);
        Check("maximizar", IsZoomed(hwnd), $"maximizada: {IsZoomed(hwnd)}");
        Check("maximizada: abas no topo da tela, inteiras", p0.Y == info.rcWork.Top, $"área do app começa em {p0.Y}, tela em {info.rcWork.Top}");
        Native.GetClientRect(hwnd, out var zoomed);
        Check("maximizada: o topo arrasta (não redimensiona)", Hit(zoomed.Width / 2, 2) == 2, $"{Hit(zoomed.Width / 2, 2)}");
        FullWindowShot(hwnd, Path.Combine(folder, "maximizada.png"));
        Press(9);
        Check("restaurar", !IsZoomed(hwnd) && Caption() == 0, $"maximizada: {IsZoomed(hwnd)}, barra {Caption()} px");
        Press(8);
        Check("minimizar", IsIconic(hwnd), $"minimizada: {IsIconic(hwnd)}");
        Native.ShowWindow(hwnd, 9);
        Pump(1);
        // Player: barra de título de volta; navegador de novo: some.
        PlayerNative.PostMessage(hwnd, PlayerNative.WM_KEYDOWN, 0x71, IntPtr.Zero);
        Pump(1);
        Check("F2 → player: barra de título volta", !player.WebMode && Caption() == captionPlayer, $"{Caption()} px");
        player.ShowBrowser();
        Pump(1);
        Check("F2 → navegador: some de novo", player.WebMode && Caption() == 0, $"{Caption()} px");
        // Tela cheia (vídeo do site) e volta.
        player.TestFullscreen();
        Pump(1);
        Native.GetClientRect(hwnd, out var full);
        Check("tela cheia: sem moldura nenhuma", Caption() == 0 && Hit(full.Width / 2, 2) == 1, $"barra {Caption()} px, topo {Hit(full.Width / 2, 2)}");
        player.TestFullscreen();
        Pump(1);
        Native.GetClientRect(hwnd, out client);
        Check("saindo da tela cheia: abas no topo de novo", Caption() == 0 && Hit(client.Width / 2, player.TestCaptionPoints.strip / 2) == 2, $"barra {Caption()} px");
        FullWindowShot(hwnd, Path.Combine(folder, "depois.png"));
        player.Dispose();
        Console.WriteLine(failures == 0 ? "tudo certo" : $"{failures} falha(s)");
        return failures == 0 ? 0 : 1;
    }

    // --webtabsqoltest <pasta>: faixa das abas como no Edge — muitas abas rolam (a ativa à vista), arrastar muda a ordem,
    // alto-falante na aba que toca (clicar silencia), dica com o título ao parar o mouse, abas paradas dormem e acordam.
    // Usar com MKV_SETTINGS, MKV_WEB_PROFILE e MKV_LOG.
    public static int TabsQol(string folder)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        Directory.CreateDirectory(folder);
        string clip = Path.Combine(Path.GetTempPath(), "mkvision-web-test.mp4");
        MediaFactory.MFStartup(true).CheckError();
        PlayerTest.WriteClip(clip);
        string videoUrl = LocalMediaServer.Register(() => File.OpenRead(clip), "video/mp4", ".mp4");
        string pageFile = Path.Combine(folder, "som.html");
        File.WriteAllText(pageFile, "<!doctype html><meta charset=utf-8><title>Página com som</title><body style='background:#223;color:#fff'>" +
            $"<button id=b style='font-size:30px;padding:30px' onclick=\"const v=document.getElementById('v');v.loop=true;v.muted=false;v.play()\">tocar</button>" +
            $"<video id=v src='{videoUrl}' width=320 height=180></video>");
        using var gpu = new Gpu();
        int failures = 0;
        void Check(string what, bool ok, string detail)
        {
            Console.WriteLine($"{(ok ? "OK    " : "FALHOU")} {what}: {detail}");
            if (!ok) failures++;
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
        bool Wait(Func<bool> condition, double seconds)
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            while (!condition() && clock.Elapsed.TotalSeconds < seconds) Pump(0.1);
            return condition();
        }
        var player = new Player(gpu, new object()) { IgnoreMouseLeave = true };
        string Eval(string js)
        {
            var task = player.TestWebEval(js);
            var clock = System.Diagnostics.Stopwatch.StartNew();
            while (!task.IsCompleted && clock.Elapsed.TotalSeconds < 4) Pump(0.02);
            string raw = task.IsCompletedSuccessfully ? task.Result : "(sem resposta)";
            try { return System.Text.Json.JsonSerializer.Deserialize<string>(raw) ?? raw; } catch { return raw; }
        }
        void Cdp(string method, object args)
        {
            var task = player.TestCdp(method, System.Text.Json.JsonSerializer.Serialize(args));
            var clock = System.Diagnostics.Stopwatch.StartNew();
            while (!task.IsCompleted && clock.Elapsed.TotalSeconds < 3) Pump(0.02);
        }
        void Mouse(uint message, int x, int y) => PlayerNative.PostMessage(player.Hwnd, message, IntPtr.Zero, (IntPtr)((y << 16) | (x & 0xffff)));
        string Order() => string.Join(" ", player.TestTabs.Select(t => (t.active ? "*" : "") + (t.url.Contains("?n=") ? t.url[(t.url.IndexOf("?n=") + 3)..] : t.url.StartsWith("file:") ? "som" : "?")));

        player.TestSetSessionTabs(new Uri(pageFile).AbsoluteUri + "?n=0");
        player.ShowBrowser();
        Wait(() => player.WebReady, 40);
        Pump(1);
        player.TestWebNavigate(new Uri(pageFile).AbsoluteUri);
        Wait(() => player.TestWebTitle == "Página com som", 10);

        // Som: tocar (com um clique de verdade na página) → alto-falante; silenciar pela aba.
        var p = Eval("(() => { const r = document.getElementById('b').getBoundingClientRect(); return Math.round(r.left + r.width / 2) + ',' + Math.round(r.top + r.height / 2); })()").Split(',');
        int bx = int.Parse(p[0]), by = int.Parse(p[1]);
        Cdp("Input.dispatchMouseEvent", new { type = "mousePressed", x = bx, y = by, button = "left", buttons = 1, clickCount = 1 });
        Cdp("Input.dispatchMouseEvent", new { type = "mouseReleased", x = bx, y = by, button = "left", buttons = 0, clickCount = 1 });
        Check("aba tocando: alto-falante", Wait(() => player.TestTabStates[0].audible, 10), $"{player.TestTabStates[0]}");
        Pump(0.5);
        WindowShot(player.Hwnd, Path.Combine(folder, "som.png"));
        player.TestToggleMute(0);
        Pump(0.5);
        Check("clicar no alto-falante silencia", player.TestTabStates[0].muted && player.TestTabs[0].muted, $"{player.TestTabStates[0]}");
        WindowShot(player.Hwnd, Path.Combine(folder, "mudo.png"));
        player.TestToggleMute(0);
        Pump(0.5);
        Check("de novo: volta o som", !player.TestTabStates[0].muted && !player.TestTabs[0].muted, $"{player.TestTabStates[0]}");

        // Dica: parar o mouse sobre a aba.
        var (tx, ty) = player.TestTabPoint(0);
        Mouse(PlayerNative.WM_MOUSEMOVE, tx, ty);
        Check("parar o mouse na aba: dica com o título", Wait(() => player.TestTipVisible, 3), $"visível: {player.TestTipVisible}");
        Pump(0.3);
        ScreenShot(player.TestTipHandle, Path.Combine(folder, "dica.png"));
        Mouse(PlayerNative.WM_MOUSEMOVE, tx, ty + 200);
        Pump(0.4);
        Check("saiu da aba: a dica some", !player.TestTipVisible, $"visível: {player.TestTipVisible}");

        // Muitas abas: encolhem e rolam; a ativa fica à vista.
        for (int i = 1; i <= 29; i++) player.TestOpenBackground($"{new Uri(pageFile).AbsoluteUri}?n={i}");
        Pump(6);
        var layout = player.TestTabLayout;
        Check("30 abas: rolam (com setas)", layout.overflow && layout.visible < 30, $"à vista {layout.visible} de {player.TestTabCount}, a partir da {layout.first}");
        WindowShot(player.Hwnd, Path.Combine(folder, "muitas.png"));
        int first = layout.first;
        player.TestWheel(400, 10, -120);
        Pump(0.3);
        Check("roda do mouse na faixa rola as abas", player.TestTabLayout.first == first + 1, $"{first} → {player.TestTabLayout.first}");
        player.TestSelectTab(29);
        Pump(1);
        layout = player.TestTabLayout;
        Check("ir para a última aba: ela aparece", 29 >= layout.first && 29 < layout.first + layout.visible, $"à vista {layout.first}–{layout.first + layout.visible - 1}");
        WindowShot(player.Hwnd, Path.Combine(folder, "ultima.png"));

        // Arrastar: a aba da tela (a última) vai para duas posições antes.
        string before = Order();
        var (ax, ay) = player.TestTabPoint(29);
        var (dx, _) = player.TestTabPoint(27);
        Mouse(PlayerNative.WM_MOUSEMOVE, ax, ay);
        Mouse(PlayerNative.WM_LBUTTONDOWN, ax, ay);
        for (int i = 1; i <= 8; i++) Mouse(PlayerNative.WM_MOUSEMOVE, ax + (dx - ax) * i / 8, ay);
        Mouse(PlayerNative.WM_LBUTTONUP, dx, ay);
        Pump(0.8);
        var tabs = player.TestTabs;
        Check("abas abertas atrás ficam na ordem em que abriu", before == "som 1 2 3 4 5 6 7 8 9 10 11 12 13 14 15 16 17 18 19 20 21 22 23 24 25 26 27 28 *29", before);
        Check("arrastar a aba muda a ordem", tabs[27].active && tabs[27].url.Contains("?n=29"), $"{before} → {Order()}");

        // Dormir: as de fundo dormem; ao voltar para uma, ela acorda e responde.
        player.TestSleepNow(1);
        Check("abas paradas dormem", Wait(() => player.TestTabStates.Count(t => t.sleeping) >= 20, 15), $"{player.TestTabStates.Count(t => t.sleeping)} dormindo de {player.TestTabCount}");
        Check("a da tela e a que toca som não dormem", !player.TestTabStates[27].sleeping && !player.TestTabStates[0].sleeping, $"{player.TestTabStates[27]} / {player.TestTabStates[0]}");
        Pump(0.5);
        WindowShot(player.Hwnd, Path.Combine(folder, "dormindo.png"));
        string sleeper = new Uri(player.TestTabs[5].url).Query;
        player.TestSelectTab(5);
        Pump(1.5);
        Check("voltar para uma aba dormindo: acorda e responde", !player.TestTabStates[5].sleeping && Eval("location.search") == sleeper, $"{player.TestTabStates[5]}, {Eval("location.search")}");
        player.Dispose();
        Console.WriteLine(failures == 0 ? "tudo certo" : $"{failures} falha(s)");
        return failures == 0 ? 0 : 1;
    }

    // --webaddresstest <pasta>: barra de endereço — sugestões só dos favoritos (sem acento, ↑/↓, Enter, Esc; nada que não
    // seja favorito) e o cadeado (https seguro, http não seguro, página local), com uma permissão de site mudada e lida.
    // Usar com MKV_SETTINGS, MKV_WEB_PROFILE e MKV_LOG.
    public static int Address(string folder)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        Directory.CreateDirectory(folder);
        using var gpu = new Gpu();
        int failures = 0;
        void Check(string what, bool ok, string detail)
        {
            Console.WriteLine($"{(ok ? "OK    " : "FALHOU")} {what}: {detail}");
            if (!ok) failures++;
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
        bool Wait(Func<bool> condition, double seconds)
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            while (!condition() && clock.Elapsed.TotalSeconds < seconds) Pump(0.1);
            return condition();
        }
        var player = new Player(gpu, new object()) { IgnoreMouseLeave = true };
        var books = player.TestBookmarks;
        books.Children!.Clear();
        var videos = BookmarkNode.Folder("Vídeos");
        videos.Children!.Add(BookmarkNode.Link("YouTube", "https://www.youtube.com/"));
        videos.Children.Add(BookmarkNode.Link("Example Vídeo", "https://example.com/video"));
        books.Children.Add(videos);
        books.Children.Add(BookmarkNode.Link("Wikipédia", "https://pt.wikipedia.org/"));
        books.Children.Add(BookmarkNode.Link("Instagram", "https://www.instagram.com/"));
        books.Children.Add(BookmarkNode.Link("IANA", "https://www.iana.org/domains"));
        player.TestSetSessionTabs("https://example.com/");
        player.ShowBrowser();
        Wait(() => player.WebReady, 40);
        Pump(2);
        string Suggest(string text) { player.TestTypeAddress(text); Pump(0.3); return string.Join(", ", player.TestSuggestions); }
        Check("\"you\" → YouTube (de dentro da pasta)", Suggest("you") == "YouTube" && player.TestSuggestVisible, $"[{Suggest("you")}], visível {player.TestSuggestVisible}");
        Check("sem acento acha com acento (\"wikipe\")", Suggest("wikipe") == "Wikipédia", $"[{Suggest("wikipe")}]");
        Check("pelo endereço (\"iana.org/dom\")", Suggest("iana.org/dom") == "IANA", $"[{Suggest("iana.org/dom")}]");
        Check("duas palavras (\"example video\")", Suggest("example video") == "Example Vídeo", $"[{Suggest("example video")}]");
        Check("o que não é favorito não aparece (\"google\")", Suggest("google") == "" && !player.TestSuggestVisible, $"[{Suggest("google")}], visível {player.TestSuggestVisible}");
        Check("nome da pasta não é sugestão (\"vídeos\" só acha o favorito)", Suggest("vídeo") == "Example Vídeo", $"[{Suggest("vídeo")}]");
        Suggest("i");
        player.TestAddressKey(0x28);
        Pump(0.3);
        ScreenShot(player.TestSuggestHandle, Path.Combine(folder, "sugestoes.png"));
        // ↓ escolhe (o endereço aparece na barra), Esc volta ao digitado, ↓ + Enter abre.
        Suggest("ia");
        player.TestAddressKey(0x28);
        Pump(0.2);
        Check("↓: o endereço da sugestão aparece na barra", player.TestAddressText == "https://www.iana.org/domains", player.TestAddressText);
        player.TestAddressKey(0x1B);
        Pump(0.2);
        Check("Esc: fecha e volta ao digitado", player.TestAddressText == "ia" && !player.TestSuggestVisible, $"{player.TestAddressText}, visível {player.TestSuggestVisible}");
        Suggest("iana");
        player.TestAddressKey(0x28);
        player.TestAddressKey(0x0D);
        Check("↓ + Enter: abre o favorito", Wait(() => player.TestWebTitle.Contains("Domains", StringComparison.OrdinalIgnoreCase) || player.TestTabs.First(t => t.active).url.Contains("iana.org/domains"), 15),
            $"{player.TestTabs.First(t => t.active).url}, sugestões visíveis {player.TestSuggestVisible}");
        // Cadeado.
        Check("https: conexão segura", player.TestSecurity == 0, $"{player.TestSecurity}");
        player.TestWebNavigate("http://neverssl.com/");
        Wait(() => player.TestTabs.First(t => t.active).url.StartsWith("http://") && player.TestTabs.First(t => t.active).url.Contains("neverssl"), 30);
        Pump(1);
        Check("http: não seguro", player.TestSecurity == 1, $"{player.TestSecurity} ({player.TestTabs.First(t => t.active).url})");
        WindowShot(player.Hwnd, Path.Combine(folder, "nao-seguro.png"));
        player.TestOpenSettings();
        Pump(2);
        Check("página do app: nem cadeado nem aviso", player.TestSecurity == 2, $"{player.TestSecurity}");
        player.TestWebNavigate("https://example.com/");
        Wait(() => player.TestTabs.First(t => t.active).url.StartsWith("https://example.com"), 15);
        var task = player.TestSitePermission(Microsoft.Web.WebView2.Core.CoreWebView2PermissionKind.Camera, Microsoft.Web.WebView2.Core.CoreWebView2PermissionState.Deny);
        Wait(() => task.IsCompleted, 5);
        Check("permissão do site: bloquear câmera", task.IsCompletedSuccessfully && task.Result == "Deny", task.IsCompletedSuccessfully ? task.Result : task.Exception?.GetBaseException().Message ?? "sem resposta");
        task = player.TestSitePermission(Microsoft.Web.WebView2.Core.CoreWebView2PermissionKind.Camera, Microsoft.Web.WebView2.Core.CoreWebView2PermissionState.Default);
        Wait(() => task.IsCompleted, 5);
        Check("permissão do site: volta a perguntar", task.IsCompletedSuccessfully && task.Result == "Default", task.IsCompletedSuccessfully ? task.Result : "?");
        player.Dispose();
        Console.WriteLine(failures == 0 ? "tudo certo" : $"{failures} falha(s)");
        return failures == 0 ? 0 : 1;
    }

    // --webpagetest <pasta>: traduzir (e voltar ao original), zoom lembrado por site, F11, "Pesquisar no Google" e
    // "Traduzir" no botão direito, tema dos sites, escurecer os sites claros e Ctrl+J. Usar com MKV_SETTINGS,
    // MKV_WEB_PROFILE e MKV_LOG.
    public static int Page(string folder)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        Directory.CreateDirectory(folder);
        string pageFile = Path.Combine(folder, "clara.html");
        File.WriteAllText(pageFile, "<!doctype html><html lang=pt-BR><meta charset=utf-8><title>Página clara</title><body style='margin:0;background:#fff;color:#000;font:20px Segoe UI'>" +
            "<p id=t style='padding:30px'>Um texto qualquer para selecionar</p><div style='height:2000px'></div>");
        using var gpu = new Gpu();
        int failures = 0;
        void Check(string what, bool ok, string detail)
        {
            Console.WriteLine($"{(ok ? "OK    " : "FALHOU")} {what}: {detail}");
            if (!ok) failures++;
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
        bool Wait(Func<bool> condition, double seconds)
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            while (!condition() && clock.Elapsed.TotalSeconds < seconds) Pump(0.1);
            return condition();
        }
        var player = new Player(gpu, new object()) { IgnoreMouseLeave = true };
        string Eval(string js)
        {
            var task = player.TestWebEval(js);
            var clock = System.Diagnostics.Stopwatch.StartNew();
            while (!task.IsCompleted && clock.Elapsed.TotalSeconds < 4) Pump(0.02);
            string raw = task.IsCompletedSuccessfully ? task.Result : "(sem resposta)";
            try { return System.Text.Json.JsonSerializer.Deserialize<string>(raw) ?? raw; } catch { return raw; }
        }
        void Cdp(string method, object args)
        {
            var task = player.TestCdp(method, System.Text.Json.JsonSerializer.Serialize(args));
            var clock = System.Diagnostics.Stopwatch.StartNew();
            while (!task.IsCompleted && clock.Elapsed.TotalSeconds < 3) Pump(0.02);
        }
        string Url() => player.TestTabs.First(t => t.active).url;
        void Go(string url, Func<string, bool> arrived) { player.TestWebNavigate(url); Wait(() => arrived(Url()), 20); Pump(1.5); }
        // Brilho do centro da página na tela (0 preto – 255 branco).
        int Brightness()
        {
            var (pixels, w, h) = ClientPixels(player.Hwnd);
            int i = ((h * 3 / 4) * w + w / 2) * 4;
            return (pixels[i] + pixels[i + 1] + pixels[i + 2]) / 3;
        }
        player.TestSetSessionTabs("https://example.com/");
        player.ShowBrowser();
        Wait(() => player.WebReady, 40);
        Pump(3);

        // Traduzir: example.com diz ser inglês.
        Check("página em inglês: botão de traduzir", Wait(() => player.TestShowTranslate, 5), $"{player.TestShowTranslate} ({Eval("document.documentElement.lang")})");
        player.TestTranslate();
        Check("traduzir abre a tradução na mesma aba", Wait(() => Url().Contains(".translate.goog"), 25), Url());
        Pump(2);
        WindowShot(player.Hwnd, Path.Combine(folder, "traduzida.png"));
        Check("traduzida: o botão continua (volta ao original)", player.TestShowTranslate, $"{player.TestShowTranslate}");
        player.TestTranslate();
        Check("de novo: volta ao original", Wait(() => Url().StartsWith("https://example.com"), 15), Url());
        Go(new Uri(pageFile).AbsoluteUri, u => u.StartsWith("file:"));
        Check("página em português: sem botão de traduzir", !player.TestShowTranslate, $"{player.TestShowTranslate}");

        // Zoom lembrado por site.
        Go("https://example.com/", u => u.StartsWith("https://example.com"));
        player.TestZoomStep(1);
        player.TestZoomStep(1);
        Pump(0.5);
        Check("Ctrl + duas vezes: 125%", Math.Abs(player.TestZoom - 1.25) < 0.01, $"{player.TestZoom * 100:0}%");
        WindowShot(player.Hwnd, Path.Combine(folder, "zoom.png"));
        Go("https://www.iana.org/", u => u.Contains("iana.org"));
        Check("outro site: 100%", Math.Abs(player.TestZoom - 1) < 0.01, $"{player.TestZoom * 100:0}%");
        Go("https://example.com/", u => u.StartsWith("https://example.com"));
        Check("de volta ao site: lembra 125%", Math.Abs(player.TestZoom - 1.25) < 0.01, $"{player.TestZoom * 100:0}% (guardados: {string.Join(", ", player.TestSavedZoom)})");
        player.TestZoomStep(0);
        Pump(0.5);
        Check("Ctrl 0: volta a 100% e esquece", Math.Abs(player.TestZoom - 1) < 0.01 && player.TestSavedZoom.Length == 0, $"{player.TestZoom * 100:0}% (guardados: {string.Join(", ", player.TestSavedZoom)})");

        // F11.
        int top = player.TestWebTop;
        player.TestToggleF11();
        Pump(1);
        Check("F11: tela cheia sem as faixas", player.TestF11 && player.State.fullscreen && player.TestWebTop == 0, $"F11 {player.TestF11}, tela cheia {player.State.fullscreen}, faixas {player.TestWebTop} px");
        player.TestPeek(true); // (o app olha o mouse de verdade 10x por segundo: confere já)
        Check("F11: mouse no topo mostra as faixas", player.TestWebTop == top, $"{player.TestWebTop} px");
        player.TestPeek(false);
        player.TestToggleF11();
        Pump(1);
        Check("F11 de novo: sai", !player.TestF11 && !player.State.fullscreen && player.TestWebTop == top, $"tela cheia {player.State.fullscreen}, faixas {player.TestWebTop} px");

        // Botão direito: texto selecionado e página.
        Go(new Uri(pageFile).AbsoluteUri, u => u.StartsWith("file:"));
        player.TestSuppressMenu = true;
        void Right(int x, int y)
        {
            player.TestMenuLabels = [];
            Cdp("Input.dispatchMouseEvent", new { type = "mousePressed", x, y, button = "right", buttons = 2, clickCount = 1 });
            Cdp("Input.dispatchMouseEvent", new { type = "mouseReleased", x, y, button = "right", buttons = 0, clickCount = 1 });
            Pump(1);
        }
        Eval("(() => { const r = document.createRange(); r.selectNodeContents(document.getElementById('t')); getSelection().removeAllRanges(); getSelection().addRange(r); })()");
        Right(60, 45);
        Check("texto selecionado: \"Pesquisar no Google por…\"", player.TestMenuLabels.FirstOrDefault()?.StartsWith("Pesquisar no Google por \"Um texto qualquer") == true, string.Join(" · ", player.TestMenuLabels.Take(3)));
        Eval("getSelection().removeAllRanges()");
        Go("https://example.com/", u => u.StartsWith("https://example.com"));
        Right(300, 600);
        Check("página: \"Traduzir para o português\"", player.TestMenuLabels.FirstOrDefault() == "Traduzir para o português", string.Join(" · ", player.TestMenuLabels.Take(3)));
        player.TestSuppressMenu = false;

        // Tema dos sites e escurecer os claros (pela página de configurações, como você faria).
        void Setting(string json)
        {
            player.TestOpenSettings();
            Pump(1.5);
            Eval($"window.chrome.webview.postMessage(Object.assign({{ type: 'settings-set' }}, {json}))");
            Pump(1);
        }
        Setting("{ colorScheme: 2 }");
        Go(new Uri(pageFile).AbsoluteUri, u => u.StartsWith("file:"));
        Check("tema dos sites: claro", Eval("String(matchMedia('(prefers-color-scheme: dark)').matches)") == "false", Eval("String(matchMedia('(prefers-color-scheme: dark)').matches)"));
        Setting("{ colorScheme: 1 }");
        Go(new Uri(pageFile).AbsoluteUri + "?2", u => u.EndsWith("?2"));
        Check("tema dos sites: escuro", Eval("String(matchMedia('(prefers-color-scheme: dark)').matches)") == "true", Eval("String(matchMedia('(prefers-color-scheme: dark)').matches)"));
        int light = Brightness();
        Setting("{ forceDark: true }");
        Go(new Uri(pageFile).AbsoluteUri + "?3", u => u.EndsWith("?3"));
        Pump(1);
        int dark = Brightness();
        WindowShot(player.Hwnd, Path.Combine(folder, "escurecida.png"));
        Check("escurecer os sites claros: a página branca fica escura", light > 200 && dark < 80, $"brilho {light} → {dark}");
        Setting("{ forceDark: false, colorScheme: 0 }");
        Go(new Uri(pageFile).AbsoluteUri + "?4", u => u.EndsWith("?4"));
        Pump(1);
        Check("desligar: volta ao normal", Brightness() > 200, $"brilho {Brightness()}");

        // Ctrl+J.
        player.TestDownloadsKey();
        Check("Ctrl+J: lista de downloads", Wait(() => player.TestDownloadsOpen, 3), $"aberta: {player.TestDownloadsOpen}");
        player.Dispose();
        Console.WriteLine(failures == 0 ? "tudo certo" : $"{failures} falha(s)");
        return failures == 0 ? 0 : 1;
    }

    // --carouseltest <pasta>: post com vários vídeos (como o carrossel do Instagram, aqui uma página local com dois vídeos
    // de duração e formato diferentes): o yt-dlp devolve os dois e o app fica com o que bate com o vídeo da tela; o
    // download baixa esse mesmo item (não o primeiro).
    public static int Carousel(string folder)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        Directory.CreateDirectory(folder);
        MediaFactory.MFStartup(true).CheckError();
        string a = Path.Combine(folder, "deitado-2s.mp4"), b = Path.Combine(folder, "em-pe-5s.mp4");
        PlayerTest.WriteBig(a, 640, 360, 30, 60);
        PlayerTest.WriteBig(b, 360, 640, 30, 150);
        string urlA = LocalMediaServer.Register(() => File.OpenRead(a), "video/mp4", ".mp4");
        string urlB = LocalMediaServer.Register(() => File.OpenRead(b), "video/mp4", ".mp4");
        byte[] html = System.Text.Encoding.UTF8.GetBytes($"<!doctype html><title>Post</title><video src='{urlA}'></video><video src='{urlB}'></video>");
        string page = LocalMediaServer.Register(() => new MemoryStream(html), "text/html; charset=utf-8", ".html");
        int failures = 0;
        void Check(string what, bool ok, string detail)
        {
            Console.WriteLine($"{(ok ? "OK    " : "FALHOU")} {what}: {detail}");
            if (!ok) failures++;
        }
        var tools = new VideoDownload();
        try
        {
            // 1) O yt-dlp devolve os itens do post (com o número de cada um) e baixa o item pedido, não o primeiro.
            var found = tools.DumpJsonItems(page, "b", null, null, CancellationToken.None).GetAwaiter().GetResult();
            var indexes = found.Select(j => System.Text.Json.JsonDocument.Parse(j).RootElement.GetProperty("playlist_index").GetInt32()).ToArray();
            Check("o post devolve os dois itens", indexes.SequenceEqual([1, 2]), string.Join(", ", indexes));
            string saved = Path.Combine(folder, "baixado.mp4");
            if (File.Exists(saved)) File.Delete(saved);
            string format = System.Text.Json.JsonDocument.Parse(found[1]).RootElement.GetProperty("format_id").GetString()!;
            tools.DownloadFormat(page, format, saved, null, null, CancellationToken.None, item: 2).GetAwaiter().GetResult();
            Check("o download baixa o item pedido (o 2º)", File.Exists(saved) && new FileInfo(saved).Length == new FileInfo(b).Length,
                File.Exists(saved) ? $"{new FileInfo(saved).Length} bytes (2º: {new FileInfo(b).Length}, 1º: {new FileInfo(a).Length})" : "nada baixado");
        }
        catch (Exception error) { Check("yt-dlp", false, error.Message); }
        // 2) A escolha, com itens como o Instagram devolve (duração e tamanho de cada vídeo do carrossel).
        string Item(int index, double duration, int w, int h) => $"{{\"playlist_index\":{index},\"duration\":{duration.ToString(System.Globalization.CultureInfo.InvariantCulture)},\"width\":{w},\"height\":{h}}}";
        string[] carousel = [Item(2, 12.4, 1080, 1080), Item(3, 31.0, 1080, 1920), Item(5, 30.2, 1920, 1080)];
        var pick = WebResolver.Choose(carousel, new MediaHint(30.9, 720, 1280));
        Check("vídeo da tela em pé, ~31 s: o item 3", pick.item == 3, $"item {pick.item}");
        pick = WebResolver.Choose(carousel, new MediaHint(30.0, 1280, 720));
        Check("deitado, ~30 s: o item 5 (a duração parecida não engana)", pick.item == 5, $"item {pick.item}");
        pick = WebResolver.Choose(carousel, new MediaHint(12.4, 640, 640));
        Check("quadrado, 12 s: o item 2", pick.item == 2, $"item {pick.item}");
        pick = WebResolver.Choose(carousel, default);
        Check("sem saber o vídeo da tela: o primeiro vídeo", pick.item == 2, $"item {pick.item}");
        pick = WebResolver.Choose([Item(1, 9, 720, 1280)], new MediaHint(30, 1280, 720));
        Check("post com um vídeo só: ele mesmo, sem número de item", pick.item == 0, $"item {pick.item}");

        // 3) O post inteiro no player: foto, vídeo, foto, vídeo (como o yt-dlp devolve um carrossel). Abre no vídeo da
        //    tela; Page Up/Down passa pelos quatro, na ordem, e volta a um vídeo já visto.
        string Png(string name, byte shade)
        {
            string path = Path.Combine(folder, name);
            var pixels = new byte[320 * 400 * 4];
            for (int i = 0; i < pixels.Length; i += 4) (pixels[i], pixels[i + 1], pixels[i + 2], pixels[i + 3]) = (shade, (byte)(255 - shade), 128, 255);
            InterpTest.Png(path, pixels, 320, 0, 0, 320, 400);
            return LocalMediaServer.Register(() => File.OpenRead(path), "image/png", ".png");
        }
        string photo1 = Png("foto1.png", 40), photo3 = Png("foto3.png", 200);
        string Json(object o) => System.Text.Json.JsonSerializer.Serialize(o);
        string[] lines =
        [
            Json(new { playlist_index = 1, title = "post", thumbnails = new[] { new { url = photo1 + "?p", width = 160, height = 200 }, new { url = photo1, width = 320, height = 400 } } }),
            Json(new { playlist_index = 2, title = "post", url = urlA, ext = "mp4", format_id = "a", duration = 2.0, width = 640, height = 360 }),
            Json(new { playlist_index = 3, title = "post", thumbnail = photo3 }),
            Json(new { playlist_index = 4, title = "post", url = urlB, ext = "mp4", format_id = "b", duration = 5.0, width = 360, height = 640 }),
        ];
        var media = WebResolver.FromPost(lines, page, "post", null, null, new MediaHint(5, 360, 640));
        Check("o post tem os 4 itens, na ordem", media.Post?.Count == 4 && media.Post[0].Image == photo1 && media.Post[1].Video?.VideoUrl == urlA && media.Post[2].Image == photo3 && media.Post[3].Video?.VideoUrl == urlB,
            string.Join(", ", media.Post?.Select(i => i.Video is not null ? $"vídeo {i.Index}" : $"foto {i.Index}") ?? []));
        Check("abre no vídeo da tela (o 4º item)", media.Item == 4 && media.VideoUrl == urlB, $"item {media.Item}");
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
        bool Wait(Func<bool> condition, double seconds)
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            while (!condition() && clock.Elapsed.TotalSeconds < seconds) Pump(0.1);
            return condition();
        }
        void Key(int vk) { PlayerNative.PostMessage(player.Hwnd, PlayerNative.WM_KEYDOWN, vk, IntPtr.Zero); Pump(0.2); }
        bool Video(int n) => player.TestTitle.Contains($"{n}/4 da pasta") && !player.TestTitle.Contains("foto") && player.State.fileTime > 0.2;
        bool Photo(int n) => player.TestTitle.Contains($"{n}/4 da pasta") && player.TestTitle.Contains("foto");
        player.OpenWeb(media, "MKVision teste", null);
        Check("abriu no 4º item (vídeo) tocando", Wait(() => Video(4), 15), player.TestTitle);
        Key(0x22); // Page Down: volta ao começo do post
        Check("Page Down depois do último: a 1ª foto", Wait(() => Photo(1), 15), player.TestTitle);
        Key(0x22);
        Check("Page Down: o 2º item (vídeo)", Wait(() => Video(2), 15), player.TestTitle);
        Key(0x22);
        Check("Page Down: a 3ª (foto)", Wait(() => Photo(3), 15), player.TestTitle);
        Key(0x21); // Page Up
        Check("Page Up: de volta ao vídeo já visto", Wait(() => Video(2), 15), player.TestTitle);
        Key(0x21);
        Key(0x21);
        Check("Page Up duas vezes: o 4º (vídeo), dando a volta", Wait(() => Video(4), 15), player.TestTitle);
        player.Dispose();

        Console.WriteLine(failures == 0 ? "tudo certo" : $"{failures} falha(s)");
        return failures == 0 ? 0 : 1;
    }

    // --cookietest: o arquivo de cookies que vai para o yt-dlp (login do navegador) — cookie de domínio (vale para os
    // subdomínios, como o da sessão do Instagram) e cookie só do endereço. Mostra nomes e domínios, nunca os valores.
    public static int Cookies()
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        using var gpu = new Gpu();
        void Pump(double seconds)
        {
            var until = DateTime.UtcNow.AddSeconds(seconds);
            while (DateTime.UtcNow < until)
            {
                while (Native.PeekMessage(out var msg, IntPtr.Zero, 0, 0, 1)) { Native.TranslateMessage(ref msg); Native.DispatchMessage(ref msg); }
                Thread.Sleep(5);
            }
        }
        var player = new Player(gpu, new object()) { IgnoreMouseLeave = true };
        player.TestSetSessionTabs("https://www.wikipedia.org/");
        player.ShowBrowser();
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (!player.WebReady && clock.Elapsed.TotalSeconds < 40) Pump(0.1);
        Pump(4);
        var set = player.TestWebEval("document.cookie = 'mkvdominio=1; domain=wikipedia.org; path=/; max-age=3600; secure'; document.cookie = 'mkvsohost=1; path=/; max-age=3600; secure'; 'ok'");
        while (!set.IsCompleted) Pump(0.05);
        Pump(0.5);
        var file = player.TestCookieFile("https://www.wikipedia.org/");
        while (!file.IsCompleted) Pump(0.05);
        foreach (var line in file.Result.Split('\n').Where(l => l.Length > 0 && !l.StartsWith('#')))
        {
            var f = line.Split('\t');
            Console.WriteLine($"domínio '{f[0]}' | subdomínios {f[1]} | caminho {f[2]} | seguro {f[3]} | expira {f[4]} | nome {f[5]}");
        }
        player.Dispose();
        return 0;
    }

    // --storytest <pasta>: stories do Instagram pela própria página (uma página local imita a API do Instagram): os itens
    // da pessoa e do destaque, o maior arquivo de cada vídeo, o story do endereço, a volta ao yt-dlp se a API falhar e
    // Page Up/Down pelos stories no player. Usar com MKV_SETTINGS, MKV_WEB_PROFILE e MKV_LOG.
    public static int Story(string folder)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        Directory.CreateDirectory(folder);
        MediaFactory.MFStartup(true).CheckError();
        string a = Path.Combine(folder, "story-deitado-2s.mp4"), b = Path.Combine(folder, "story-em-pe-5s.mp4"), png = Path.Combine(folder, "story-foto.png");
        PlayerTest.WriteBig(a, 640, 360, 30, 60);
        PlayerTest.WriteBig(b, 360, 640, 30, 150);
        var pixels = new byte[320 * 400 * 4];
        for (int i = 0; i < pixels.Length; i += 4) (pixels[i], pixels[i + 1], pixels[i + 2], pixels[i + 3]) = (90, 160, 220, 255);
        InterpTest.Png(png, pixels, 320, 0, 0, 320, 400);
        string urlA = LocalMediaServer.Register(() => File.OpenRead(a), "video/mp4", ".mp4");
        string urlB = LocalMediaServer.Register(() => File.OpenRead(b), "video/mp4", ".mp4");
        string urlPhoto = LocalMediaServer.Register(() => File.OpenRead(png), "image/png", ".png");
        string items = $$"""
            [{ "pk": "111", "video_duration": 5, "original_width": 360, "original_height": 640,
               "video_versions": [{ "width": 180, "height": 320, "url": "{{urlB}}?pequeno" }, { "width": 360, "height": 640, "url": "{{urlB}}" }] },
             { "pk": "222", "image_versions2": { "candidates": [{ "width": 320, "height": 400, "url": "{{urlPhoto}}" }, { "width": 64, "height": 80, "url": "x" }] } },
             { "id": "333_123", "video_duration": 2, "original_width": 640, "original_height": 360, "video_versions": [{ "width": 640, "height": 360, "url": "{{urlA}}" }] }]
            """;
        byte[] html = System.Text.Encoding.UTF8.GetBytes($$"""
            <!doctype html><meta charset=utf-8><title>Instagram (imitação)</title><body style='background:#000'>
            <script>
              window.__calls = [];
              window.fetch = async (path, options) => {
                window.__calls.push(path === location.pathname ? 'página do story login=' + options.credentials
                  : path.split('?')[0] + ' app=' + options.headers['X-IG-App-ID'] + ' login=' + options.credentials);
                const ok = body => ({ ok: true, status: 200, json: async () => body });
                if (path === location.pathname)  // a página do story traz o dono (como no Instagram)
                  return { ok: true, status: 200, text: async () => window.__noOwner ? '<html></html>' : '<script>{"props": {"user":{"pk":"123","username":"fulano"} } }<\/script>' };
                if (path.startsWith('/api/v1/users/web_profile_info/') && window.__limit) return { ok: false, status: 429, json: async () => ({}) };
                if (path.startsWith('/api/v1/users/web_profile_info/')) return ok({ data: { user: { id: '123' } } });
                if (path.startsWith('/api/v1/feed/reels_media/')) {
                  const reel = decodeURIComponent(path.split('reel_ids=')[1]);
                  if (window.__fail) return { ok: false, status: 401, json: async () => ({}) };
                  return ok({ reels: { [reel]: { items: {{items}} } } });
                }
                return { ok: false, status: 404, json: async () => ({}) };
              };
            </script>
            """);
        string page = LocalMediaServer.Register(() => new MemoryStream(html), "text/html; charset=utf-8", ".html");
        using var gpu = new Gpu();
        int failures = 0;
        void Check(string what, bool ok, string detail)
        {
            Console.WriteLine($"{(ok ? "OK    " : "FALHOU")} {what}: {detail}");
            if (!ok) failures++;
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
        bool Wait(Func<bool> condition, double seconds)
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            while (!condition() && clock.Elapsed.TotalSeconds < seconds) Pump(0.1);
            return condition();
        }
        Player player = null!;
        T Await<T>(Task<T> task) { Wait(() => task.IsCompleted, 10); return task.Result; }
        string Eval(string js)
        {
            var task = player.TestWebEval(js);
            Wait(() => task.IsCompleted, 5);
            try { return System.Text.Json.JsonSerializer.Deserialize<string>(task.Result) ?? task.Result; } catch { return task.IsCompletedSuccessfully ? task.Result : "(sem resposta)"; }
        }
        player = new Player(gpu, new object()) { IgnoreMouseLeave = true };
        player.TestSetSessionTabs("https://example.com/");
        player.ShowBrowser();
        Wait(() => player.WebReady, 40);
        player.TestWebNavigate(page);
        Wait(() => player.TestWebTitle.Contains("imitação"), 15);
        Pump(0.5);

        var list = Await(player.TestStoryItems("fulano", "333"));
        Check("story da pessoa: os 3 itens", list.Count == 3, string.Join(", ", list.Select(i => $"{i.Pk}:{(i.Video is not null ? "vídeo" : "foto")}")));
        string calls = Eval("window.__calls.join(' | ')");
        Check("o dono pela página do story, sem o endereço limitado de perfil", calls.StartsWith("página do story login=include") && !calls.Contains("web_profile_info") && calls.Contains("reels_media/ app=936619743392459 login=include"), calls);
        Check("o maior arquivo de cada vídeo e a maior foto", list[0].Video == urlB && list[1].Image == urlPhoto && list[2].Pk == "333", $"{list[0].Video?[^12..]}, {list[1].Image?[^12..]}, pk {list[2].Pk}");
        var media = Player.StoryMedia(list, "333", default, "https://www.instagram.com/stories/fulano/333/", "teste", "Story de @fulano");
        Check("abre o story do endereço (o 3º), com os 3 na pasta", media?.VideoUrl == urlA && media.Item == 3 && media.Post?.Count == 3, $"{media?.Item}, {media?.Post?.Count} itens");
        var guess = Player.StoryMedia(list, "", new MediaHint(5, 360, 640), "https://www.instagram.com/stories/highlights/9/", "teste", "Destaque");
        Check("destaque (sem o número do story): o que bate com a duração", guess?.VideoUrl == urlB && guess.Item == 1, $"{guess?.Item}");
        Eval("window.__calls = []");
        list = Await(player.TestStoryItems("highlights", "999"));
        Check("destaque: pede direto pelo número dele", list.Count == 3 && Eval("window.__calls.join(' | ')").StartsWith("/api/v1/feed/reels_media/"), Eval("window.__calls.join(' | ')"));
        Eval("window.__calls = []; window.__noOwner = true");
        list = Await(player.TestStoryItems("fulano", "333"));
        calls = Eval("window.__calls.join(' | ')");
        Check("página sem o dono: o endereço de perfil de reserva", list.Count == 3 && calls.Contains("web_profile_info/ app=936619743392459"), calls);
        Eval("window.__limit = true");
        var limited = player.TestStoryItems("fulano", "333");
        Wait(() => limited.IsCompleted, 10);
        Check("Instagram limitando (429): erro com o 429", limited.IsFaulted && limited.Exception!.GetBaseException().Message.Contains(" 429"), limited.IsFaulted ? limited.Exception!.GetBaseException().Message : "não falhou");
        Eval("window.__limit = false; window.__noOwner = false; window.__fail = true");
        var failed = player.TestStoryItems("fulano", "333");
        Wait(() => failed.IsCompleted, 10);
        Check("a API falhou: erro (e o app segue para o yt-dlp)", failed.IsFaulted, failed.IsFaulted ? failed.Exception!.GetBaseException().Message : "não falhou");

        // No player: abre no story do endereço; Page Up/Down passa pelos três.
        bool Video(int n) => player.TestTitle.Contains($"{n}/3 da pasta") && !player.TestTitle.Contains("foto") && player.State.fileTime > 0.2;
        bool Photo(int n) => player.TestTitle.Contains($"{n}/3 da pasta") && player.TestTitle.Contains("foto");
        void Key(int vk) { PlayerNative.PostMessage(player.Hwnd, PlayerNative.WM_KEYDOWN, vk, IntPtr.Zero); Pump(0.2); }
        player.ToggleWeb(); // como o ".": sai do navegador para o player
        player.OpenWeb(media!, "teste", null);
        Check("no player: o 3º story tocando", Wait(() => Video(3), 15), player.TestTitle);
        Key(0x22);
        Check("Page Down: o 1º (vídeo)", Wait(() => Video(1), 15), player.TestTitle);
        Key(0x22);
        Check("Page Down: o 2º (foto)", Wait(() => Photo(2), 15), player.TestTitle);
        Key(0x21);
        Check("Page Up: de volta ao 1º", Wait(() => Video(1), 15), player.TestTitle);
        player.Dispose();
        Console.WriteLine(failures == 0 ? "tudo certo" : $"{failures} falha(s)");
        return failures == 0 ? 0 : 1;
    }

    // --savefoldertest <pasta>: Ctrl+S (foto) e Shift+S (vídeo) lembram a última pasta onde você salvou — o "Salvar como"
    // abre nela, também depois de fechar e abrir o app; se ela some, volta à pasta de sempre. Usar com MKV_SETTINGS e MKV_LOG.
    public static int SaveFolder(string folder)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        Directory.CreateDirectory(folder);
        string photos = Path.Combine(folder, "fotos salvas"), videos = Path.Combine(folder, "vídeos salvos"), normal = Path.Combine(folder, "pasta de sempre");
        foreach (var d in new[] { photos, videos }) { if (Directory.Exists(d)) Directory.Delete(d, true); Directory.CreateDirectory(d); }
        string png = Path.Combine(folder, "foto.png"), clip = Path.Combine(folder, "clip.mp4");
        var pixels = new byte[200 * 150 * 4];
        for (int i = 0; i < pixels.Length; i += 4) (pixels[i], pixels[i + 1], pixels[i + 2], pixels[i + 3]) = (30, 90, 200, 255);
        InterpTest.Png(png, pixels, 200, 0, 0, 200, 150);
        MediaFactory.MFStartup(true).CheckError();
        PlayerTest.WriteClip(clip);
        using var gpu = new Gpu();
        int failures = 0;
        void Check(string what, bool ok, string detail)
        {
            Console.WriteLine($"{(ok ? "OK    " : "FALHOU")} {what}: {detail}");
            if (!ok) failures++;
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
        bool Wait(Func<bool> condition, double seconds)
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            while (!condition() && clock.Elapsed.TotalSeconds < seconds) Pump(0.1);
            return condition();
        }
        var player = new Player(gpu, new object()) { IgnoreMouseLeave = true };
        Check("nunca salvou: abre na pasta de sempre", player.TestSaveFolder(normal) == normal, player.TestSaveFolder(normal));
        // Ctrl+S numa foto, escolhendo a pasta "fotos salvas".
        player.Open(png);
        Wait(() => player.TestTitle.Contains("foto"), 10);
        Pump(1); // a foto termina de abrir (como você, que só salva depois de vê-la)
        string savedPhoto = Path.Combine(photos, "foto (cópia).png");
        Environment.SetEnvironmentVariable("MKV_SAVE_AS", savedPhoto);
        player.TestSavePhoto();
        Check("Ctrl+S salvou a foto", Wait(() => File.Exists(savedPhoto), 10), $"{savedPhoto} — aviso na tela: \"{player.TestOsdText}\"");
        Check("a pasta da foto fica guardada", Wait(() => player.TestLastSaveFolder == photos, 5), player.TestLastSaveFolder);
        Check("o próximo \"Salvar como\" abre nela", player.TestSaveFolder(normal) == photos, player.TestSaveFolder(normal));
        // Shift+S num vídeo, escolhendo outra pasta: passa a ser ela.
        player.Open(clip);
        Wait(() => player.State.fileTime > 0.2, 10);
        string savedVideo = Path.Combine(videos, "clip.mp4");
        Environment.SetEnvironmentVariable("MKV_SAVE_AS", savedVideo);
        player.TestSaveVideo();
        Check("Shift+S salvou o vídeo", Wait(() => File.Exists(savedVideo), 10), savedVideo);
        Check("agora a última é a do vídeo", Wait(() => player.TestLastSaveFolder == videos, 5), player.TestLastSaveFolder);
        Environment.SetEnvironmentVariable("MKV_SAVE_AS", null);
        player.Dispose();
        Pump(0.5);
        // Abre o app de novo: continua lembrando.
        player = new Player(gpu, new object()) { IgnoreMouseLeave = true };
        Check("depois de fechar e abrir o app: continua na última", player.TestSaveFolder(normal) == videos, player.TestSaveFolder(normal));
        Directory.Delete(videos, true);
        Check("a pasta foi apagada: volta à de sempre", player.TestSaveFolder(normal) == normal, player.TestSaveFolder(normal));
        player.Dispose();
        Console.WriteLine(failures == 0 ? "tudo certo" : $"{failures} falha(s)");
        return failures == 0 ? 0 : 1;
    }

    // --annotatetest <pasta>: anotações (F3) — pincel e retângulo na imagem da tela (conferidos pela cor dos pixels), o
    // traço preso à imagem com zoom e giro, desfazer, Esc mantém os traços, Ctrl+S salva com eles, outra foto começa sem
    // nada e o vídeo pausa ao abrir o painel. Usar com MKV_SETTINGS e MKV_LOG.
    public static int Annotate(string folder)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        Directory.CreateDirectory(folder);
        string png = Path.Combine(folder, "cinza.png"), other = Path.Combine(folder, "outra.png"), clip = Path.Combine(folder, "clip.mp4");
        foreach (var (path, shade) in new[] { (png, (byte)128), (other, (byte)90) })
        {
            var gray = new byte[800 * 600 * 4];
            for (int i = 0; i < gray.Length; i += 4) (gray[i], gray[i + 1], gray[i + 2], gray[i + 3]) = (shade, shade, shade, 255);
            InterpTest.Png(path, gray, 800, 0, 0, 800, 600);
        }
        MediaFactory.MFStartup(true).CheckError();
        PlayerTest.WriteClip(clip);
        using var gpu = new Gpu();
        int failures = 0;
        void Check(string what, bool ok, string detail)
        {
            Console.WriteLine($"{(ok ? "OK    " : "FALHOU")} {what}: {detail}");
            if (!ok) failures++;
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
        bool Wait(Func<bool> condition, double seconds)
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            while (!condition() && clock.Elapsed.TotalSeconds < seconds) Pump(0.1);
            return condition();
        }
        var player = new Player(gpu, new object()) { IgnoreMouseLeave = true };
        void Mouse(uint message, double x, double y) => PlayerNative.PostMessage(player.Hwnd, message, IntPtr.Zero, (IntPtr)(((int)y << 16) | ((int)x & 0xffff)));
        void Key(int vk) { PlayerNative.PostMessage(player.Hwnd, PlayerNative.WM_KEYDOWN, vk, IntPtr.Zero); Pump(0.3); }
        // Cor do pixel da tela onde fica o ponto (u, v) da imagem.
        (int r, int g, int b) At(double u, double v)
        {
            Pump(0.3);
            var (x, y) = player.TestImageToScreen(u, v);
            var shot = player.Snapshot()!.Value;
            int i = ((int)y * shot.width + (int)x) * 4;
            return (shot.pixels[i + 2], shot.pixels[i + 1], shot.pixels[i]);
        }
        bool Red((int r, int g, int b) c) => c.r > 200 && c.g < 100 && c.b < 100;
        bool Gray((int r, int g, int b) c) => Math.Abs(c.r - c.g) < 25 && Math.Abs(c.g - c.b) < 25;
        void Drag(double u0, double v0, double u1, double v1)
        {
            var (x0, y0) = player.TestImageToScreen(u0, v0);
            var (x1, y1) = player.TestImageToScreen(u1, v1);
            Mouse(PlayerNative.WM_MOUSEMOVE, x0, y0);
            Mouse(PlayerNative.WM_LBUTTONDOWN, x0, y0);
            for (int i = 1; i <= 12; i++) Mouse(PlayerNative.WM_MOUSEMOVE, x0 + (x1 - x0) * i / 12, y0 + (y1 - y0) * i / 12);
            Mouse(PlayerNative.WM_LBUTTONUP, x1, y1);
            Pump(0.5);
        }

        player.Open(png);
        Wait(() => player.TestTitle.Contains("foto"), 10);
        Pump(1);
        Key(0x72); // F3
        Check("F3 abre o painel", player.TestAnnotating, $"{player.TestAnnotating}");
        Pump(0.5);
        Check("antes de desenhar: a imagem cinza", Gray(At(0.5, 0.5)), $"{At(0.5, 0.5)}");
        Drag(0.25, 0.5, 0.75, 0.5); // pincel (vermelho, o padrão)
        Check("pincel: um traço", player.TestStrokeCount == 1, $"{player.TestStrokeCount}");
        Check("o traço aparece na tela (vermelho)", Red(At(0.5, 0.5)) && Gray(At(0.5, 0.3)), $"no traço {At(0.5, 0.5)}, fora {At(0.5, 0.3)}");
        if (player.Snapshot() is { } s1) InterpTest.Png(Path.Combine(folder, "pincel.png"), s1.pixels, s1.width, 0, 0, s1.width, s1.height);
        var (rx, ry) = player.TestAnnoButton(1); // retângulo
        Mouse(PlayerNative.WM_MOUSEMOVE, rx, ry);
        Mouse(PlayerNative.WM_LBUTTONDOWN, rx, ry);
        Mouse(PlayerNative.WM_LBUTTONUP, rx, ry);
        Pump(0.3);
        Drag(0.1, 0.1, 0.3, 0.3);
        Check("retângulo: borda vermelha, dentro continua", player.TestStrokeCount == 2 && Red(At(0.2, 0.1)) && Gray(At(0.2, 0.2)), $"borda {At(0.2, 0.1)}, dentro {At(0.2, 0.2)}");
        // Zoom (roda do mouse): o traço continua no mesmo lugar da imagem.
        var wheel = new Native.POINT();
        var (cx, cy) = player.TestImageToScreen(0.6, 0.6);
        (wheel.X, wheel.Y) = ((int)cx, (int)cy);
        ClientToScreen(player.Hwnd, ref wheel);
        for (int i = 0; i < 3; i++) PlayerNative.PostMessage(player.Hwnd, PlayerNative.WM_MOUSEWHEEL, (IntPtr)(120 << 16), (IntPtr)((wheel.Y << 16) | (wheel.X & 0xffff)));
        Pump(0.6);
        Check("com zoom: o traço acompanha a imagem", Red(At(0.5, 0.5)) && Gray(At(0.5, 0.35)), $"no traço {At(0.5, 0.5)}, fora {At(0.5, 0.35)}");
        if (player.Snapshot() is { } s2) InterpTest.Png(Path.Combine(folder, "zoom.png"), s2.pixels, s2.width, 0, 0, s2.width, s2.height);
        Key('Z'); // sem zoom
        Key(0x2E); Key(0x2E); // Delete duas vezes: 90°
        Check("girada 90°: o traço gira junto", Red(At(0.5, 0.5)) && Red(At(0.7, 0.5)) && Gray(At(0.5, 0.35)), $"{At(0.5, 0.5)} {At(0.7, 0.5)}, fora {At(0.5, 0.35)}");
        if (player.Snapshot() is { } s3) InterpTest.Png(Path.Combine(folder, "girada.png"), s3.pixels, s3.width, 0, 0, s3.width, s3.height);
        Key(0x2D); Key(0x2D); // volta
        player.TestUndoStroke(); // Ctrl+Z
        Check("Ctrl+Z desfaz o retângulo (o pincel fica)", player.TestStrokeCount == 1 && Gray(At(0.2, 0.1)) && Red(At(0.5, 0.5)), $"{player.TestStrokeCount} traço(s), borda {At(0.2, 0.1)}");
        // Ctrl+S: a imagem salva tem o traço no mesmo lugar.
        string saved = Path.Combine(folder, "salva-anotada.png");
        if (File.Exists(saved)) File.Delete(saved);
        Environment.SetEnvironmentVariable("MKV_SAVE_AS", saved);
        player.TestSavePhoto();
        Wait(() => File.Exists(saved), 10);
        Environment.SetEnvironmentVariable("MKV_SAVE_AS", null);
        if (File.Exists(saved))
        {
            var (pixels, w, h) = ImageFile.LoadPixels(saved);
            (int r, int g, int b) Px(double u, double v) { int i = ((int)(v * h) * w + (int)(u * w)) * 4; return (pixels[i + 2], pixels[i + 1], pixels[i]); }
            Check("Ctrl+S salva com o traço, no lugar certo", w == 800 && h == 600 && Red(Px(0.5, 0.5)) && Gray(Px(0.5, 0.3)), $"{w}x{h}, no traço {Px(0.5, 0.5)}, fora {Px(0.5, 0.3)}");
        }
        else Check("Ctrl+S salva com o traço, no lugar certo", false, "não salvou");
        // G: a imagem que vai para o ComfyUI tem o traço (na orientação da tela, como a captura sai).
        (int r, int g, int b) Sent((byte[] pixels, int w, int h) img, double x, double y) { int i = ((int)(y * img.h) * img.w + (int)(x * img.w)) * 4; return (img.pixels[i + 2], img.pixels[i + 1], img.pixels[i]); }
        var sent = player.TestComfyPixels()!.Value;
        Check("G manda a imagem com o traço", Red(Sent(sent, 0.5, 0.5)) && Gray(Sent(sent, 0.5, 0.3)), $"{sent.w}x{sent.h}, no traço {Sent(sent, 0.5, 0.5)}, fora {Sent(sent, 0.5, 0.3)}");
        Key(0x2E); Key(0x2E); // 90°: a captura sai girada, o traço (horizontal na foto) fica em pé
        sent = player.TestComfyPixels()!.Value;
        Check("G com a imagem girada 90°: o traço gira junto", sent.w == 600 && Red(Sent(sent, 0.5, 0.3)) && Red(Sent(sent, 0.5, 0.7)) && Gray(Sent(sent, 0.3, 0.5)) && Gray(Sent(sent, 0.5, 0.15)),
            $"{sent.w}x{sent.h}: {Sent(sent, 0.5, 0.3)} {Sent(sent, 0.5, 0.7)}, fora {Sent(sent, 0.3, 0.5)} {Sent(sent, 0.5, 0.15)}");
        Key(0x2D); Key(0x2D);
        // A resposta do ComfyUI (simulada: verde) é outra versão: o traço não fica por cima dela; voltando à original, volta.
        var green = new byte[800 * 600 * 4];
        for (int i = 0; i < green.Length; i += 4) (green[i], green[i + 1], green[i + 2], green[i + 3]) = (60, 180, 60, 255);
        player.TestComfyResult(green, 800, 600);
        Pump(1);
        var onComfy = At(0.5, 0.5);
        Check("resposta do ComfyUI na tela: sem o traço por cima", onComfy.g > 150 && onComfy.r < 100 && player.TestStrokeCount == 1, $"{onComfy}, {player.TestStrokeCount} traço(s) guardado(s)");
        player.TestComfyKey(); // T: volta à original
        Pump(1);
        Check("T volta à original: o traço reaparece", Red(At(0.5, 0.5)) && Gray(At(0.5, 0.3)), $"{At(0.5, 0.5)}");
        Key(0x1B); // Esc
        Check("Esc fecha o painel e mantém o traço", !player.TestAnnotating && player.TestStrokeCount == 1 && Red(At(0.5, 0.5)), $"aberto {player.TestAnnotating}, {player.TestStrokeCount} traço(s)");
        player.Open(other);
        Wait(() => player.TestTitle.Contains("outra"), 10);
        Pump(1);
        Check("outra foto: começa sem traços", player.TestStrokeCount == 0 && Gray(At(0.5, 0.5)), $"{player.TestStrokeCount} traço(s), {At(0.5, 0.5)}");
        player.Open(clip);
        Wait(() => player.State.playing && player.State.fileTime > 0.3, 10);
        Key(0x72);
        Check("vídeo: F3 pausa no quadro", player.TestAnnotating && !player.State.playing, $"painel {player.TestAnnotating}, tocando {player.State.playing}");
        player.Dispose();
        Console.WriteLine(failures == 0 ? "tudo certo" : $"{failures} falha(s)");
        return failures == 0 ? 0 : 1;
    }

    // --comfyareatest <pasta>: a área de edição do G a partir das anotações — retângulo e círculo à mão viram a área
    // (a máscara exclui o contorno), risco aberto não; o texto que vai junto do prompt; e a resposta (simulada: verde,
    // noutro tamanho, ainda com o contorno vermelho) só vale dentro da área — fora e no contorno volta a original.
    public static int ComfyAreaTest(string folder)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        Directory.CreateDirectory(folder);
        string png = Path.Combine(folder, "cinza.png");
        var gray = new byte[800 * 600 * 4];
        for (int i = 0; i < gray.Length; i += 4) (gray[i], gray[i + 1], gray[i + 2], gray[i + 3]) = (128, 128, 128, 255);
        InterpTest.Png(png, gray, 800, 0, 0, 800, 600);
        using var gpu = new Gpu();
        int failures = 0;
        void Check(string what, bool ok, string detail)
        {
            Console.WriteLine($"{(ok ? "OK    " : "FALHOU")} {what}: {detail}");
            if (!ok) failures++;
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
        var player = new Player(gpu, new object()) { IgnoreMouseLeave = true };
        void Mouse(uint message, double x, double y) => PlayerNative.PostMessage(player.Hwnd, message, IntPtr.Zero, (IntPtr)(((int)y << 16) | ((int)x & 0xffff)));
        void Trace((double u, double v)[] points)
        {
            var screen = points.Select(p => player.TestImageToScreen(p.u, p.v)).ToArray();
            Mouse(PlayerNative.WM_MOUSEMOVE, screen[0].x, screen[0].y);
            Mouse(PlayerNative.WM_LBUTTONDOWN, screen[0].x, screen[0].y);
            foreach (var (x, y) in screen.Skip(1)) Mouse(PlayerNative.WM_MOUSEMOVE, x, y);
            Mouse(PlayerNative.WM_LBUTTONUP, screen[^1].x, screen[^1].y);
            Pump(0.5);
        }
        float M((float[]? mask, int w, int h) a, double u, double v) => a.mask is null ? -1 : a.mask[(int)(v * a.h) * a.w + (int)(u * a.w)];
        (int r, int g, int b) Px(byte[] pixels, int w, int h, double u, double v) { int i = ((int)(v * h) * w + (int)(u * w)) * 4; return (pixels[i + 2], pixels[i + 1], pixels[i]); }

        player.Open(png);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (!player.TestTitle.Contains("foto") && clock.Elapsed.TotalSeconds < 10) Pump(0.1);
        Pump(1);
        PlayerNative.PostMessage(player.Hwnd, PlayerNative.WM_KEYDOWN, 0x72, IntPtr.Zero); // F3
        Pump(0.5);

        // 1) Retângulo vermelho.
        player.TestAnnoAction(1);
        Trace([(0.3, 0.3), (0.5, 0.5), (0.7, 0.7)]);
        var area = player.TestComfyArea()!.Value;
        var m = (area.mask, area.w, area.h);
        Check("retângulo: dentro é a área, fora não, o contorno não", M(m, 0.5, 0.5) > 0.99 && M(m, 0.1, 0.1) == 0 && M(m, 0.3, 0.5) == 0 && M(m, 0.5, 0.7) == 0,
            $"centro {M(m, 0.5, 0.5):0.00}, fora {M(m, 0.1, 0.1):0.00}, contorno {M(m, 0.3, 0.5):0.00}");
        Check("texto do prompt: editar só dentro do contorno vermelho e tirá-lo", area.instruction.Contains("only inside the area outlined in red") && area.instruction.Contains("Remove the red outline"), area.instruction);
        Check("vai com o desenho; a cópia limpa não tem", Px(area.sent, area.w, area.h, 0.3, 0.5).r > 200 && Px(area.clean, area.w, area.h, 0.3, 0.5).r < 150,
            $"enviada {Px(area.sent, area.w, area.h, 0.3, 0.5)}, limpa {Px(area.clean, area.w, area.h, 0.3, 0.5)}");
        // A resposta simulada: verde, 1,5× maior, com o contorno vermelho ainda lá.
        int rw = area.w * 3 / 2, rh = area.h * 3 / 2;
        var reply = new byte[rw * rh * 4];
        for (int y = 0; y < rh; y++)
        for (int x = 0; x < rw; x++)
        {
            double u = (x + 0.5) / rw, v = (y + 0.5) / rh;
            bool ring = (Math.Abs(u - 0.3) < 0.006 || Math.Abs(u - 0.7) < 0.006) && v > 0.29 && v < 0.71 || (Math.Abs(v - 0.3) < 0.008 || Math.Abs(v - 0.7) < 0.008) && u > 0.29 && u < 0.71;
            int i = (y * rw + x) * 4;
            (reply[i], reply[i + 1], reply[i + 2], reply[i + 3]) = ring ? ((byte)40, (byte)40, (byte)230, (byte)255) : ((byte)60, (byte)190, (byte)60, (byte)255);
        }
        var final = ComfyArea.KeepOutside(reply, rw, rh, area.clean, area.w, area.h, area.mask!);
        var center = Px(final, rw, rh, 0.5, 0.5); var outside = Px(final, rw, rh, 0.1, 0.1); var border = Px(final, rw, rh, 0.3, 0.5); var border2 = Px(final, rw, rh, 0.5, 0.7);
        Check("resposta: a edição dentro da área", center.g > 170 && center.r < 90, $"{center}");
        Check("resposta: fora da área, a original", Math.Abs(outside.r - 128) < 4 && Math.Abs(outside.g - 128) < 4, $"{outside}");
        Check("resposta: sem o contorno vermelho", border.r < 140 && border2.r < 140 && Math.Abs(border.r - border.g) < 10, $"{border} {border2}");
        InterpTest.Png(System.IO.Path.Combine(folder, "resposta-recomposta.png"), final, rw, 0, 0, rw, rh);
        player.TestComfyResult(final, rw, rh);
        Pump(1);
        if (player.Snapshot() is { } shot) InterpTest.Png(System.IO.Path.Combine(folder, "tela-resposta.png"), shot.pixels, shot.width, 0, 0, shot.width, shot.height);
        player.TestComfyKey(); // T: volta à original para os próximos desenhos
        Pump(0.5);

        // 2) Círculo à mão (azul), com uma pequena abertura entre o começo e o fim.
        player.TestClearStrokes();
        player.TestAnnoAction(0);
        player.TestAnnoAction(13); // azul
        var circle = Enumerable.Range(0, 34).Select(i => (0.5 + 0.18 * Math.Cos(i * 2 * Math.PI / 36), 0.5 + 0.24 * Math.Sin(i * 2 * Math.PI / 36))).ToArray();
        Trace(circle);
        area = player.TestComfyArea()!.Value;
        m = (area.mask, area.w, area.h);
        Check("círculo à mão (quase fechado): vira área", M(m, 0.5, 0.5) > 0.99 && M(m, 0.1, 0.1) == 0, $"centro {M(m, 0.5, 0.5):0.00}, fora {M(m, 0.1, 0.1):0.00}");
        Check("texto: contorno azul", area.instruction.Contains("outlined in blue"), area.instruction);

        // 3) Risco aberto (amarelo) junto: é indicação, não área.
        player.TestAnnoAction(11);
        Trace([(0.1, 0.9), (0.3, 0.85), (0.5, 0.9)]);
        area = player.TestComfyArea()!.Value;
        Check("risco aberto: indicação (sai da resposta), não área", area.instruction.Contains("outlined in blue") && area.instruction.Contains("yellow marks") && M((area.mask, area.w, area.h), 0.3, 0.85) == 0, area.instruction);

        // 4) Só um risco aberto: sem área (a resposta vale inteira).
        player.TestClearStrokes();
        player.TestAnnoAction(10);
        Trace([(0.2, 0.5), (0.5, 0.45), (0.8, 0.5)]);
        area = player.TestComfyArea()!.Value;
        Check("só risco aberto: sem área, só a indicação", area.mask is null && area.instruction.Contains("red marks"), $"máscara {(area.mask is null ? "nenhuma" : "?")}, \"{area.instruction}\"");
        player.Dispose();
        Console.WriteLine(failures == 0 ? "tudo certo" : $"{failures} falha(s)");
        return failures == 0 ? 0 : 1;
    }

    // --playermenutest <pasta>: o menu do botão direito do player — tela vazia, foto (salvar, anotações, a pasta) e vídeo
    // (salvar vídeo, velocidade, quadro a quadro); só abre na imagem (nas barras não); e "Ir para o navegador".
    // Usar com MKV_SETTINGS, MKV_LOG e MKV_WEB_PROFILE.
    public static int PlayerMenu(string folder)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        Directory.CreateDirectory(folder);
        string photos = Path.Combine(folder, "fotos");
        Directory.CreateDirectory(photos);
        string png = Path.Combine(photos, "a.png"), other = Path.Combine(photos, "b.png"), clip = Path.Combine(folder, "clip.mp4");
        foreach (var (path, shade) in new[] { (png, (byte)128), (other, (byte)90) })
        {
            var gray = new byte[800 * 600 * 4];
            for (int i = 0; i < gray.Length; i += 4) (gray[i], gray[i + 1], gray[i + 2], gray[i + 3]) = (shade, shade, shade, 255);
            InterpTest.Png(path, gray, 800, 0, 0, 800, 600);
        }
        MediaFactory.MFStartup(true).CheckError();
        PlayerTest.WriteClip(clip);
        using var gpu = new Gpu();
        int failures = 0;
        void Check(string what, bool ok, string detail)
        {
            Console.WriteLine($"{(ok ? "OK    " : "FALHOU")} {what}: {detail}");
            if (!ok) failures++;
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
        bool Wait(Func<bool> condition, double seconds)
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            while (!condition() && clock.Elapsed.TotalSeconds < seconds) Pump(0.1);
            return condition();
        }
        var player = new Player(gpu, new object()) { IgnoreMouseLeave = true };
        IntPtr Pos(int x, int y) => (IntPtr)((y << 16) | (x & 0xffff));
        // Botão direito de verdade (a mensagem) em (x, y), escolhendo o item pelo texto.
        string[] RightClick(int x, int y, string choice)
        {
            player.TestMenuShown = [];
            player.TestMenuChoice = choice;
            PlayerNative.PostMessage(player.Hwnd, PlayerNative.WM_MOUSEMOVE, IntPtr.Zero, Pos(x, y));
            PlayerNative.PostMessage(player.Hwnd, 0x0205, IntPtr.Zero, Pos(x, y));
            Pump(0.5);
            player.TestMenuChoice = null;
            return player.TestMenuShown;
        }
        string Show(string[] menu) => string.Join(" | ", menu);
        Pump(1);

        var menu = player.TestMenu();
        Check("tela vazia: navegador e abrir", menu.SequenceEqual(["Voltar para o navegador", "Abrir..."]), Show(menu));

        player.Open(png);
        Wait(() => player.TestTitle.Contains("foto"), 10);
        Pump(1);
        var shot = player.Snapshot()!.Value;
        int cx = shot.width / 2, cy = shot.height / 2;
        menu = player.TestMenu();
        Check("foto: navegador no topo, depois arquivo e edição",
            menu.Take(9).SequenceEqual(["Voltar para o navegador", "Salvar imagem", "Mostrar no Explorer", "Anterior da pasta", "Próximo da pasta",
                "Editar com o ComfyUI...", "Desenhar / marcar área", "Cortar", "Melhorar com a IA de fotos"]), Show(menu));
        Check("foto: efeitos e imagem (girar, espelhar); sem o que é de vídeo",
            menu.Contains("Luz e cor") && menu.Contains("Comparar sem ajustes") && menu.Contains("Nitidez do player ✓") && menu.Any(m => m.StartsWith("Potência da IA (")) &&
            menu.Contains("Girar 90° para a direita") && menu.Contains("Espelhar na horizontal") && menu.Contains("Espelhar na vertical") &&
            !menu.Contains("Salvar vídeo") && !menu.Any(m => m.StartsWith("Velocidade/")), Show(menu));
        // Efeitos e imagem pelo menu: o mesmo que as teclas, e marcados quando ligados.
        RightClick(cx, cy, "Comparar sem ajustes");
        Check("Comparar sem ajustes (B): liga e fica marcado", player.TestMenu().Contains("Comparar sem ajustes ✓"), Show(player.TestMenu()));
        RightClick(cx, cy, "Comparar sem ajustes");
        RightClick(cx, cy, "Espelhar na horizontal");
        Check("Espelhar na horizontal (Home)", player.TestFlip.h && player.TestMenu().Contains("Espelhar na horizontal ✓"), $"{player.TestFlip}");
        RightClick(cx, cy, "Espelhar na horizontal");
        RightClick(cx, cy, "Girar 90° para a direita");
        Check("Girar 90° para a direita; aparece \"Sem giro\"", player.TestTitle.Contains("girado 90°") && player.TestMenu().Contains("Sem giro"), player.TestTitle);
        RightClick(cx, cy, "Sem giro");
        Check("Sem giro", !player.TestTitle.Contains("girado"), player.TestTitle);
        string suave = player.TestMenu().First(m => m.StartsWith("Perfil (") && m.EndsWith("/Suave"));
        RightClick(cx, cy, suave);
        Check("Perfil pelo submenu", player.TestMenu().Any(m => m.StartsWith("Perfil (Suave)/")), suave);
        string strength = player.TestMenu().First(m => m.StartsWith("Força (") && m.EndsWith("/150%"));
        RightClick(cx, cy, strength);
        Check("Força pelo submenu", player.TestMenu().Contains("Força (150%)/150% ✓"), strength);
        var shown = RightClick(cx, cy, "Desenhar / marcar área");
        Check("botão direito na imagem: o menu, e o item escolhido funciona", shown.Length > 0 && player.TestAnnotating, $"{shown.Length} itens, desenhando {player.TestAnnotating}");
        Check("o item fica marcado", player.TestMenu().Contains("Desenhar / marcar área ✓"), Show(player.TestMenu()));
        RightClick(cx, cy, "Desenhar / marcar área");
        Check("de novo: fecha", !player.TestAnnotating, $"desenhando {player.TestAnnotating}");
        string saved = Path.Combine(folder, "salva.png");
        if (File.Exists(saved)) File.Delete(saved);
        Environment.SetEnvironmentVariable("MKV_SAVE_AS", saved);
        RightClick(cx, cy, "Salvar imagem");
        Check("Salvar imagem", Wait(() => File.Exists(saved), 10), saved);
        Environment.SetEnvironmentVariable("MKV_SAVE_AS", null);
        RightClick(cx, cy, "Próximo da pasta");
        Check("Próximo da pasta", Wait(() => player.TestTitle.Contains("b.png"), 10), player.TestTitle);
        // Nas barras: nada (o menu é só da imagem).
        PlayerNative.PostMessage(player.Hwnd, PlayerNative.WM_MOUSEMOVE, IntPtr.Zero, Pos(cx, player.UiScaled(10)));
        Pump(0.6);
        shown = RightClick(cx, player.UiScaled(10), "Salvar imagem");
        Check("no menu do topo: sem menu", player.BarsVisible.top && shown.Length == 0, $"topo {player.BarsVisible.top}, {Show(shown)}");

        player.Open(clip);
        Wait(() => player.State.playing && player.State.fileTime > 0.3, 10);
        menu = player.TestMenu();
        Check("vídeo: quadro, vídeo, velocidade, quadro a quadro",
            menu.Take(4).SequenceEqual(["Voltar para o navegador", "Salvar o quadro como imagem", "Salvar vídeo", "Mostrar no Explorer"]) && menu.Contains("Velocidade/1x (normal) ✓") &&
            menu.Contains("Quadro anterior") && menu.Contains("Próximo quadro") && menu.Contains("Repetir ✓") && menu.Any(m => m.StartsWith("Câmera lenta/")), Show(menu));
        PlayerNative.PostMessage(player.Hwnd, PlayerNative.WM_MOUSEMOVE, IntPtr.Zero, Pos(cx, shot.height - player.UiScaled(20)));
        Pump(0.6);
        if (player.BarsVisible.bottom)
        {
            shown = RightClick(cx, shot.height - player.UiScaled(20), "Salvar imagem");
            Check("na barra de baixo: sem menu", shown.Length == 0, Show(shown));
        }
        else Check("na barra de baixo: sem menu", false, "a barra não apareceu");

        RightClick(cx, cy, "Velocidade/0,5x");
        Check("Velocidade 0,5x", Math.Abs(player.State.speed - 0.5) < 0.001 && player.TestMenu().Contains("Velocidade/0,5x ✓"), $"{player.State.speed}");
        RightClick(cx, cy, "Velocidade/1x (normal)");
        Check("Velocidade 1x", Math.Abs(player.State.speed - 1) < 0.001, $"{player.State.speed}");
        RightClick(cx, cy, "Próximo quadro");
        Check("Próximo quadro: pausa", !player.State.playing, $"tocando {player.State.playing}");
        string savedVideo = Path.Combine(folder, "salvo.mp4");
        if (File.Exists(savedVideo)) File.Delete(savedVideo);
        Environment.SetEnvironmentVariable("MKV_SAVE_AS", savedVideo);
        RightClick(cx, cy, "Salvar vídeo");
        Check("Salvar vídeo", Wait(() => File.Exists(savedVideo) && new FileInfo(savedVideo).Length > 0, 15), savedVideo);
        Environment.SetEnvironmentVariable("MKV_SAVE_AS", null);
        RightClick(cx, cy, "Voltar para o navegador");
        Check("Voltar para o navegador", Wait(() => player.WebMode, 5), $"navegador {player.WebMode}");
        player.Dispose();
        Console.WriteLine(failures == 0 ? "tudo certo" : $"{failures} falha(s)");
        return failures == 0 ? 0 : 1;
    }

    // --croptest <pasta>: o corte (F4) — a moldura pelos cantos, Aceitar, e a tela, o G e o Ctrl+S só com a parte cortada
    // (conferidos pelas cores de uma foto em 4 quadrantes); traços (F3) no lugar certo com corte; proporção 1:1, mover,
    // "Imagem inteira", Esc cancela; girar desfaz; com giro, o arquivo salvo tem a mesma parte; espelhar espelha o corte;
    // o menu do botão direito; e no vídeo pausa. Usar com MKV_SETTINGS e MKV_LOG.
    public static int Crop(string folder)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        Directory.CreateDirectory(folder);
        string png = Path.Combine(folder, "quadrantes.png"), clip = Path.Combine(folder, "clip.mp4");
        var quad = new byte[800 * 600 * 4];
        for (int y = 0; y < 600; y++)
        for (int x = 0; x < 800; x++)
        {
            // Cima: vermelho | verde. Baixo: azul | branco.
            var (r, g, b) = (x < 400, y < 300) switch { (true, true) => (255, 0, 0), (false, true) => (0, 255, 0), (true, false) => (0, 0, 255), _ => (255, 255, 255) };
            int i = (y * 800 + x) * 4;
            (quad[i], quad[i + 1], quad[i + 2], quad[i + 3]) = ((byte)b, (byte)g, (byte)r, 255);
        }
        InterpTest.Png(png, quad, 800, 0, 0, 800, 600);
        MediaFactory.MFStartup(true).CheckError();
        PlayerTest.WriteClip(clip);
        using var gpu = new Gpu();
        int failures = 0;
        void Check(string what, bool ok, string detail)
        {
            Console.WriteLine($"{(ok ? "OK    " : "FALHOU")} {what}: {detail}");
            if (!ok) failures++;
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
        bool Wait(Func<bool> condition, double seconds)
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            while (!condition() && clock.Elapsed.TotalSeconds < seconds) Pump(0.1);
            return condition();
        }
        var player = new Player(gpu, new object()) { IgnoreMouseLeave = true };
        IntPtr Pos(double x, double y) => (IntPtr)(((int)y << 16) | ((int)x & 0xffff));
        void Key(int vk) { PlayerNative.PostMessage(player.Hwnd, PlayerNative.WM_KEYDOWN, vk, IntPtr.Zero); Pump(0.4); }
        void Drag((double x, double y) from, (double x, double y) to)
        {
            PlayerNative.PostMessage(player.Hwnd, PlayerNative.WM_MOUSEMOVE, IntPtr.Zero, Pos(from.x, from.y));
            PlayerNative.PostMessage(player.Hwnd, PlayerNative.WM_LBUTTONDOWN, IntPtr.Zero, Pos(from.x, from.y));
            for (int i = 1; i <= 10; i++) PlayerNative.PostMessage(player.Hwnd, PlayerNative.WM_MOUSEMOVE, IntPtr.Zero, Pos(from.x + (to.x - from.x) * i / 10, from.y + (to.y - from.y) * i / 10));
            PlayerNative.PostMessage(player.Hwnd, PlayerNative.WM_LBUTTONUP, IntPtr.Zero, Pos(to.x, to.y));
            Pump(0.5);
        }
        void Click((int x, int y) at)
        {
            PlayerNative.PostMessage(player.Hwnd, PlayerNative.WM_MOUSEMOVE, IntPtr.Zero, Pos(at.x, at.y));
            PlayerNative.PostMessage(player.Hwnd, PlayerNative.WM_LBUTTONDOWN, IntPtr.Zero, Pos(at.x, at.y));
            PlayerNative.PostMessage(player.Hwnd, PlayerNative.WM_LBUTTONUP, IntPtr.Zero, Pos(at.x, at.y));
            Pump(0.5);
        }
        string Name((int r, int g, int b) c) => c.r > 170 && c.g > 170 && c.b > 170 ? "branco" : c.r > 170 && c.g < 90 && c.b < 90 ? "vermelho" :
            c.g > 170 && c.r < 90 && c.b < 90 ? "verde" : c.b > 170 && c.r < 90 && c.g < 90 ? "azul" : $"outra {c}";
        (int r, int g, int b) Px(byte[] pixels, int w, int h, double u, double v) { int i = (Math.Min(h - 1, (int)(v * h)) * w + Math.Min(w - 1, (int)(u * w))) * 4; return (pixels[i + 2], pixels[i + 1], pixels[i]); }
        (int r, int g, int b) Screen(double x, double y) { var shot = player.Snapshot()!.Value; return Px(shot.pixels, shot.width, shot.height, x / shot.width, y / shot.height); }
        bool Near((double x, double y, double w, double h) a, (double x, double y, double w, double h) b) =>
            Math.Abs(a.x - b.x) < 0.02 && Math.Abs(a.y - b.y) < 0.02 && Math.Abs(a.w - b.w) < 0.02 && Math.Abs(a.h - b.h) < 0.02;
        const int Accept = 12, Full = 10, Ratio = 0;

        player.Open(png);
        Wait(() => player.TestTitle.Contains("foto"), 10);
        Pump(1);
        var shot0 = player.Snapshot()!.Value;
        int cw = shot0.width, ch = shot0.height;
        Check("menu do botão direito: Cortar", player.TestMenu().Contains("Cortar"), string.Join(" | ", player.TestMenu()));

        // 1) F4 e o canto de baixo à direita até o meio: só o quadrante vermelho.
        Key(0x73);
        Check("F4 abre o corte", player.TestCropping, $"{player.TestCropping}");
        Check("cortando: o menu só com aceitar/cancelar", player.TestMenu().SequenceEqual(["Aceitar o corte", "Cancelar o corte", "Imagem inteira"]), string.Join(" | ", player.TestMenu()));
        Drag(player.TestCropToScreen(1, 1), player.TestCropToScreen(0.5, 0.5));
        Check("arrastar o canto: a moldura", Near(player.TestCropSelection, (0, 0, 0.5, 0.5)), $"{player.TestCropSelection}");
        if (player.Snapshot() is { } s1) InterpTest.Png(Path.Combine(folder, "moldura.png"), s1.pixels, s1.width, 0, 0, s1.width, s1.height);
        Click(player.TestCropButton(Accept));
        Check("Aceitar: cortada", !player.TestCropping && Near(player.TestCrop, (0, 0, 0.5, 0.5)), $"{player.TestCrop}");
        Pump(0.5);
        var shot = player.Snapshot()!.Value;
        InterpTest.Png(Path.Combine(folder, "cortada.png"), shot.pixels, shot.width, 0, 0, shot.width, shot.height);
        int greenOrBlue = 0;
        for (int i = 0; i < shot.pixels.Length; i += 16) if (shot.pixels[i + 1] > 170 && shot.pixels[i + 2] < 90 || shot.pixels[i] > 170 && shot.pixels[i + 2] < 90) greenOrBlue++;
        Check("tela: só o vermelho, ocupando a janela", Name(Screen(cw / 2.0, ch / 2.0)) == "vermelho" && greenOrBlue == 0, $"centro {Name(Screen(cw / 2.0, ch / 2.0))}, pontos verdes/azuis {greenOrBlue}");
        var sent = player.TestComfyArea()!.Value;
        Check("G manda só a parte cortada", sent.w == 400 && sent.h == 300 && Name(Px(sent.sent, sent.w, sent.h, 0.05, 0.05)) == "vermelho" && Name(Px(sent.sent, sent.w, sent.h, 0.95, 0.95)) == "vermelho",
            $"{sent.w}x{sent.h}, cantos {Name(Px(sent.sent, sent.w, sent.h, 0.05, 0.05))} {Name(Px(sent.sent, sent.w, sent.h, 0.95, 0.95))}");
        string saved = Path.Combine(folder, "salva.png");
        if (File.Exists(saved)) File.Delete(saved);
        Environment.SetEnvironmentVariable("MKV_SAVE_AS", saved);
        player.TestSavePhoto();
        Wait(() => File.Exists(saved), 10);
        Environment.SetEnvironmentVariable("MKV_SAVE_AS", null);
        if (File.Exists(saved))
        {
            var (pixels, w, h) = ImageFile.LoadPixels(saved);
            Check("Ctrl+S salva só a parte cortada", w == 400 && h == 300 && Name(Px(pixels, w, h, 0.02, 0.02)) == "vermelho" && Name(Px(pixels, w, h, 0.98, 0.98)) == "vermelho",
                $"{w}x{h}, cantos {Name(Px(pixels, w, h, 0.02, 0.02))} {Name(Px(pixels, w, h, 0.98, 0.98))}");
        }
        else Check("Ctrl+S salva só a parte cortada", false, "não salvou");

        // 2) Um traço branco (F3) no meio da parte cortada: na tela e no G, no mesmo lugar.
        Key(0x72);
        player.TestAnnoAction(14); // branco
        var (ax, ay) = player.TestImageToScreen(0.25, 0.25);
        Drag((ax - 30, ay), (ax + 30, ay));
        Check("traço com corte: na tela, no lugar", Name(Screen(ax, ay)) == "branco", Name(Screen(ax, ay)));
        sent = player.TestComfyArea()!.Value;
        Check("traço com corte: no G, no meio", Name(Px(sent.sent, sent.w, sent.h, 0.5, 0.5)) == "branco" && Name(Px(sent.sent, sent.w, sent.h, 0.5, 0.3)) == "vermelho",
            $"{Name(Px(sent.sent, sent.w, sent.h, 0.5, 0.5))}, acima {Name(Px(sent.sent, sent.w, sent.h, 0.5, 0.3))}");
        player.TestClearStrokes();
        Key(0x72);

        // 3) Girar desfaz o corte; com giro de 90°, cortar a metade da esquerda da tela.
        Key(0x2E); Key(0x2E); // Delete duas vezes: 90°
        Check("girar desfaz o corte", Near(player.TestCrop, (0, 0, 1, 1)), $"{player.TestCrop}");
        Key(0x73);
        Drag(player.TestCropToScreen(1, 0.5), player.TestCropToScreen(0.5, 0.5)); // lado direito até o meio
        Check("lado direito: só a largura", Near(player.TestCropSelection, (0, 0, 0.5, 1)), $"{player.TestCropSelection}");
        Key(0x0D); // Enter
        Check("Enter aceita", !player.TestCropping && Near(player.TestCrop, (0, 0, 0.5, 1)), $"{player.TestCrop}");
        sent = player.TestComfyArea()!.Value;
        var sentColors = new[] { Name(Px(sent.sent, sent.w, sent.h, 0.5, 0.25)), Name(Px(sent.sent, sent.w, sent.h, 0.5, 0.75)) }.Order().ToArray();
        if (File.Exists(saved)) File.Delete(saved);
        Environment.SetEnvironmentVariable("MKV_SAVE_AS", saved);
        player.TestSavePhoto();
        Wait(() => File.Exists(saved), 10);
        Environment.SetEnvironmentVariable("MKV_SAVE_AS", null);
        if (File.Exists(saved))
        {
            var (pixels, w, h) = ImageFile.LoadPixels(saved);
            var savedColors = new[] { Name(Px(pixels, w, h, 0.25, 0.5)), Name(Px(pixels, w, h, 0.75, 0.5)) }.Order().ToArray();
            Check("girada 90°: G 300x800 e o arquivo (sem giro) 800x300, com a mesma parte", sent.w == 300 && sent.h == 800 && w == 800 && h == 300 && sentColors.SequenceEqual(savedColors),
                $"G {sent.w}x{sent.h} {string.Join("+", sentColors)}, arquivo {w}x{h} {string.Join("+", savedColors)}");
        }
        else Check("girada 90°: o arquivo", false, "não salvou");
        Key(0x2D); Key(0x2D); // volta o giro

        // 4) Proporção 1:1, mover, "Imagem inteira", Esc cancela.
        Key(0x73);
        Click(player.TestCropButton(Ratio + 2));
        var sel = player.TestCropSelection;
        Check("1:1: quadrada (em pixels), no centro", Math.Abs(sel.w * 800 - sel.h * 600) < 3 && Math.Abs(sel.x + sel.w / 2 - 0.5) < 0.01, $"{sel} = {sel.w * 800:0}x{sel.h * 600:0}");
        var (mx, my) = player.TestCropToScreen(0.5, 0.5);
        var (ex, _) = player.TestCropToScreen(1, 0.5);
        Drag((mx, my), (ex + 50, my)); // arrastar por dentro, além da borda
        sel = player.TestCropSelection;
        Check("mover: vai até a borda e para", Math.Abs(sel.x + sel.w - 1) < 0.01 && Math.Abs(sel.w * 800 - sel.h * 600) < 3, $"{sel}");
        Click(player.TestCropButton(Full));
        Check("Imagem inteira", Near(player.TestCropSelection, (0, 0, 1, 1)), $"{player.TestCropSelection}");
        Drag(player.TestCropToScreen(0, 0), player.TestCropToScreen(0.3, 0.3));
        Key(0x1B); // Esc
        Check("Esc cancela (fica como estava)", !player.TestCropping && Near(player.TestCrop, (0, 0, 1, 1)), $"{player.TestCrop}");

        // 5) Espelhar espelha o corte (continua na mesma parte da foto: o vermelho).
        Key(0x73);
        Drag(player.TestCropToScreen(1, 1), player.TestCropToScreen(0.5, 0.5));
        Key(0x0D);
        Key(0x24); // Home: espelho horizontal
        Pump(0.5);
        Check("espelhar: o corte espelha junto", Near(player.TestCrop, (0.5, 0, 0.5, 0.5)) && Name(Screen(cw / 2.0, ch / 2.0)) == "vermelho", $"{player.TestCrop}, centro {Name(Screen(cw / 2.0, ch / 2.0))}");
        Key(0x24);

        // 6) Vídeo: F4 pausa; o corte vale para o G.
        player.Open(clip);
        Wait(() => player.State.playing && player.State.fileTime > 0.3, 10);
        Check("outro arquivo: sem corte", Near(player.TestCrop, (0, 0, 1, 1)), $"{player.TestCrop}");
        Key(0x73);
        Check("vídeo: F4 pausa", player.TestCropping && !player.State.playing, $"cortando {player.TestCropping}, tocando {player.State.playing}");
        Drag(player.TestCropToScreen(0, 0), player.TestCropToScreen(0.5, 0.5));
        Click(player.TestCropButton(Accept));
        sent = player.TestComfyArea()!.Value;
        Check("vídeo: G com o quadro cortado", sent.w == 320 && sent.h == 180, $"{sent.w}x{sent.h}");
        player.Dispose();
        Console.WriteLine(failures == 0 ? "tudo certo" : $"{failures} falha(s)");
        return failures == 0 ? 0 : 1;
    }

    // --webfullscreentest <pasta>: do player em tela cheia para o navegador (botão direito → "Ir para o navegador" e F2):
    // o navegador abre em janela com a barra de cima desenhada (antes ficava preta) e, de volta, o player fica em tela
    // cheia de novo; no F11, o mouse no topo mostra a barra desenhada. Usar com MKV_SETTINGS, MKV_LOG e MKV_WEB_PROFILE.
    public static int WebFullscreen(string folder)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        Directory.CreateDirectory(folder);
        string png = Path.Combine(folder, "cinza.png");
        var gray = new byte[800 * 600 * 4];
        for (int i = 0; i < gray.Length; i += 4) (gray[i], gray[i + 1], gray[i + 2], gray[i + 3]) = (128, 128, 128, 255);
        InterpTest.Png(png, gray, 800, 0, 0, 800, 600);
        using var gpu = new Gpu();
        int failures = 0;
        void Check(string what, bool ok, string detail)
        {
            Console.WriteLine($"{(ok ? "OK    " : "FALHOU")} {what}: {detail}");
            if (!ok) failures++;
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
        bool Wait(Func<bool> condition, double seconds)
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            while (!condition() && clock.Elapsed.TotalSeconds < seconds) Pump(0.1);
            return condition();
        }
        var player = new Player(gpu, new object()) { IgnoreMouseLeave = true };
        IntPtr Pos(int x, int y) => (IntPtr)((y << 16) | (x & 0xffff));
        void Key(int vk) { PlayerNative.PostMessage(player.Hwnd, PlayerNative.WM_KEYDOWN, vk, IntPtr.Zero); Pump(0.6); }
        // A barra de cima na tela: o mais claro da faixa (preta = 0).
        int Bars(bool pump = true)
        {
            if (pump) Pump(0.5); // (no F11, o app olha o mouse de verdade 10x por segundo: sem esperar, para o topo valer)
            var shot = player.Snapshot()!.Value;
            int top = Math.Max(1, player.TestWebTop), brightest = 0;
            for (int y = 2; y < Math.Min(top, shot.height); y += 3)
            for (int x = 0; x < shot.width; x += 7)
            {
                int i = (y * shot.width + x) * 4;
                brightest = Math.Max(brightest, Math.Max(shot.pixels[i], Math.Max(shot.pixels[i + 1], shot.pixels[i + 2])));
            }
            return brightest;
        }

        player.Open(png);
        Wait(() => player.TestTitle.Contains("foto"), 10);
        Pump(1);
        Key('F');
        Check("player em tela cheia", player.State.fullscreen, $"{player.State.fullscreen}");
        var shot0 = player.Snapshot()!.Value;
        player.TestMenuChoice = "Voltar para o navegador";
        PlayerNative.PostMessage(player.Hwnd, PlayerNative.WM_MOUSEMOVE, IntPtr.Zero, Pos(shot0.width / 2, shot0.height / 2));
        PlayerNative.PostMessage(player.Hwnd, 0x0205, IntPtr.Zero, Pos(shot0.width / 2, shot0.height / 2));
        Wait(() => player.WebMode, 5);
        Wait(() => player.TestTabCount > 0, 20);
        Pump(2);
        int bars = Bars();
        if (player.Snapshot() is { } s1) InterpTest.Png(Path.Combine(folder, "navegador.png"), s1.pixels, s1.width, 0, 0, s1.width, s1.height);
        Check("botão direito → navegador: em janela, com a barra de cima desenhada", player.WebMode && !player.State.fullscreen && player.TestWebTop > 0 && bars > 25,
            $"navegador {player.WebMode}, tela cheia {player.State.fullscreen}, faixa {player.TestWebTop} px, brilho {bars}");
        Key(0x71); // F2
        Check("F2 de volta ao player: tela cheia de novo", !player.WebMode && player.State.fullscreen, $"navegador {player.WebMode}, tela cheia {player.State.fullscreen}");
        Key(0x71);
        bars = Bars();
        Check("F2 de novo (da tela cheia): navegador em janela, com a barra", player.WebMode && !player.State.fullscreen && bars > 25, $"tela cheia {player.State.fullscreen}, brilho {bars}");
        player.TestToggleF11();
        Pump(0.5);
        Check("F11: tela cheia sem a barra", player.State.fullscreen && player.TestWebTop == 0, $"tela cheia {player.State.fullscreen}, faixa {player.TestWebTop} px");
        player.TestPeek(true);
        int peekTop = player.TestWebTop;
        bars = Bars(pump: false);
        Check("F11 com o mouse no topo: a barra desenhada", peekTop > 0 && bars > 25, $"faixa {peekTop} px, brilho {bars}");
        player.TestPeek(false);
        player.TestToggleF11();
        Pump(0.5);
        Check("F11 de novo: em janela", !player.State.fullscreen && player.TestWebTop > 0, $"tela cheia {player.State.fullscreen}");
        Key(0x71);
        Check("de volta ao player: a tela cheia de antes do navegador", !player.WebMode && player.State.fullscreen, $"tela cheia {player.State.fullscreen}");
        Key('F'); // player em janela
        Key(0x71);
        Key(0x71);
        Check("player em janela → navegador → player: continua em janela", !player.WebMode && !player.State.fullscreen, $"tela cheia {player.State.fullscreen}");
        player.Dispose();
        Console.WriteLine(failures == 0 ? "tudo certo" : $"{failures} falha(s)");
        return failures == 0 ? 0 : 1;
    }

    // --webmenutest <pasta>: clicar na barra de endereço seleciona o endereço inteiro (o 2º clique põe o cursor); o botão
    // direito da barra tem "Colar e ir" (com um endereço copiado; com texto, "Colar e pesquisar"; sem nada, cinza); e o
    // botão direito num link (ou com um endereço no texto selecionado) tem "Abrir link em uma nova aba" (atrás, ao lado).
    // Usar com MKV_SETTINGS, MKV_LOG e MKV_WEB_PROFILE.
    public static int WebMenu(string folder)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        Directory.CreateDirectory(folder);
        string pageFile = Path.Combine(folder, "links.html");
        File.WriteAllText(pageFile, "<!doctype html><html lang=pt-BR><meta charset=utf-8><title>Links</title><body style='margin:0;font:20px Segoe UI'>" +
            "<p style='padding:30px'><a id=l href='https://example.com/?link'>um link para o example</a></p>" +
            "<p id=t style='padding:30px'>veja em https://www.iana.org/domains, por favor</p>");
        using var gpu = new Gpu();
        int failures = 0;
        void Check(string what, bool ok, string detail)
        {
            Console.WriteLine($"{(ok ? "OK    " : "FALHOU")} {what}: {detail}");
            if (!ok) failures++;
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
        bool Wait(Func<bool> condition, double seconds)
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            while (!condition() && clock.Elapsed.TotalSeconds < seconds) Pump(0.1);
            return condition();
        }
        var player = new Player(gpu, new object()) { IgnoreMouseLeave = true };
        string Eval(string js)
        {
            var task = player.TestWebEval(js);
            var clock = System.Diagnostics.Stopwatch.StartNew();
            while (!task.IsCompleted && clock.Elapsed.TotalSeconds < 4) Pump(0.02);
            string raw = task.IsCompletedSuccessfully ? task.Result : "(sem resposta)";
            try { return System.Text.Json.JsonSerializer.Deserialize<string>(raw) ?? raw; } catch { return raw; }
        }
        void Cdp(string method, object args)
        {
            var task = player.TestCdp(method, System.Text.Json.JsonSerializer.Serialize(args));
            var clock = System.Diagnostics.Stopwatch.StartNew();
            while (!task.IsCompleted && clock.Elapsed.TotalSeconds < 3) Pump(0.02);
        }
        string Url() => player.TestTabs.First(t => t.active).url;
        string pageUrl = new Uri(pageFile).AbsoluteUri;
        player.TestSetSessionTabs(pageUrl);
        player.ShowBrowser();
        Wait(() => player.WebReady, 40);
        Wait(() => Url().StartsWith("file:"), 15);
        Pump(2);

        // Clique na barra: tudo selecionado; o segundo clique (já nela) põe o cursor.
        player.TestFocusPage();
        Pump(0.5);
        player.TestAddressClick();
        Pump(0.3);
        var sel = player.TestAddressSelection;
        Check("clique na barra: o endereço inteiro selecionado", player.TestAddressFocused && sel.start == 0 && sel.end == sel.length && sel.length > 0, $"foco {player.TestAddressFocused}, seleção {sel}");
        player.TestAddressClick();
        Pump(0.3);
        sel = player.TestAddressSelection;
        Check("segundo clique: o cursor no ponto (sem seleção)", sel.start == sel.end, $"{sel}");

        // Botão direito na barra.
        player.TestClipboard = "";
        Check("nada copiado: \"Colar e ir\" cinza", player.TestAddressMenu().Contains("Colar e ir (cinza)"), string.Join(" · ", player.TestAddressMenu()));
        player.TestClipboard = "gatos fofos";
        Check("texto copiado: \"Colar e pesquisar\"", player.TestAddressMenu().Contains("Colar e pesquisar"), string.Join(" · ", player.TestAddressMenu()));
        player.TestClipboard = "https://example.com/?colado";
        var items = player.TestAddressMenu();
        Check("endereço copiado: \"Colar e ir\" (e o menu de editar)", items.Contains("Colar e ir") && items.Contains("Colar") && items.Contains("Selecionar tudo") && items.Contains("Copiar (cinza)"),
            string.Join(" · ", items));
        player.TestAddressMenuRun("Colar e ir");
        Check("Colar e ir: abre o endereço sem Enter", Wait(() => Url().Contains("example.com/?colado"), 20), Url());
        player.TestClipboard = null;
        player.TestWebNavigate(pageUrl);
        Wait(() => Url().StartsWith("file:"), 15);
        Pump(1.5);

        // Botão direito num link e num endereço no texto selecionado.
        player.TestSuppressMenu = true;
        void Right(string id)
        {
            player.TestMenuLabels = [];
            var p = Eval($"(() => {{ const r = document.getElementById('{id}').getBoundingClientRect(); return Math.round(r.left + 20) + ',' + Math.round(r.top + r.height / 2); }})()").Split(',');
            if (p.Length != 2 || !int.TryParse(p[0], out int x) || !int.TryParse(p[1], out int y)) return;
            Cdp("Input.dispatchMouseEvent", new { type = "mouseMoved", x, y });
            Cdp("Input.dispatchMouseEvent", new { type = "mousePressed", x, y, button = "right", buttons = 2, clickCount = 1 });
            Cdp("Input.dispatchMouseEvent", new { type = "mouseReleased", x, y, button = "right", buttons = 0, clickCount = 1 });
            Pump(1.2);
        }
        Right("l");
        Check("link: \"Abrir link em uma nova aba\" no topo", player.TestMenuLabels.FirstOrDefault() == "Abrir link em uma nova aba", string.Join(" · ", player.TestMenuLabels.Take(4)));
        int before = player.TestTabCount;
        player.TestPickMenuLink();
        Wait(() => player.TestTabs.Any(t => t.url.Contains("example.com/?link")), 15); // a aba de trás abre sozinha (o endereço vem quando ela carrega)
        var tabs = player.TestTabs;
        int active = Array.FindIndex(tabs, t => t.active);
        Check("abre em uma aba nova, atrás, ao lado desta", player.TestTabCount == before + 1 && tabs[active].url.StartsWith("file:") && active + 1 < tabs.Length && tabs[active + 1].url.Contains("example.com/?link"),
            string.Join(" | ", tabs.Select(t => (t.active ? "*" : "") + t.url)));
        Eval("(() => { const r = document.createRange(); r.selectNodeContents(document.getElementById('t')); getSelection().removeAllRanges(); getSelection().addRange(r); })()");
        Right("t");
        Check("endereço no texto selecionado: \"Abrir link em uma nova aba\" e a pesquisa",
            player.TestMenuLabels.FirstOrDefault() == "Abrir link em uma nova aba" && player.TestMenuLabels.ElementAtOrDefault(1)?.StartsWith("Pesquisar no Google") == true,
            string.Join(" · ", player.TestMenuLabels.Take(4)));
        player.TestPickMenuLink();
        Check("abre o endereço do texto", Wait(() => player.TestTabs.Any(t => t.url.Contains("iana.org/domains")), 5), string.Join(" | ", player.TestTabs.Select(t => t.url)));
        Eval("getSelection().removeAllRanges()");
        Right("t");
        Check("texto sem link, sem seleção: sem o item", !player.TestMenuLabels.Contains("Abrir link em uma nova aba"), string.Join(" · ", player.TestMenuLabels.Take(4)));
        player.TestSuppressMenu = false;
        player.Dispose();
        Console.WriteLine(failures == 0 ? "tudo certo" : $"{failures} falha(s)");
        return failures == 0 ? 0 : 1;
    }

    // --webopenbuttontest <pasta>: o botão "Abrir no player" sobre os vídeos — aparece com o mouse sobre um vídeo grande
    // (no canto de cima), não nos pequenos, some ao sair; clicar abre o vídeo no player. Numa página local e no YouTube
    // (que troca o menu do botão direito pelo dele). Usar com MKV_SETTINGS, MKV_LOG e MKV_WEB_PROFILE.
    public static int WebOpenButton(string folder)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        Directory.CreateDirectory(folder);
        string clip = Path.Combine(folder, "clip.mp4"), pageFile = Path.Combine(folder, "videos.html");
        MediaFactory.MFStartup(true).CheckError();
        PlayerTest.WriteClip(clip);
        File.WriteAllText(pageFile, "<!doctype html><html lang=pt-BR><meta charset=utf-8><title>Vídeos</title><body style='margin:0;font:16px Segoe UI'>" +
            "<div style='position:relative;margin:40px;width:640px;height:360px'><video id=big src='clip.mp4' width=640 height=360 autoplay muted loop></video>" +
            "<div style='position:absolute;inset:0'></div></div>" + // camada por cima (como os controles dos sites)
            "<video id=small src='clip.mp4' width=120 height=68 autoplay muted loop style='margin:40px'></video>");
        // A página e o vídeo por http (como um site: o app não abre vídeos de páginas file://), com pedidos por partes.
        int port = 18000 + Random.Shared.Next(1000);
        var server = new System.Net.HttpListener();
        server.Prefixes.Add($"http://localhost:{port}/");
        server.Start();
        new Thread(() =>
        {
            while (server.IsListening)
            {
                System.Net.HttpListenerContext context;
                try { context = server.GetContext(); } catch { return; }
                try
                {
                    string file = Path.Combine(folder, Path.GetFileName(context.Request.Url!.AbsolutePath));
                    var response = context.Response;
                    if (!File.Exists(file)) { response.StatusCode = 404; response.Close(); continue; }
                    var bytes = File.ReadAllBytes(file);
                    response.ContentType = file.EndsWith(".mp4") ? "video/mp4" : "text/html; charset=utf-8";
                    response.Headers["Accept-Ranges"] = "bytes";
                    long from = 0, to = bytes.Length - 1;
                    if (context.Request.Headers["Range"] is { } range && range.StartsWith("bytes="))
                    {
                        var parts = range[6..].Split('-');
                        from = long.Parse(parts[0]);
                        if (parts[1].Length > 0) to = Math.Min(to, long.Parse(parts[1]));
                        response.StatusCode = 206;
                        response.Headers["Content-Range"] = $"bytes {from}-{to}/{bytes.Length}";
                    }
                    response.ContentLength64 = to - from + 1;
                    response.OutputStream.Write(bytes, (int)from, (int)(to - from + 1));
                    response.Close();
                }
                catch { }
            }
        }) { IsBackground = true }.Start();
        string pageUrl = $"http://localhost:{port}/videos.html";
        using var gpu = new Gpu();
        int failures = 0;
        void Check(string what, bool ok, string detail)
        {
            Console.WriteLine($"{(ok ? "OK    " : "FALHOU")} {what}: {detail}");
            if (!ok) failures++;
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
        bool Wait(Func<bool> condition, double seconds)
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            while (!condition() && clock.Elapsed.TotalSeconds < seconds) Pump(0.1);
            return condition();
        }
        var player = new Player(gpu, new object()) { IgnoreMouseLeave = true };
        string Eval(string js)
        {
            var task = player.TestWebEval(js);
            var clock = System.Diagnostics.Stopwatch.StartNew();
            while (!task.IsCompleted && clock.Elapsed.TotalSeconds < 4) Pump(0.02);
            string raw = task.IsCompletedSuccessfully ? task.Result : "(sem resposta)";
            try { return System.Text.Json.JsonSerializer.Deserialize<string>(raw) ?? raw; } catch { return raw; }
        }
        void Cdp(string method, object args)
        {
            var task = player.TestCdp(method, System.Text.Json.JsonSerializer.Serialize(args));
            var clock = System.Diagnostics.Stopwatch.StartNew();
            while (!task.IsCompleted && clock.Elapsed.TotalSeconds < 3) Pump(0.02);
        }
        string Url() => player.TestTabs.First(t => t.active).url;
        (int x, int y)? Center(string selector)
        {
            var p = Eval($"(() => {{ const e = document.querySelector('{selector}'); if (!e) return ''; const r = e.getBoundingClientRect(); return Math.round(r.left + r.width / 2) + ',' + Math.round(r.top + r.height / 2); }})()").Split(',');
            return p.Length == 2 && int.TryParse(p[0], out int x) && int.TryParse(p[1], out int y) ? (x, y) : null;
        }
        void Move((int x, int y) at) { Cdp("Input.dispatchMouseEvent", new { type = "mouseMoved", at.x, at.y }); Pump(0.4); }
        // O botão: "" = não existe/escondido; senão "x,y,largura,altura" (e o centro, para clicar).
        string Button() => Eval("(() => { const h = document.getElementById('__mkvision_open'); const b = h && h.shadowRoot && h.shadowRoot.querySelector('button'); " +
            "if (!b || b.style.display === 'none') return ''; const r = b.getBoundingClientRect(); return [r.left, r.top, r.width, r.height].map(Math.round).join(','); })()");
        void ClickButton(string video)
        {
            Move(Center(video)!.Value); // o mouse sobre o vídeo de novo (a página pode ter mudado de lugar)
            string now = Button();
            if (now == "") { Console.WriteLine("(o botão sumiu antes do clique)"); return; }
            var r = now.Split(',').Select(int.Parse).ToArray();
            int x = r[0] + r[2] / 2, y = r[1] + r[3] / 2;
            Cdp("Input.dispatchMouseEvent", new { type = "mouseMoved", x, y });
            Cdp("Input.dispatchMouseEvent", new { type = "mousePressed", x, y, button = "left", buttons = 1, clickCount = 1 });
            Cdp("Input.dispatchMouseEvent", new { type = "mouseReleased", x, y, button = "left", buttons = 0, clickCount = 1 });
        }

        player.ShowBrowser();
        Wait(() => player.WebReady, 40);
        player.TestWebNavigate(pageUrl);
        Wait(() => Url().StartsWith(pageUrl), 15);
        Wait(() => Eval("String(!!document.getElementById('big') && document.getElementById('big').currentTime > 0.2)") == "true", 10);
        Check("antes do mouse: sem botão", Button() == "", Button());
        Move(Center("#big")!.Value);
        string shown = Button();
        string bigRect = Eval("(() => { const r = document.getElementById('big').getBoundingClientRect(); return Math.round(r.left) + ',' + Math.round(r.top); })()");
        var b = shown.Split(',');
        var v = bigRect.Split(',');
        Check("mouse sobre o vídeo (mesmo com camada por cima): o botão no canto de cima", shown != "" && int.Parse(b[0]) == int.Parse(v[0]) + 12 && int.Parse(b[1]) == int.Parse(v[1]) + 12,
            $"botão {shown}, vídeo em {bigRect}");
        WindowShot(player.Hwnd, Path.Combine(folder, "botao.png"));
        Move(Center("#small")!.Value);
        Pump(1);
        Check("vídeo pequeno: sem botão (e saiu do grande: some)", Button() == "", Button());
        ClickButton("#big");
        Check("clicar: abre o vídeo no player", Wait(() => !player.WebMode && player.State.fileTime > 0.3, 20), $"modo {(player.WebMode ? "navegador" : "player")}, vídeo em {player.State.fileTime:0.00} s — {player.TestTitle}");

        // YouTube: o menu do botão direito é o dele; o botão sobre o vídeo resolve.
        player.ShowBrowser();
        Pump(1);
        player.TestWebNavigate("https://www.youtube.com/watch?v=jNQXAC9IVRw");
        Wait(() => Url().Contains("youtube.com/watch"), 20);
        bool ready = Wait(() => Center("video") is { } c && Eval("(() => { const r = document.querySelector('video').getBoundingClientRect(); return String(r.width > 300); })()") == "true", 30);
        Pump(2);
        if (ready)
        {
            Move(Center("video")!.Value);
            shown = Button();
            WindowShot(player.Hwnd, Path.Combine(folder, "youtube.png"));
            Check("YouTube: o botão sobre o vídeo", shown != "", shown);
            if (shown != "")
            {
                ClickButton("video");
                Check("YouTube: clicar abre no player", Wait(() => !player.WebMode && player.TestTitle.Contains("zoo") && player.State.fileTime > 0.3, 60), $"modo {(player.WebMode ? "navegador" : "player")}, {player.State.fileTime:0.00} s — {player.TestTitle}");
            }
        }
        else Check("YouTube: a página do vídeo abriu", false, Url());
        player.Dispose();
        server.Stop();
        Console.WriteLine(failures == 0 ? "tudo certo" : $"{failures} falha(s)");
        return failures == 0 ? 0 : 1;
    }

    // --updateflowtest <pasta>: a atualização de ponta a ponta, fingindo ser a versão 1.0.0-beta e com a "pasta do app"
    // em <pasta>\app: acha a versão nova no GitHub, baixa o .zip, fecha e o script copia por cima (sem abrir de novo).
    // Depois de sair, confira <pasta>\app: o MKVisionDesktop.exe novo e o "fica.txt" (o que não vem no .zip) intacto.
    public static int UpdateFlow(string folder)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        string app = Path.Combine(folder, "app");
        Directory.CreateDirectory(Path.Combine(app, "Models", "rife"));
        File.WriteAllText(Path.Combine(app, "Models", "rife", "fica.txt"), "o que não vem no .zip fica");
        Environment.SetEnvironmentVariable("MKV_VERSION", "1.0.0-beta");
        Environment.SetEnvironmentVariable("MKV_UPDATE_DIR", app);
        Environment.SetEnvironmentVariable("MKV_UPDATE_NORESTART", "1");
        using var gpu = new Gpu();
        void Pump(double seconds)
        {
            var until = DateTime.UtcNow.AddSeconds(seconds);
            while (DateTime.UtcNow < until)
            {
                while (Native.PeekMessage(out var msg, IntPtr.Zero, 0, 0, 1)) { Native.TranslateMessage(ref msg); Native.DispatchMessage(ref msg); }
                Thread.Sleep(5);
            }
        }
        var player = new Player(gpu, new object()) { IgnoreMouseLeave = true };
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (player.TestUpdateVersion is null && clock.Elapsed.TotalSeconds < 30) Pump(0.2);
        Console.WriteLine($"{(player.TestUpdateVersion is not null ? "OK    " : "FALHOU")} versão nova achada: {player.TestUpdateVersion ?? "nenhuma"} (esta: {Updater.Current})");
        if (player.TestUpdateVersion is null) return 1;
        Console.WriteLine($"menu: {string.Join(" | ", player.TestMenu().Take(2))}");
        player.TestUpdateAnswer = true;
        player.TestStartUpdate();
        // Baixa e fecha sozinho (WM_CLOSE); o script espera este processo sair para copiar.
        clock.Restart();
        while (!player.TestClosed && clock.Elapsed.TotalSeconds < 600) Pump(0.5);
        Console.WriteLine($"{(player.TestClosed ? "OK    " : "FALHOU")} baixou e fechou em {clock.Elapsed.TotalSeconds:0} s");
        return player.TestClosed ? 0 : 1;
    }

    public static int Tabs(string folder)
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
                while (Native.PeekMessage(out var msg, IntPtr.Zero, 0, 0, 1)) { Native.TranslateMessage(ref msg); Native.DispatchMessage(ref msg); }
                Thread.Sleep(5);
            }
        }
        int failures = 0;
        void Check(string what, bool ok, string detail)
        {
            Console.WriteLine($"{(ok ? "OK    " : "FALHOU")} {what}: {detail}");
            if (!ok) failures++;
        }
        var books = player.TestBookmarks;
        books.Children!.Clear();
        var videos = BookmarkNode.Folder("Vídeos");
        videos.Children!.Add(BookmarkNode.Link("YouTube", "https://www.youtube.com/"));
        books.Children.Add(videos);
        books.Children.Add(BookmarkNode.Link("Wikipédia", "https://pt.wikipedia.org/"));
        books.Children.Add(BookmarkNode.Link("YouTube", "https://www.youtube.com/"));
        PlayerNative.PostMessage(player.Hwnd, PlayerNative.WM_KEYDOWN, 0x71, IntPtr.Zero);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (!player.WebReady && clock.Elapsed.TotalSeconds < 30) Pump(0.1);
        int before = player.TestTabCount;
        player.TestNewTab("https://www.youtube.com/");
        Pump(5);
        player.TestNewTab("https://pt.wikipedia.org/");
        Pump(5);
        Check("abas abertas", player.TestTabCount == before + 2, $"{before} → {player.TestTabCount}");
        Pump(2);
        Console.WriteLine($"bloqueador ligado: {AdBlock.On}");
        WindowShot(player.Hwnd, Path.Combine(folder, "abas.png"));
        // Arrastar o ícone do site para a barra de favoritos, entre o 1º e o 2º favorito.
        void Mouse(uint message, int x, int y) => PlayerNative.PostMessage(player.Hwnd, message, IntPtr.Zero, (IntPtr)((y << 16) | (x & 0xffff)));
        player.IgnoreMouseLeave = true;
        int count = books.Children.Count;
        var (sx, sy) = player.TestSiteIconPoint;
        var (b1, by) = player.TestBookmarkPoint(1);
        Mouse(PlayerNative.WM_MOUSEMOVE, sx, sy);
        Mouse(PlayerNative.WM_LBUTTONDOWN, sx, sy);
        for (int i = 1; i <= 8; i++) Mouse(PlayerNative.WM_MOUSEMOVE, sx + (b1 - player.UiScaled(30) - sx) * i / 8, sy + (by - sy) * i / 8);
        Pump(0.3);
        Mouse(PlayerNative.WM_LBUTTONUP, b1 - player.UiScaled(30), by);
        Pump(0.8);
        Check("arrastar o site para os favoritos", books.Children.Count == count + 1 && books.Children[1].Url is { } u && u.Contains("wikipedia"),
            string.Join(", ", books.Children.Select(b => b.Name)));
        // Arrastar o favorito novo para o fim.
        var (n1, ny) = player.TestBookmarkPoint(1);
        var (last, _) = player.TestBookmarkPoint(books.Children.Count - 1);
        Mouse(PlayerNative.WM_MOUSEMOVE, n1, ny);
        Mouse(PlayerNative.WM_LBUTTONDOWN, n1, ny);
        for (int i = 1; i <= 8; i++) Mouse(PlayerNative.WM_MOUSEMOVE, n1 + (last + player.UiScaled(40) - n1) * i / 8, ny);
        Pump(0.3);
        Mouse(PlayerNative.WM_LBUTTONUP, last + player.UiScaled(40), ny);
        Pump(0.8);
        Check("mudar a ordem", books.Children[^1].Url is { } v && v.Contains("wikipedia"), string.Join(", ", books.Children.Select(b => b.Name)));
        WindowShot(player.Hwnd, Path.Combine(folder, "favoritos.png"));
        player.TestCloseTab();
        Pump(1);
        Check("fechar aba", player.TestTabCount == before + 1, $"{player.TestTabCount} abas, ativa: {player.TestWebTitle}");
        player.Dispose();
        Console.WriteLine(failures == 0 ? "Tudo certo." : $"Falhou: {failures}");
        return failures == 0 ? 0 : 1;
    }

    public static int Run(string folder, string? realPage)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        Directory.CreateDirectory(folder);
        string clip = Path.Combine(Path.GetTempPath(), "mkvision-web-test.mp4");
        MediaFactory.MFStartup(true).CheckError();
        PlayerTest.WriteClip(clip);
        string videoUrl = LocalMediaServer.Register(() => File.OpenRead(clip), "video/mp4", ".mp4");
        string pageFile = Path.Combine(folder, "pagina.html");
        File.WriteAllText(pageFile, $"<!doctype html><meta charset=utf-8><title>Página de teste</title><body style='background:#333;color:#fff;font:16px Segoe UI'>" +
            $"<h1>Página de teste do MK Vision</h1><video id=v src='{videoUrl}' width=640 height=360 controls></video></body>");

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
        bool Wait(Func<bool> condition, double seconds)
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            while (!condition() && clock.Elapsed.TotalSeconds < seconds) Pump(0.1);
            return condition();
        }
        int failures = 0;
        void Check(string what, bool ok, string detail)
        {
            Console.WriteLine($"{(ok ? "OK    " : "FALHOU")} {what}: {detail}");
            if (!ok) failures++;
        }
        void Save(string name)
        {
            if (player.Snapshot() is { } s) InterpTest.Png(Path.Combine(folder, name), s.pixels, s.width, 0, 0, s.width, Math.Min(s.height, player.UiScaled(46)));
        }

        PlayerNative.PostMessage(player.Hwnd, PlayerNative.WM_KEYDOWN, 0x71, IntPtr.Zero); // F2
        Check("navegador abriu", Wait(() => player.WebReady, 30), player.WebReady ? "pronto" : "não ficou pronto");
        player.TestWebNavigate(new Uri(pageFile).AbsoluteUri);
        Check("página carregou", Wait(() => player.TestWebTitle == "Página de teste", 15), player.TestWebTitle);
        Pump(1);
        Save("barra.png");
        WindowShot(player.Hwnd, Path.Combine(folder, "janela.png"));
        player.TestWebScript("document.getElementById('v').play().catch(()=>{}); setTimeout(() => document.dispatchEvent(new KeyboardEvent('keydown', { key: '.', bubbles: true })), 500);");
        Check("vídeo foi para o player", Wait(() => !player.WebMode && player.State.fileTime > 0.3, 20),
            $"modo {(player.WebMode ? "navegador" : "player")}, vídeo em {player.State.fileTime:0.00} s — {player.TestTitle}");

        Check("som do vídeo da página", Wait(() => player.HasAudioForTest, 5), player.HasAudioForTest ? "abriu" : "sem som");
        PlayerNative.PostMessage(player.Hwnd, PlayerNative.WM_KEYDOWN, PlayerNative.VK_ESCAPE, IntPtr.Zero);
        Check("ESC volta ao navegador", Wait(() => player.WebMode, 3), player.WebMode ? "navegador" : "continuou no player");

        if (realPage is not null)
        {
            Pump(0.5);
            player.TestWebNavigate(realPage);
            Wait(() => player.TestWebTitle.Length > 0 && player.TestWebTitle != "Página de teste", 20);
            Pump(4);
            Console.WriteLine($"página real: {player.TestWebTitle}");
            WindowShot(player.Hwnd, Path.Combine(folder, "janela-real.png"));
            if (Environment.GetEnvironmentVariable("MKV_DOWNLOADS") is { Length: > 0 } saveTo)
            {
                // Ctrl+S: baixa o vídeo da tela (o site continua no navegador).
                int before = Directory.Exists(saveTo) ? Directory.GetFiles(saveTo).Length : 0;
                player.TestDownload();
                Wait(() => Directory.Exists(saveTo) && Directory.GetFiles(saveTo).Any(f => !f.EndsWith(".part") && new FileInfo(f).Length > 100_000), 90);
                var saved = Directory.Exists(saveTo) ? Directory.GetFiles(saveTo).Where(f => !f.EndsWith(".part")).ToArray() : [];
                Check("Ctrl+S baixou o vídeo", saved.Length > before && player.WebMode,
                    saved.Length > 0 ? $"{Path.GetFileName(saved[^1])} ({new FileInfo(saved[^1]).Length / 1048576.0:0.0} MB)" : "nada");
            }
            player.TestWebScript("document.dispatchEvent(new KeyboardEvent('keydown', { key: '.', bubbles: true }));");
            var opening = System.Diagnostics.Stopwatch.StartNew();
            string titleBefore = player.TestTitle;
            Wait(() => !player.WebMode && player.TestTitle.Contains(" / ") && player.TestTitle != titleBefore && !player.TestTitle.Contains("Página de teste") && player.State.fileTime > 0.05, 40);
            Console.WriteLine($"do \".\" até tocar: {opening.Elapsed.TotalSeconds:0.0} s");
            Check("vídeo da página real no player", Wait(() => !player.WebMode && player.State.fileTime > 1, 40),
                $"modo {(player.WebMode ? "navegador" : "player")}, vídeo em {player.State.fileTime:0.00} s — {player.TestTitle}");
            Check("som", Wait(() => player.HasAudioForTest, 8), player.HasAudioForTest ? "abriu" : "sem som");
            if (Environment.GetEnvironmentVariable("MKV_LONG") == "1")
            {
                // Tocar além do que existia ao abrir (reaberturas) e pular para a frente.
                double t0 = player.State.fileTime;
                var watch = System.Diagnostics.Stopwatch.StartNew();
                double worst = 0, before = t0;
                while (watch.Elapsed.TotalSeconds < 30)
                {
                    Pump(1);
                    double now = player.State.fileTime;
                    worst = Math.Max(worst, 1 - (now - before)); // quanto ficou parado em 1 s
                    before = now;
                }
                double played = player.State.fileTime - t0;
                Check("tocar 30 s seguidos", played > 27, $"andou {played:0.0} s; maior atraso num segundo: {worst:0.00} s; som {(player.HasAudioForTest ? "ok" : "mudo")}");
                player.TestSeek(400);
                Check("pular para 400 s", Wait(() => Math.Abs(player.State.fileTime - 400) < 3, 40), $"{player.State.fileTime:0.0} s");
                double t1 = player.State.fileTime;
                Pump(3);
                Check("continua depois do pulo", player.State.fileTime > t1 + 2, $"{t1:0.0} → {player.State.fileTime:0.0} s");
            }
        }
        player.Dispose();
        Console.WriteLine(failures == 0 ? "Tudo certo." : $"Falhou: {failures}");
        return failures == 0 ? 0 : 1;
    }
}
