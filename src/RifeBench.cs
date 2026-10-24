using System.Diagnostics;

namespace MKVision.Desktop;

// "--rifebench": mede a IA de interpolação no app (DirectML) e confere se o movimento sai alinhado:
// bloco texturizado andando 24 px sobre fundo parado; compara o quadro do meio com o resultado exato.
internal static class RifeBench
{
    public static int Run()
    {
        foreach (var engine in new[] { RifeEngine.Cuda, RifeEngine.TensorRT }) Bench("rife_v4.26.onnx", engine);
        return 0;
    }

    private static void Bench(string file, RifeEngine engine)
    {
        string model = Path.Combine(AppContext.BaseDirectory, "Models", "rife", file);
        var clock = Stopwatch.StartNew();
        using var rife = new Rife(model, engine);
        Console.WriteLine($"\n== {file} — {rife.Engine} — carregado em {clock.ElapsedMilliseconds} ms (1ª vez do TensorRT inclui a otimização)");

        foreach (var (w, h) in new[] { (1280, 720), (1920, 1080) })
        {
            var (a, b, truth) = Scene(w, h);
            rife.Run(a, b, w, h, 0.5f); // aquece (a 1ª execução compila os shaders)
            const int runs = 8;
            clock.Restart();
            Rife.Result? result = null;
            for (int i = 0; i < runs; i++) result = rife.Run(a, b, w, h, 0.5f, withFrame: i == runs - 1);
            double ms = clock.Elapsed.TotalMilliseconds / runs;

            // Bordas do fluxo horizontal na coluna do meio do bloco (esperado: topo 200·s, base 360·s).
            float scale = h / 540f;
            int col = (int)(380 * scale), pw = result!.PaddedWidth, plane = pw * result.PaddedHeight;
            int top = -1, bottom = -1;
            for (int y = 0; y < result.PaddedHeight; y++)
            {
                float f = result.Flow[2 * plane + y * pw + col];
                if (top < 0 && f > 6 * scale) top = y;
                if (top >= 0 && bottom < 0 && f < 6 * scale && y > top + 5) bottom = y;
            }
            // Qualidade do quadro do meio pronto da IA contra o exato (só canal R, região do bloco).
            double error = 0; long n = 0;
            for (int y = (int)(150 * scale); y < (int)(410 * scale); y++)
            for (int x = (int)(250 * scale); x < (int)(560 * scale); x++)
            {
                double d = result.Frame![y * w + x] - truth[y * w + x];
                error += d * d; n++;
            }
            double psnr = 10 * Math.Log10(1 / (error / n));
            Console.WriteLine($"{w}x{h}: {ms:0.0} ms por análise | bordas do movimento {top}–{bottom} (esperado {(int)(200 * scale)}–{(int)(360 * scale)}) | quadro do meio {psnr:0.0} dB");

            // Quanto é cálculo e quanto é cópia: mesma entrada já pronta, pedindo só a máscara (saída pequena).
            clock.Restart();
            for (int i = 0; i < runs; i++) rife.RunMaskOnly(a, b, w, h, 0.5f);
            Console.WriteLine($"   só a máscara (menos cópia de saída): {clock.Elapsed.TotalMilliseconds / runs:0.0} ms");
            clock.Restart();
            for (int i = 0; i < runs; i++) rife.PrepareInput(a, b, w, h, 0.5f);
            Console.WriteLine($"   só montar a entrada na CPU: {clock.Elapsed.TotalMilliseconds / runs:0.0} ms");
        }
    }

    // Fundo com ruído parado + bloco texturizado 160x160 (em 540p) andando 24 px; escala para w×h.
    private static (float[] a, float[] b, float[] truth) Scene(int w, int h)
    {
        float s = h / 540f;
        static double Rnd(int i, int j) { double v = Math.Sin(i * 127.1 + j * 311.7) * 43758.5453; return v - Math.Floor(v); }
        static double Noise(double x, double y, int k)
        {
            int xi = (int)Math.Floor(x), yi = (int)Math.Floor(y);
            double fx = x - xi, fy = y - yi, u = fx * fx * (3 - 2 * fx), v = fy * fy * (3 - 2 * fy);
            return (Rnd(xi + k, yi) * (1 - u) + Rnd(xi + 1 + k, yi) * u) * (1 - v) + (Rnd(xi + k, yi + 1) * (1 - u) + Rnd(xi + 1 + k, yi + 1) * u) * v;
        }
        double Scene(double x, double y, double t)
        {
            double ox = (300 + 24 * t) * s;
            return x >= ox && x < ox + 160 * s && y >= 200 * s && y < 360 * s
                ? 0.5 + 0.45 * Noise((x - ox) / (5 * s), (y - 200 * s) / (5 * s), 50)
                : 0.3 + 0.4 * Noise(x / (9 * s), y / (9 * s), 0);
        }
        int plane = w * h;
        float[] a = new float[3 * plane], b = new float[3 * plane], truth = new float[plane];
        Parallel.For(0, h, y =>
        {
            for (int x = 0; x < w; x++)
            {
                int p = y * w + x;
                float va = (float)Scene(x, y, 0), vb = (float)Scene(x, y, 1);
                a[p] = a[plane + p] = a[2 * plane + p] = va;
                b[p] = b[plane + p] = b[2 * plane + p] = vb;
                truth[p] = (float)Scene(x, y, 0.5);
            }
        });
        return (a, b, truth);
    }
}
