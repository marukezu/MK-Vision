using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace MKVision.Desktop;

// Luz e cor da imagem (tecla Y): duas IAs pequenas que não redesenham nada — só mudam a cor e a luz de cada pixel
// (créditos e licenças em Models\tone\CREDITOS.txt).
//   1. Cor da luz (Deep White-Balance): roda numa versão reduzida (até 656 px); a mudança de cor que ela fez vira
//      uma fórmula (polinômio de 11 termos por canal) aplicada à imagem inteira, em resolução cheia.
//   2. Exposição (IAT, treinada para fotos escuras e estouradas, inclusive as duas coisas na mesma foto): roda numa
//      versão reduzida (até 1024 px) da imagem já com a cor certa; o ganho de luz de cada ponto (saída ÷ entrada)
//      volta ao tamanho cheio, então os detalhes continuam os da imagem. "exposure" (0–1) mistura com a só-cor.
// Pela placa (CUDA; sem ela, processador). Uma imagem de cada vez.
internal static class ToneAi
{
    private const string WbFile = "deep_wb_awb.onnx", ExposureFile = "iat_exposure.onnx";
    private const int WbSide = 656, ExposureSide = 1024, Multiple = 16;
    private static readonly string Folder = Path.Combine(AppContext.BaseDirectory, "Models", "tone");
    private static readonly object Lock = new();
    private static InferenceSession? _wb, _exposure;

    public static bool Available => File.Exists(Path.Combine(Folder, WbFile)) && File.Exists(Path.Combine(Folder, ExposureFile));

    // BGRA (opaco) → BGRA com a luz e a cor corrigidas, no mesmo tamanho.
    public static byte[] Apply(byte[] bgra, int w, int h, float exposure)
    {
        lock (Lock)
        {
            var rgb = ToRgb(bgra, w, h);
            // 1. Cor: a IA na reduzida; a fórmula que leva a reduzida até o resultado, na imagem inteira.
            var (small, sw, sh) = Resize(rgb, w, h, WbSide);
            var corrected = Run(ref _wb, WbFile, small, sw, sh);
            var fit = FitPolynomial(small, corrected);
            var wb = new float[rgb.Length];
            Parallel.For(0, h, y =>
            {
                Span<double> k = stackalloc double[11];
                for (int i = y * w * 3, end = i + w * 3; i < end; i += 3)
                {
                    Kernel(rgb[i], rgb[i + 1], rgb[i + 2], k);
                    for (int c = 0; c < 3; c++)
                    {
                        double v = 0;
                        for (int f = 0; f < 11; f++) v += k[f] * fit[f, c];
                        wb[i + c] = (float)Math.Clamp(v, 0, 1);
                    }
                }
            });
            if (exposure <= 0.001f) return ToBgra(wb, w, h);

            // 2. Exposição: ganho por ponto na reduzida (saída ÷ entrada), de volta ao tamanho cheio por interpolação.
            var (es, ew, eh) = Resize(wb, w, h, ExposureSide);
            var lit = Run(ref _exposure, ExposureFile, es, ew, eh);
            const float Eps = 0.02f;
            var gain = new float[es.Length];
            for (int i = 0; i < gain.Length; i++) gain[i] = Math.Clamp((lit[i] + Eps) / (es[i] + Eps), 0.2f, 5f);
            var result = new float[wb.Length];
            Parallel.For(0, h, y =>
            {
                double gy = Math.Clamp((y + 0.5) * eh / h - 0.5, 0, eh - 1);
                int y0 = (int)gy, y1 = Math.Min(y0 + 1, eh - 1);
                float fy = (float)(gy - y0);
                for (int x = 0; x < w; x++)
                {
                    double gx = Math.Clamp((x + 0.5) * ew / w - 0.5, 0, ew - 1);
                    int x0 = (int)gx, x1 = Math.Min(x0 + 1, ew - 1);
                    float fx = (float)(gx - x0);
                    int i = (y * w + x) * 3;
                    for (int c = 0; c < 3; c++)
                    {
                        float g00 = gain[(y0 * ew + x0) * 3 + c], g01 = gain[(y0 * ew + x1) * 3 + c];
                        float g10 = gain[(y1 * ew + x0) * 3 + c], g11 = gain[(y1 * ew + x1) * 3 + c];
                        float g = (g00 * (1 - fx) + g01 * fx) * (1 - fy) + (g10 * (1 - fx) + g11 * fx) * fy;
                        result[i + c] = Math.Clamp(wb[i + c] * (1 + exposure * (g - 1)), 0f, 1f);
                    }
                }
            });
            return ToBgra(result, w, h);
        }
    }

    // Libera a memória da placa (ex.: para o ComfyUI na tecla G). O próximo Y carrega de novo (segundos).
    public static bool Release()
    {
        lock (Lock)
        {
            if (_wb is null && _exposure is null) return false;
            _wb?.Dispose();
            _exposure?.Dispose();
            (_wb, _exposure) = (null, null);
        }
        Log.Write("Luz e cor: IAs descarregadas (memória da placa livre).");
        return true;
    }

    // RGB intercalado (0–1), tamanho w×h → planar 1×3×h×w para a IA → RGB intercalado do mesmo tamanho.
    private static float[] Run(ref InferenceSession? session, string file, float[] rgb, int w, int h)
    {
        session ??= Open(file);
        int plane = w * h;
        var input = new float[3 * plane];
        for (int p = 0; p < plane; p++)
            for (int c = 0; c < 3; c++) input[c * plane + p] = rgb[p * 3 + c];
        var tensor = new DenseTensor<float>(input, [1, 3, h, w]);
        using var results = session.Run([NamedOnnxValue.CreateFromTensor("input", tensor)]);
        var output = results[0].AsTensor<float>().ToArray();
        var rgbOut = new float[3 * plane];
        for (int p = 0; p < plane; p++)
            for (int c = 0; c < 3; c++) rgbOut[p * 3 + c] = output[c * plane + p];
        return rgbOut;
    }

    private static InferenceSession Open(string file)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        // Só erros no log do ONNX Runtime (o aviso de "partes da IA no processador" é normal: são as contas de tamanho).
        var options = new SessionOptions { GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL, LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_ERROR };
        try
        {
            Rife.LoadNvidiaLibraries();
            options.AppendExecutionProvider_CUDA(0);
        }
        catch (Exception error) { Log.Write($"Luz e cor: sem a placa ({error.Message.Split('\n')[0]}); pelo processador."); }
        var session = new InferenceSession(Path.Combine(Folder, file), options);
        Log.Write($"Luz e cor: {file} pronta ({clock.Elapsed.TotalSeconds:0.0} s).");
        return session;
    }

    // Termos do polinômio de cor: (r, g, b, rg, rb, gb, r², g², b², rgb, 1) — o mesmo do Deep White-Balance.
    private static void Kernel(double r, double g, double b, Span<double> k)
    {
        k[0] = r; k[1] = g; k[2] = b; k[3] = r * g; k[4] = r * b; k[5] = g * b;
        k[6] = r * r; k[7] = g * g; k[8] = b * b; k[9] = r * g * b; k[10] = 1;
    }

    // Mínimos quadrados: a fórmula (11×3) que leva "from" até "to" (mesmos pixels).
    private static double[,] FitPolynomial(float[] from, float[] to)
    {
        var ata = new double[11, 11];
        var atb = new double[11, 3];
        Span<double> k = stackalloc double[11];
        for (int i = 0; i < from.Length; i += 3)
        {
            Kernel(from[i], from[i + 1], from[i + 2], k);
            for (int a = 0; a < 11; a++)
            {
                for (int b = a; b < 11; b++) ata[a, b] += k[a] * k[b];
                for (int c = 0; c < 3; c++) atb[a, c] += k[a] * to[i + c];
            }
        }
        for (int a = 0; a < 11; a++)
        {
            for (int b = 0; b < a; b++) ata[a, b] = ata[b, a];
            ata[a, a] += 1e-6; // estabilidade (imagem quase de uma cor só)
        }
        // Eliminação de Gauss com pivô.
        for (int col = 0; col < 11; col++)
        {
            int pivot = col;
            for (int r = col + 1; r < 11; r++) if (Math.Abs(ata[r, col]) > Math.Abs(ata[pivot, col])) pivot = r;
            if (pivot != col)
            {
                for (int c = 0; c < 11; c++) (ata[col, c], ata[pivot, c]) = (ata[pivot, c], ata[col, c]);
                for (int c = 0; c < 3; c++) (atb[col, c], atb[pivot, c]) = (atb[pivot, c], atb[col, c]);
            }
            for (int r = 0; r < 11; r++)
            {
                if (r == col) continue;
                double f = ata[r, col] / ata[col, col];
                if (f == 0) continue;
                for (int c = col; c < 11; c++) ata[r, c] -= f * ata[col, c];
                for (int c = 0; c < 3; c++) atb[r, c] -= f * atb[col, c];
            }
        }
        var fit = new double[11, 3];
        for (int a = 0; a < 11; a++)
            for (int c = 0; c < 3; c++) fit[a, c] = atb[a, c] / ata[a, a];
        return fit;
    }

    // Reduz (média da área) para até "side" de lado, em múltiplos de 16 (o que as duas IAs pedem).
    private static (float[] rgb, int w, int h) Resize(float[] src, int w, int h, int side)
    {
        double scale = Math.Min(1.0, (double)side / Math.Max(w, h));
        int nw = Math.Max(Multiple, (int)Math.Round(w * scale / Multiple) * Multiple);
        int nh = Math.Max(Multiple, (int)Math.Round(h * scale / Multiple) * Multiple);
        var dst = new float[nw * nh * 3];
        Parallel.For(0, nh, y =>
        {
            int y0 = y * h / nh, y1 = Math.Max(y0 + 1, (y + 1) * h / nh);
            for (int x = 0; x < nw; x++)
            {
                int x0 = x * w / nw, x1 = Math.Max(x0 + 1, (x + 1) * w / nw);
                float r = 0, g = 0, b = 0;
                for (int sy = y0; sy < y1; sy++)
                    for (int sx = x0; sx < x1; sx++)
                    {
                        int i = (sy * w + sx) * 3;
                        r += src[i]; g += src[i + 1]; b += src[i + 2];
                    }
                float n = (y1 - y0) * (x1 - x0);
                int o = (y * nw + x) * 3;
                (dst[o], dst[o + 1], dst[o + 2]) = (r / n, g / n, b / n);
            }
        });
        return (dst, nw, nh);
    }

    private static float[] ToRgb(byte[] bgra, int w, int h)
    {
        var rgb = new float[w * h * 3];
        Parallel.For(0, h, y =>
        {
            for (int x = 0; x < w; x++)
            {
                int s = (y * w + x) * 4, d = (y * w + x) * 3;
                (rgb[d], rgb[d + 1], rgb[d + 2]) = (bgra[s + 2] / 255f, bgra[s + 1] / 255f, bgra[s] / 255f);
            }
        });
        return rgb;
    }

    private static byte[] ToBgra(float[] rgb, int w, int h)
    {
        var bgra = new byte[w * h * 4];
        Parallel.For(0, h, y =>
        {
            for (int x = 0; x < w; x++)
            {
                int s = (y * w + x) * 3, d = (y * w + x) * 4;
                bgra[d] = (byte)(rgb[s + 2] * 255f + 0.5f);
                bgra[d + 1] = (byte)(rgb[s + 1] * 255f + 0.5f);
                bgra[d + 2] = (byte)(rgb[s] * 255f + 0.5f);
                bgra[d + 3] = 255;
            }
        });
        return bgra;
    }

    // --tonetest <foto> <saída> [exposição 0–1]: aplica e salva (para comparar com a avaliação em Python).
    public static int Test(string input, string output, float exposure)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        var (pixels, w, h) = ImageFile.LoadPixels(input);
        for (int i = 3; i < pixels.Length; i += 4) pixels[i] = 255;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var first = Apply(pixels, w, h, exposure);
        double firstSeconds = clock.Elapsed.TotalSeconds;
        clock.Restart();
        var result = Apply(pixels, w, h, exposure);
        Console.WriteLine($"{w}x{h}: 1ª vez {firstSeconds:0.00} s (carrega as IAs), depois {clock.Elapsed.TotalSeconds:0.00} s");
        ImageFile.Save(output, result, w, h);
        return 0;
    }
}
