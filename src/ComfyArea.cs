namespace MKVision.Desktop;

// Área de edição do ComfyUI (G) a partir das anotações (F3): o que está dentro dos contornos fechados (retângulo, ou
// um traço à mão que termina perto de onde começou) é a área que a IA pode mudar. A máscara exclui o próprio contorno
// (a faixa do traço) e tem a borda suave; com ela, a resposta só vale dentro da área — fora, volta a imagem original,
// sem o desenho.
internal static class ComfyArea
{
    private static readonly Dictionary<uint, string> ColorNames = new()
    {
        [0xFF3B30] = "red", [0xFFCC00] = "yellow", [0x34C759] = "green", [0x0A84FF] = "blue", [0xFFFFFF] = "white", [0x111111] = "black"
    };

    public static string ColorName(uint color) => ColorNames.GetValueOrDefault(color, "colored");

    // Um traço à mão é uma área se volta perto do começo: a distância entre as pontas é pequena perto do tamanho dele.
    public static bool IsClosed((double x, double y)[] points, double halfWidth)
    {
        if (points.Length < 8) return false;
        double minX = points.Min(p => p.x), maxX = points.Max(p => p.x), minY = points.Min(p => p.y), maxY = points.Max(p => p.y);
        double size = Math.Sqrt((maxX - minX) * (maxX - minX) + (maxY - minY) * (maxY - minY));
        if (size < halfWidth * 6) return false; // um ponto ou um rabisco pequeno
        var (ax, ay) = points[0];
        var (bx, by) = points[^1];
        double gap = Math.Sqrt((ax - bx) * (ax - bx) + (ay - by) * (ay - by));
        return gap <= Math.Max(size * 0.2, halfWidth * 4);
    }

    // Soma (o máximo) a área de um contorno fechado na máscara (w×h, 0–1): dentro dele, longe do traço, 1; a faixa do
    // traço e o lado de fora, 0; entre os dois, uma transição de "feather" pixels.
    public static void AddRegion(float[] mask, int w, int h, (double x, double y)[] polygon, double halfWidth, double feather)
    {
        int x0 = Math.Max(0, (int)Math.Floor(polygon.Min(p => p.x)) - 1), x1 = Math.Min(w - 1, (int)Math.Ceiling(polygon.Max(p => p.x)) + 1);
        int y0 = Math.Max(0, (int)Math.Floor(polygon.Min(p => p.y)) - 1), y1 = Math.Min(h - 1, (int)Math.Ceiling(polygon.Max(p => p.y)) + 1);
        if (x1 <= x0 || y1 <= y0) return;
        int bw = x1 - x0 + 1, bh = y1 - y0 + 1;
        // Dentro do contorno (regra par-ímpar, pelo centro de cada pixel).
        var inside = new bool[bw * bh];
        var crossings = new List<double>();
        for (int y = 0; y < bh; y++)
        {
            double cy = y0 + y + 0.5;
            crossings.Clear();
            for (int i = 0; i < polygon.Length; i++)
            {
                var (ax, ay) = polygon[i];
                var (bx, by) = polygon[(i + 1) % polygon.Length];
                if ((ay <= cy && by > cy) || (by <= cy && ay > cy)) crossings.Add(ax + (cy - ay) / (by - ay) * (bx - ax));
            }
            crossings.Sort();
            for (int k = 0; k + 1 < crossings.Count; k += 2)
            {
                int from = Math.Max(0, (int)Math.Ceiling(crossings[k] - 0.5) - x0), to = Math.Min(bw - 1, (int)Math.Floor(crossings[k + 1] - 0.5) - x0);
                for (int x = from; x <= to; x++) inside[y * bw + x] = true;
            }
        }
        // Distância até a borda (chanfro 3-4: em terços de pixel), só dentro.
        const int Far = int.MaxValue / 4;
        var d = new int[bw * bh];
        for (int i = 0; i < d.Length; i++) d[i] = inside[i] ? Far : 0;
        int At(int x, int y) => x < 0 || y < 0 || x >= bw || y >= bh ? 0 : d[y * bw + x];
        for (int y = 0; y < bh; y++)
        for (int x = 0; x < bw; x++)
            if (d[y * bw + x] != 0)
                d[y * bw + x] = Math.Min(d[y * bw + x], Math.Min(Math.Min(At(x - 1, y) + 3, At(x, y - 1) + 3), Math.Min(At(x - 1, y - 1) + 4, At(x + 1, y - 1) + 4)));
        for (int y = bh - 1; y >= 0; y--)
        for (int x = bw - 1; x >= 0; x--)
            if (d[y * bw + x] != 0)
                d[y * bw + x] = Math.Min(d[y * bw + x], Math.Min(Math.Min(At(x + 1, y) + 3, At(x, y + 1) + 3), Math.Min(At(x + 1, y + 1) + 4, At(x - 1, y + 1) + 4)));
        for (int y = 0; y < bh; y++)
        for (int x = 0; x < bw; x++)
        {
            double distance = d[y * bw + x] / 3.0;
            // O contorno inteiro fica de fora, com folga (a IA costuma deixar um rastro mais largo que o traço).
            float a = (float)Math.Clamp((distance - halfWidth * 2 - 2) / feather, 0, 1);
            int i = (y0 + y) * w + x0 + x;
            if (a > mask[i]) mask[i] = a;
        }
    }

    // A resposta (BGRA, rw×rh) só dentro da área: fora, a imagem original limpa (cw×ch; a máscara no mesmo tamanho). Os
    // tamanhos podem ser diferentes (o ComfyUI arredonda): a original e a máscara se ajustam à resposta.
    public static byte[] KeepOutside(byte[] result, int rw, int rh, byte[] clean, int cw, int ch, float[] mask)
    {
        var output = new byte[result.Length];
        for (int y = 0; y < rh; y++)
        {
            double sy = Math.Clamp((y + 0.5) * ch / rh - 0.5, 0, ch - 1);
            int y0 = (int)sy, y1 = Math.Min(y0 + 1, ch - 1);
            double fy = sy - y0;
            for (int x = 0; x < rw; x++)
            {
                double sx = Math.Clamp((x + 0.5) * cw / rw - 0.5, 0, cw - 1);
                int x0 = (int)sx, x1 = Math.Min(x0 + 1, cw - 1);
                double fx = sx - x0;
                double Bilinear(Func<int, double> at) =>
                    (at(y0 * cw + x0) * (1 - fx) + at(y0 * cw + x1) * fx) * (1 - fy) + (at(y1 * cw + x0) * (1 - fx) + at(y1 * cw + x1) * fx) * fy;
                double a = Bilinear(i => mask[i]);
                int o = (y * rw + x) * 4;
                for (int c = 0; c < 3; c++)
                {
                    double original = Bilinear(i => clean[i * 4 + c]);
                    output[o + c] = (byte)Math.Clamp(result[o + c] * a + original * (1 - a) + 0.5, 0, 255);
                }
                output[o + 3] = 255;
            }
        }
        return output;
    }
}
