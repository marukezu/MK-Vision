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
        if (cancel)
        {
            if (_comfyJob is { IsCancellationRequested: false } running)
            {
                running.Cancel();
                _ = ComfyClient.Interrupt();
                Notify("ComfyUI: cancelado");
            }
            else Notify("Nada sendo gerado no ComfyUI");
            return;
        }
        if (_comfyJob is { IsCancellationRequested: false })
        {
            Notify("O ComfyUI ainda está gerando · Shift+G cancela");
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

        var job = _comfyJob = new CancellationTokenSource();
        var token = job.Token;
        if (video) Interlocked.Increment(ref _stillJobs); // o clique no vídeo não dá play enquanto gera
        Task.Run(async () =>
        {
            var clock = Stopwatch.StartNew();
            string lastStatus = "";
            void Progress(string text)
            {
                if (token.IsCancellationRequested) return;
                lastStatus = text;
                Notify(text);
            }
            // O aviso some em 1,5 s: repete o último enquanto espera.
            using var repeat = new Timer(_ => { if (lastStatus.Length > 0 && !token.IsCancellationRequested) Notify(lastStatus); }, null, 1200, 1200);
            try
            {
                // A placa (8 GB) toda para o ComfyUI: as IAs do MK Vision saem da memória dela e voltam sozinhas quando
                // usadas de novo (I: alguns segundos; câmera lenta: ao tocar o vídeo com ela ligada).
                Progress("Liberando a memória da placa para o ComfyUI...");
                if (PhotoAi.Release() | RifeHost.Release() | ToneAi.Release()) GC.Collect();
                Log.Write(pixels is null ? "Comfy: gerando do zero (sem imagem de entrada)." : $"Comfy: enviando {(video ? $"o quadro de {TimeText(time)}" : "a foto")} ({w}x{h})" +
                    (area is { } a ? $", com {(a.mask is not null ? "área marcada" : "marcas")} (anotações)" : "") +
                    (texts.second is { } secondFile ? $" e a imagem 2 ({Path.GetFileName(secondFile)})." : "."));
                var generated = await ComfyClient.Run(workflow, pixels, w, h, prompt, texts.negative, texts.loras, texts.sampler, Progress, token, texts.second);
                var result = generated.Image;
                RememberSeed(workflow, generated.Seed);
                token.ThrowIfCancellationRequested();
                if (generated.IsVideo)
                {
                    // Vídeo (Wan): arquivo temporário (sai na limpeza, ao fechar) aberto no player; Shift+S salva onde quiser.
                    lastStatus = "";
                    string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MKVisionDesktop", "comfy-videos");
                    Directory.CreateDirectory(folder);
                    string file = Path.Combine(folder, $"wan_{DateTime.Now:yyyyMMdd_HHmmss}_seed{generated.Seed}{generated.Extension}");
                    File.WriteAllBytes(file, result);
                    ComfyClient.RememberFile(file);
                    double seconds = clock.Elapsed.TotalSeconds;
                    Log.Write($"Comfy: vídeo pronto em {seconds:0.0} s ({result.Length / 1048576.0:0.0} MB).");
                    _uiQueue.Enqueue((_ =>
                    {
                        // O vídeo já vem girado e espelhado como a tela estava: sem girar de novo.
                        lock (_lock) (_rotation, _flipH, _flipV) = (0, false, false);
                        Open(file);
                        Notify($"Vídeo do ComfyUI pronto em {seconds / 60:0.0} min (seed {generated.Seed}) · Shift+S salva o vídeo · Ctrl+S o quadro");
                    }, null));
                    PlayerNative.PostMessage(Hwnd, WM_RUN, IntPtr.Zero, IntPtr.Zero);
                    return;
                }
                var (output, ow, oh) = ImageFile.DecodePixels(result);
                for (int i = 3; i < output.Length; i += 4) output[i] = 255;
                // Área marcada: a edição só vale dentro dela; fora (e no contorno) volta a imagem original, sem o desenho.
                if (area?.mask is { } editMask && clean is not null) output = ComfyArea.KeepOutside(output, ow, oh, clean, cleanW, cleanH, editMask);
                lastStatus = "";
                bool shown = ShowComfy(output, ow, oh, serial);
                Log.Write($"Comfy: pronto em {clock.Elapsed.TotalSeconds:0.0} s ({ow}x{oh}){(shown ? "" : ", mas a tela já mudou")}.");
                Notify(shown ? $"ComfyUI: pronto em {clock.Elapsed.TotalSeconds:0} s (seed {generated.Seed}) · T alterna com a original · I melhora · Ctrl+S salva" :
                    "ComfyUI terminou, mas o quadro da tela mudou (o resultado foi descartado)");
            }
            catch (OperationCanceledException) { Log.Write("Comfy: cancelado."); }
            catch (Exception error)
            {
                lastStatus = "";
                Log.Write($"Comfy: falhou — {error.Message}");
                Notify("ComfyUI: " + error.Message.Split('\n')[0]);
            }
            finally
            {
                if (video) Interlocked.Decrement(ref _stillJobs);
                if (_comfyJob == job) _comfyJob = null;
                job.Dispose();
                _renderWake.Set();
            }
        }, token);
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
