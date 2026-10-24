using System.IO.Compression;
using Vortice.DXGI;

namespace MKVision.Desktop;

// O pacote de IA da NVIDIA (CUDA, cuBLAS, cuFFT, cuDNN e TensorRT): as bibliotecas que a IA de fotos (I), a câmera
// lenta com IA e a luz e cor (Y, na placa) usam. São ~4,4 GB, então não vêm no .zip do app: numa placa NVIDIA, o app
// baixa dos pacotes oficiais da NVIDIA (os mesmos do "pip install", ~2,9 GB compactados) e tira deles só as DLLs, para
// %LOCALAPPDATA%\MKVisionDesktop\nvidia (Rife.NvidiaDir). Versões fixas: as que o ONNX Runtime do app usa (conferidas
// bit a bit com as que funcionam: CUDA 12.8, cuDNN 9.27, TensorRT 10.9).
internal static class NvidiaPack
{
    // Pacote (wheel = zip) e as DLLs dele que o app usa, com o tamanho de cada uma (confere a cópia).
    private sealed record Package(string Name, string Url, (string entry, long size)[] Dlls);

    private static readonly Package[] Packages =
    [
        new("CUDA", "https://files.pythonhosted.org/packages/30/a5/a515b7600ad361ea14bfa13fb4d6687abf500adc270f19e89849c0590492/nvidia_cuda_runtime_cu12-12.8.90-py3-none-win_amd64.whl",
            [("nvidia/cuda_runtime/bin/cudart64_12.dll", 573952)]),
        new("cuBLAS", "https://files.pythonhosted.org/packages/74/65/d9db5b0754559f6ed279c4a6cf1192dbf581f7d01e5d3d2882f577936049/nvidia_cublas_cu12-12.8.5.5-py3-none-win_amd64.whl",
            [("nvidia/cublas/bin/cublas64_12.dll", 113716224), ("nvidia/cublas/bin/cublasLt64_12.dll", 674670592)]),
        new("cuFFT", "https://files.pythonhosted.org/packages/7d/ec/ce1629f1e478bb5ccd208986b5f9e0316a78538dd6ab1d0484f012f8e2a1/nvidia_cufft_cu12-11.3.3.83-py3-none-win_amd64.whl",
            [("nvidia/cufft/bin/cufft64_11.dll", 276121600)]),
        new("cuDNN", "https://files.pythonhosted.org/packages/aa/38/f856579877f7c1c5066e61182e7de7bc27bf35a78c8d1b0fa592e6985bc4/nvidia_cudnn_cu12-9.27.0.42-py3-none-win_amd64.whl",
        [
            ("nvidia/cudnn/bin/cudnn64_9.dll", 270448), ("nvidia/cudnn/bin/cudnn_adv64_9.dll", 270983792), ("nvidia/cudnn/bin/cudnn_cnn64_9.dll", 2997872),
            ("nvidia/cudnn/bin/cudnn_engines_precompiled64_9.dll", 528169072), ("nvidia/cudnn/bin/cudnn_engines_runtime_compiled64_9.dll", 45452912),
            ("nvidia/cudnn/bin/cudnn_engines_tensor_ir64_9.dll", 156272), ("nvidia/cudnn/bin/cudnn_ext64_9.dll", 130160),
            ("nvidia/cudnn/bin/cudnn_graph64_9.dll", 99919472), ("nvidia/cudnn/bin/cudnn_heuristic64_9.dll", 94795376), ("nvidia/cudnn/bin/cudnn_ops64_9.dll", 105686640)
        ]),
        new("TensorRT", "https://pypi.nvidia.com/tensorrt-cu12-libs/tensorrt_cu12_libs-10.9.0.34-py2.py3-none-win_amd64.whl",
        [
            ("tensorrt_libs/nvinfer_10.dll", 440983072), ("tensorrt_libs/nvinfer_plugin_10.dll", 51002400), ("tensorrt_libs/nvonnxparser_10.dll", 2993696),
            ("tensorrt_libs/nvinfer_builder_resource_10.dll", 1971797024)
        ])
    ];

    private static string FileOf(string entry) => Path.Combine(Rife.NvidiaDir, Path.GetFileName(entry));

    // Todas as DLLs no lugar, com o tamanho certo.
    public static bool Installed => Packages.All(p => p.Dlls.All(d => new FileInfo(FileOf(d.entry)) is { Exists: true } f && f.Length == d.size));

    // A placa de vídeo: é NVIDIA? (e o nome, para os avisos). Pelo DXGI, sem depender de nada da NVIDIA.
    public static (bool nvidia, string name) Gpu()
    {
        try
        {
            using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
            for (uint i = 0; factory.EnumAdapters1(i, out var adapter).Success; i++)
                using (adapter)
                {
                    var description = adapter.Description1;
                    if ((description.Flags & AdapterFlags.Software) != 0) continue;
                    if (description.VendorId == 0x10DE) return (true, description.Description);
                }
        }
        catch { }
        return (false, "");
    }

    private static int _downloading;
    public static bool Downloading => Volatile.Read(ref _downloading) == 1;

    // Baixa e instala o que falta. "progress": (baixado, total) em bytes. Um pacote por vez: o arquivo vai para uma
    // pasta temporária ao lado (o mesmo disco), as DLLs saem dele (".part" e depois o nome certo) e ele é apagado.
    public static async Task Download(Action<long, long> progress, CancellationToken token)
    {
        if (Interlocked.Exchange(ref _downloading, 1) == 1) throw new InvalidOperationException("O download já está em andamento");
        string temp = Path.Combine(Rife.NvidiaDir, ".baixando");
        try
        {
            Directory.CreateDirectory(Rife.NvidiaDir);
            Directory.CreateDirectory(temp);
            using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("MKVisionDesktop");
            var missing = Packages.Where(p => !p.Dlls.All(d => new FileInfo(FileOf(d.entry)) is { Exists: true } f && f.Length == d.size)).ToList();
            // Tamanho total (para a porcentagem): o cabeçalho de cada pacote.
            long total = 0, done = 0;
            foreach (var package in missing)
            {
                using var head = await http.SendAsync(new HttpRequestMessage(HttpMethod.Head, package.Url), token);
                head.EnsureSuccessStatusCode();
                total += head.Content.Headers.ContentLength ?? 0;
            }
            progress(0, total);
            foreach (var package in missing)
            {
                string wheel = Path.Combine(temp, Path.GetFileName(new Uri(package.Url).LocalPath));
                using (var response = await http.GetAsync(package.Url, HttpCompletionOption.ResponseHeadersRead, token))
                {
                    response.EnsureSuccessStatusCode();
                    await using var input = await response.Content.ReadAsStreamAsync(token);
                    await using var output = File.Create(wheel);
                    var buffer = new byte[1 << 20];
                    long last = 0;
                    for (int n; (n = await input.ReadAsync(buffer, token)) > 0;)
                    {
                        await output.WriteAsync(buffer.AsMemory(0, n), token);
                        done += n;
                        if (done - last > 8 << 20) { progress(done, total); last = done; }
                    }
                }
                progress(done, total);
                using (var zip = ZipFile.OpenRead(wheel))
                    foreach (var (entry, size) in package.Dlls)
                    {
                        var item = zip.GetEntry(entry) ?? throw new InvalidDataException($"o pacote {package.Name} não tem {Path.GetFileName(entry)}");
                        string target = FileOf(entry), part = target + ".part";
                        item.ExtractToFile(part, overwrite: true);
                        if (new FileInfo(part).Length != size) throw new InvalidDataException($"{Path.GetFileName(entry)} veio com o tamanho errado");
                        File.Move(part, target, overwrite: true);
                    }
                File.Delete(wheel);
                Log.Write($"IA da NVIDIA: {package.Name} instalado.");
            }
        }
        finally
        {
            try { Directory.Delete(temp, recursive: true); } catch { }
            Volatile.Write(ref _downloading, 0);
        }
    }

    // --nvidiapackdownload: baixa e instala o pacote em Rife.NvidiaDir (use MKV_NVIDIA_DIR para uma pasta de teste).
    public static int DownloadTest()
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        int lastPercent = -1;
        try
        {
            Download((done, total) =>
            {
                int percent = total > 0 ? (int)(done * 100 / total) : 0;
                if (percent / 10 != lastPercent / 10) Console.WriteLine($"{clock.Elapsed.TotalSeconds,5:0}s  {percent}% de {total / 1073741824.0:0.0} GB");
                lastPercent = percent;
            }, CancellationToken.None).GetAwaiter().GetResult();
        }
        catch (Exception error)
        {
            Console.WriteLine($"FALHOU: {error.Message}");
            return 1;
        }
        Console.WriteLine($"{(Installed ? "OK    " : "FALHOU")} instalado em {Rife.NvidiaDir} ({clock.Elapsed.TotalMinutes:0.0} min)");
        return Installed ? 0 : 1;
    }

    // --nvidiapacktest: confere os endereços (cabeçalho de cada pacote) e se as DLLs deste computador estão completas.
    public static int Test()
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        var (nvidia, name) = Gpu();
        Console.WriteLine($"placa: {(nvidia ? name : "não é NVIDIA")}; instalado: {Installed}");
        using var http = new HttpClient();
        http.DefaultRequestHeaders.UserAgent.ParseAdd("MKVisionDesktop");
        int failures = 0;
        long total = 0;
        foreach (var package in Packages)
        {
            using var head = http.Send(new HttpRequestMessage(HttpMethod.Head, package.Url));
            long size = head.Content.Headers.ContentLength ?? 0;
            total += size;
            Console.WriteLine($"{(head.IsSuccessStatusCode ? "OK    " : "FALHOU")} {package.Name}: {(int)head.StatusCode}, {size / 1048576.0:0} MB");
            if (!head.IsSuccessStatusCode) failures++;
        }
        Console.WriteLine($"total para baixar: {total / 1073741824.0:0.0} GB");
        Console.WriteLine(failures == 0 ? "tudo certo" : $"{failures} falha(s)");
        return failures == 0 ? 0 : 1;
    }
}
