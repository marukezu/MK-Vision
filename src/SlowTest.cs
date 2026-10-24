using System.Diagnostics;
using System.Runtime.InteropServices;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace MKVision.Desktop;

// "--slowtest": câmera lenta do app de ponta a ponta (captura → resolução original → IA TensorRT → quadro
// intermediário). Bloco xadrez andando 30 px por quadro real sobre fundo com ruído; um quadro real a cada
// 250 ms (como um vídeo de 24 fps em ~0.15x). Mede onde o bloco aparece nos intermediários.
internal static class SlowTest
{
    private const int W = 1280, H = 720, Block = 160, Step = 30, X0 = 200, Y0 = 280;

    public static int Run()
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        using var gpu = new Gpu();
        using var pipeline = new Pipeline(gpu);
        using var slow = new SlowMotion(gpu, new object());
        var clock = Stopwatch.StartNew();
        while (!slow.Ready && clock.Elapsed.TotalSeconds < 240) Thread.Sleep(100);
        if (!slow.Ready) { Console.WriteLine("IA não carregou."); return 1; }
        Console.WriteLine($"IA pronta em {clock.Elapsed.TotalSeconds:0.0} s");

        var view = new VideoView(new Box(0, 0, 0, W, H, 1), W, H, 0, 0, 1, 1);
        using var staging = gpu.Device.CreateTexture2D(new Texture2DDescription(Format.R16G16B16A16_Float, W, H, 1, 1,
            BindFlags.None, ResourceUsage.Staging, CpuAccessFlags.Read));
        const double span = 0.25;
        int frames = 12, bad = 0;
        double drawMs = 0; int draws = 0;
        var times = new List<double>();             // hora em que cada quadro real entrou
        var samples = new List<(double time, double left, double width)>();
        for (int k = 0; k < frames; k++)
        {
            using (var captured = MakeFrame(gpu, k))
            {
                double t = SlowMotion.Now;
                if (!slow.Feed(() => pipeline.Extract(captured, view, out _), t)) Console.WriteLine($"quadro {k}: não detectado como novo!");
                times.Add(t);
            }
            var arrived = Stopwatch.StartNew();
            // Quadro igual chegando de novo (o navegador recompõe): não pode contar como novo.
            using (var again = MakeFrame(gpu, k))
                if (slow.Feed(() => pipeline.Extract(again, view, out _), SlowMotion.Now)) Console.WriteLine($"quadro {k}: repetição contada como nova!");

            var line = new List<string>();
            while (arrived.Elapsed.TotalSeconds < span)
            {
                var drawClock = Stopwatch.StartNew();
                double now = SlowMotion.Now;
                var result = slow.Interpolate();
                gpu.Context.CopyResource(staging, result!.Texture!);
                var (left, width) = Locate(gpu, staging);
                drawMs += drawClock.Elapsed.TotalMilliseconds; draws++;
                if (k >= 5) samples.Add((now, left, width));
                line.Add($"{left:0}/{width:0}");
                Thread.Sleep(30);
            }
            Console.WriteLine($"quadro {k}: borda/largura do bloco = {string.Join(" ", line)}");
            Console.WriteLine($"   {slow.Info}");
        }

        // Esperado: velocidade constante (o bloco anda Step px por quadro real). Ajusta uma reta posição × tempo
        // nas amostras depois do aquecimento: desvio da reta = acelera/freia; largura fora = fantasma.
        double meanSpan = (times[^1] - times[1]) / (times.Count - 2);
        int n = samples.Count;
        double mt = samples.Average(x => x.time), ml = samples.Average(x => x.left);
        double slope = samples.Sum(x => (x.time - mt) * (x.left - ml)) / samples.Sum(x => (x.time - mt) * (x.time - mt));
        double worst = 0, lastLeft = 0;
        foreach (var (time, left, width) in samples)
        {
            double error = Math.Abs(left - (ml + slope * (time - mt)));
            worst = Math.Max(worst, error);
            if (error > 6 || Math.Abs(width - Block) > 8 || left < lastLeft - 1) bad++;
            lastLeft = left;
        }
        double expectedSlope = Step / meanSpan;
        if (Math.Abs(slope / expectedSlope - 1) > 0.05) bad++;
        Console.WriteLine($"\nDesenho do intermediário + leitura: {drawMs / draws:0.0} ms | velocidade {slope:0} px/s (esperado {expectedSlope:0}) | maior desvio da reta: {worst:0.0} px | amostras fora: {bad} de {n}");
        return bad == 0 ? 0 : 2;
    }

    private static ID3D11Texture2D MakeFrame(Gpu gpu, int k)
    {
        var pixels = new byte[W * H * 4];
        int bx = X0 + Step * k;
        for (int y = 0; y < H; y++)
        for (int x = 0; x < W; x++)
        {
            int i = (y * W + x) * 4;
            byte v;
            if (x >= bx && x < bx + Block && y >= Y0 && y < Y0 + Block)
                v = (byte)((((x - bx) / 20 + (y - Y0) / 20) & 1) == 0 ? 235 : 170);
            else
            {
                uint hsh = (uint)(x * 73856093) ^ (uint)(y * 19349663);
                hsh = (hsh ^ (hsh >> 13)) * 0x5bd1e995;
                v = (byte)(50 + ((hsh >> 24) & 63)); // fundo parado com textura (50–113)
            }
            pixels[i] = pixels[i + 1] = pixels[i + 2] = v;
            pixels[i + 3] = 255;
        }
        var handle = GCHandle.Alloc(pixels, GCHandleType.Pinned);
        try
        {
            return gpu.Device.CreateTexture2D(new Texture2DDescription(Format.B8G8R8A8_UNorm, W, H, 1, 1, BindFlags.ShaderResource),
                [new SubresourceData(handle.AddrOfPinnedObject(), W * 4)]);
        }
        finally { handle.Free(); }
    }

    // Média de cada coluna nas linhas do bloco; o bloco é o trecho acima de 0.55.
    private static (double left, double width) Locate(Gpu gpu, ID3D11Texture2D staging)
    {
        var map = gpu.Context.Map(staging, 0, MapMode.Read);
        var columns = new double[W];
        for (int y = Y0 + 10; y < Y0 + Block - 10; y++)
        {
            nint row = map.DataPointer + (nint)(y * map.RowPitch);
            for (int x = 0; x < W; x++) columns[x] += (float)BitConverter.UInt16BitsToHalf((ushort)Marshal.ReadInt16(row, x * 8));
        }
        gpu.Context.Unmap(staging, 0);
        int n = Block - 20, first = -1, last = -1;
        for (int x = 0; x < W; x++)
            if (columns[x] / n > 0.55) { if (first < 0) first = x; last = x; }
        return (first, last - first + 1);
    }
}
