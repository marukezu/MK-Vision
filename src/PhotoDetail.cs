namespace MKVision.Desktop;

// Realce de textura na foto já ampliada pela IA (Ajustes → Fotos): reforça o contraste dos detalhes finos — linhas da
// pele (digitais da sola e dos dedos), rugas, poros — que no óculos de VR somem por serem de 1–2 pixels. Não inventa
// nada: realça o que a IA trouxe. Nas bordas fortes (contorno contra o fundo) o realce diminui, para não criar halo.
// "Grão" devolve um granulado fino nos meios-tons, contra o efeito "plástico" das IAs. Roda na CPU, em paralelo.
// "skin": realce e grão cheios onde a cor é de pele e 40% no resto (fundo, asfalto, grama, tecido), com transição
// suave — a pele ganha textura sem deixar o fundo "crocante".
internal static class PhotoDetail
{
    // texture e grain de 0 a 1. Devolve um novo BGRA (ou o mesmo, se os dois forem 0).
    public static byte[] Apply(byte[] src, int w, int h, float texture, float grain, bool skin = false)
    {
        if (texture <= 0 && grain <= 0) return src;
        int n = w * h;
        var luma = new float[n];
        float[]? skinMask = skin ? new float[n] : null;
        Parallel.For(0, h, y =>
        {
            for (int x = 0, i = y * w; x < w; x++, i++)
            {
                float b = src[i * 4], g = src[i * 4 + 1], r = src[i * 4 + 2];
                luma[i] = r * 0.2126f + g * 0.7152f + b * 0.0722f;
                if (skinMask is not null) skinMask[i] = SkinLikeness(r, g, b);
            }
        });
        // A máscara de pele é suavizada (~15 px na 4x): sem bordas duras entre a parte realçada e a outra.
        if (skinMask is not null) skinMask = Blur(Blur(skinMask, w, h, 7), w, h, 7);
        // Duas faixas de detalhe, na resolução 4x: a média (~2–6 px: as linhas da pele, que continuam visíveis depois de
        // reduzir para a tela ou para o óculos) pesa mais; a fina (~1 px, onde fica o "chuvisco" da IA) pesa menos.
        // O desfoque largo também dá as bordas fortes.
        var fine = Blur(Blur(luma, w, h, 1), w, h, 1);
        var wide = Blur(Blur(fine, w, h, 3), w, h, 3);
        // Grão do tamanho de ~1 pixel da tela (células de 4 px na 4x, suavizadas): pixel a pixel na 4x ele sumia ao
        // reduzir para a tela ou o óculos (cada 4x4 vira um).
        float gain = texture * 2.5f, grainAmount = grain * 56f; // 100: ~5 níveis de variação visível na tela
        const float Edge = 9f; // diferença (em níveis, a 4 px) a partir da qual o realce cai pela metade
        var dst = new byte[src.Length];
        Parallel.For(0, h, y =>
        {
            int yu = Math.Max(0, y - 4), yd = Math.Min(h - 1, y + 4);
            for (int x = 0; x < w; x++)
            {
                int i = y * w + x, xl = Math.Max(0, x - 4), xr = Math.Min(w - 1, x + 4);
                float gx = wide[y * w + xr] - wide[y * w + xl], gy = wide[yd * w + x] - wide[yu * w + x];
                float edge = MathF.Sqrt(gx * gx + gy * gy) / Edge;
                float detail = 0.35f * (luma[i] - fine[i]) + (fine[i] - wide[i]);
                float region = skinMask is null ? 1f : 0.4f + 0.6f * skinMask[i];
                float delta = gain * region * detail / (1f + edge * edge);
                delta = Math.Clamp(delta, -48f, 48f);
                if (grainAmount > 0)
                {
                    float l = luma[i] / 255f, mid = 4f * l * (1f - l); // mais nos meios-tons, nada no preto e no branco
                    delta += (Grain(x, y) * 0.75f + (Hash(x, y) - 0.5f) * 0.5f) * grainAmount * mid * region;
                }
                int o = i * 4;
                dst[o] = Byte(src[o] + delta);
                dst[o + 1] = Byte(src[o + 1] + delta);
                dst[o + 2] = Byte(src[o + 2] + delta);
                dst[o + 3] = src[o + 3];
            }
        });
        return dst;
    }

    // Quanto a cor parece pele (0–1), pela crominância (YCbCr). A faixa clássica (Cr 133–173) cortava a sola rosada
    // (Cr ~172) e incluía cinzas quentes como asfalto (Cr 133–142): aqui Cr sobe de 136 a 148 e vai até 180–190 (pele
    // clara, rosada e escura ficam; cinza, céu e grama saem), Cb de 80 a 124, nem preto nem estourado.
    private static float SkinLikeness(float r, float g, float b)
    {
        float y = 0.299f * r + 0.587f * g + 0.114f * b;
        float cb = 128f - 0.168736f * r - 0.331264f * g + 0.5f * b;
        float cr = 128f + 0.5f * r - 0.418688f * g - 0.081312f * b;
        static float Band(float v, float zero, float full, float fullEnd, float end) =>
            Math.Clamp((v - zero) / (full - zero), 0f, 1f) * Math.Clamp((end - v) / (end - fullEnd), 0f, 1f);
        return Band(cr, 136f, 148f, 180f, 190f) * Band(cb, 70f, 80f, 124f, 134f) * Math.Clamp((y - 35f) / 25f, 0f, 1f) * Math.Clamp((250f - y) / 15f, 0f, 1f);
    }

    private static byte Byte(float v) => (byte)Math.Clamp((int)(v + 0.5f), 0, 255);

    // Ruído de valor em células de 4 px, interpolado (-0,5…0,5, com uma segunda escala de 2 px por cima).
    private static float Grain(int x, int y)
    {
        static float Cell(int x, int y, int size)
        {
            int cx = x / size, cy = y / size;
            float fx = (x % size + 0.5f) / size, fy = (y % size + 0.5f) / size;
            fx = fx * fx * (3 - 2 * fx);
            fy = fy * fy * (3 - 2 * fy);
            float a = Hash(cx, cy), b = Hash(cx + 1, cy), c = Hash(cx, cy + 1), d = Hash(cx + 1, cy + 1);
            return a + (b - a) * fx + (c - a) * fy + (a - b - c + d) * fx * fy - 0.5f;
        }
        return Cell(x, y, 4) * 0.8f + Cell(x + 31337, y + 4242, 2) * 0.4f;
    }

    private static float Hash(int x, int y)
    {
        uint h = (uint)x * 374761393u + (uint)y * 668265263u;
        h = (h ^ (h >> 13)) * 1274126177u;
        return ((h ^ (h >> 16)) & 0xFFFFFF) / 16777216f;
    }

    // Média de uma caixa de (2r+1)x(2r+1), em duas passadas (linhas, colunas), com somas correntes.
    private static float[] Blur(float[] src, int w, int h, int r)
    {
        var tmp = new float[src.Length];
        var dst = new float[src.Length];
        float norm = 1f / (2 * r + 1);
        Parallel.For(0, h, y =>
        {
            int row = y * w;
            float sum = 0;
            for (int k = -r; k <= r; k++) sum += src[row + Math.Clamp(k, 0, w - 1)];
            for (int x = 0; x < w; x++)
            {
                tmp[row + x] = sum * norm;
                sum += src[row + Math.Min(x + r + 1, w - 1)] - src[row + Math.Max(x - r, 0)];
            }
        });
        Parallel.For(0, (w + 63) / 64, block =>
        {
            int x0 = block * 64, x1 = Math.Min(w, x0 + 64);
            var sums = new float[x1 - x0];
            for (int x = x0; x < x1; x++)
                for (int k = -r; k <= r; k++) sums[x - x0] += tmp[Math.Clamp(k, 0, h - 1) * w + x];
            for (int y = 0; y < h; y++)
            {
                int add = Math.Min(y + r + 1, h - 1) * w, sub = Math.Max(y - r, 0) * w, row = y * w;
                for (int x = x0; x < x1; x++)
                {
                    dst[row + x] = sums[x - x0] * norm;
                    sums[x - x0] += tmp[add + x] - tmp[sub + x];
                }
            }
        });
        return dst;
    }
}
