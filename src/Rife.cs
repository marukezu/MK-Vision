using System.Runtime.InteropServices;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace MKVision.Desktop;

public enum RifeEngine { TensorRT, Cuda }

// IA de interpolação de quadros (RIFE v4.26, formato "v2" da vs-mlrt) via ONNX Runtime + TensorRT (NVIDIA).
// Entrada: [1, 7, H, W] = quadro A (RGB), quadro B (RGB), momento t. Ao carregar, o modelo recebe duas
// saídas extras (mesma "cirurgia" da extensão): o fluxo óptico e a máscara de mistura.
//
// As bibliotecas da NVIDIA (CUDA, cuDNN, TensorRT) ficam em %LOCALAPPDATA%\MKVisionDesktop\nvidia.
// O TensorRT otimiza o modelo para a placa na primeira vez (alguns minutos) e guarda em trt-cache.
internal sealed class Rife : IDisposable
{
    public const string FlowOutput = "/Add_7_output_0";   // [1, 4, Hp, Wp]: fluxo para A (xy) e para B (zw), em pixels
    public const string MaskOutput = "/Sigmoid_output_0"; // [1, 1, Hp, Wp]: peso do quadro A

    private static readonly string AppData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MKVisionDesktop");
    public static readonly string NvidiaDir = Environment.GetEnvironmentVariable("MKV_NVIDIA_DIR") ?? Path.Combine(AppData, "nvidia"); // autoteste: outra pasta
    private static readonly string CacheDir = Path.Combine(AppData, "trt-cache");

    private readonly InferenceSession _session;
    public string Engine { get; }

    // DLLs da NVIDIA que o ONNX Runtime (CUDA/TensorRT) importa, em ordem de dependência.
    private static readonly string[] NvidiaLibraries =
    [
        "cudart64_12.dll", "cublasLt64_12.dll", "cublas64_12.dll", "cufft64_11.dll", "cudnn64_9.dll",
        "nvinfer_10.dll", "nvinfer_plugin_10.dll", "nvonnxparser_10.dll"
    ];
    private static bool _nvidiaLoaded;

    // Carrega as DLLs da NVIDIA pelo caminho completo, resolvendo as dependências na própria pasta delas.
    // Só o PATH não basta: dependendo de como o app é aberto, o Windows deixa de procurar no PATH
    // (aconteceu ao abrir pelo .bat: "cublas64_12.dll is missing"). Com elas já carregadas, o ONNX Runtime
    // as encontra pelo nome. As que elas mesmas carregam depois (cudnn_*, nvinfer_builder_resource) vêm
    // pela pasta adicionada (AddDllDirectory) e pelo PATH, de reserva.
    public static void LoadNvidiaLibraries()
    {
        lock (NvidiaLibraries)
        {
            if (_nvidiaLoaded) return;
            string path = Environment.GetEnvironmentVariable("PATH") ?? "";
            if (!path.Contains(NvidiaDir)) Environment.SetEnvironmentVariable("PATH", NvidiaDir + ";" + path);
            AddDllDirectory(NvidiaDir);
            foreach (string name in NvidiaLibraries)
            {
                string file = Path.Combine(NvidiaDir, name);
                if (!File.Exists(file)) throw new FileNotFoundException($"falta {name} em {NvidiaDir}");
                if (LoadLibraryExW(file, IntPtr.Zero, LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR | LOAD_LIBRARY_SEARCH_DEFAULT_DIRS) == IntPtr.Zero)
                {
                    int code = Marshal.GetLastWin32Error();
                    throw new InvalidOperationException($"não consegui carregar {name} (erro {code} do Windows: {new System.ComponentModel.Win32Exception(code).Message})");
                }
            }
            _nvidiaLoaded = true;
        }
    }

    private const uint LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR = 0x100, LOAD_LIBRARY_SEARCH_DEFAULT_DIRS = 0x1000;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadLibraryExW(string fileName, IntPtr file, uint flags);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr AddDllDirectory(string directory);

    // Pasta da otimização do TensorRT para este modelo. O TensorRT reconhece o motor pela estrutura do modelo, não
    // pelos pesos: o modelo treinado (mesma estrutura do original) usaria o motor do original. Cada outro modelo tem
    // a sua pasta, marcada pelo conteúdo do arquivo (treinou de novo = motor novo; as pastas antigas dele saem).
    // O original fica na pasta de sempre (não precisa otimizar de novo).
    private static string CacheFor(string modelPath, byte[] model)
    {
        string name = Path.GetFileNameWithoutExtension(modelPath);
        if (name == "rife_v4.26") return CacheDir;
        string mark = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(model))[..12].ToLowerInvariant();
        string folder = Path.Combine(CacheDir, $"{name}-{mark}");
        try
        {
            if (Directory.Exists(CacheDir))
                foreach (var old in Directory.GetDirectories(CacheDir, name + "-*"))
                    if (!string.Equals(old, folder, StringComparison.OrdinalIgnoreCase)) Directory.Delete(old, recursive: true);
        }
        catch { } // em uso por outra janela: fica para a próxima
        return folder;
    }

    public Rife(string modelPath, RifeEngine engine = RifeEngine.TensorRT)
    {
        LoadNvidiaLibraries();
        byte[] model = File.ReadAllBytes(modelPath);
        string cacheDir = CacheFor(modelPath, model);
        Directory.CreateDirectory(cacheDir);

        var options = new SessionOptions { GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL };
        if (engine == RifeEngine.TensorRT)
        {
            var trt = new OrtTensorRTProviderOptions();
            trt.UpdateOptions(new Dictionary<string, string>
            {
                ["device_id"] = "0",
                ["trt_fp16_enable"] = "1",              // núcleos de IA (Tensor Cores) em meia precisão
                ["trt_engine_cache_enable"] = "1",      // guarda a otimização para a placa
                ["trt_engine_cache_path"] = cacheDir,
                ["trt_timing_cache_enable"] = "1",
                ["trt_timing_cache_path"] = cacheDir,
                ["trt_builder_optimization_level"] = "3",
                // Tamanhos de entrada aceitos por um único motor otimizado (até 1080p; o modelo arredonda por dentro)
                ["trt_profile_min_shapes"] = "input:1x7x128x128",
                ["trt_profile_opt_shapes"] = "input:1x7x720x1280",
                ["trt_profile_max_shapes"] = "input:1x7x1088x1920"
            });
            options.AppendExecutionProvider_Tensorrt(trt);
        }
        options.AppendExecutionProvider_CUDA(0); // o que o TensorRT não suportar roda no CUDA
        Engine = engine.ToString();

        if (!modelPath.Contains("_fp16")) model = AddOutputs(model, [FlowOutput, MaskOutput]);
        _session = new InferenceSession(model, options);
    }

    public sealed record Result(float[] Flow, float[] Mask, float[]? Frame, int PaddedWidth, int PaddedHeight);

    // a, b: RGB planar (3·w·h, 0–1). Com withFrame, devolve também o quadro pronto do momento t.
    public Result Run(float[] a, float[] b, int width, int height, float t, bool withFrame = false)
    {
        int plane = width * height;
        var input = new float[7 * plane];
        Array.Copy(a, 0, input, 0, 3 * plane);
        Array.Copy(b, 0, input, 3 * plane, 3 * plane);
        Array.Fill(input, t, 6 * plane, plane);
        var tensor = new DenseTensor<float>(input, [1, 7, height, width]);
        string[] outputs = withFrame ? ["output", FlowOutput, MaskOutput] : [FlowOutput, MaskOutput];
        using var results = _session.Run([NamedOnnxValue.CreateFromTensor("input", tensor)], outputs);
        var flow = results.First(r => r.Name == FlowOutput).AsTensor<float>();
        var mask = results.First(r => r.Name == MaskOutput).AsTensor<float>();
        var frame = withFrame ? results.First(r => r.Name == "output").AsTensor<float>().ToArray() : null;
        return new Result(flow.ToArray(), mask.ToArray(), frame, flow.Dimensions[3], flow.Dimensions[2]);
    }

    // Para a câmera lenta, que roda várias análises por segundo: reaproveita a entrada e entrega o fluxo e a
    // máscara direto da memória do ONNX Runtime, sem cópias. Blocos grandes novos a cada análise (~30 MB)
    // faziam o .NET parar tudo para limpar a memória — travadinhas na tela. Uma análise por vez.
    public delegate void MotionSink(ReadOnlyMemory<float> flow, ReadOnlyMemory<float> mask, int paddedWidth, int paddedHeight);
    private float[] _input = [];

    private readonly object _runLock = new(); // o modo Edge e o player podem pedir ao mesmo tempo
    private bool _disposed; // descarregada (RifeHost.Release) — com _runLock

    public void RunMotion(float[] a, float[] b, int width, int height, float t, MotionSink sink)
    {
        lock (_runLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            int plane = width * height;
            if (_input.Length != 7 * plane) _input = new float[7 * plane];
            Array.Copy(a, 0, _input, 0, 3 * plane);
            Array.Copy(b, 0, _input, 3 * plane, 3 * plane);
            Array.Fill(_input, t, 6 * plane, plane);
            var tensor = new DenseTensor<float>(_input, [1, 7, height, width]);
            using var results = _session.Run([NamedOnnxValue.CreateFromTensor("input", tensor)], [FlowOutput, MaskOutput]);
            var flow = results.First(r => r.Name == FlowOutput).AsTensor<float>();
            var mask = results.First(r => r.Name == MaskOutput).AsTensor<float>();
            ReadOnlyMemory<float> Data(Tensor<float> tensor) => tensor is DenseTensor<float> dense ? dense.Buffer : tensor.ToArray();
            sink(Data(flow), Data(mask), flow.Dimensions[3], flow.Dimensions[2]);
        }
    }

    // Para medições.
    public DenseTensor<float> PrepareInput(float[] a, float[] b, int width, int height, float t)
    {
        int plane = width * height;
        var input = new float[7 * plane];
        Array.Copy(a, 0, input, 0, 3 * plane);
        Array.Copy(b, 0, input, 3 * plane, 3 * plane);
        Array.Fill(input, t, 6 * plane, plane);
        return new DenseTensor<float>(input, [1, 7, height, width]);
    }

    public void RunMaskOnly(float[] a, float[] b, int width, int height, float t)
    {
        using var results = _session.Run([NamedOnnxValue.CreateFromTensor("input", PrepareInput(a, b, width, height, t))], [MaskOutput]);
    }

    // Espera a análise em andamento terminar (a memória da placa só sai depois dela).
    public void Dispose()
    {
        lock (_runLock)
        {
            if (_disposed) return;
            _disposed = true;
            _session.Dispose();
        }
    }

    // ---------- Protobuf mínimo: acrescenta saídas ao grafo (igual ao smooth.js da extensão) ----------

    private static (ulong value, int pos) ReadVarint(byte[] bytes, int pos)
    {
        ulong value = 0;
        int shift = 0;
        byte b;
        do { b = bytes[pos++]; value |= (ulong)(b & 0x7f) << shift; shift += 7; } while ((b & 0x80) != 0);
        return (value, pos);
    }

    private static List<byte> Varint(ulong value)
    {
        var output = new List<byte>();
        do { byte b = (byte)(value & 0x7f); value >>= 7; if (value != 0) b |= 0x80; output.Add(b); } while (value != 0);
        return output;
    }

    private static List<byte> LengthField(int number, IReadOnlyCollection<byte> body)
    {
        var output = Varint((ulong)(number * 8 + 2));
        output.AddRange(Varint((ulong)body.Count));
        output.AddRange(body);
        return output;
    }

    // ValueInfoProto { name, type { tensor_type { elem_type = FLOAT } } } como GraphProto.output (campo 12).
    private static List<byte> OutputInfo(string name) =>
        LengthField(12, [.. LengthField(1, System.Text.Encoding.UTF8.GetBytes(name)), .. LengthField(2, LengthField(1, new byte[] { 8, 1 }))]);

    private static byte[] AddOutputs(byte[] model, string[] names)
    {
        var output = new List<byte>(model.Length + 256);
        int pos = 0;
        while (pos < model.Length)
        {
            int start = pos;
            (ulong key, pos) = ReadVarint(model, pos);
            int field = (int)(key >> 3), wire = (int)(key & 7);
            if (wire == 0)
            {
                (_, pos) = ReadVarint(model, pos);
                output.AddRange(model[start..pos]);
            }
            else if (wire == 2)
            {
                (ulong length, pos) = ReadVarint(model, pos);
                int end = pos + (int)length;
                if (field == 7) // ModelProto.graph
                {
                    var graph = new List<byte>(model[pos..end]);
                    foreach (string name in names) graph.AddRange(OutputInfo(name));
                    output.AddRange(LengthField(7, graph));
                }
                else
                {
                    output.AddRange(model[start..end]);
                }
                pos = end;
            }
            else
            {
                throw new InvalidDataException("ONNX inesperado");
            }
        }
        return [.. output];
    }
}
