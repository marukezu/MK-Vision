namespace MKVision.Desktop;

// --imagetest <vídeo> <pasta>: cria uma foto de teste grande (6000x4000, linhas finas e texto em grade), abre no
// player, tira fotos da tela inteira e com zoom 8x, troca para um vídeo (precisa voltar a tocar) e de volta
// para a foto.
internal static class ImageTest
{
    // --colortest <foto> <pasta>: abre a foto e tira fotos da tela com cada ajuste de "Cor e luz", conferindo que
    // cada um faz o que diz (brilho sobe a média, contraste espalha, saturação −100 tira a cor, vibração e
    // clareza aumentam cor e contraste local). Usar com MKV_SETTINGS na pasta de teste.
    // --rtxtest <foto pequena> <pasta>: a foto (menor que a janela) ampliada com o RTX Video e com a nossa IA;
    // confere que o RTX Video entrou, que não mudou a cor e salva as duas para comparar.
    public static int Rtx(string photo, string folder)
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
        (double mean, double local, string info) Shot(string name)
        {
            Pump(1);
            var s = player.Snapshot()!.Value;
            string info = player.TestPipelineInfo;
            InterpTest.Png(Path.Combine(folder, name), s.pixels, s.width, 0, 0, s.width, s.height);
            double sum = 0, local = 0;
            int n = 0;
            for (int y = s.height / 6; y < s.height * 5 / 6; y += 2)
            for (int x = s.width / 6; x < s.width * 5 / 6 - 1; x += 2)
            {
                int i = (y * s.width + x) * 4;
                double l = 0.2126 * s.pixels[i + 2] + 0.7152 * s.pixels[i + 1] + 0.0722 * s.pixels[i];
                double l2 = 0.2126 * s.pixels[i + 6] + 0.7152 * s.pixels[i + 5] + 0.0722 * s.pixels[i + 4];
                sum += l; local += Math.Abs(l - l2); n++;
            }
            Console.WriteLine($"  {name}: brilho {sum / n:0.0}, detalhe (vizinho a 1 px) {local / n:0.000}  [{info}]");
            return (sum / n, local / n, info);
        }
        var f = player.TestFilter;
        player.Open(photo);
        Pump(4);
        f.RtxVideo = true;
        player.TestSettingsChanged();
        var rtx = Shot("rtx-ligado.png");
        f.RtxVideo = false;
        player.TestSettingsChanged();
        var ours = Shot("rtx-desligado.png");
        Check("RTX Video entrou", rtx.info.Contains("RTX Video"), rtx.info);
        Check("sem ele, a nossa IA", !ours.info.Contains("RTX Video"), ours.info);
        Check("a cor não mudou", Math.Abs(rtx.mean - ours.mean) < 2, $"{ours.mean:0.0} → {rtx.mean:0.0}");
        f.RtxVideo = true;
        player.TestSettingsChanged();
        player.Dispose();
        Console.WriteLine(failures == 0 ? "tudo certo" : $"{failures} falha(s)");
        return failures == 0 ? 0 : 1;
    }

    // --fliptest <foto>: Home e End espelham a imagem da tela (comparada com a tela de antes espelhada em código).
    public static int Flip(string photo)
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
        void Key(int vk) => PlayerNative.PostMessage(player.Hwnd, PlayerNative.WM_KEYDOWN, vk, IntPtr.Zero);
        // Diferença média por canal entre a tela "b" e a "a" espelhada (h/v), só no retângulo da foto.
        static double Diff((byte[] px, int w, int h, double) a, (byte[] px, int w, int h, double) b, bool h, bool v)
        {
            long sum = 0, n = 0;
            for (int y = 0; y < a.h; y += 2)
                for (int x = 0; x < a.w; x += 2)
                {
                    int sx = h ? a.w - 1 - x : x, sy = v ? a.h - 1 - y : y;
                    int i = (y * a.w + x) * 4, j = (sy * a.w + sx) * 4;
                    sum += Math.Abs(b.px[i] - a.px[j]) + Math.Abs(b.px[i + 1] - a.px[j + 1]) + Math.Abs(b.px[i + 2] - a.px[j + 2]);
                    n += 3;
                }
            return (double)sum / n;
        }
        int failures = 0;
        void Check(string what, bool ok, string detail)
        {
            Console.WriteLine($"{(ok ? "OK    " : "FALHOU")} {what}: {detail}");
            if (!ok) failures++;
        }
        player.Open(photo);
        Pump(1.5);
        var before = player.Snapshot()!.Value;
        Key(PlayerNative.VK_HOME);
        Pump(0.6);
        var mirrored = player.Snapshot()!.Value;
        double asMirror = Diff(before, mirrored, true, false), asSame = Diff(before, mirrored, false, false);
        Check("Home: espelho horizontal", asMirror < 3 && asSame > asMirror * 3, $"diferença do espelho {asMirror:0.0}, da original {asSame:0.0}");
        Key(PlayerNative.VK_HOME);
        Key(0x23); // End
        Pump(0.6);
        var flipped = player.Snapshot()!.Value;
        asMirror = Diff(before, flipped, false, true);
        asSame = Diff(before, flipped, false, false);
        Check("End: espelho vertical", asMirror < 3 && asSame > asMirror * 3, $"diferença do espelho {asMirror:0.0}, da original {asSame:0.0}");
        Key(0x23);
        Pump(2.5); // o aviso ("Espelho vertical: desligado") some antes da comparação
        double back = Diff(before, player.Snapshot()!.Value, false, false);
        Check("End de novo: volta ao normal", back < 1, $"diferença da original {back:0.00}");

        // G: vai a imagem como a tela mostra — espelhada, girada e com os efeitos — inteira e no tamanho da foto.
        var (raw, rw, rh) = ImageFile.LoadPixels(photo);
        static double Mean(byte[] px) { long s = 0; for (int i = 0; i < px.Length; i += 4) s += px[i] + px[i + 1] + px[i + 2]; return s / (px.Length / 4 * 3.0); }
        var filter = player.TestFilter;
        filter.Bypass = true;
        player.TestSettingsChanged();
        Pump(0.5);
        var plain = player.TestComfyPixels()!.Value;
        double same = Diff((raw, rw, rh, 0), (plain.pixels, plain.w, plain.h, 0), false, false);
        Check("G sem efeitos: a foto como é", plain.w == rw && plain.h == rh && same < 2, $"{plain.w}x{plain.h}, diferença {same:0.00}");
        Key(PlayerNative.VK_HOME);
        Pump(0.5);
        var mirror = player.TestComfyPixels()!.Value;
        double asFlip = Diff((raw, rw, rh, 0), (mirror.pixels, mirror.w, mirror.h, 0), true, false);
        Check("G com espelho: vai espelhada", asFlip < 2, $"diferença da foto espelhada {asFlip:0.00}");
        Key(PlayerNative.VK_HOME);
        player.TestRotate(90);
        Pump(0.5);
        var turned = player.TestComfyPixels()!.Value;
        Check("G com giro de 90°: vai girada", (turned.w, turned.h) == (rh, rw), $"{turned.w}x{turned.h} (a foto: {rw}x{rh})");
        filter.Bypass = false;
        filter.Brightness = 0.25f;
        player.TestSettingsChanged();
        Pump(0.5);
        var effects = player.TestComfyPixels()!.Value;
        Check("G com efeitos: vai com eles (brilho +25)", Mean(effects.pixels) > Mean(turned.pixels) + 10, $"média {Mean(turned.pixels):0} → {Mean(effects.pixels):0}");

        // O resultado (já girado e com os efeitos) aparece como veio: em pé, sem girar nem clarear de novo.
        player.TestComfyResult(effects.pixels, effects.w, effects.h);
        Pump(2.5);
        var shown = player.Snapshot()!.Value;
        int left = shown.width, right = 0, top = shown.height, bottom = 0;
        for (int y = 0; y < shown.height; y += 2)
            for (int x = 0; x < shown.width; x += 2)
            {
                int i = (y * shown.width + x) * 4;
                if (shown.pixels[i] + shown.pixels[i + 1] + shown.pixels[i + 2] < 30) continue;
                (left, right, top, bottom) = (Math.Min(left, x), Math.Max(right, x), Math.Min(top, y), Math.Max(bottom, y));
            }
        double aspect = (right - left + 1.0) / (bottom - top + 1.0), expected = (double)effects.w / effects.h;
        Check("resultado do G: aparece sem girar de novo", Math.Abs(aspect - expected) < 0.05, $"proporção na tela {aspect:0.00} (a do resultado: {expected:0.00})");
        filter.Brightness = 0;
        player.TestSettingsChanged();
        player.Dispose();
        Console.WriteLine(failures == 0 ? "tudo certo" : $"{failures} falha(s)");
        return failures == 0 ? 0 : 1;
    }

    // --photoplayertest <foto pequena>: com a IA de fotos ligada (MKV_PHOTO_MODEL, padrão 2), a foto abre como é, o "I"
    // troca pela ampliada 4x quando a IA termina, e "T" alterna com a original. Usar com MKV_SETTINGS e MKV_LOG.
    public static int PhotoPlayer(string photo)
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
        int failures = 0;
        void Check(string what, bool ok, string detail)
        {
            Console.WriteLine($"{(ok ? "OK    " : "FALHOU")} {what}: {detail}");
            if (!ok) failures++;
        }
        player.Open(photo);
        Pump(1.5);
        var first = player.TestPhotoSize;
        Check("foto abre na hora (original)", first.w > 0, $"{first.w}x{first.h}");
        player.TestPhotoKey(); // I: a IA só roda quando pedida
        Pump(0.2);
        Check("I: efeitos do player desligados enquanto a IA trabalha", player.TestBypass, "");
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (player.TestPhotoSize == first && clock.Elapsed.TotalSeconds < 300) Pump(0.2);
        Pump(0.5);
        Check("IA pronta: efeitos de volta", !player.TestBypass, "");
        var ai = player.TestPhotoSize;
        // Foto grande com "resolução cheia": a IA roda na foto inteira e a saída é 2x (MKV_PHOTO_FACTOR=2).
        int factor = Environment.GetEnvironmentVariable("MKV_PHOTO_FACTOR") == "2" ? 2 : 4;
        Check($"trocada pela ampliada {factor}x", ai.w == first.w * factor && ai.h == first.h * factor, $"{ai.w}x{ai.h} em {clock.Elapsed.TotalSeconds:0.0} s");
        player.TestToggleKey();
        Pump(0.5);
        Check("U desliga a melhoria (original)", player.TestPhotoSize == first, $"{player.TestPhotoSize}");
        player.TestToggleKey();
        Pump(0.5);
        Check("U liga de novo", player.TestPhotoSize == ai, $"{player.TestPhotoSize}");
        // Y: luz e cor por cima da melhorada (mesmo tamanho); Y de novo desliga.
        player.TestToneKey();
        clock.Restart();
        while (!player.TestShownName.EndsWith("+ luz") && clock.Elapsed.TotalSeconds < 120) Pump(0.2);
        Check("Y: luz e cor na melhorada", player.TestShownName == "IA 4x + luz" && player.TestPhotoSize == ai, $"\"{player.TestShownName}\" {player.TestPhotoSize} em {clock.Elapsed.TotalSeconds:0.0} s");
        player.TestToneKey();
        Pump(0.3);
        Check("Y de novo: sem a luz e cor", player.TestShownName == "IA 4x", $"\"{player.TestShownName}\"");
        // Ctrl+S: salva a melhorada (MKV_SAVE_AS diz onde, sem a janela).
        string saved = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(photo))!, "salva-ia.png");
        Environment.SetEnvironmentVariable("MKV_SAVE_AS", saved);
        if (File.Exists(saved)) File.Delete(saved);
        player.TestSavePhoto();
        clock.Restart();
        while (!File.Exists(saved) && clock.Elapsed.TotalSeconds < 30) Pump(0.2);
        Pump(0.5);
        var (_, sw, sh) = File.Exists(saved) ? ImageFile.LoadPixels(saved) : (Array.Empty<byte>(), 0, 0);
        Check("Ctrl+S salva a melhorada", sw == ai.w && sh == ai.h, $"{saved}: {sw}x{sh}");
        string savedJpg = Path.ChangeExtension(saved, ".jpg");
        Environment.SetEnvironmentVariable("MKV_SAVE_AS", savedJpg);
        if (File.Exists(savedJpg)) File.Delete(savedJpg);
        player.TestSavePhoto();
        clock.Restart();
        while (!File.Exists(savedJpg) && clock.Elapsed.TotalSeconds < 30) Pump(0.2);
        Pump(0.5);
        var (_, jw, jh) = File.Exists(savedJpg) ? ImageFile.LoadPixels(savedJpg) : (Array.Empty<byte>(), 0, 0);
        Check("também em JPG", jw == ai.w && jh == ai.h, $"{jw}x{jh}, {(File.Exists(savedJpg) ? new FileInfo(savedJpg).Length / 1024 : 0)} KB");
        // Versões: um resultado "do ComfyUI" (cinza, metade do tamanho), T, I nele, U, e trocar o modelo.
        int cw = first.w / 2, ch = first.h / 2;
        player.TestComfyResult(Enumerable.Repeat((byte)128, cw * ch * 4).ToArray(), cw, ch);
        Pump(0.5);
        Check("G: a do ComfyUI na tela (sem melhoria)", player.TestPhotoSize == (cw, ch), $"{player.TestPhotoSize}");
        player.TestComfyKey();
        Pump(0.3);
        Check("T: a original (o G desliga a melhoria; o U liga)", player.TestPhotoSize == first, $"{player.TestPhotoSize}");
        player.TestComfyKey();
        Pump(0.3);
        Check("T de novo: a do ComfyUI", player.TestPhotoSize == (cw, ch), $"{player.TestPhotoSize}");
        player.TestPhotoKey(); // I na do ComfyUI
        clock.Restart();
        while (player.TestPhotoSize == (cw, ch) && clock.Elapsed.TotalSeconds < 300) Pump(0.2);
        Check("I: a do ComfyUI melhorada (4x dela)", player.TestPhotoSize == (cw * factor, ch * factor), $"{player.TestPhotoSize}");
        player.TestToggleKey();
        Pump(0.3);
        Check("U: a do ComfyUI sem melhoria", player.TestPhotoSize == (cw, ch), $"{player.TestPhotoSize}");
        player.TestToggleKey();
        Pump(0.3);
        player.TestComfyKey();
        Pump(0.3);
        Check("T com a melhoria ligada: a original melhorada", player.TestPhotoSize == ai, $"{player.TestPhotoSize}");
        player.TestComfyKey();
        Pump(0.3);
        player.TestPhotoModel(player.TestPhotoModelNow == 1 ? 2 : 1);
        Pump(0.5);
        Check("trocar o modelo: as melhorias saem, a do ComfyUI fica", player.TestPhotoSize == (cw, ch), $"{player.TestPhotoSize}");
        if (player.Snapshot() is { } s) InterpTest.Png(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(photo))!, "player-ia.png"), s.pixels, s.width, 0, 0, s.width, s.height);
        player.Dispose();
        Console.WriteLine(failures == 0 ? "tudo certo" : $"{failures} falha(s)");
        return failures == 0 ? 0 : 1;
    }

    // --stilltest <pasta>: vídeo pausado + I = o quadro da tela melhorado pela IA de fotos (4x) aparece no lugar;
    // Ctrl+S salva; play volta ao vídeo. Usar com MKV_SETTINGS (PhotoAi) e MKV_LOG.
    public static int Still(string folder)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        Directory.CreateDirectory(folder);
        string clip = Path.Combine(folder, "clip-quadro.mp4");
        Vortice.MediaFoundation.MediaFactory.MFStartup(true).CheckError();
        if (!File.Exists(clip)) PlayerTest.WriteClip(clip);
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
        player.Open(clip);
        Pump(1.2);
        player.TestPhotoKey();
        Pump(0.3);
        Check("tocando: I não faz nada (pede para pausar)", !player.TestStillShown, "");
        player.TestPause();
        Pump(0.5);
        player.TestPhotoKey();
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (!player.TestStillShown && clock.Elapsed.TotalSeconds < 300) Pump(0.2);
        var size = player.TestStillSize;
        Check("pausado + I: quadro melhorado 4x na tela", player.TestStillShown && size.w == 640 * 4, $"{size.w}x{size.h} em {clock.Elapsed.TotalSeconds:0.0} s");
        if (player.Snapshot() is { } s) InterpTest.Png(Path.Combine(folder, "quadro-ia.png"), s.pixels, s.width, 0, 0, s.width, s.height);
        player.TestToggleKey();
        Pump(0.3);
        Check("U: o quadro original (a melhorada fica guardada)", !player.TestStillShown && player.TestStillSize.w > 0, "");
        player.TestToggleKey();
        Pump(0.3);
        string saved = Path.Combine(folder, "quadro-salvo.png");
        if (File.Exists(saved)) File.Delete(saved);
        Environment.SetEnvironmentVariable("MKV_SAVE_AS", saved);
        player.TestSavePhoto();
        clock.Restart();
        while (!File.Exists(saved) && clock.Elapsed.TotalSeconds < 30) Pump(0.2);
        Pump(0.5);
        var (_, sw, sh) = File.Exists(saved) ? ImageFile.LoadPixels(saved) : (Array.Empty<byte>(), 0, 0);
        Check("Ctrl+S salva o quadro melhorado", sw == size.w && sh == size.h, $"{sw}x{sh}");
        // Clique no vídeo com o quadro da IA na tela: não dá play (perderia o quadro).
        Native.GetClientRect(player.Hwnd, out var area);
        IntPtr at = (IntPtr)(((area.Height / 2) << 16) | (area.Width / 2));
        player.IgnoreMouseLeave = true;
        void Click()
        {
            PlayerNative.PostMessage(player.Hwnd, PlayerNative.WM_MOUSEMOVE, IntPtr.Zero, at);
            PlayerNative.PostMessage(player.Hwnd, PlayerNative.WM_LBUTTONDOWN, 1, at);
            PlayerNative.PostMessage(player.Hwnd, PlayerNative.WM_LBUTTONUP, IntPtr.Zero, at);
            Pump(0.4);
        }
        Click();
        Check("clique com o quadro da IA na tela: continua pausado", !player.TestPlaying && player.TestStillShown, $"tocando {player.TestPlaying}");
        // Clique que traz a janela de volta (Alt+Tab): só ativa. Fora do quadro da IA, para ver que é o 1º ponto.
        PlayerNative.PostMessage(player.Hwnd, PlayerNative.WM_KEYDOWN, PlayerNative.VK_SPACE, IntPtr.Zero); // play
        Pump(1);
        PlayerNative.PostMessage(player.Hwnd, 0x0006, 2, IntPtr.Zero); // WM_ACTIVATE, WA_CLICKACTIVE
        Click();
        Check("clique que traz a janela: não pausa", player.TestPlaying, $"tocando {player.TestPlaying}");
        Pump(0.6);
        Click();
        Check("clique seguinte: pausa normal", !player.TestPlaying, $"tocando {player.TestPlaying}");
        Click();
        PlayerNative.PostMessage(player.Hwnd, PlayerNative.WM_KEYDOWN, PlayerNative.VK_SPACE, IntPtr.Zero); // pausa
        Pump(0.5);
        player.TestPhotoKey(); // de novo o I: com a IA trabalhando, o clique também não dá play
        Pump(0.3);
        Click();
        Check("clique com a IA trabalhando: continua pausado", !player.TestPlaying, $"tocando {player.TestPlaying}");
        clock.Restart();
        while (!player.TestStillShown && clock.Elapsed.TotalSeconds < 300) Pump(0.2);
        PlayerNative.PostMessage(player.Hwnd, PlayerNative.WM_KEYDOWN, PlayerNative.VK_SPACE, IntPtr.Zero); // play
        Pump(1);
        Check("play: volta ao vídeo", player.TestPlaying && player.TestStillSize.w == 0, $"tocando {player.TestPlaying}");
        // Ctrl+S sem versão da IA: o quadro da tela (no tamanho do vídeo); Shift+S: o vídeo inteiro.
        string frameFile = Path.Combine(folder, "quadro-tela.png"), videoFile = Path.Combine(folder, "video-salvo.mp4");
        foreach (var f in new[] { frameFile, videoFile }) if (File.Exists(f)) File.Delete(f);
        Environment.SetEnvironmentVariable("MKV_SAVE_AS", frameFile);
        player.TestSavePhoto();
        clock.Restart();
        while (!File.Exists(frameFile) && clock.Elapsed.TotalSeconds < 30) Pump(0.2);
        Pump(0.5);
        var (_, fw, fh) = File.Exists(frameFile) ? ImageFile.LoadPixels(frameFile) : (Array.Empty<byte>(), 0, 0);
        Check("Ctrl+S sem IA: salva o quadro da tela", fw == 640 && fh == 360, $"{fw}x{fh}");
        Environment.SetEnvironmentVariable("MKV_SAVE_AS", videoFile);
        player.TestSaveVideo();
        clock.Restart();
        while (!File.Exists(videoFile) && clock.Elapsed.TotalSeconds < 30) Pump(0.2);
        Pump(0.5);
        long sizes = File.Exists(videoFile) ? new FileInfo(videoFile).Length : 0;
        Check("Shift+S: salva o vídeo inteiro", sizes == new FileInfo(clip).Length, $"{sizes} de {new FileInfo(clip).Length} bytes");
        Environment.SetEnvironmentVariable("MKV_SAVE_AS", null);
        player.Dispose();
        Console.WriteLine(failures == 0 ? "tudo certo" : $"{failures} falha(s)");
        return failures == 0 ? 0 : 1;
    }

    public static int Color(string photo, string folder)
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
        // Média do brilho, espalhamento (desvio), cor média (croma) e contraste local (diferença para o vizinho a 8 px).
        (double mean, double spread, double chroma, double local) Stats(string name)
        {
            Pump(0.6);
            var s = player.Snapshot()!.Value;
            InterpTest.Png(Path.Combine(folder, name), s.pixels, s.width, 0, 0, s.width, s.height);
            double sum = 0, sq = 0, chroma = 0, local = 0;
            int n = 0;
            for (int y = s.height / 6; y < s.height * 5 / 6; y += 4)
            for (int x = s.width / 6; x < s.width * 5 / 6 - 8; x += 4)
            {
                int i = (y * s.width + x) * 4, j = i + 8 * 4;
                double l = 0.2126 * s.pixels[i + 2] + 0.7152 * s.pixels[i + 1] + 0.0722 * s.pixels[i];
                double l2 = 0.2126 * s.pixels[j + 2] + 0.7152 * s.pixels[j + 1] + 0.0722 * s.pixels[j];
                sum += l; sq += l * l; local += Math.Abs(l - l2);
                chroma += Math.Max(s.pixels[i], Math.Max(s.pixels[i + 1], s.pixels[i + 2])) - Math.Min(s.pixels[i], Math.Min(s.pixels[i + 1], s.pixels[i + 2]));
                n++;
            }
            double mean = sum / n;
            var r = (mean, Math.Sqrt(sq / n - mean * mean), chroma / n, local / n);
            Console.WriteLine($"  {name}: brilho {r.Item1:0.0}, espalhamento {r.Item2:0.0}, cor {r.Item3:0.0}, contraste local {r.Item4:0.00}");
            return r;
        }
        var f = player.TestFilter;
        void Set(float brightness = 0, float contrast = 0, float saturation = 0, float vibrance = 0, float clarity = 0)
        {
            (f.Brightness, f.Contrast, f.Saturation, f.Vibrance, f.Clarity) = (brightness, contrast, saturation, vibrance, clarity);
            player.TestSettingsChanged();
        }
        player.Open(photo);
        Pump(4);
        Set();
        var normal = Stats("cor-normal.png");
        Set(brightness: 0.2f);
        Check("brilho +40 clareia", Stats("cor-brilho.png").mean > normal.mean + 8, "");
        Set(contrast: 0.25f);
        var contrast = Stats("cor-contraste.png");
        Check("contraste +50 espalha", contrast.spread > normal.spread * 1.1, "");
        Check("contraste não escurece a cena escura", Math.Abs(contrast.mean - normal.mean) < 6, $"{normal.mean:0.0} → {contrast.mean:0.0}");
        Set(saturation: -1);
        Check("saturação −100 tira a cor", Stats("cor-cinza.png").chroma < 2, "");
        Set(vibrance: 0.5f);
        Check("vibração +50 aviva", Stats("cor-vibracao.png").chroma > normal.chroma * 1.1, "");
        Set(clarity: 0.6f);
        Check("clareza +60 aumenta o relevo", Stats("cor-clareza.png").local > normal.local * 1.05, "");
        Set(contrast: 0.05f, vibrance: 0.3f);
        Stats("cor-quest.png");
        Set();
        f.SharpenOff = true;
        player.TestSettingsChanged();
        var plain = Stats("cor-sem-nitidez.png");
        f.SharpenOff = false;
        player.TestSettingsChanged();
        Check("D desliga a nitidez (menos contraste fino)", plain.local < normal.local * 0.97, $"{normal.local:0.00} → {plain.local:0.00}");
        Set();
        player.Dispose();
        Console.WriteLine(failures == 0 ? "tudo certo" : $"{failures} falha(s)");
        return failures == 0 ? 0 : 1;
    }

    public static int Run(string video, string folder)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        Directory.CreateDirectory(folder);
        string photo = Path.Combine(folder, "foto-teste.png");
        const int W = 6000, H = 4000;
        var pixels = new byte[W * H * 4];
        for (int y = 0; y < H; y++)
        for (int x = 0; x < W; x++)
        {
            int i = (y * W + x) * 4;
            // Fundo em degradê, linhas de 1 px a cada 3 px (serrilha se a redução for ruim) e quadrados de 200 px.
            byte r = (byte)(x * 255 / W), g = (byte)(y * 255 / H), b = 128;
            if ((x / 200 + y / 200) % 2 == 0 && x % 3 == 0) (r, g, b) = (255, 255, 255);
            if (Math.Abs(x - W / 2) < 2 || Math.Abs(y - H / 2) < 2) (r, g, b) = (255, 0, 0);
            (pixels[i], pixels[i + 1], pixels[i + 2], pixels[i + 3]) = (b, g, r, 255);
        }
        InterpTest.Png(photo, pixels, W, 0, 0, W, H);

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
        void Save(string name)
        {
            if (player.Snapshot() is not { } s) { Console.WriteLine($"{name}: sem foto"); return; }
            InterpTest.Png(Path.Combine(folder, name), s.pixels, s.width, 0, 0, s.width, s.height);
        }
        int failures = 0;
        void Check(string what, bool ok, string detail)
        {
            Console.WriteLine($"{(ok ? "OK    " : "FALHOU")} {what}: {detail}");
            if (!ok) failures++;
        }
        player.IgnoreMouseLeave = true;
        player.Open(photo);
        Pump(4); // IA de ampliação carrega
        Save("1-foto.png");
        Check("foto aberta", player.TestTitle.Contains("foto 6000x4000"), player.TestTitle);
        Native.GetClientRect(player.Hwnd, out var client);
        var center = new Native.POINT { X = client.Width / 2, Y = client.Height / 2 };
        Native.ClientToScreen(player.Hwnd, ref center);
        for (int i = 0; i < 12; i++) // 1.2^12 ≈ 8.9x
            PlayerNative.PostMessage(player.Hwnd, PlayerNative.WM_MOUSEWHEEL, (IntPtr)(120 << 16), (IntPtr)((center.Y << 16) | (center.X & 0xffff)));
        Pump(1.5);
        Save("2-foto-zoom.png");
        Check("zoom na foto", player.State.zoom > 8, $"{player.State.zoom:0.0}x");
        player.Open(video);
        Pump(2);
        var t0 = player.State.fileTime;
        Pump(1);
        Check("vídeo depois da foto toca", player.State.fileTime > t0 + 0.5, $"{t0:0.00} s → {player.State.fileTime:0.00} s");
        player.Open(photo);
        Pump(1.5);
        Check("foto de novo", player.TestTitle.Contains("foto"), player.TestTitle);
        player.Dispose();
        Console.WriteLine(failures == 0 ? "Tudo certo." : $"Falhou: {failures}");
        return failures == 0 ? 0 : 1;
    }
}
