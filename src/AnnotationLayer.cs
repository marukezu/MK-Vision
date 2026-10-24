using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace MKVision.Desktop;

// Um traço das anotações (F3): pincel (pontos em sequência) ou retângulo (os 4 cantos, fechado). Os pontos ficam na
// imagem — fração da largura e da altura do quadro original, antes do giro e do espelho — e a espessura em fração da
// largura dela: o traço acompanha zoom, movimento, giro e espelho, e vai para a imagem salva no lugar certo.
internal sealed class AnnoStroke
{
    public uint Color;     // 0xRRGGBB
    public float Width;    // espessura / largura da imagem
    public bool Rect;
    public List<(float u, float v)> Points = [];
}

// Camada das anotações por cima da imagem do player: uma textura do tamanho da janela, refeita só na região que
// mudou (o traço sendo desenhado) — a janela inteira só quando a imagem muda de lugar (zoom, giro, janela).
// Thread da tela.
internal sealed class AnnotationLayer : IDisposable
{
    // Onde a imagem está na tela: tamanho do quadro original, giro (cos/sen), espelho, a "caixa" girada e o retângulo
    // dela na tela (com o zoom e o movimento). Mesma conta do Rotate.hlsl.
    public readonly record struct Map(int Fw, int Fh, double Cos, double Sin, int FlipX, int FlipY, double Bw, double Bh, double Ix, double Iy, double Iw, double Ih)
    {
        public double Scale => Iw / Bw; // pixels da tela por pixel da imagem

        public (double x, double y) ToScreen(double u, double v)
        {
            double qx = (u - 0.5) * Fw, qy = (v - 0.5) * Fh;
            double px = (Cos * qx - Sin * qy) * FlipX, py = (Sin * qx + Cos * qy) * FlipY;
            return (Ix + (px / Bw + 0.5) * Iw, Iy + (py / Bh + 0.5) * Ih);
        }

        public (double u, double v) ToImage(double x, double y)
        {
            double px = ((x - Ix) / Iw - 0.5) * Bw * FlipX, py = ((y - Iy) / Ih - 0.5) * Bh * FlipY;
            double qx = Cos * px + Sin * py, qy = -Sin * px + Cos * py;
            return (qx / Fw + 0.5, qy / Fh + 0.5);
        }
    }

    // Um traço como a camada desenha (cópia: o da tela muda enquanto você desenha).
    public readonly record struct Shape(uint Color, float Width, bool Rect, (float u, float v)[] Points);

    private byte[] _pixels = []; // RGBA pré-multiplicado, do tamanho da janela
    private int _w, _h;
    private ID3D11Texture2D? _texture;
    private ID3D11ShaderResourceView? _view;
    private Map? _map;
    private (int x0, int y0, int x1, int y1) _clip;

    // Refaz a região que mudou (toda, se a imagem mudou de lugar ou "full") e devolve a textura. "dirty" = região da
    // imagem (u0, v0, u1, v1) mais a espessura do traço que mudou.
    // "clip": onde a imagem aparece na janela (com corte ou zoom, os traços fora dela não aparecem).
    public ID3D11ShaderResourceView Render(Gpu gpu, int width, int height, Map map, IReadOnlyList<Shape> shapes, bool full, (double u0, double v0, double u1, double v1, float width)? dirty,
        (int x0, int y0, int x1, int y1) clip)
    {
        if (_texture is null || _w != width || _h != height)
        {
            _view?.Dispose();
            _texture?.Dispose();
            (_w, _h) = (width, height);
            _pixels = new byte[width * height * 4];
            _texture = gpu.Device.CreateTexture2D(new Texture2DDescription(Format.R8G8B8A8_UNorm, (uint)width, (uint)height, 1, 1, BindFlags.ShaderResource));
            _view = gpu.Device.CreateShaderResourceView(_texture);
            full = true;
        }
        if (_map != map || _clip != clip) full = true;
        (_map, _clip) = (map, clip);
        int x0 = 0, y0 = 0, x1 = width, y1 = height;
        if (!full)
        {
            if (dirty is not { } d) return _view!;
            // A região da imagem vira um retângulo na tela (os 4 cantos, com o giro), com a espessura de folga.
            double pad = d.width * map.Fw * map.Scale / 2 + 3;
            var corners = new[] { map.ToScreen(d.u0, d.v0), map.ToScreen(d.u1, d.v0), map.ToScreen(d.u0, d.v1), map.ToScreen(d.u1, d.v1) };
            x0 = Math.Max(0, (int)Math.Floor(corners.Min(c => c.x) - pad));
            y0 = Math.Max(0, (int)Math.Floor(corners.Min(c => c.y) - pad));
            x1 = Math.Min(width, (int)Math.Ceiling(corners.Max(c => c.x) + pad));
            y1 = Math.Min(height, (int)Math.Ceiling(corners.Max(c => c.y) + pad));
            if (x1 <= x0 || y1 <= y0) return _view!;
        }
        for (int y = y0; y < y1; y++) Array.Clear(_pixels, (y * width + x0) * 4, (x1 - x0) * 4);
        foreach (var shape in shapes)
        {
            var points = shape.Points.Select(p => map.ToScreen(p.u, p.v)).ToArray();
            var region = (Math.Max(x0, clip.x0), Math.Max(y0, clip.y0), Math.Min(x1, clip.x1), Math.Min(y1, clip.y1));
            if (region.Item3 > region.Item1 && region.Item4 > region.Item2)
                Draw(_pixels, width, height, false, region, shape.Color, shape.Width * map.Fw * map.Scale, points, shape.Rect);
        }
        unsafe
        {
            fixed (byte* start = &_pixels[(y0 * width + x0) * 4])
                gpu.Context.UpdateSubresource(_texture, 0, new Box(x0, y0, 0, x1, y1, 1), (IntPtr)start, (uint)(width * 4), 0);
        }
        return _view!;
    }

    // Desenha um traço (pontos já em pixels de "pixels") com borda suave, só dentro de "region". bgra: ordem dos canais
    // (a imagem salva) ou RGBA (a camada da tela). A cor cobre por cima (opaca).
    public static void Draw(byte[] pixels, int width, int height, bool bgra, (int x0, int y0, int x1, int y1) region, uint color, double thickness,
        (double x, double y)[] points, bool closed)
    {
        if (points.Length == 0) return;
        byte r = (byte)(color >> 16), g = (byte)(color >> 8), b = (byte)color;
        (byte c0, byte c2) = bgra ? (b, r) : (r, b);
        double radius = Math.Max(0.75, thickness / 2);
        int segments = closed ? points.Length : Math.Max(1, points.Length - 1);
        for (int s = 0; s < segments; s++)
        {
            var (ax, ay) = points[s];
            var (bx, by) = points[Math.Min((s + 1) % (closed ? points.Length : int.MaxValue), points.Length - 1)];
            int sx0 = Math.Max(region.x0, (int)Math.Floor(Math.Min(ax, bx) - radius - 1)), sx1 = Math.Min(region.x1, (int)Math.Ceiling(Math.Max(ax, bx) + radius + 1));
            int sy0 = Math.Max(region.y0, (int)Math.Floor(Math.Min(ay, by) - radius - 1)), sy1 = Math.Min(region.y1, (int)Math.Ceiling(Math.Max(ay, by) + radius + 1));
            double dx = bx - ax, dy = by - ay, length2 = dx * dx + dy * dy;
            for (int y = Math.Max(0, sy0); y < Math.Min(height, sy1); y++)
            for (int x = Math.Max(0, sx0); x < Math.Min(width, sx1); x++)
            {
                // Distância do centro do pixel até o segmento (cápsula: pontas redondas).
                double px = x + 0.5 - ax, py = y + 0.5 - ay;
                double t = length2 > 0 ? Math.Clamp((px * dx + py * dy) / length2, 0, 1) : 0;
                double ex = px - t * dx, ey = py - t * dy;
                double coverage = Math.Clamp(radius - Math.Sqrt(ex * ex + ey * ey) + 0.5, 0, 1);
                if (coverage <= 0) continue;
                int i = (y * width + x) * 4;
                double keep = 1 - coverage;
                pixels[i] = (byte)(c0 * coverage + pixels[i] * keep + 0.5);
                pixels[i + 1] = (byte)(g * coverage + pixels[i + 1] * keep + 0.5);
                pixels[i + 2] = (byte)(c2 * coverage + pixels[i + 2] * keep + 0.5);
                pixels[i + 3] = (byte)(255 * coverage + pixels[i + 3] * keep + 0.5);
            }
        }
    }

    public void Dispose()
    {
        _view?.Dispose();
        _texture?.Dispose();
    }
}
