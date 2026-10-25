using System.Diagnostics;
using Vortice.Direct3D11;
using Vortice.Mathematics;
using Windows.Graphics;

namespace MKVision.Desktop;

internal static class Program
{
    [STAThread] // o navegador (WebView2) precisa de uma thread STA com as mensagens da janela
    private static int Main(string[] args)
    {
        // Limpeza de memória do .NET sem parar o programa por muito tempo (a câmera lenta desenha a 144 Hz).
        System.Runtime.GCSettings.LatencyMode = System.Runtime.GCLatencyMode.SustainedLowLatency;
        // Coordenadas em pixels físicos, sem a escala do Windows atrapalhar o alinhamento.
        Native.SetProcessDpiAwarenessContext(Native.DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
        if (args.Length == 2 && args[0] == "--selftest") return SelfTest.Run(new IntPtr(Convert.ToInt64(args[1], 16)));
        if (args.Length == 1 && args[0] == "--overlaytest") return SelfTest.Overlay();
        if (args.Length == 1 && args[0] == "--pipelinetest") return PipelineTest.Run();
        if (args.Length == 1 && args[0] == "--linktest") return LinkTest.Run();
        if (args.Length == 1 && args[0] == "--rifebench") return RifeBench.Run();
        if (args.Length == 1 && args[0] == "--slowtest") return SlowTest.Run();
        if (args.Length == 1 && args[0] == "--playertest") return PlayerTest.Run();
        if (args.Length == 1 && args[0] == "--playeruitest") return PlayerTest.RunWindow();
        if (args.Length >= 1 && args[0] == "--playerslowtest") return PlayerTest.RunSlow(args.Length > 1 && args[1] == "ensemble");
        if (args.Length == 1 && args[0] == "--hovertest") return PlayerTest.HoverTest();
        if (args.Length == 2 && args[0] == "--panelshot") return PlayerTest.PanelShot(args[1]);
        if (args.Length == 2 && args[0] == "--playershot") return PlayerTest.PlayerShot(args[1]);
        if (args.Length == 1 && args[0] == "--audiotest") return PlayerTest.RunAudio();
        if (args.Length == 1 && args[0] == "--assoctest") return PlayerTest.RunAssociation();
        if (args.Length >= 1 && args[0] == "--playerperf") return PlayerTest.RunPerf(args.Length > 1 ? int.Parse(args[1]) : -1);
        if (args.Length == 5 && args[0] == "--slowstarttest") return FrameDiffTest.RunSlowStart(args[1], double.Parse(args[2], System.Globalization.CultureInfo.InvariantCulture), int.Parse(args[3]), args[4] == "1");
        if (args.Length >= 2 && args[0] == "--dedupetest") return FrameDiffTest.RunPlayer(args[1], args.Length > 2 ? double.Parse(args[2], System.Globalization.CultureInfo.InvariantCulture) : 0);
        if (args.Length >= 2 && args[0] == "--cadencetest") return CadenceTest.Run(args[1], args.Length > 2 ? double.Parse(args[2], System.Globalization.CultureInfo.InvariantCulture) : 0);
        if (args.Length == 3 && args[0] == "--interpscore") return InterpTest.Score(args[1], double.Parse(args[2], System.Globalization.CultureInfo.InvariantCulture));
        if (args.Length >= 3 && args[0] == "--audiourltest") return AudioUrlTest.Run(args[1], args[2], args.Length > 3);
        if (args.Length == 3 && args[0] == "--webinspect") return WebBrowserTest.Inspect(args[1], args[2]);
        if (args.Length >= 3 && args[0] == "--cleantest") return PhotoAi.CleanTest(args[1], args[2], args.Length > 3 ? int.Parse(args[3]) : 5);
        if (args.Length >= 3 && args[0] == "--detailtest") return PhotoAi.DetailTest(args[1], args[2], args.Length > 3 ? int.Parse(args[3]) : 1);
        if (args.Length == 2 && args[0] == "--stilltest") return ImageTest.Still(args[1]);
        if (args.Length == 2 && args[0] == "--photoplayertest") return ImageTest.PhotoPlayer(args[1]);
        if (args.Length == 3 && args[0] == "--photoaitest") return PhotoAi.Test(args[1], args[2]);
        if (args.Length == 3 && args[0] == "--vsrtest") return VsrTest.Run(args[1], args[2]);
        if (args.Length == 3 && args[0] == "--rtxtest") return ImageTest.Rtx(args[1], args[2]);
        if (args.Length == 3 && args[0] == "--colortest") return ImageTest.Color(args[1], args[2]);
        if (args.Length >= 2 && args[0] == "--colorcheck") return ColorCheck.Run(args[1], args.Length > 2 ? int.Parse(args[2]) : 20);
        if (args.Length == 2 && args[0] == "--igimagetest") return WebBrowserTest.InstagramImage(args[1]);
        if (args.Length == 1 && args[0] == "--privacytest") return WebBrowserTest.Privacy();
        if (args.Length == 1 && args[0] == "--adblocksitetest") return WebBrowserTest.AdBlockSites();
        if (args.Length == 1 && args[0] == "--robusttest") return WebBrowserTest.Robust();
        if (args.Length == 1 && args[0] == "--starttest") return WebBrowserTest.Start();
        if (args.Length == 2 && args[0] == "--mobiletest") return WebBrowserTest.Mobile(args[1]);
        if (args.Length == 2 && args[0] == "--settingstest") return WebBrowserTest.Settings(args[1]);
        if (args.Length == 2 && args[0] == "--webtabstest") return WebBrowserTest.Tabs(args[1]);
        if (args.Length == 2 && args[0] == "--newtabtest") return WebBrowserTest.NewTabs(args[1]);
        if (args.Length == 2 && args[0] == "--annotatetest") return WebBrowserTest.Annotate(args[1]);
        if (args.Length == 2 && args[0] == "--comfyareatest") return WebBrowserTest.ComfyAreaTest(args[1]);
        if (args.Length == 2 && args[0] == "--playermenutest") return WebBrowserTest.PlayerMenu(args[1]);
        if (args.Length == 2 && args[0] == "--croptest") return WebBrowserTest.Crop(args[1]);
        if (args.Length == 2 && args[0] == "--webmenutest") return WebBrowserTest.WebMenu(args[1]);
        if (args.Length == 1 && args[0] == "--nvidiapacktest") return NvidiaPack.Test();
        if (args.Length == 1 && args[0] == "--updatetest") return Updater.Test();
        if (args.Length == 2 && args[0] == "--playertabstest") return WebBrowserTest.PlayerTabs(args[1]);
        if (args.Length == 2 && args[0] == "--comfyqueuetest") return WebBrowserTest.ComfyQueue(args[1]);
        if (args.Length is 2 or 3 && args[0] == "--performancetest") return WebBrowserTest.Performance(args[1], args.Length == 3 && args[2] == "comfy");
        if (args.Length == 2 && args[0] == "--updateflowtest") return WebBrowserTest.UpdateFlow(args[1]);
        if (args.Length == 1 && args[0] == "--nvidiapackdownload") return NvidiaPack.DownloadTest();
        if (args.Length == 2 && args[0] == "--webopenbuttontest") return WebBrowserTest.WebOpenButton(args[1]);
        if (args.Length == 2 && args[0] == "--webfullscreentest") return WebBrowserTest.WebFullscreen(args[1]);
        if (args.Length == 2 && args[0] == "--promptboxtest") return PromptBoxTest.Run(args[1]);
        if (args.Length == 2 && args[0] == "--savefoldertest") return WebBrowserTest.SaveFolder(args[1]);
        if (args.Length == 2 && args[0] == "--webframetest") return WebBrowserTest.Frame(args[1]);
        if (args.Length == 2 && args[0] == "--webtabsqoltest") return WebBrowserTest.TabsQol(args[1]);
        if (args.Length == 2 && args[0] == "--webaddresstest") return WebBrowserTest.Address(args[1]);
        if (args.Length == 2 && args[0] == "--webpagetest") return WebBrowserTest.Page(args[1]);
        if (args.Length == 2 && args[0] == "--carouseltest") return WebBrowserTest.Carousel(args[1]);
        if (args.Length == 2 && args[0] == "--storytest") return WebBrowserTest.Story(args[1]);
        if (args.Length == 1 && args[0] == "--cookietest") return WebBrowserTest.Cookies();
        if (args.Length >= 2 && args[0] == "--webtest") return WebBrowserTest.Run(args[1], args.Length > 2 ? args[2] : null);
        if (args.Length == 2 && args[0] == "--webplaytest") return WebPlayTest.Run(args[1]);
        if (args.Length == 3 && args[0] == "--tsfix") { TsFixer.Run(args[1], args[2], Task.CompletedTask, CancellationToken.None).GetAwaiter().GetResult(); return 0; }
        if (args.Length == 4 && args[0] == "--growtest") return GrowTest.Run(args[1], long.Parse(args[2]), double.Parse(args[3], System.Globalization.CultureInfo.InvariantCulture));
        if (args.Length == 2 && args[0] == "--videocheck") return VideoCheck.Run(args[1]);
        if (args.Length is 4 or 5 && args[0] == "--comfytest")
            return ComfyClient.Test(args[1], args[2], args[3], args.Length == 5 && args[4] == "restore" ? Array.FindIndex(ComfyClient.Workflows, w => w.Megapixels > 0) : 0);
        if (args.Length == 6 && args[0] == "--comfytest" && args[4] == "dois")
            return ComfyClient.Test(args[1], args[2], args[3], Array.FindIndex(ComfyClient.Workflows, w => w.SecondImage), args[5]);
        if (args.Length == 3 && args[0] == "--encodetest") return ImageFile.EncodeTest(args[1], args[2]);
        if (args.Length >= 1 && args[0] == "--comfybuild") return ComfyClient.BuildTest(args.Length > 1 && args[1] == "zero",
            args.Length > 1 ? args[1] switch
            {
                "restore" => Array.FindIndex(ComfyClient.Workflows, w => w.Megapixels > 0),
                "wan" => Array.FindIndex(ComfyClient.Workflows, w => w.Video),
                "wan3" => Array.FindLastIndex(ComfyClient.Workflows, w => w.Video),
                "dois" => Array.FindIndex(ComfyClient.Workflows, w => w.SecondImage),
                _ => 0
            } : 0);
        if (args.Length == 1 && args[0] == "--comfycleanup") { ComfyClient.Cleanup(); return 0; } // com MKV_COMFY_DIR numa pasta de teste
        if (args.Length == 1 && args[0] == "--comfyforget") { Player.ForgetComfyTexts(); return 0; }
        if (args.Length == 2 && args[0] == "--nometadata") // tira os metadados (o prompt) de um vídeo do ComfyUI
        {
            var bytes = File.ReadAllBytes(args[1]);
            ComfyClient.WithoutMetadata(bytes);
            File.WriteAllBytes(args[1], bytes);
            return 0;
        }
        if (args.Length == 2 && args[0] == "--releasetest") return ReleaseTest(args[1]);
        if (args.Length == 2 && args[0] == "--fliptest") return ImageTest.Flip(args[1]);
        if (args.Length >= 3 && args[0] == "--tonetest") return ToneAi.Test(args[1], args[2], args.Length > 3 ? float.Parse(args[3], System.Globalization.CultureInfo.InvariantCulture) : 0.7f);
        if (args.Length >= 3 && args[0] == "--dataset") return DatasetTool.Run(args[1], args[2], args.Length > 3 ? int.Parse(args[3]) : 8000, args.Length > 4 ? args[4] : null);
        if (args.Length == 2 && args[0] == "--tsprobe") return TsProbe.Run(args[1]);
        if (args.Length >= 2 && args[0] == "--urltest") return UrlTest.Run(args[1], args.Length > 2 && args[2] == "stream");
        if (args.Length == 3 && args[0] == "--imagetest") return ImageTest.Run(args[1], args[2]);
        if (args.Length == 2 && args[0] == "--listfolderstest") return BrowserTest.Folders(args[1]);
        if (args.Length == 2 && args[0] == "--idleclicktest") return BrowserTest.IdleClick(args[1]);
        if (args.Length == 3 && args[0] == "--browsertest") return BrowserTest.Run(args[1], args[2]);
        if (args.Length == 4 && args[0] == "--interptest") return InterpTest.Run(args[1], double.Parse(args[2], System.Globalization.CultureInfo.InvariantCulture), args[3]);
        if (args.Length >= 2 && args[0] == "--framediff") return FrameDiffTest.Run(args[1], args.Length > 2 ? double.Parse(args[2], System.Globalization.CultureInfo.InvariantCulture) : 0);
        if (args.Length == 2 && args[0] == "--foldertest") return PlayerTest.RunFolder(args[1]);
        if (args.Length == 2 && args[0] == "--downloadtest")
        {
            // Baixa um vídeo do YouTube como a extensão pediria (instala yt-dlp + Deno na 1ª vez).
            Console.OutputEncoding = System.Text.Encoding.UTF8;
            try
            {
                string file = new VideoDownload().Run(args[1], (p, t) => Console.WriteLine(p >= 0 ? $"{p,5:0.0}% {t}" : t)).GetAwaiter().GetResult();
                Console.WriteLine($"Salvo: {file} ({new FileInfo(file).Length / 1024} KB)");
                return 0;
            }
            catch (Exception error)
            {
                Console.WriteLine($"Falhou: {error.Message}");
                return 1;
            }
        }

        Console.OutputEncoding = System.Text.Encoding.UTF8;
        Console.WriteLine("MK Vision");
        Console.WriteLine();
        Console.WriteLine("  O player abre junto: Arquivo → Abrir vídeo (menu no topo da janela), ou arraste um vídeo para ela.");
        Console.WriteLine("  Fechar o player fecha o MK Vision.");
        Console.WriteLine();
        Console.WriteLine("  Modo Edge (com a extensão):");
        Console.WriteLine("  Ctrl+Alt+M  liga/desliga na janela em foco (deixe o Edge em foco)");
        Console.WriteLine("  Ctrl+Alt+B  compara: mostra a captura sem processar");
        Console.WriteLine("  Ctrl+Alt+↑/↓  aumenta/diminui a força (0–400%)");
        Console.WriteLine("  Ctrl+Alt+1…5  potência (padrão 3; 5 = IA dupla, mais pesada)");
        Console.WriteLine("  Ctrl+Alt+A  câmera lenta: tamanho da análise (100% → 75% → 50%)");
        Console.WriteLine("  Ctrl+Alt+P  traz o player para a frente");
        Console.WriteLine("  Ctrl+Alt+Q  sair");
        Console.WriteLine();

        // Uma janela só: com o MK Vision já aberto (ex.: dois cliques em outro vídeo no Explorer), o vídeo vai para
        // ela e esta cópia fecha.
        var video = args.FirstOrDefault(File.Exists);
        using var instance = new Mutex(true, @"Local\MKVision.Player", out bool first);
        if (!first)
        {
            SingleInstance.Forward(video is null ? null : Path.GetFullPath(video));
            return 0;
        }

        // Sobras do ComfyUI (tecla G) de uma vez em que o MK Vision fechou à força, sem limpar.
        Task.Run(ComfyClient.Cleanup);
        Player.ForgetComfyTexts(); // prompts de versões anteriores: hoje só ficam na memória da sessão

        using var gpu = new Gpu();
        using var overlay = new Overlay(gpu);
        using var app = new App(gpu, overlay);
        // O player abre junto: com o vídeo arrastado sobre o .bat ou aberto pelo Explorer, se houver; sem vídeo,
        // já no navegador.
        app.OpenPlayer(video is null ? null : Path.GetFullPath(video), browser: video is null);
        app.Run();
        ComfyClient.Cleanup(); // o que o G deixou no ComfyUI nesta sessão
        return 0;
    }

    // --releasetest <foto>: carrega a IA de fotos (amplia a foto) e a de câmera lenta, e mede a memória da placa em uso
    // antes e depois de descarregar (o que a tecla G faz antes de chamar o ComfyUI).
    private static int ReleaseTest(string photo)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        static int UsedMb()
        {
            var info = new ProcessStartInfo("nvidia-smi", "--query-gpu=memory.used --format=csv,noheader,nounits") { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
            using var p = Process.Start(info)!;
            string text = p.StandardOutput.ReadToEnd();
            p.WaitForExit();
            return int.TryParse(text.Trim(), out int mb) ? mb : -1;
        }
        int before = UsedMb();
        var (pixels, w, h) = ImageFile.LoadPixels(photo, 512);
        PhotoAi.Upscale(1, pixels, w, h, null, CancellationToken.None);
        RifeHost.Start(false);
        var clock = Stopwatch.StartNew();
        while (RifeHost.Instance is null && clock.Elapsed.TotalSeconds < 180) Thread.Sleep(200);
        Thread.Sleep(1000);
        int loaded = UsedMb();
        bool photoFreed = PhotoAi.Release(), rifeFreed = RifeHost.Release();
        GC.Collect();
        Thread.Sleep(1500);
        int after = UsedMb();
        Console.WriteLine($"placa em uso: antes {before} MB · com as IAs {loaded} MB (+{loaded - before}) · depois de descarregar {after} MB (liberou {loaded - after} MB)");
        Console.WriteLine($"descarregou: fotos {photoFreed}, câmera lenta {rifeFreed}");
        return loaded - after > 200 ? 0 : 1;
    }
}

