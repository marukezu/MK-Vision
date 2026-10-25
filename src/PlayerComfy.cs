using System.Diagnostics;
using System.Text.Json;

namespace MKVision.Desktop;

// Tecla G: manda o quadro da tela (vídeo, que é pausado) ou a foto aberta ao ComfyUI, com o prompt e o negativo
// digitados na janela (PromptBox), pelo workflow "Qwen Image 2.1 - Editar imagem" (ComfyClient). O resultado vira a
// versão "ComfyUI" da imagem (PlayerPhotoAi): T alterna com a original, I a melhora, Ctrl+S salva, o play volta ao
// vídeo. Vai a versão que está na tela (dá para editar em cima do que o Qwen fez). Nada vai para a pasta output do
// ComfyUI. Shift+G cancela.
// Uma aba por workflow (ComfyClient.Workflows: Qwen 2.1, Qwen 2.1 restaurar, Wan 2.2). As LoRAs e o sampler
// de cada aba ficam em %LOCALAPPDATA%\MKVisionDesktop\comfy.json (na 1ª vez: os do workflow salvo). O prompt e o
// negativo, não: ficam só na memória enquanto o MK Vision está aberto (cada sessão começa sem texto).
internal sealed partial class Player
{
    private const int ComfyMaxSide = 2048; // o Qwen reduz para ~1 megapixel de qualquer jeito: não envia 4K
    private static readonly string ComfyTextsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MKVisionDesktop", "comfy.json");
    // Os textos de cada aba (por workflow) nesta sessão.
    private static readonly Dictionary<string, (string Prompt, string Negative)> SessionTexts = [];
    private static readonly Dictionary<string, string> SessionSecond = []; // a imagem 2 escolhida em cada aba (nesta sessão)

    private CancellationTokenSource? _comfyJob;

    // O que a janela usou por último em cada aba (por workflow) e a aba escolhida. Loras: por linha ("#" + nó do workflow:
    // o arquivo escolhido nela, ligada e força); as de antes das listas, por arquivo.
    private sealed record ComfyState(int Tab = 0, Dictionary<string, ComfyTexts>? Workflows = null);
    // Sampler: passos, CFG, resolução e seed (com "fixa") da aba; a seed guardada é a da última geração.
    private sealed record ComfyTexts(string Prompt, string Negative, bool Scratch = false, Dictionary<string, ComfyLora>? Loras = null,
        ComfyClient.SamplerSetting? Sampler = null);
    private sealed record ComfyLora(bool On, double Strength, string? File = null);

    private void ComfyKey(bool cancel)
    {
        ComfyJob? mine;
        lock (_lock) mine = _activeTab is { } here ? ComfyJobOf(here) : null;
        if (cancel)
        {
            if (mine is not null) CancelComfyJob(mine);
            else Notify("Esta aba não tem nada no ComfyUI");
            return;
        }
        if (mine is not null)
        {
            Notify(mine.Running ? "Esta aba já está gerando no ComfyUI · Shift+G cancela" : $"Esta aba já está na fila do ComfyUI ({QueuePosition(mine)}º) · Shift+G cancela");
            return;
        }
        bool video, photo, playing;
        lock (_lock) (video, photo, playing) = (_file is not null && !_image, _queue.Any(f => f.Image), _playing);
        if (!video && !photo)
        {
            Notify("Abra um vídeo ou uma foto para enviar ao ComfyUI");
            return;
        }
        if (video && playing) TogglePlay(); // o quadro de agora; a janela dá tempo de a tela assentar nele

        // Uma aba por workflow, com os textos usados nela nesta sessão (vazios na 1ª vez) e as LoRAs do workflow salvo
        // com os últimos valores (uma LoRA nova vem como está no workflow). Nada é lido do ComfyUI aqui: só os arquivos
        // dos workflows.
        var state = LoadComfyState();
        var workflows = ComfyClient.Workflows;
        var tabs = workflows.Select(wf =>
        {
            var (prompt, negative) = SessionTexts.GetValueOrDefault(wf.Name, (wf.Prompt, ""));
            var last = (state.Workflows?.GetValueOrDefault(wf.Name) ?? new ComfyTexts("", "")) with { Prompt = prompt, Negative = negative };
            // Cada linha: o arquivo escolhido nela da última vez (se ainda estiver na pasta), ligada e força.
            var files = ComfyClient.CompatibleLoras(wf.Name);
            var loras = ComfyClient.SavedLoras(wf.Name).Select(l =>
                last.Loras?.GetValueOrDefault($"#{l.Id}") is { } slot
                    ? l with { On = slot.On, Strength = slot.Strength, File = slot.File is { } f && files.Contains(f, StringComparer.OrdinalIgnoreCase) ? f : l.File }
                    : last.Loras?.GetValueOrDefault(l.File) is { } used ? l with { On = used.On, Strength = used.Strength } : l).ToList();
            bool lightning = loras.Any(l => l.File.Contains("lightning", StringComparison.OrdinalIgnoreCase));
            bool fourSteps = loras.Any(l => l.File.Contains("4steps", StringComparison.OrdinalIgnoreCase));
            return new PromptBox.Tab
            {
                Label = wf.Tab, CanScratch = wf.Scratch, Prompt = last.Prompt, Negative = last.Negative, Scratch = last.Scratch, Loras = loras, LoraFiles = files,
                SecondImage = wf.SecondImage, SecondPath = SessionSecond.GetValueOrDefault(wf.Name, ""),
                Sampler = last.Sampler ?? ComfyClient.SavedSampler(wf.Name),
                Hint = wf.Video ? "Vídeo: resolução em p (480 ≈ 832×480) na proporção da imagem · passos em todos os samplers" +
                       (fourSteps ? " (com a aceleração de 4 passos: 4-8)" : "") + " · CFG no 1º sampler" :
                       wf.Megapixels > 0 ? $"Restaurar: sai com ~{wf.Megapixels:0} MP · força 1.0 (pequena/comprimida), 0.5 (quadro de vídeo, borrada) · CFG 1 · uma passada só" :
                       lightning ? "Com a Lightning ligada: CFG 1 e poucos passos (os dela). Para CFG maior, desmarque a Lightning e use 25-40 passos." : ""
            };
        }).ToList();
        var answer = PromptBox.Show(Hwnd, "ComfyUI (tecla G)", tabs, state.Tab);
        // Guarda o que ficou em todas as abas (mesmo cancelando: o texto digitado não se perde na sessão; no arquivo,
        // só as LoRAs e o sampler).
        var remembered = new Dictionary<string, ComfyTexts>(state.Workflows ?? []);
        for (int i = 0; i < workflows.Length; i++)
        {
            SessionTexts[workflows[i].Name] = (tabs[i].Prompt, tabs[i].Negative);
            if (tabs[i].SecondImage) SessionSecond[workflows[i].Name] = tabs[i].SecondPath;
            var loraMemory = new Dictionary<string, ComfyLora>(remembered.GetValueOrDefault(workflows[i].Name)?.Loras ?? []);
            foreach (var l in tabs[i].Loras) loraMemory[$"#{l.Id}"] = new ComfyLora(l.On, l.Strength, l.File);
            remembered[workflows[i].Name] = new ComfyTexts("", "", tabs[i].Scratch, loraMemory, tabs[i].Sampler);
        }
        SaveComfyState(new ComfyState(answer?.tab ?? state.Tab, remembered));
        if (answer is not { } texts) return;
        if (texts.prompt.Length == 0)
        {
            Notify("Escreva o prompt para enviar ao ComfyUI");
            return;
        }
        string workflow = workflows[texts.tab].Name;

        // A imagem exatamente como a tela mostra: a versão escolhida (original, do ComfyUI, melhorada, com luz e cor),
        // com o giro, o espelho e os efeitos do player (limpeza, nitidez, Cor e luz), inteira e no tamanho real (sem o
        // zoom). "Do zero": nada vai; o quadro só marca onde o resultado aparece.
        byte[]? pixels = null, clean = null;
        int w = 0, h = 0;
        long serial;
        double time = 0;
        (float[]? mask, string[] areaColors, string[] markColors)? area = null;
        lock (_gpuLock)
        lock (_lock)
        {
            var frame = ScreenFrame();
            if (frame is null)
            {
                Notify(video ? "Pause o vídeo e aperte G de novo" : "A foto não está mais aberta");
                return;
            }
            serial = frame.Serial;
            if (video) time = frame.FileTime;
            var shown = _still is not null && _stillShowAi && _stillSerial == frame.Serial ? _still : frame;
            if (!texts.scratch)
            {
                (pixels, w, h) = ScreenPixels(shown, ComfyMaxSide);
                // Com anotações (F3): a cópia sem o desenho e a área marcada (o que fica fora dela volta como era).
                area = EditArea(shown, w, h);
                if (area?.mask is not null) clean = ScreenPixels(shown, ComfyMaxSide, annotations: false).pixels;
            }
        }
        int cleanW = w, cleanH = h; // a máscara e a cópia limpa ficam no tamanho em que foram feitas
        if (pixels is not null && Math.Max(w, h) > ComfyMaxSide) (pixels, w, h) = Shrink(pixels, w, h, ComfyMaxSide);
        string prompt = texts.prompt;
        if (area is { } marked && AreaInstruction(marked.areaColors, marked.markColors) is { Length: > 0 } instruction)
            prompt = prompt.TrimEnd() + (prompt.TrimEnd().EndsWith('.') ? " " : ". ") + instruction;

        PlayerTab? tab;
        string tabPath;
        lock (_lock)
        {
            tab = _activeTab;
            tabPath = tab?.Path ?? "";
        }
        if (tab is null) return;
        EnqueueComfy(new ComfyJob
        {
            Tab = tab, Path = tabPath, Time = time, Video = video, Serial = serial, Workflow = workflow, Pixels = pixels, W = w, H = h,
            Clean = clean, CleanW = cleanW, CleanH = cleanH, Area = area, Prompt = prompt, Negative = texts.negative, Loras = texts.loras,
            Sampler = texts.sampler, Second = texts.second
        });
    }

    // Autoteste: um pedido da aba da tela, como o G (sem a janela): o 1º workflow, com "steps" passos.
    public bool TestEnqueueComfy(string prompt, int steps)
    {
        var shot = TestComfyPixels();
        PlayerTab? tab;
        string path;
        long serial;
        lock (_lock)
        {
            (tab, path) = (_activeTab, _activeTab?.Path ?? "");
            serial = ScreenFrame()?.Serial ?? -1;
        }
        if (shot is not { } s || tab is null) return false;
        var workflow = ComfyClient.Workflows[0].Name;
        var sampler = ComfyClient.SavedSampler(workflow) is { } saved ? saved with { Steps = steps } : null;
        EnqueueComfy(new ComfyJob { Tab = tab, Path = path, Serial = serial, Workflow = workflow, Pixels = s.pixels, W = s.w, H = s.h, Prompt = prompt, Negative = "", Sampler = sampler });
        return true;
    }
    public int TestComfyQueueCount { get { lock (_lock) return _comfyQueue.Count; } }

    // ---------- Fila do ComfyUI ----------
    // Cada aba manda o seu pedido (G); um roda por vez, na ordem, e o próximo começa sozinho. A resposta volta para a aba
    // que pediu (na tela: aparece na hora; fora: espera a aba voltar). Fechar a aba cancela o pedido dela.

    private sealed class ComfyJob
    {
        public required PlayerTab Tab;
        public required string Path; // o que a aba mostrava (a resposta só vale para ele)
        public double Time;          // o momento do vídeo
        public bool Video;
        public long Serial;
        public required string Workflow;
        public byte[]? Pixels, Clean;
        public int W, H, CleanW, CleanH;
        public (float[]? mask, string[] areaColors, string[] markColors)? Area;
        public required string Prompt, Negative;
        public List<ComfyClient.LoraSetting>? Loras;
        public ComfyClient.SamplerSetting? Sampler;
        public string? Second;
        public readonly CancellationTokenSource Cts = new();
        public bool Running;
        public double Fraction;
        public string Status = "";             // o último aviso (enviando, na fila, preparando o modelo)
        public ComfyClient.StepInfo? Step;     // o último passo (e quando chegou: o relógio continua entre eles)
        public double StepAt;
    }

    private readonly List<ComfyJob> _comfyQueue = []; // com _lock; o primeiro é o que roda
    private bool _comfyWorker;

    private ComfyJob? ComfyJobOf(PlayerTab tab)
    {
        lock (_lock) return _comfyQueue.FirstOrDefault(j => j.Tab == tab);
    }

    // Posição na espera (1 = o próximo).
    private int QueuePosition(ComfyJob job)
    {
        lock (_lock) return Math.Max(1, _comfyQueue.Where(j => !j.Running).ToList().IndexOf(job) + 1);
    }

    private void EnqueueComfy(ComfyJob job)
    {
        bool start;
        int waiting;
        lock (_lock)
        {
            _comfyQueue.Add(job);
            waiting = _comfyQueue.Count - 1;
            start = !_comfyWorker;
            _comfyWorker = true;
            _dirty = true;
        }
        Log.Write($"Comfy: pedido da aba \"{job.Tab.Title}\" na fila ({waiting} na frente).");
        Notify(waiting == 0 ? "Enviando ao ComfyUI..." : $"Na fila do ComfyUI ({waiting}º): começa quando o pedido de antes terminar");
        if (start) _ = Task.Run(ComfyWorker);
    }

    // Um pedido por vez, até a fila esvaziar.
    private async Task ComfyWorker()
    {
        while (true)
        {
            ComfyJob? job;
            lock (_lock)
            {
                job = _comfyQueue.FirstOrDefault();
                if (job is null)
                {
                    _comfyWorker = false;
                    return;
                }
                job.Running = true;
                _dirty = true;
            }
            _comfyJob = job.Cts;
            try { await RunComfyJob(job); }
            catch (Exception error) { Log.Write($"Comfy: o pedido falhou — {error.Message}"); }
            finally
            {
                lock (_lock)
                {
                    _comfyQueue.Remove(job);
                    _dirty = true;
                }
                if (_comfyJob == job.Cts) _comfyJob = null;
                _renderWake.Set();
            }
        }
    }

    // Cancela o pedido: na fila, sai; gerando, o ComfyUI para.
    private void CancelComfyJob(ComfyJob job, bool quiet = false)
    {
        bool running;
        lock (_lock)
        {
            running = job.Running;
            if (!running) _comfyQueue.Remove(job);
            _dirty = true;
        }
        job.Cts.Cancel();
        if (running) _ = ComfyClient.Interrupt();
        Log.Write($"Comfy: pedido da aba \"{job.Tab.Title}\" cancelado{(running ? " (estava gerando)" : " (estava na fila)")}.");
        if (!quiet) Notify("ComfyUI: cancelado");
        _renderWake.Set();
    }

    // Fechar a aba: tudo dela no ComfyUI sai.
    private void CancelComfyFor(PlayerTab tab)
    {
        if (ComfyJobOf(tab) is { } job) CancelComfyJob(job, quiet: true);
        tab.Comfy = null;
    }

    // "Passo 01/25 · 00:12 decorridos · 03:27 restantes (8,3 s por passo)" — com o relógio andando entre os passos.
    private static string StepText(ComfyClient.StepInfo step, double since)
    {
        var pt = System.Globalization.CultureInfo.GetCultureInfo("pt-BR");
        static string Clock(double seconds)
        {
            var t = TimeSpan.FromSeconds(Math.Max(0, Math.Round(seconds)));
            return t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes:00}:{t.Seconds:00}";
        }
        string stage = step.Stages > 1 ? $"Etapa {step.Stage}/{step.Stages} · " : "";
        string text = $"{stage}Passo {step.Step:00}/{step.Steps:00} · {Clock(step.Elapsed + since)} decorridos";
        if (double.IsNaN(step.Remaining)) return text + " · calculando o restante";
        return text + $" · {Clock(step.Remaining - since)} restantes ({step.SecondsPerStep.ToString("0.0", pt)} s por passo)";
    }

    private string JobText(ComfyJob job) => job.Step is { } step ? StepText(step, Now - job.StepAt) : job.Status;

    private async Task RunComfyJob(ComfyJob job)
    {
        var token = job.Cts.Token;
        var clock = Stopwatch.StartNew();
        // Os avisos do pedido aparecem com a aba dele na tela (ou no navegador); a aba mostra a porcentagem no selo.
        bool Visible()
        {
            lock (_lock) return _webMode || _activeTab == job.Tab;
        }
        void Progress(string text)
        {
            if (token.IsCancellationRequested) return;
            (job.Status, job.Step) = (text, null);
            if (Visible()) Notify(text);
        }
        void Step(ComfyClient.StepInfo step)
        {
            if (token.IsCancellationRequested) return;
            (job.Step, job.StepAt) = (step, Now);
            int before = (int)(job.Fraction * 100);
            job.Fraction = step.Fraction;
            if (Visible()) Notify(JobText(job));
            else if ((int)(job.Fraction * 100) != before)
            {
                lock (_lock) _dirty = true; // o selo da aba
                _renderWake.Set();
            }
        }
        // O aviso some em 1,5 s: repete a cada segundo (com o relógio andando).
        using var repeat = new Timer(_ =>
        {
            if (token.IsCancellationRequested) return;
            string text = JobText(job);
            if (text.Length > 0 && Visible()) Notify(text);
        }, null, 1000, 1000);
        try
        {
            // A placa toda para o ComfyUI: as IAs do MK Vision saem da memória dela e voltam sozinhas quando usadas de
            // novo (I: alguns segundos; câmera lenta: ao tocar o vídeo com ela ligada).
            Progress("Liberando a memória da placa para o ComfyUI...");
            if (PhotoAi.Release() | RifeHost.Release() | ToneAi.Release()) GC.Collect();
            Log.Write(job.Pixels is null ? $"Comfy: gerando do zero (aba \"{job.Tab.Title}\")." :
                $"Comfy: enviando {(job.Video ? $"o quadro de {TimeText(job.Time)}" : "a foto")} da aba \"{job.Tab.Title}\" ({job.W}x{job.H})" +
                (job.Area is { } a ? $", com {(a.mask is not null ? "área marcada" : "marcas")} (anotações)" : "") +
                (job.Second is { } secondFile ? $" e a imagem 2 ({System.IO.Path.GetFileName(secondFile)})." : "."));
            var generated = await ComfyClient.Run(job.Workflow, job.Pixels, job.W, job.H, job.Prompt, job.Negative, job.Loras, job.Sampler,
                Progress, token, job.Second, Step);
            var result = generated.Image;
            RememberSeed(job.Workflow, generated.Seed);
            token.ThrowIfCancellationRequested();
            double seconds = clock.Elapsed.TotalSeconds;
            string took = TimeSpan.FromSeconds(seconds).ToString(seconds >= 3600 ? @"h\:mm\:ss" : @"mm\:ss");
            (job.Status, job.Step) = ("", null);
            if (generated.IsVideo)
            {
                // Vídeo (Wan): arquivo temporário (sai na limpeza, ao fechar), aberto numa aba nova ao lado da que pediu
                // (a imagem de origem continua na dela). Shift+S salva onde quiser.
                string folder = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MKVisionDesktop", "comfy-videos");
                Directory.CreateDirectory(folder);
                string file = System.IO.Path.Combine(folder, $"wan_{DateTime.Now:yyyyMMdd_HHmmss}_seed{generated.Seed}{generated.Extension}");
                File.WriteAllBytes(file, result);
                ComfyClient.RememberFile(file);
                Log.Write($"Comfy: vídeo pronto em {seconds:0.0} s ({result.Length / 1048576.0:0.0} MB).");
                PostUi(() =>
                {
                    bool exists;
                    lock (_lock) exists = _mediaTabs.Contains(job.Tab);
                    OpenTab(file, null, exists ? job.Tab : null); // aba nova: sem giro nem espelho (o vídeo já vem como a tela estava)
                    Notify($"Vídeo do ComfyUI pronto em {took} (seed {generated.Seed}) · Shift+S salva o vídeo · Ctrl+S o quadro");
                });
                return;
            }
            var (output, ow, oh) = ImageFile.DecodePixels(result);
            for (int i = 3; i < output.Length; i += 4) output[i] = 255;
            // Área marcada: a edição só vale dentro dela; fora (e no contorno) volta a imagem original, sem o desenho.
            if (job.Area?.mask is { } editMask && job.Clean is not null) output = ComfyArea.KeepOutside(output, ow, oh, job.Clean, job.CleanW, job.CleanH, editMask);
            string where = DeliverComfy(job, output, ow, oh);
            Log.Write($"Comfy: pronto em {seconds:0.0} s ({ow}x{oh}) — {where}.");
            Notify(where switch
            {
                "na tela" => $"ComfyUI: pronto em {took} (seed {generated.Seed}) · T alterna com a original · I melhora · Ctrl+S salva",
                "na aba" => $"ComfyUI terminou na aba \"{job.Tab.Title}\" (pronto em {took})",
                _ => "ComfyUI terminou, mas a aba já mostra outra coisa (o resultado foi descartado)"
            });
        }
        catch (OperationCanceledException) { Log.Write("Comfy: cancelado."); }
        catch (Exception error)
        {
            Log.Write($"Comfy: falhou — {error.Message}");
            Notify("ComfyUI: " + error.Message.Split('\n')[0]);
        }
    }

    // A resposta vai para a aba que pediu: na tela (o mesmo quadro), aparece na hora; fora da tela (ou o quadro ainda
    // voltando), espera ela. A aba fechada ou mostrando outro arquivo: descartada. Devolve onde ficou.
    private string DeliverComfy(ComfyJob job, byte[] pixels, int w, int h)
    {
        lock (_gpuLock)
        lock (_lock)
        {
            if (!_mediaTabs.Contains(job.Tab) || job.Tab.Path != job.Path) return "descartado";
            var pending = new ComfyPending(pixels, w, h, job.Path, job.Time, job.Video);
            if (job.Tab != _activeTab)
            {
                (job.Tab.Comfy, job.Tab.ComfyDone) = (pending, true);
                _dirty = true;
                return "na aba";
            }
            double tolerance = Math.Max(_frameDuration * 0.6, 0.02);
            if (ScreenFrame() is { } frame && (frame.Serial == job.Serial || (job.Video ? Math.Abs(frame.FileTime - job.Time) <= tolerance : frame.Image)))
            {
                ApplyComfy(frame, pixels, w, h);
                return "na tela";
            }
            if (job.Video && !_playing)
            {
                _comfyRestore = pending; // o quadro ainda voltando (a aba acabou de voltar): aparece quando ele chegar
                return "na tela";
            }
            return "descartado";
        }
    }

    // O resultado vira a versão "ComfyUI" da imagem (se a tela ainda for a mesma); a anterior do ComfyUI e a melhorada
    // dela saem. Aparece na hora, sem a melhoria (o I a faz).
    private bool ShowComfy(byte[] pixels, int w, int h, long serial)
    {
        var layer = NewLayer(pixels, w, h, baked: true); // veio da imagem como a tela mostrava
        lock (_gpuLock)
        lock (_lock)
        {
            if (ScreenFrame() is { } frame && frame.Serial == serial)
            {
                LayersFor(frame);
                DisposeLayer(ref _comfyLayer);
                DisposeLayer(ref _aiComfy);
                layer.Serial = ++_nextSerial;
                (_comfyLayer, _showComfy, _showAi) = (layer, true, false);
                _cropBaked = FullCrop; // a resposta já vem cortada (foi feita da parte cortada)
                ShowLayers();
                return true;
            }
        }
        Frame? unused = layer;
        DisposeLayer(ref unused);
        return false;
    }

    // A seed que a geração usou passa a ser a do campo da aba (marcando "fixa", repete esta imagem).
    private static void RememberSeed(string workflow, long seed)
    {
        var state = LoadComfyState();
        if (state.Workflows?.GetValueOrDefault(workflow) is not { Sampler: { } sampler } texts) return;
        state.Workflows[workflow] = texts with { Sampler = sampler with { Seed = seed } };
        SaveComfyState(state);
    }

    // O comfy.json de antes das abas (só o Qwen 2.1) vira a aba dele.
    private static ComfyState LoadComfyState()
    {
        try
        {
            if (!File.Exists(ComfyTextsPath)) return new ComfyState();
            string json = File.ReadAllText(ComfyTextsPath);
            if (JsonSerializer.Deserialize<ComfyState>(json) is { Workflows: not null } state) return state;
            if (JsonSerializer.Deserialize<ComfyTexts>(json) is { Prompt: not null } old)
                return new ComfyState(0, new Dictionary<string, ComfyTexts> { [ComfyClient.Workflows[0].Name] = old });
        }
        catch (Exception error) { Log.Write($"Comfy: não li os últimos textos — {error.Message}"); }
        return new ComfyState();
    }

    // Ao abrir o MK Vision: apaga do comfy.json os textos que versões anteriores guardavam (as LoRAs e o sampler ficam).
    public static void ForgetComfyTexts()
    {
        if (File.Exists(ComfyTextsPath)) SaveComfyState(LoadComfyState());
    }

    private static void SaveComfyState(ComfyState state)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ComfyTextsPath)!);
            // Nunca grava o prompt e o negativo (ficam só na memória da sessão).
            var withoutTexts = state with
            {
                Workflows = state.Workflows?.ToDictionary(p => p.Key, p => p.Value with { Prompt = "", Negative = "" })
            };
            File.WriteAllText(ComfyTextsPath, JsonSerializer.Serialize(withoutTexts));
        }
        catch (Exception error) { Log.Write($"Comfy: não guardei os textos — {error.Message}"); }
    }
}
