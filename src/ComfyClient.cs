using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MKVision.Desktop;

// ComfyUI pela API local (http://127.0.0.1:8188), sem a página dele aberta: tecla G do player (PlayerComfy.cs).
// Os workflows são os salvos no ComfyUI (uma aba da janela do G cada: Workflows), lidos a cada pedido e convertidos
// para o formato da API — LoRAs, passos e CFG mexidos (e salvos) lá valem aqui. Troca só a imagem de entrada, o
// prompt, o negativo e a seed; o "Salvar imagem" vira "Prévia" (o resultado não vai para a pasta output; só aparece
// no player) e saídas extras (ex.: a ampliação do SeedVR2) ficam de fora.
// Sem o ComfyUI no ar, liga o servidor escondido (os mesmos argumentos do run_comfyui.bat, sem abrir o navegador) e
// deixa ligado para os próximos pedidos (os modelos ficam carregados) — até o MK Vision fechar, quando ele fecha junto.
// Pasta do ComfyUI: C:\ComfyUI (ou MKV_COMFY_DIR).
internal static class ComfyClient
{
    // Os workflows da janela do G (uma aba cada). Scratch: aceita "gerar do zero" (só texto; restaurar precisa de imagem).
    // Video: o resultado é um vídeo (Wan); a "resolução" da aba é em p (480 ≈ 832×480) na proporção da imagem.
    // Megapixels: a imagem entra ampliada para este tamanho (o "Redimensionar imagem" do workflow), na proporção dela e em
    // múltiplos de 32. Prompt: o texto da aba ao abrir o G pela 1ª vez na sessão (um pedido fixo, não um digitado).
    // SecondImage: tem a imagem 2 (<image2> no prompt), escolhida num arquivo pelo botão da aba; a 1 é a da tela.
    public sealed record Workflow(string Tab, string Name, bool Scratch, bool Video = false, double Megapixels = 0, string Prompt = "",
        bool SecondImage = false);
    public static readonly Workflow[] Workflows =
    [
        new("Qwen 2.1", "Qwen Image 2.1 - Editar imagem", true),
        new("Qwen 2.1 · 2 imagens", "Qwen Image 2.1 - Editar com 2 imagens", false, SecondImage: true),
        new("Qwen 2.1 · restaurar", "Qwen Image 2.1 - Restaurar imagem", false, Megapixels: 2,
            Prompt: "Restore the photo: remove blur, noise and compression artifacts and recover sharp, natural detail, keeping the same composition, people and colors."),
        new("Wan 2.2 · vídeo", "Wan22 I2V - Padrão 480p + LoRA", false, true),
        new("Wan 2.2 · vídeo (3 samplers)", "Wan22 I2V - Padrão 480p + LoRA (3 samplers)", false, true),
    ];

    // Wan: 480p ≈ 832×480 (~400 mil pixels); quadros por segundo do modelo (a duração vira quadros: 4k+1).
    private const double PixelsAt480 = 832 * 480;
    private const int WanFps = 16;
    private const string Host = "127.0.0.1:8188";
    private const string UploadFolder = "mkvision";
    private const string UploadName = "frame.png"; // sempre o mesmo arquivo: a pasta input não acumula quadros
    private const string SecondName = "second.png"; // a imagem 2 (abas com SecondImage), idem
    private const int SecondMaxSide = 2048;          // a imagem 2 vai reduzida até isto (o Qwen a reduz de qualquer jeito)

    public static string Folder => Environment.GetEnvironmentVariable("MKV_COMFY_DIR") ?? @"C:\ComfyUI";
    private static string WorkflowPath(string name) => Path.Combine(Folder, "user", "default", "workflows", name + ".json");
    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MKVisionDesktop", "comfy-server.log");

    // O que o G deixou no ComfyUI (quadro enviado, imagem gerada na pasta temp, geração no histórico): anotado aqui e
    // apagado ao fechar o MK Vision — e de novo ao abrir, se ele fechou à força antes de limpar.
    private static readonly string CleanupList = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MKVisionDesktop", "comfy-cleanup.txt");
    private static readonly object CleanupLock = new();

    private static readonly HttpClient Http = new() { BaseAddress = new Uri($"http://{Host}/"), Timeout = TimeSpan.FromMinutes(5) };
    private static readonly Dictionary<string, JsonObject> NodeInfo = []; // /object_info por tipo de nó

    // Prompt e negativo como estão no workflow salvo (para a primeira vez da aba na janela do G). O Qwen 2.1 tem os
    // dois no mesmo nó; nos outros, o texto que chega às entradas "positivo" e "negativo" do sampler.
    public static (string prompt, string negative)? SavedTexts(string workflowName)
    {
        try
        {
            var workflow = JsonNode.Parse(File.ReadAllText(WorkflowPath(workflowName)))!.AsObject();
            var nodes = workflow["nodes"]!.AsArray().Select(n => n!.AsObject()).ToDictionary(n => n["id"]!.GetValue<int>());
            foreach (var node in nodes.Values)
                if (node["type"]?.GetValue<string>() == "TextEncodeQwenImage21" && node["widgets_values"] is JsonArray values && values.Count >= 2)
                    return (values[0]?.GetValue<string>() ?? "", values[1]?.GetValue<string>() ?? "");
            var links = workflow["links"]!.AsArray().Select(l => l!.AsArray()).ToDictionary(l => l[0]!.GetValue<int>(), l => l[1]!.GetValue<int>());
            // Sobe pelas ligações até um nó com texto (o primeiro campo de texto dele).
            string? TextFrom(int? link, int depth = 0)
            {
                if (link is not int id || depth > 10 || !links.TryGetValue(id, out int src) || !nodes.TryGetValue(src, out var node)) return null;
                if (node["type"]!.GetValue<string>().Contains("TextEncode") && node["widgets_values"] is JsonArray v && v.Count > 0 && v[0] is JsonValue text)
                    return text.GetValue<string>();
                foreach (var input in node["inputs"]?.AsArray() ?? [])
                    if (TextFrom(input?["link"].Int(), depth + 1) is { } found) return found;
                return null;
            }
            var sampler = nodes.Values.FirstOrDefault(n => n["type"]!.GetValue<string>() is "KSampler" or "KSamplerAdvanced");
            int? Input(string name) => sampler?["inputs"]?.AsArray().FirstOrDefault(i => i?["name"]?.GetValue<string>() == name)?["link"].Int();
            if (sampler is not null && TextFrom(Input("positive")) is { } positive) return (positive, TextFrom(Input("negative")) ?? "");
        }
        catch (Exception error) { Log.Write($"Comfy: não consegui ler o workflow salvo — {error.Message}"); }
        return null;
    }

    // Uma LoRA do workflow, como a janela do G mostra e devolve: ligada (senão é pulada, como o "ignorar" do ComfyUI) e
    // a força. Id = nó no workflow; File = arquivo da LoRA (a janela deixa trocar por outro da mesma família do modelo).
    public sealed record LoraSetting(int Id, string Title, string File, bool On, double Strength);

    // Família de cada arquivo de LoRA (pelo cabeçalho: nº de blocos e largura das camadas; ex.: Qwen 2.1 = 32×4096,
    // Qwen Image/2511 = 60×3072, Wan 14B = 40×5120), guardada por caminho, tamanho e data.
    private static readonly Dictionary<string, (long size, DateTime time, string? family)> LoraFamilies = [];

    // As LoRAs da pasta do ComfyUI que servem no modelo deste workflow (da mesma família das que ele já usa), pelo nome
    // que o ComfyUI usa (relativo a models\loras). Sem como saber a família: todas.
    public static List<string> CompatibleLoras(string workflowName)
    {
        string folder = Path.Combine(Folder, "models", "loras");
        if (!Directory.Exists(folder)) return [];
        var files = Directory.EnumerateFiles(folder, "*.safetensors", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(folder, f)).Order(StringComparer.OrdinalIgnoreCase).ToList();
        var wanted = SavedLoras(workflowName).Select(l => LoraFamily(Path.Combine(folder, l.File))).OfType<string>().ToHashSet();
        return wanted.Count == 0 ? files : files.Where(f => LoraFamily(Path.Combine(folder, f)) is { } family && wanted.Contains(family)).ToList();
    }

    private static string? LoraFamily(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) return null;
            lock (LoraFamilies)
                if (LoraFamilies.TryGetValue(path, out var known) && known.size == info.Length && known.time == info.LastWriteTimeUtc) return known.family;
            // safetensors: 8 bytes com o tamanho do cabeçalho (JSON: nome → forma de cada tensor) e o cabeçalho.
            using var stream = info.OpenRead();
            var lengthBytes = new byte[8];
            stream.ReadExactly(lengthBytes);
            long length = BitConverter.ToInt64(lengthBytes);
            if (length <= 0 || length > 64 * 1024 * 1024) return null;
            var header = new byte[length];
            stream.ReadExactly(header);
            using var json = JsonDocument.Parse(header);
            var blockPattern = new System.Text.RegularExpressions.Regex(@"blocks[._](\d+)[._]");
            int blocks = 0;
            var widths = new Dictionary<long, int>();
            foreach (var tensor in json.RootElement.EnumerateObject())
            {
                if (tensor.Name == "__metadata__") continue;
                var match = blockPattern.Match(tensor.Name);
                if (match.Success) blocks = Math.Max(blocks, int.Parse(match.Groups[1].Value) + 1);
                if ((tensor.Name.EndsWith("lora_down.weight") || tensor.Name.EndsWith("lora_A.weight")) &&
                    tensor.Value.TryGetProperty("shape", out var shape) && shape.GetArrayLength() == 2)
                {
                    long width = shape[1].GetInt64();
                    widths[width] = widths.GetValueOrDefault(width) + 1;
                }
            }
            string? family = blocks > 0 && widths.Count > 0 ? $"{blocks}x{widths.MaxBy(p => p.Value).Key}" : null;
            lock (LoraFamilies) LoraFamilies[path] = (info.Length, info.LastWriteTimeUtc, family);
            return family;
        }
        catch (Exception error)
        {
            Log.Write($"Comfy: não li o cabeçalho da LoRA {Path.GetFileName(path)} — {error.Message}");
            return null;
        }
    }

    // Passos, CFG, resolução (Qwen 2.1: o "resolution" dele; Wan: em p, na proporção da imagem; 0 = o workflow não tem),
    // duração em segundos (Wan; 0 = não tem) e seed (fixa = repete; senão, sorteia a cada pedido) da janela do G.
    // Passos valem para todos os samplers (no Wan eles dividem os mesmos passos); CFG e seed, para o principal (o que
    // põe o ruído). O workflow salvo não muda.
    public sealed record SamplerSetting(int Steps, double Cfg, int Resolution, long Seed, bool FixedSeed, double Seconds = 0);

    // O resultado de um pedido: o arquivo (imagem ou vídeo, pela extensão) e a seed que o sampler principal usou.
    public sealed record ComfyResult(byte[] Image, long Seed, string Extension = ".png")
    {
        public bool IsVideo => Extension is ".mp4" or ".webm" or ".mov" or ".mkv";
    }

    // Os valores do sampler e da resolução como estão no workflow salvo (a seed começa sorteada, como sempre foi no G).
    public static SamplerSetting? SavedSampler(string workflowName)
    {
        try
        {
            var workflow = JsonNode.Parse(File.ReadAllText(WorkflowPath(workflowName)))!.AsObject();
            var nodes = workflow["nodes"]!.AsArray().Select(n => n!.AsObject()).Where(n => (n["mode"]?.GetValue<int>() ?? 0) == 0).ToList();
            // O principal: o que põe o ruído (no Wan, o avançado com add_noise "enable"); senão, o primeiro.
            var samplers = nodes.Where(n => n["type"]!.GetValue<string>() is "KSampler" or "KSamplerAdvanced").ToList();
            var sampler = samplers.FirstOrDefault(n => n["type"]!.GetValue<string>() == "KSampler" ||
                                                       (n["widgets_values"] as JsonArray)?[0]?.ToString() == "enable") ?? samplers.FirstOrDefault();
            if (sampler?["widgets_values"] is not JsonArray v) return null;
            int at = sampler["type"]!.GetValue<string>() == "KSamplerAdvanced" ? 1 : 0; // o avançado tem "add_noise" antes
            long seed = v[at] is JsonValue s && s.TryGetValue<long>(out long n) ? n : 0;
            int steps = v[at + 2] is JsonValue st && st.TryGetValue<int>(out int count) ? count : 20;
            double cfg = v[at + 3] is JsonValue c && c.TryGetValue<double>(out double value) ? value : 1;
            int resolution = nodes.FirstOrDefault(x => x["type"]!.GetValue<string>() == "TextEncodeQwenImage21")?["widgets_values"] is JsonArray q &&
                q.Count > 2 && q[2] is JsonValue r && r.TryGetValue<int>(out int res) ? res : 0;
            double seconds = 0;
            if (nodes.FirstOrDefault(x => x["type"]!.GetValue<string>() == "WanImageToVideo")?["widgets_values"] is JsonArray wan && wan.Count >= 3 &&
                wan[0] is JsonValue ww && ww.TryGetValue<int>(out int width) && wan[1] is JsonValue wh && wh.TryGetValue<int>(out int height) &&
                wan[2] is JsonValue wl && wl.TryGetValue<int>(out int length))
            {
                resolution = (int)Math.Round(Math.Sqrt(width * height / PixelsAt480) * 480 / 16) * 16; // 544×736 → 480
                seconds = Math.Round((length - 1) / (double)WanFps, 2);
            }
            return new SamplerSetting(steps, cfg, resolution, seed, false, seconds);
        }
        catch (Exception error)
        {
            Log.Write($"Comfy: não li o sampler do workflow — {error.Message}");
            return null;
        }
    }

    // As LoRAs do workflow salvo, na ordem dele (de cima para baixo), com os valores de lá.
    public static List<LoraSetting> SavedLoras(string workflowName)
    {
        var list = new List<LoraSetting>();
        try
        {
            var workflow = JsonNode.Parse(File.ReadAllText(WorkflowPath(workflowName)))!.AsObject();
            foreach (var node in workflow["nodes"]!.AsArray().Select(n => n!.AsObject())
                         .OrderBy(n => n["pos"]?[1]?.GetValue<double>() ?? 0).ThenBy(n => n["pos"]?[0]?.GetValue<double>() ?? 0))
            {
                string type = node["type"]!.GetValue<string>();
                if (type is not ("LoraLoaderModelOnly" or "LoraLoader") || node["widgets_values"] is not JsonArray values || values.Count < 2) continue;
                string file = values[0]?.GetValue<string>() ?? "";
                double strength = values[1] is JsonValue v && v.TryGetValue<double>(out double s) ? s : 1;
                int mode = node["mode"]?.GetValue<int>() ?? 0;
                if (mode == 2) continue; // mudo: fora do workflow
                list.Add(new LoraSetting(node["id"]!.GetValue<int>(), node["title"]?.GetValue<string>() ?? Path.GetFileNameWithoutExtension(file), file, mode == 0, strength));
            }
        }
        catch (Exception error) { Log.Write($"Comfy: não consegui ler as LoRAs do workflow — {error.Message}"); }
        return list;
    }

    public static async Task<bool> Online()
    {
        try
        {
            using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            using var response = await Http.GetAsync("system_stats", cancel.Token);
            return response.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    // Garante o servidor no ar: liga escondido se preciso e espera responder (até 4 min; a 1ª vez demora mais).
    public static async Task EnsureRunning(Action<string> status, CancellationToken token)
    {
        if (await Online()) return;
        string python = Path.Combine(Folder, ".venv", "Scripts", "python.exe");
        if (!File.Exists(python)) throw new InvalidOperationException($"Não achei o ComfyUI em {Folder}");
        string args = LaunchArguments();
        Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
        status("Ligando o ComfyUI (escondido)...");
        Log.Write($"Comfy: ligando o servidor — {python} main.py {args}");
        StartServer(python, args);
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < TimeSpan.FromMinutes(4))
        {
            token.ThrowIfCancellationRequested();
            await Task.Delay(2000, token);
            if (await Online())
            {
                Log.Write($"Comfy: servidor no ar em {clock.Elapsed.TotalSeconds:0} s.");
                return;
            }
            status($"Ligando o ComfyUI... {clock.Elapsed.TotalSeconds:0} s");
        }
        throw new TimeoutException($"O ComfyUI não respondeu (veja {LogPath})");
    }

    // O servidor ligado por nós fica num "job" do Windows que se encerra junto com o MK Vision (fechado, travado ou
    // finalizado à força): o ComfyUI não fica rodando sozinho depois. Um ComfyUI aberto pelo run_comfyui.bat não é nosso
    // e continua. A saída dele vai para comfy-server.log.
    private static IntPtr _job;
    private static StreamWriter? _serverLog;

    private static void StartServer(string python, string args)
    {
        if (_job == IntPtr.Zero)
        {
            _job = CreateJobObject(IntPtr.Zero, null);
            var limits = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
            limits.BasicLimitInformation.LimitFlags = 0x2000; // JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
            int size = System.Runtime.InteropServices.Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
            if (_job == IntPtr.Zero || !SetInformationJobObject(_job, 9 /* JobObjectExtendedLimitInformation */, ref limits, (uint)size))
                Log.Write("Comfy: não consegui criar o vínculo com o app (o ComfyUI pode continuar aberto depois).");
        }
        _serverLog?.Dispose();
        var logFile = _serverLog = new StreamWriter(LogPath, append: false, Encoding.UTF8) { AutoFlush = true };
        var info = new ProcessStartInfo(python, $"main.py {args}")
        {
            WorkingDirectory = Folder,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        info.Environment["PYTHONIOENCODING"] = "utf-8";
        info.Environment["PYTHONUNBUFFERED"] = "1";
        var process = Process.Start(info) ?? throw new InvalidOperationException("Não consegui ligar o ComfyUI");
        if (_job != IntPtr.Zero && !AssignProcessToJobObject(_job, process.Handle))
            Log.Write("Comfy: o ComfyUI ligou, mas sem o vínculo com o app (pode continuar aberto depois).");
        // O python.exe da .venv é só um lançador: o Python de verdade é um processo filho, que pode nascer antes do
        // vínculo acima (aí não entraria no job). Vincula os filhos que aparecerem nos primeiros segundos.
        if (_job != IntPtr.Zero)
        {
            var root = process.Id;
            Task.Run(async () =>
            {
                var done = new HashSet<int> { root };
                for (int i = 0; i < 20; i++)
                {
                    foreach (int child in Descendants(root))
                        if (done.Add(child))
                        {
                            using var p = Process.GetProcessById(child);
                            if (!AssignProcessToJobObject(_job, p.Handle)) Log.Write($"Comfy: o processo {child} ficou sem o vínculo com o app.");
                        }
                    await Task.Delay(250);
                }
            });
        }
        void Write(string? line)
        {
            if (line is null) return;
            lock (logFile) logFile.WriteLine(line);
        }
        process.OutputDataReceived += (_, e) => Write(e.Data);
        process.ErrorDataReceived += (_, e) => Write(e.Data);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
    }

    // Filhos, netos... de um processo (lista de processos do Windows).
    private static List<int> Descendants(int root)
    {
        var parents = new Dictionary<int, int>();
        var snapshot = CreateToolhelp32Snapshot(0x2 /* TH32CS_SNAPPROCESS */, 0);
        if (snapshot == new IntPtr(-1)) return [];
        try
        {
            var entry = new PROCESSENTRY32W { dwSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<PROCESSENTRY32W>() };
            for (bool ok = Process32FirstW(snapshot, ref entry); ok; ok = Process32NextW(snapshot, ref entry))
                parents[(int)entry.th32ProcessID] = (int)entry.th32ParentProcessID;
        }
        finally { CloseHandle(snapshot); }
        var found = new List<int>();
        var queue = new Queue<int>([root]);
        while (queue.Count > 0)
        {
            int parent = queue.Dequeue();
            foreach (var (id, p) in parents)
                if (p == parent && id != root && !found.Contains(id))
                {
                    found.Add(id);
                    queue.Enqueue(id);
                }
        }
        return found;
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private struct PROCESSENTRY32W
    {
        public uint dwSize, cntUsage, th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint th32ModuleID, cntThreads, th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szExeFile;
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);
    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern bool Process32FirstW(IntPtr snapshot, ref PROCESSENTRY32W entry);
    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern bool Process32NextW(IntPtr snapshot, ref PROCESSENTRY32W entry);
    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern IntPtr CreateJobObject(IntPtr attributes, string? name);
    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern bool SetInformationJobObject(IntPtr job, int infoClass, ref JOBOBJECT_EXTENDED_LIMIT_INFORMATION info, uint length);
    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass, SchedulingClass;
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount;
        public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
    }

    // Os argumentos da linha do main.py no run_comfyui.bat, sem abrir o navegador.
    private static string LaunchArguments()
    {
        // --disable-metadata: o prompt não fica gravado nas imagens e vídeos que o ComfyUI salva.
        const string fallback = "--reserve-vram 1 --cache-ram 4 --disable-fast-disk --disable-metadata";
        try
        {
            string bat = Path.Combine(Folder, "run_comfyui.bat");
            var line = File.ReadAllLines(bat).FirstOrDefault(l => l.Contains("main.py"));
            if (line is null) return fallback;
            string args = line[(line.IndexOf("main.py", StringComparison.Ordinal) + "main.py".Length)..];
            var list = args.Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(a => a is not ("--auto-launch" or "%*")).ToList();
            if (!list.Contains("--disable-metadata")) list.Add("--disable-metadata");
            return string.Join(' ', list);
        }
        catch { return fallback; }
    }

    // Executa o workflow com esta imagem (BGRA) e textos; devolve a imagem gerada (PNG/JPG). "progress" recebe o
    // texto de andamento. Sem imagem (bgra = null): "do zero", só pelo texto — sem a imagem 1 e sem a LoRA de
    // consistência (ela serve para manter a imagem de entrada); o Qwen 2.1 gera quadrada (resolution × resolution).
    // "second": o arquivo da imagem 2 (abas com SecondImage).
    public static async Task<ComfyResult> Run(string workflowName, byte[]? bgra, int w, int h, string prompt, string negative,
        IReadOnlyList<LoraSetting>? loras, SamplerSetting? settings, Action<string> progress, CancellationToken token, string? second = null)
    {
        await EnsureRunning(progress, token);
        string clientId = Guid.NewGuid().ToString("N");

        // 1. Sobe a imagem (e a imagem 2) para a pasta input\mkvision.
        async Task Upload(byte[] png, string name)
        {
            using var form = new MultipartFormDataContent();
            var file = new ByteArrayContent(png);
            file.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/png");
            form.Add(file, "image", name);
            form.Add(new StringContent(UploadFolder), "subfolder");
            form.Add(new StringContent("true"), "overwrite");
            using var response = await Http.PostAsync("upload/image", form, token);
            response.EnsureSuccessStatusCode();
        }
        if (bgra is not null)
        {
            progress("Enviando o quadro ao ComfyUI...");
            await Upload(ImageFile.EncodePng(bgra, w, h), UploadName);
        }
        if (second is not null)
        {
            progress("Enviando a imagem 2 ao ComfyUI...");
            var (pixels2, w2, h2) = ImageFile.LoadPixels(second, SecondMaxSide);
            await Upload(ImageFile.EncodePng(pixels2, w2, h2), SecondName);
        }

        // 2. Workflow salvo → formato da API, com a imagem, os textos e uma seed nova.
        var (api, outputNode, steps, seed, stages) = await BuildPrompt(workflowName, bgra is null ? null : $"{UploadFolder}/{UploadName}",
            second is null ? null : $"{UploadFolder}/{SecondName}", prompt, negative, loras, settings, (w, h), token);

        // 3. Andamento pelo WebSocket (conectado antes de enfileirar, para não perder o começo).
        using var socket = new ClientWebSocket();
        bool live = true;
        try { await socket.ConnectAsync(new Uri($"ws://{Host}/ws?clientId={clientId}"), token); }
        catch (Exception error)
        {
            live = false;
            Log.Write($"Comfy: sem WebSocket ({error.Message}); acompanhando pelo histórico.");
        }

        string promptId;
        {
            var body = new JsonObject { ["prompt"] = api, ["client_id"] = clientId };
            using var response = await Http.PostAsync("prompt", new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"), token);
            string text = await response.Content.ReadAsStringAsync(token);
            if (!response.IsSuccessStatusCode) throw new InvalidOperationException("O ComfyUI recusou o workflow: " + ShortError(text));
            promptId = JsonNode.Parse(text)!["prompt_id"]!.GetValue<string>();
        }
        Remember("history", promptId);
        Log.Write($"Comfy: na fila ({promptId}), {steps} passos, seed {seed}.");
        progress("Na fila do ComfyUI...");

        if (live) await Follow(socket, promptId, steps, stages, progress, token);
        else await Poll(promptId, progress, token);

        // 4. Resultado: a imagem da prévia (pasta temp do ComfyUI).
        using var history = await Http.GetAsync($"history/{promptId}", token);
        var entry = JsonNode.Parse(await history.Content.ReadAsStringAsync(token))?[promptId]
            ?? throw new InvalidOperationException("O ComfyUI não devolveu o resultado");
        if (entry["status"]?["status_str"]?.GetValue<string>() == "error")
            throw new InvalidOperationException("O ComfyUI deu erro: " + ErrorOf(entry));
        // Imagem (prévia, na pasta temp) ou vídeo (o "Salvar vídeo" grava na pasta output; sai na limpeza, como a temp).
        var outputs = entry["outputs"]?[outputNode];
        var media = (outputs?["images"] ?? outputs?["videos"] ?? outputs?["gifs"])?[0]
            ?? throw new InvalidOperationException("O workflow terminou sem imagem nem vídeo");
        string fileName = media["filename"]!.GetValue<string>(), subfolder = media["subfolder"]?.GetValue<string>() ?? "",
            type = media["type"]?.GetValue<string>() ?? "temp";
        if (type is "temp" or "output") Remember("temp", Path.Combine(Folder, type, subfolder, fileName));
        string query = $"view?filename={Uri.EscapeDataString(fileName)}&subfolder={Uri.EscapeDataString(subfolder)}&type={Uri.EscapeDataString(type)}";
        string extension = Path.GetExtension(fileName).ToLowerInvariant();
        var bytes = await Http.GetByteArrayAsync(query, token);
        if (extension is ".mp4" or ".mov") WithoutMetadata(bytes);
        return new ComfyResult(bytes, seed, extension);
    }

    // O ComfyUI grava o pedido (com o prompt) dentro do vídeo; o servidor que o app liga não grava (--disable-metadata),
    // mas um aberto à mão grava. Os blocos de metadados do MP4 (moov/udta e moov/meta) viram "free" (que os players
    // ignoram) cheios de zeros: o tamanho e o resto do arquivo não mudam.
    public static void WithoutMetadata(byte[] data)
    {
        static long Box(byte[] d, long at, long end, out int header)
        {
            header = 8;
            long size = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan((int)at, 4));
            if (size == 1 && at + 16 <= end)
            {
                size = (long)System.Buffers.Binary.BinaryPrimitives.ReadUInt64BigEndian(d.AsSpan((int)at + 8, 8));
                header = 16;
            }
            else if (size == 0) size = end - at;
            return size < header || at + size > end ? -1 : size;
        }
        for (long at = 0; at + 8 <= data.Length;)
        {
            long size = Box(data, at, data.Length, out int header);
            if (size < 0) return;
            if (Encoding.ASCII.GetString(data, (int)at + 4, 4) == "moov")
                for (long child = at + header; child + 8 <= at + size;)
                {
                    long childSize = Box(data, child, at + size, out int childHeader);
                    if (childSize < 0) break;
                    if (Encoding.ASCII.GetString(data, (int)child + 4, 4) is "udta" or "meta")
                    {
                        "free"u8.CopyTo(data.AsSpan((int)child + 4, 4));
                        data.AsSpan((int)(child + childHeader), (int)(childSize - childHeader)).Clear(); // o texto some do arquivo
                    }
                    child += childSize;
                }
            at += size;
        }
    }

    private static void Remember(string kind, string value)
    {
        try
        {
            lock (CleanupLock)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(CleanupList)!);
                File.AppendAllText(CleanupList, $"{kind}|{value}{Environment.NewLine}");
            }
        }
        catch (Exception error) { Log.Write($"Comfy: não anotei para limpar — {error.Message}"); }
    }

    // Apaga o que o G deixou: a pasta input\mkvision, as imagens geradas (pasta temp) e as gerações no histórico do
    // ComfyUI (se ele estiver no ar). Só o que foi anotado: nada gerado direto no ComfyUI é tocado.
    public static void Cleanup()
    {
        lock (CleanupLock)
        {
            int files = 0, entries = 0;
            try
            {
                string uploads = Path.Combine(Folder, "input", UploadFolder);
                if (Directory.Exists(uploads))
                {
                    Directory.Delete(uploads, recursive: true);
                    files++;
                }
                if (!File.Exists(CleanupList)) return;
                var history = new List<string>();
                foreach (var line in File.ReadAllLines(CleanupList))
                {
                    var (kind, value) = (line.Split('|', 2)[0], line.Split('|', 2).ElementAtOrDefault(1) ?? "");
                    if (kind == "temp" && File.Exists(value))
                    {
                        File.Delete(value);
                        files++;
                    }
                    else if (kind == "history" && value.Length > 0) history.Add(value);
                }
                if (history.Count > 0)
                {
                    try
                    {
                        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                        var body = new JsonObject { ["delete"] = new JsonArray(history.Select(h => (JsonNode)h).ToArray()) };
                        using var response = Http.PostAsync("history", new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"), cancel.Token)
                            .GetAwaiter().GetResult();
                        if (response.IsSuccessStatusCode) entries = history.Count;
                    }
                    catch { } // ComfyUI fora do ar: o histórico (só na memória dele) já se foi com ele
                }
                File.Delete(CleanupList);
            }
            catch (Exception error) { Log.Write($"Comfy: limpeza incompleta — {error.Message}"); }
            finally
            {
                if (files + entries > 0) Log.Write($"Comfy: limpeza — {files} arquivo(s)/pasta(s) e {entries} geração(ões) do histórico apagados.");
            }
        }
    }

    // --comfytest <imagem> <prompt> <saída> [aba] [imagem 2]: o caminho inteiro da tecla G, sem o player (liga o ComfyUI se
    // preciso). Com a imagem 2 (aba "dois"): 4 passos, só para conferir o pedido inteiro.
    public static int Test(string input, string prompt, string output, int tab = 0, string? second = null)
    {
        Console.OutputEncoding = Encoding.UTF8;
        try
        {
            var (pixels, w, h) = ImageFile.LoadPixels(input, 2048);
            var clock = Stopwatch.StartNew();
            string last = "";
            var quick = second is null ? null : SavedSampler(Workflows[tab].Name) is { } saved ? saved with { Steps = 4 } : null;
            var result = Run(Workflows[tab].Name, pixels, w, h, prompt, "blurry, lowres", null, quick, p =>
            {
                if (p != last) Console.WriteLine($"{clock.Elapsed.TotalSeconds,5:0}s  {p}");
                last = p;
            }, CancellationToken.None, second).GetAwaiter().GetResult().Image;
            File.WriteAllBytes(output, result);
            var (_, ow, oh) = ImageFile.DecodePixels(result);
            Console.WriteLine($"OK em {clock.Elapsed.TotalSeconds:0} s: {output} ({ow}x{oh})");
            Log.Write($"Comfy (teste): OK em {clock.Elapsed.TotalSeconds:0} s, {ow}x{oh}.");
            return 0;
        }
        catch (Exception error)
        {
            Console.WriteLine($"Falhou: {error.Message}");
            Log.Write($"Comfy (teste): falhou — {error}");
            return 1;
        }
    }

    // --comfybuild [zero|restore|wan|wan3]: mostra o pedido que iria ao ComfyUI (sem enviar; só lê as definições dos nós).
    public static int BuildTest(bool scratch, int tab = 0)
    {
        Console.OutputEncoding = Encoding.UTF8;
        try
        {
            EnsureRunning(Console.WriteLine, CancellationToken.None).GetAwaiter().GetResult();
            string name = Workflows[tab].Name;
            var saved = SavedSampler(name);
            bool video = Workflows[tab].Video;
            var settings = saved is null ? null : saved with { Steps = saved.Steps + 1, Cfg = saved.Cfg + 0.5,
                Resolution = saved.Resolution > 0 ? (video ? 480 : 1280) : 0, Seed = 12345, FixedSeed = true, Seconds = video ? 3 : 0 };
            Console.WriteLine($"do workflow: {saved} → pedido: {settings}");
            var (api, output, steps, seed, stages) = BuildPrompt(name, scratch ? null : $"{UploadFolder}/{UploadName}",
                Workflows[tab].SecondImage ? $"{UploadFolder}/{SecondName}" : null, "teste positivo", "teste negativo",
                TestLoras(name), settings, (1024, 1536), CancellationToken.None).GetAwaiter().GetResult();
            Console.WriteLine($"etapas: {string.Join(" → ", stages)}");
            foreach (var (id, node) in api)
            {
                var inputs = node!["inputs"]!.AsObject().Select(p => p.Key + "=" + (p.Value is JsonArray l ? $"→{l[0]}" : p.Value?.ToJsonString() is { Length: > 40 } s ? s[..40] + "…" : p.Value?.ToJsonString()));
                Console.WriteLine($"{id,4} {node["class_type"]}: {string.Join(", ", inputs)}");
            }
            Console.WriteLine($"saída: {output} · passos: {steps} · seed: {seed}");
            return 0;
        }
        catch (Exception error)
        {
            Console.WriteLine($"Falhou: {error}");
            return 1;
        }
    }

    // Para o --comfybuild: a 1ª LoRA do workflow desligada, as outras com força 0.33 e a última trocada pela última
    // compatível da pasta (como escolhida na lista da janela).
    private static List<LoraSetting> TestLoras(string workflowName)
    {
        var compatible = CompatibleLoras(workflowName);
        Console.WriteLine($"compatíveis ({compatible.Count}): {string.Join(", ", compatible.Select(Path.GetFileNameWithoutExtension))}");
        var loras = SavedLoras(workflowName);
        return loras.Select((l, i) => i == 0 && loras.Count > 1 ? l with { On = false } :
            i == loras.Count - 1 && compatible.Count > 0 ? l with { On = true, Strength = 0.33, File = compatible[^1] } : l with { On = true, Strength = 0.33 }).ToList();
    }

    // Cancela o que está rodando (Shift+G).
    public static async Task Interrupt()
    {
        try { using var _ = await Http.PostAsync("interrupt", null); }
        catch { } // fora do ar: nada a cancelar
    }

    // Arquivo nosso (ex.: o vídeo do Wan guardado para o player) para apagar na limpeza, como os do ComfyUI.
    public static void RememberFile(string path) => Remember("temp", path);

    // "stages": os samplers na ordem em que rodam (Wan: HIGH, LOW...) — o andamento mostra a etapa de cada um.
    private static async Task Follow(ClientWebSocket socket, string promptId, int steps, List<string> stages, Action<string> progress,
        CancellationToken token)
    {
        var buffer = new byte[1 << 16];
        using var message = new MemoryStream();
        var clock = Stopwatch.StartNew();
        while (socket.State == WebSocketState.Open)
        {
            message.SetLength(0);
            WebSocketReceiveResult result;
            do
            {
                result = await socket.ReceiveAsync(buffer, token);
                message.Write(buffer, 0, result.Count);
            } while (!result.EndOfMessage);
            if (result.MessageType != WebSocketMessageType.Text) continue; // prévias binárias do sampler
            var json = JsonNode.Parse(message.ToArray());
            var data = json?["data"];
            if (data?["prompt_id"]?.GetValue<string>() is { } id && id != promptId) continue;
            switch (json?["type"]?.GetValue<string>())
            {
                case "execution_start":
                    progress("ComfyUI: preparando o modelo na placa...");
                    break;
                case "progress":
                    int value = data!["value"]!.GetValue<int>(), max = data["max"]!.GetValue<int>();
                    string node = data["node"]?.ToString() ?? "";
                    if (stages.Count > 1 && stages.IndexOf(node) is int stage and >= 0)
                    {
                        // Vários samplers (Wan): cada um faz um trecho dos passos.
                        progress($"ComfyUI: etapa {stage + 1}/{stages.Count} · {value}/{max} · {clock.Elapsed.TotalSeconds:0} s");
                        break;
                    }
                    if (max != steps) break; // outro nó com andamento (não o sampler)
                    if (value <= 1) clock.Restart(); // o sampler começou: a estimativa conta do 1º passo
                    double left = value > 1 ? clock.Elapsed.TotalSeconds * (max - value) / (value - 1) : -1;
                    progress($"ComfyUI: {value}/{max} passos" + (left >= 0 ? $" · falta ~{Math.Ceiling(left):0} s" : ""));
                    break;
                case "executing" when (data?["node"] is null):
                case "execution_success":
                    return;
                case "execution_error":
                    throw new InvalidOperationException("O ComfyUI deu erro: " + (data?["exception_message"]?.GetValue<string>() ?? "?").Split('\n')[0]);
                case "execution_interrupted":
                    throw new OperationCanceledException();
            }
        }
        await Poll(promptId, progress, token); // a conexão caiu: termina pelo histórico
    }

    private static async Task Poll(string promptId, Action<string> progress, CancellationToken token)
    {
        var clock = Stopwatch.StartNew();
        while (true)
        {
            await Task.Delay(2000, token);
            using var response = await Http.GetAsync($"history/{promptId}", token);
            if (JsonNode.Parse(await response.Content.ReadAsStringAsync(token))?[promptId] is not null) return;
            progress($"ComfyUI: gerando... {clock.Elapsed.TotalSeconds:0} s");
        }
    }

    // ---------- Workflow salvo (formato da página) → formato da API ----------

    // "loras": o que a janela do G escolheu (ligada e força); as que não estão na lista ficam como no workflow.
    // "settings": passos, CFG, resolução, duração e seed da janela do G (null = como no workflow, seed sorteada).
    // "size": a imagem de entrada (o Wan faz o vídeo na proporção dela). Devolve também os samplers na ordem em que rodam.
    private static async Task<(JsonObject api, string outputNode, int steps, long seed, List<string> stages)> BuildPrompt(string workflowName,
        string? image, string? image2, string prompt, string negative, IReadOnlyList<LoraSetting>? loras, SamplerSetting? settings, (int w, int h) size,
        CancellationToken token)
    {
        string path = WorkflowPath(workflowName);
        if (!File.Exists(path)) throw new InvalidOperationException($"Não achei o workflow \"{workflowName}\" no ComfyUI");
        var workflow = JsonNode.Parse(await File.ReadAllTextAsync(path, token))!.AsObject();
        var nodes = workflow["nodes"]!.AsArray().Select(n => n!.AsObject()).ToDictionary(n => n["id"]!.GetValue<int>());
        var links = new Dictionary<int, (int src, int slot, string type)>();
        foreach (var link in workflow["links"]!.AsArray())
        {
            var l = link!.AsArray();
            links[l[0]!.GetValue<int>()] = (l[1]!.GetValue<int>(), l[2]!.GetValue<int>(), l[5]?.ToString() ?? "");
        }

        // Só da página (não executam): notas, reroute, primitivos; mode 2 = mudo, mode 4 = ignorado (bypass).
        static bool Virtual(string type) => type is "MarkdownNote" or "Note" or "Reroute" or "PrimitiveNode";
        var chosen = (loras ?? []).ToDictionary(l => l.Id);
        int Mode(JsonObject n) => chosen.TryGetValue(n["id"]!.GetValue<int>(), out var l) ? (l.On ? 0 : 4) : n["mode"]?.GetValue<int>() ?? 0;

        // De onde vem uma ligação, atravessando reroutes e nós ignorados. null = de um nó mudo (a entrada fica sem).
        JsonNode? Source(int linkId, int depth = 0)
        {
            if (depth > 50 || !links.TryGetValue(linkId, out var link) || !nodes.TryGetValue(link.src, out var src)) return null;
            string type = src["type"]!.GetValue<string>();
            if (type == "PrimitiveNode") return (src["widgets_values"] as JsonArray)?[0]?.DeepClone();
            if (type == "Reroute" || Mode(src) == 4)
            {
                // Passa adiante a entrada do mesmo tipo.
                foreach (var input in src["inputs"]?.AsArray() ?? [])
                    if (input?["link"].Int() is int up && (type == "Reroute" || input["type"]?.ToString() == link.type))
                        return Source(up, depth + 1);
                return null;
            }
            if (Mode(src) == 2) return null;
            return new JsonArray(link.src.ToString(), link.slot);
        }

        var api = new JsonObject();
        foreach (var (id, node) in nodes)
        {
            string type = node["type"]!.GetValue<string>();
            if (Virtual(type) || Mode(node) is 2 or 4) continue;
            var info = await Info(type, token);
            var inputs = new JsonObject();
            var linked = new Dictionary<string, int?>();
            foreach (var input in node["inputs"]?.AsArray() ?? [])
                if (input?["name"]?.GetValue<string>() is { } name) linked[name] = input["link"].Int();

            // Valores dos campos: pelo nome, quando a página os guardou assim; senão na ordem da definição do nó (com o
            // "depois de gerar" das seeds ocupando um lugar).
            var values = node["widgets_values_named"] as JsonObject ?? node["widgets_values"];
            int at = 0;
            foreach (var (name, spec) in WidgetInputs(info))
            {
                JsonNode? value = values switch
                {
                    JsonArray array => at < array.Count ? array[at]?.DeepClone() : null,
                    JsonObject named => named[name]?.DeepClone(),
                    _ => null
                };
                at++;
                if (values is JsonArray && HasControl(name, spec)) at++; // "fixed" / "randomize"
                if (linked.TryGetValue(name, out var l) && l is int fromLink)
                {
                    if (Source(fromLink) is { } source) inputs[name] = source;
                }
                else if (value is not null) inputs[name] = value;
            }
            // Listas com sub-campos (o "formato" do Salvar vídeo): a API recebe cada sub-campo como "formato.codec".
            if (values is JsonObject all)
                foreach (var (name, value) in all)
                    if (name.Contains('.') && !inputs.ContainsKey(name) && !(linked.GetValueOrDefault(name.Split('.')[0]) is int)) inputs[name] = value?.DeepClone();
            // Ligações (modelos, imagens, condicionamento...).
            foreach (var (name, l) in linked)
                if (l is int fromLink && !inputs.ContainsKey(name) && Source(fromLink) is { } source) inputs[name] = source;
            api[id.ToString()] = new JsonObject { ["class_type"] = type, ["inputs"] = inputs };
        }

        // Saída: a primeira que não passa pela ampliação do SeedVR2 (lenta e pesada); as outras saem do pedido (e o
        // que só elas usavam não roda).
        HashSet<string> Ancestors(string id)
        {
            var seen = new HashSet<string>();
            var stack = new Stack<string>([id]);
            while (stack.Count > 0)
                foreach (var (_, input) in api[stack.Pop()]?["inputs"]?.AsObject() ?? [])
                    if (input is JsonArray link && seen.Add(link[0]!.GetValue<string>())) stack.Push(link[0]!.GetValue<string>());
            return seen;
        }
        var outputs = api.Where(p => NodeInfo.TryGetValue(p.Value!["class_type"]!.GetValue<string>(), out var i) && i["output_node"]?.GetValue<bool>() == true)
                         .Select(p => p.Key).ToList();
        string output = outputs.FirstOrDefault(o => !Ancestors(o).Any(a => api[a]!["class_type"]!.GetValue<string>().StartsWith("SeedVR2")))
            ?? throw new InvalidOperationException("O workflow não tem um \"Salvar imagem\"");
        foreach (var other in outputs.Where(o => o != output)) api.Remove(other);
        var used = Ancestors(output);

        // Textos e imagem de entrada. Qwen 2.1: prompt e negativo no mesmo nó, a imagem na "image_1" dele. Outros: o nó de
        // texto que chega ao "positivo" e ao "negativo" do sampler, e o único "Carregar imagem" do caminho.
        var qwen21 = api.Select(p => p.Value!.AsObject()).FirstOrDefault(n => n["class_type"]!.GetValue<string>() == "TextEncodeQwenImage21");
        if (qwen21 is not null)
        {
            qwen21["inputs"]!["prompt"] = Expand(prompt);
            qwen21["inputs"]!["negative_prompt"] = Expand(negative);
            if (image is null)
            {
                FromScratch(api, qwen21);
                used.RemoveWhere(id => !api.ContainsKey(id)); // as LoRAs de consistência saíram do pedido
            }
            else
            {
                // Sobe pela imagem N (passando por um "Redimensionar imagem", se houver) até o "Carregar imagem".
                JsonObject? LoaderOf(string input)
                {
                    var link = qwen21["inputs"]![input];
                    for (int depth = 0; depth < 5 && link is JsonArray l && api[l[0]!.GetValue<string>()] is JsonObject node; depth++)
                    {
                        if (node["class_type"]!.GetValue<string>() == "LoadImage") return node;
                        link = node["inputs"]?["image"];
                    }
                    return null;
                }
                var loader = LoaderOf("images.image_1") ?? throw new InvalidOperationException("No workflow, a imagem 1 do prompt não vem de um \"Carregar imagem\"");
                loader["inputs"]!["image"] = image;
                if (Workflows.FirstOrDefault(w => w.Name == workflowName) is { SecondImage: true })
                {
                    if (image2 is null) throw new InvalidOperationException("Escolha a imagem 2 (o botão embaixo, na aba)");
                    var second = LoaderOf("images.image_2") ?? throw new InvalidOperationException("No workflow, a imagem 2 do prompt não vem de um \"Carregar imagem\"");
                    second["inputs"]!["image"] = image2;
                }
            }
        }
        else
        {
            var sampler = api.Where(p => used.Contains(p.Key) || p.Key == output).Select(p => p.Value!.AsObject())
                .FirstOrDefault(n => n["class_type"]!.GetValue<string>() is "KSampler" or "KSamplerAdvanced")
                ?? throw new InvalidOperationException("O workflow não tem um sampler (KSampler)");
            // Sobe pelas ligações até o nó com o campo de texto ("prompt" ou "text"). Nó com "positive" e "negative" (ex.:
            // o WanImageToVideo): a saída 0 é o positivo e a 1 o negativo — segue a entrada correspondente.
            (JsonObject node, string field)? Encoder(JsonNode? link, int depth = 0)
            {
                if (link is not JsonArray l || depth > 10 || api[l[0]!.GetValue<string>()]?["inputs"] is not JsonObject inputs) return null;
                foreach (var field in new[] { "prompt", "text" })
                    if (inputs[field] is JsonValue v && v.GetValueKind() == JsonValueKind.String) return (inputs, field);
                if (inputs["positive"] is JsonArray && inputs["negative"] is JsonArray)
                    return Encoder(l[1]!.GetValue<int>() == 1 ? inputs["negative"] : inputs["positive"], depth + 1);
                foreach (var (_, input) in inputs)
                    if (Encoder(input, depth + 1) is { } found) return found;
                return null;
            }
            var positive = Encoder(sampler["inputs"]!["positive"]) ?? throw new InvalidOperationException("Não achei o nó do prompt no workflow");
            positive.node[positive.field] = Expand(prompt);
            if (Encoder(sampler["inputs"]!["negative"]) is { } neg && neg.node != positive.node) neg.node[neg.field] = Expand(negative);
            if (image is null) throw new InvalidOperationException("Este modelo edita uma imagem: desmarque \"Gerar do zero\"");
            var loaders = used.Where(id => api[id]!["class_type"]!.GetValue<string>() == "LoadImage").ToList();
            if (loaders.Count != 1) throw new InvalidOperationException($"O workflow precisa de um só \"Carregar imagem\" (tem {loaders.Count})");
            api[loaders[0]]!["inputs"]!["image"] = image;
        }

        // Arquivo e força da janela do G (as desligadas já ficaram de fora, como "ignoradas").
        foreach (var lora in chosen.Values.Where(l => l.On))
            if (api[lora.Id.ToString()]?["inputs"] is JsonObject inputs)
            {
                if (lora.File.Length > 0) inputs["lora_name"] = lora.File;
                inputs["strength_model"] = lora.Strength;
                if (inputs.ContainsKey("strength_clip")) inputs["strength_clip"] = lora.Strength;
            }

        // Samplers na ordem em que rodam (pelo passo em que começam). O principal põe o ruído: o KSampler, ou o avançado
        // com add_noise "enable" (Wan). Passos: em todos (no Wan eles dividem os mesmos passos); CFG e seed: no principal.
        var samplerIds = used.Where(id => api[id]!["class_type"]!.GetValue<string>() is "KSampler" or "KSamplerAdvanced")
            .OrderBy(id => api[id]!["inputs"]!["start_at_step"] is JsonValue start && start.TryGetValue<int>(out int at) ? at : 0)
            .ThenBy(id => api[id]!["inputs"]!["add_noise"]?.ToString() == "enable" ? 0 : 1).ToList();
        string? main = samplerIds.FirstOrDefault(id => api[id]!["class_type"]!.GetValue<string>() == "KSampler" ||
                                                       api[id]!["inputs"]!["add_noise"]?.ToString() == "enable") ?? samplerIds.FirstOrDefault();
        if (settings is not null && main is not null)
        {
            foreach (var id in samplerIds) api[id]!["inputs"]!["steps"] = Math.Clamp(settings.Steps, 1, 200);
            api[main]!["inputs"]!["cfg"] = Math.Clamp(settings.Cfg, 0, 30);
        }
        if (settings is { Resolution: > 0 } && qwen21 is not null) qwen21["inputs"]!["resolution"] = Math.Clamp(settings.Resolution / 32 * 32, 256, 4096);
        // Wan: o vídeo na proporção da imagem de entrada, com os pixels da resolução em p (480p ≈ 832×480), em múltiplos
        // de 32; a duração vira quadros (16 por segundo, 4k+1: 5 s = 81).
        foreach (var id in used.Where(id => api[id]!["class_type"]!.GetValue<string>() == "WanImageToVideo"))
        {
            var inputs = api[id]!["inputs"]!;
            if (settings is { Resolution: > 0 } && size.w > 0 && size.h > 0)
            {
                double pixels = PixelsAt480 * Math.Pow(Math.Clamp(settings.Resolution, 240, 1080) / 480.0, 2);
                double k = Math.Sqrt(pixels / (size.w * (double)size.h));
                inputs["width"] = Math.Max(128, (int)Math.Round(size.w * k / 32) * 32);
                inputs["height"] = Math.Max(128, (int)Math.Round(size.h * k / 32) * 32);
            }
            if (settings is { Seconds: > 0 }) inputs["length"] = (int)Math.Round(Math.Clamp(settings.Seconds, 1, 10) * WanFps / 4) * 4 + 1;
        }
        // Restaurar: a imagem ampliada (ou reduzida) para ~N megapixels, na proporção dela, lados em múltiplos de 32.
        if (Workflows.FirstOrDefault(w => w.Name == workflowName) is { Megapixels: > 0 } target && size.w > 0 && size.h > 0)
            foreach (var id in used.Where(id => api[id]!["class_type"]!.GetValue<string>() == "ImageScale"))
            {
                double k = Math.Sqrt(target.Megapixels * 1_000_000 / (size.w * (double)size.h));
                api[id]!["inputs"]!["width"] = Math.Max(256, (int)Math.Round(size.w * k / 32) * 32);
                api[id]!["inputs"]!["height"] = Math.Max(256, (int)Math.Round(size.h * k / 32) * 32);
            }
        int steps = 0;
        long usedSeed = 0;
        foreach (var (id, value) in api)
        {
            var inputs = value!["inputs"]!.AsObject();
            foreach (var field in new[] { "seed", "noise_seed" })
                if (inputs[field] is JsonValue)
                {
                    long seed = id == main && settings is { FixedSeed: true } ? settings.Seed : Random.Shared.NextInt64(1, 1L << 50);
                    inputs[field] = seed;
                    if (id == main) usedSeed = seed;
                }
            if (used.Contains(id) && inputs["steps"] is JsonValue s && s.TryGetValue<int>(out int count)) steps = Math.Max(steps, count);
        }
        if (api[output]!["class_type"]!.GetValue<string>() == "SaveImage")
        {
            api[output]!["class_type"] = "PreviewImage"; // só para o player: nada na pasta output
            api[output]!["inputs"]!.AsObject().Remove("filename_prefix");
        }
        // Só vai o que leva à saída (ex.: os nós do SeedVR2 e o "Carregar imagem" do "do zero" ficam de fora).
        var needed = Ancestors(output);
        foreach (var id in api.Select(p => p.Key).Where(id => id != output && !needed.Contains(id)).ToList()) api.Remove(id);
        return (api, output, steps, usedSeed, samplerIds);
    }

    // "Do zero": o nó do prompt sem nenhuma imagem (o Carregar imagem fica sem uso e não roda) e as LoRAs de consistência
    // puladas (quem recebia o modelo delas passa a receber o que entrava nelas). Só neste pedido: o arquivo não muda.
    private static void FromScratch(JsonObject api, JsonObject encode)
    {
        var inputs = encode["inputs"]!.AsObject();
        foreach (var name in inputs.Select(p => p.Key).Where(k => k.StartsWith("images.")).ToList()) inputs.Remove(name);
        var skipped = api.Where(p => p.Value!["class_type"]!.GetValue<string>() is "LoraLoaderModelOnly" or "LoraLoader" &&
                                     (p.Value["inputs"]?["lora_name"]?.ToString() ?? "").Contains("consistency", StringComparison.OrdinalIgnoreCase))
                         .Select(p => p.Key).ToList();
        foreach (var id in skipped)
        {
            var lora = api[id]!["inputs"]!;
            foreach (var (_, value) in api)
                foreach (var (name, input) in value!["inputs"]!.AsObject().ToList())
                    if (input is JsonArray link && link[0]!.GetValue<string>() == id)
                    {
                        int slot = link[1]!.GetValue<int>(); // 0 = MODEL, 1 = CLIP (LoraLoader)
                        var upstream = lora[slot == 0 ? "model" : "clip"];
                        if (upstream is not null) value["inputs"]![name] = upstream.DeepClone();
                    }
            api.Remove(id);
        }
        if (skipped.Count > 0) Log.Write($"Comfy: do zero — sem a imagem 1 e sem a LoRA de consistência ({skipped.Count}).");
    }

    private static async Task<JsonObject> Info(string type, CancellationToken token)
    {
        if (NodeInfo.TryGetValue(type, out var known)) return known;
        using var response = await Http.GetAsync($"object_info/{Uri.EscapeDataString(type)}", token);
        var info = JsonNode.Parse(await response.Content.ReadAsStringAsync(token))?[type]?.AsObject()
            ?? throw new InvalidOperationException($"O ComfyUI não conhece o nó \"{type}\" (falta instalar?)");
        NodeInfo[type] = info;
        return info;
    }

    // Entradas que aparecem como campo na página (número, texto, lista, liga/desliga), na ordem.
    private static IEnumerable<(string name, JsonNode? spec)> WidgetInputs(JsonObject info)
    {
        var input = info["input"]!.AsObject();
        var order = info["input_order"]?.AsObject();
        foreach (var section in new[] { "required", "optional" })
        {
            if (input[section] is not JsonObject defs) continue;
            var names = order?[section] is JsonArray o ? o.Select(n => n!.GetValue<string>()) : defs.Select(p => p.Key);
            foreach (var name in names)
            {
                var spec = defs[name];
                var kind = spec is JsonArray a ? a[0] : null;
                bool widget = kind is JsonArray || kind?.GetValueKind() == JsonValueKind.String &&
                    kind.GetValue<string>() is "INT" or "FLOAT" or "STRING" or "BOOLEAN" or "COMBO" or "COMFY_DYNAMICCOMBO_V3";
                if (widget) yield return (name, spec);
            }
        }
    }

    private static bool HasControl(string name, JsonNode? spec)
    {
        if (spec is JsonArray a && a.Count > 1 && a[1] is JsonObject options && options["control_after_generate"] is JsonValue v)
            return v.GetValueKind() != JsonValueKind.False;
        return name is "seed" or "noise_seed";
    }

    // {a|b|c}: sorteia uma opção, como a página do ComfyUI faz antes de enviar (a API recebe o texto já pronto).
    private static string Expand(string text)
    {
        var pattern = new System.Text.RegularExpressions.Regex(@"\{([^{}]*)\}");
        for (int i = 0; i < 20 && pattern.IsMatch(text); i++)
            text = pattern.Replace(text, m =>
            {
                var options = m.Groups[1].Value.Split('|');
                return options.Length < 2 ? m.Value.Trim('{', '}') : options[Random.Shared.Next(options.Length)];
            });
        return text;
    }

    private static string ShortError(string response)
    {
        try
        {
            var json = JsonNode.Parse(response);
            var first = json?["node_errors"]?.AsObject().Select(p => p.Value?["errors"]?[0]).FirstOrDefault(e => e is not null);
            string message = first?["details"]?.GetValue<string>() is { Length: > 0 } d ? $"{first["message"]}: {d}" : first?["message"]?.ToString()
                ?? json?["error"]?["message"]?.ToString() ?? response;
            return message.Split('\n')[0];
        }
        catch { return response.Length > 200 ? response[..200] : response; }
    }

    // Número de um campo do JSON (null = vazio ou não é número).
    private static int? Int(this JsonNode? node) => node is JsonValue v && v.TryGetValue<int>(out int i) ? i : null;

    private static string ErrorOf(JsonNode entry)
    {
        foreach (var m in entry["status"]?["messages"]?.AsArray() ?? [])
            if (m?[0]?.GetValue<string>() == "execution_error")
                return (m[1]?["exception_message"]?.GetValue<string>() ?? "?").Split('\n')[0];
        return "?";
    }
}
