using System.Runtime.InteropServices;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace MKVision.Desktop;

// Tela de desenho da interface do player (barra de controles, menu do topo, avisos): formas desenhadas aqui
// (retângulos, círculos com borda suave, degradê) e textos/ícones pelo Windows (GDI, com suavização), numa
// imagem com transparência (alfa pré-multiplicado) que vira uma textura desenhada por cima do vídeo.
// Uma thread por vez (a da tela).
internal sealed class UiCanvas : IDisposable
{
    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public int biSize, biWidth, biHeight;
        public short biPlanes, biBitCount;
        public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SIZE { public int cx, cy; }

    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateDIBSection(IntPtr dc, ref BITMAPINFOHEADER info, uint usage, out IntPtr bits, IntPtr section, uint offset);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] private static extern int SetBkMode(IntPtr dc, int mode);
    [DllImport("gdi32.dll")] private static extern uint SetTextColor(IntPtr dc, uint color);
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)] private static extern bool TextOut(IntPtr dc, int x, int y, string text, int length);
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)] private static extern bool GetTextExtentPoint32(IntPtr dc, string text, int length, out SIZE size);
    [DllImport("gdi32.dll")] private static extern bool GdiFlush();
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateFont(int height, int width, int escapement, int orientation, int weight, uint italic,
        uint underline, uint strikeOut, uint charSet, uint outPrecision, uint clipPrecision, uint quality, uint pitchAndFamily, string face);

    public int Width { get; private set; }
    public int Height { get; private set; }
    public int OffsetX; // somado ao x de tudo o que é desenhado (desenhar uma parte ao lado da outra)

    private float[] _pixels = []; // RGBA pré-multiplicado, 0–1
    private byte[] _bytes = [];
    private IntPtr _dc, _bitmap, _bits;
    private int _dibW, _dibH;
    private ID3D11Texture2D? _texture;
    private ID3D11ShaderResourceView? _view;
    private int _texW, _texH;

    // Fonte do Windows (quem cria apaga com DeleteFont). quality 4 = suavizada em tons de cinza (sem ClearType colorido).
    public static IntPtr Font(string face, int pixels, int weight = 400) => CreateFont(-pixels, 0, 0, 0, weight, 0, 0, 0, 1, 0, 0, 4, 0, face);
    public static void DeleteFont(IntPtr font) => DeleteObject(font);

    public void Begin(int width, int height)
    {
        OffsetX = 0;
        (Width, Height) = (Math.Max(1, width), Math.Max(1, height));
        int n = Width * Height * 4;
        if (_pixels.Length != n) _pixels = new float[n];
        else Array.Clear(_pixels);
        if (_dc == IntPtr.Zero || _dibW < Width || _dibH < Height)
        {
            if (_bitmap != IntPtr.Zero) DeleteObject(_bitmap);
            if (_dc == IntPtr.Zero) _dc = CreateCompatibleDC(IntPtr.Zero);
            (_dibW, _dibH) = (Math.Max(Width, _dibW), Math.Max(Height, _dibH));
            var header = new BITMAPINFOHEADER { biSize = 40, biWidth = _dibW, biHeight = -_dibH, biPlanes = 1, biBitCount = 32 };
            _bitmap = CreateDIBSection(_dc, ref header, 0, out _bits, IntPtr.Zero, 0);
            SelectObject(_dc, _bitmap);
            SetBkMode(_dc, 1); // transparente
            SetTextColor(_dc, 0x00FFFFFF);
        }
    }

    // Mistura (por cima) uma cor com transparência em um pixel.
    private void Blend(int x, int y, float r, float g, float b, float alpha)
    {
        if (alpha <= 0 || (uint)x >= (uint)Width || (uint)y >= (uint)Height) return;
        int i = (y * Width + x) * 4;
        float keep = 1 - alpha;
        _pixels[i] = r * alpha + _pixels[i] * keep;
        _pixels[i + 1] = g * alpha + _pixels[i + 1] * keep;
        _pixels[i + 2] = b * alpha + _pixels[i + 2] * keep;
        _pixels[i + 3] = alpha + _pixels[i + 3] * keep;
    }

    private static (float r, float g, float b) Rgb(uint rgb) => (((rgb >> 16) & 0xff) / 255f, ((rgb >> 8) & 0xff) / 255f, (rgb & 0xff) / 255f);

    // Preto do topo (alphaTop) até a base (alphaBottom) — o fundo que destaca os controles sobre o vídeo.
    public void Gradient(float alphaTop, float alphaBottom)
    {
        for (int y = 0; y < Height; y++)
        {
            float t = Height > 1 ? (float)y / (Height - 1) : 0;
            float a = alphaTop + (alphaBottom - alphaTop) * t * t;
            for (int x = 0; x < Width; x++) Blend(x, y, 0, 0, 0, a);
        }
    }

    public void Rect(float x, float y, float w, float h, uint rgb, float alpha)
    {
        x += OffsetX;
        var (r, g, b) = Rgb(rgb);
        int x0 = (int)Math.Floor(x), y0 = (int)Math.Floor(y), x1 = (int)Math.Ceiling(x + w), y1 = (int)Math.Ceiling(y + h);
        for (int py = y0; py < y1; py++)
        {
            float cy = Math.Clamp(Math.Min(py + 1, y + h) - Math.Max(py, y), 0, 1); // cobertura parcial nas bordas
            for (int px = x0; px < x1; px++)
            {
                float cx = Math.Clamp(Math.Min(px + 1, x + w) - Math.Max(px, x), 0, 1);
                Blend(px, py, r, g, b, alpha * cx * cy);
            }
        }
    }

    public void Circle(float cx, float cy, float radius, uint rgb, float alpha)
    {
        cx += OffsetX;
        var (r, g, b) = Rgb(rgb);
        for (int y = (int)(cy - radius - 1); y <= (int)(cy + radius + 1); y++)
        for (int x = (int)(cx - radius - 1); x <= (int)(cx + radius + 1); x++)
        {
            float d = MathF.Sqrt((x + 0.5f - cx) * (x + 0.5f - cx) + (y + 0.5f - cy) * (y + 0.5f - cy));
            Blend(x, y, r, g, b, alpha * Math.Clamp(radius - d + 0.5f, 0, 1)); // borda suave
        }
    }

    // Imagem (BGRA, opaca) encaixada sem cortar dentro do retângulo, com suavização (bilinear).
    public void Image(byte[] bgra, int iw, int ih, float x, float y, float w, float h)
    {
        x += OffsetX;
        float scale = Math.Min(w / iw, h / ih);
        float dw = iw * scale, dh = ih * scale, ox = x + (w - dw) / 2, oy = y + (h - dh) / 2;
        for (int py = (int)Math.Floor(oy); py < (int)Math.Ceiling(oy + dh); py++)
        {
            float sy = Math.Clamp((py + 0.5f - oy) / scale - 0.5f, 0, ih - 1);
            int y0 = (int)sy, y1 = Math.Min(y0 + 1, ih - 1);
            float fy = sy - y0;
            for (int px = (int)Math.Floor(ox); px < (int)Math.Ceiling(ox + dw); px++)
            {
                float sx = Math.Clamp((px + 0.5f - ox) / scale - 0.5f, 0, iw - 1);
                int x0 = (int)sx, x1 = Math.Min(x0 + 1, iw - 1);
                float fx = sx - x0;
                float Channel(int c) =>
                    ((bgra[(y0 * iw + x0) * 4 + c] * (1 - fx) + bgra[(y0 * iw + x1) * 4 + c] * fx) * (1 - fy) +
                     (bgra[(y1 * iw + x0) * 4 + c] * (1 - fx) + bgra[(y1 * iw + x1) * 4 + c] * fx) * fy) / 255f;
                Blend(px, py, Channel(2), Channel(1), Channel(0), 1);
            }
        }
    }

    // Ícone com transparência (alfa pré-multiplicado, como o Shell entrega), centralizado em w×h.
    public void Icon(byte[] bgra, int iw, int ih, float x, float y, float w, float h)
    {
        x += OffsetX;
        float scale = Math.Min(w / iw, h / ih);
        float dw = iw * scale, dh = ih * scale, ox = x + (w - dw) / 2, oy = y + (h - dh) / 2;
        for (int py = (int)Math.Floor(oy); py < (int)Math.Ceiling(oy + dh); py++)
        {
            float sy = Math.Clamp((py + 0.5f - oy) / scale - 0.5f, 0, ih - 1);
            int y0 = (int)sy, y1 = Math.Min(y0 + 1, ih - 1);
            float fy = sy - y0;
            for (int px = (int)Math.Floor(ox); px < (int)Math.Ceiling(ox + dw); px++)
            {
                float sx = Math.Clamp((px + 0.5f - ox) / scale - 0.5f, 0, iw - 1);
                int x0 = (int)sx, x1 = Math.Min(x0 + 1, iw - 1);
                float fx = sx - x0;
                float Channel(int c) =>
                    ((bgra[(y0 * iw + x0) * 4 + c] * (1 - fx) + bgra[(y0 * iw + x1) * 4 + c] * fx) * (1 - fy) +
                     (bgra[(y1 * iw + x0) * 4 + c] * (1 - fx) + bgra[(y1 * iw + x1) * 4 + c] * fx) * fy) / 255f;
                float a = Channel(3);
                if (a <= 0.004f) continue;
                Blend(px, py, Math.Min(1, Channel(2) / a), Math.Min(1, Channel(1) / a), Math.Min(1, Channel(0) / a), a);
            }
        }
    }

    public int TextWidth(string text, IntPtr font)
    {
        SelectObject(_dc, font);
        GetTextExtentPoint32(_dc, text, text.Length, out var size);
        return size.cx;
    }

    public int TextHeight(IntPtr font)
    {
        SelectObject(_dc, font);
        GetTextExtentPoint32(_dc, "Ág", 2, out var size);
        return size.cy;
    }

    // Texto (ou ícone de uma fonte de ícones) com o topo em y; o Windows desenha branco sobre preto e o tom de
    // cinza vira a cobertura da cor pedida.
    public void Text(string text, IntPtr font, int x, int y, uint rgb, float alpha = 1)
    {
        x += OffsetX;
        if (text.Length == 0) return;
        SelectObject(_dc, font);
        GetTextExtentPoint32(_dc, text, text.Length, out var size);
        int x0 = Math.Max(0, x), y0 = Math.Max(0, y), x1 = Math.Min(Width, x + size.cx + 2), y1 = Math.Min(Height, y + size.cy);
        if (x1 <= x0 || y1 <= y0) return;
        unsafe
        {
            uint* bits = (uint*)_bits;
            for (int py = y0; py < y1; py++) new Span<uint>(bits + py * _dibW + x0, x1 - x0).Clear();
            TextOut(_dc, x, y, text, text.Length);
            GdiFlush();
            var (r, g, b) = Rgb(rgb);
            for (int py = y0; py < y1; py++)
            for (int px = x0; px < x1; px++)
            {
                uint value = bits[py * _dibW + px];
                float coverage = Math.Max((value & 0xff), Math.Max((value >> 8) & 0xff, (value >> 16) & 0xff)) / 255f;
                if (coverage > 0) Blend(px, py, r, g, b, alpha * coverage);
            }
        }
    }

    // Envia para a placa (textura do tamanho da tela de desenho) e devolve a textura para desenhar.
    public ID3D11ShaderResourceView Upload(Gpu gpu)
    {
        if (_texture is null || _texW != Width || _texH != Height)
        {
            _view?.Dispose();
            _texture?.Dispose();
            _texture = gpu.Device.CreateTexture2D(new Texture2DDescription(Format.R8G8B8A8_UNorm, (uint)Width, (uint)Height, 1, 1, BindFlags.ShaderResource));
            _view = gpu.Device.CreateShaderResourceView(_texture);
            (_texW, _texH) = (Width, Height);
        }
        int n = Width * Height * 4;
        if (_bytes.Length != n) _bytes = new byte[n];
        for (int i = 0; i < n; i++) _bytes[i] = (byte)(Math.Clamp(_pixels[i], 0, 1) * 255 + 0.5f);
        gpu.Context.UpdateSubresource(_bytes, _texture, 0, (uint)(Width * 4), 0);
        return _view!;
    }

    public void Dispose()
    {
        _view?.Dispose();
        _texture?.Dispose();
        if (_bitmap != IntPtr.Zero) DeleteObject(_bitmap);
        if (_dc != IntPtr.Zero) DeleteDC(_dc);
    }
}
