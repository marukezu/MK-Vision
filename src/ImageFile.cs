using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.WIC;

namespace MKVision.Desktop;

// Abre uma foto com o WIC do Windows (o mesmo leitor do Visualizador de Fotos: JPEG, PNG, BMP, GIF, TIFF, WebP;
// HEIC e AVIF com as extensões da Store) e a envia para a placa como textura BGRA. Já vem de pé (orientação
// da câmera, EXIF) e com a transparência sobre preto. Fotos maiores que o limite de textura são reduzidas.
internal static class ImageFile
{
    public static readonly string[] Extensions = [".jpg", ".jpeg", ".jfif", ".png", ".bmp", ".gif", ".tif", ".tiff", ".webp", ".heic", ".heif", ".avif", ".jxr", ".wdp"];

    public static bool IsImage(string path) => Extensions.Contains(Path.GetExtension(path).ToLowerInvariant());

    private const int MaxSide = 16384; // limite de textura do Direct3D 11

    public static (ID3D11Texture2D texture, int width, int height) Load(string path, ID3D11Device device)
    {
        var (pixels, w, h) = LoadPixels(path);
        return (CreateTexture(device, pixels, w, h), w, h);
    }

    // BGRA pré-multiplicado, de pé, até "maxSide" de lado (para a IA de fotos, que trabalha na CPU/placa por conta).
    public static unsafe ID3D11Texture2D CreateTexture(ID3D11Device device, byte[] pixels, int w, int h)
    {
        fixed (byte* p = pixels)
            return device.CreateTexture2D(new Texture2DDescription(Format.B8G8R8A8_UNorm, (uint)w, (uint)h, 1, 1, BindFlags.ShaderResource,
                ResourceUsage.Immutable), [new SubresourceData((nint)p, (uint)w * 4)]);
    }

    public static unsafe (byte[] pixels, int width, int height) LoadPixels(string path, int maxSide = MaxSide)
    {
        using var factory = new IWICImagingFactory();
        using var decoder = factory.CreateDecoderFromFileName(path, FileAccess.Read, DecodeOptions.CacheOnLoad);
        using var frame = decoder.GetFrame(0);
        IWICBitmapSource source = frame;
        var owned = new List<IDisposable>();
        try
        {
            // Orientação da câmera (EXIF 274): 3 = 180°, 6 = 90° horário, 8 = 270°; 2/4/5/7 = espelhadas.
            var transform = Orientation(frame) switch
            {
                2 => BitmapTransformOptions.FlipHorizontal,
                3 => BitmapTransformOptions.Rotate180,
                4 => BitmapTransformOptions.FlipVertical,
                5 => BitmapTransformOptions.Rotate90 | BitmapTransformOptions.FlipHorizontal,
                6 => BitmapTransformOptions.Rotate90,
                7 => BitmapTransformOptions.Rotate270 | BitmapTransformOptions.FlipHorizontal,
                8 => BitmapTransformOptions.Rotate270,
                _ => BitmapTransformOptions.Rotate0
            };
            if (transform != BitmapTransformOptions.Rotate0)
            {
                var rotator = factory.CreateBitmapFlipRotator();
                owned.Add(rotator);
                rotator.Initialize(source, transform);
                source = rotator;
            }
            source.GetSize(out uint w, out uint h);
            if (w > maxSide || h > maxSide)
            {
                double scale = Math.Min((double)maxSide / w, (double)maxSide / h);
                var scaler = factory.CreateBitmapScaler();
                owned.Add(scaler);
                (w, h) = ((uint)Math.Max(1, w * scale), (uint)Math.Max(1, h * scale));
                scaler.Initialize(source, w, h, BitmapInterpolationMode.Fant);
                source = scaler;
            }
            // Transparência pré-multiplicada: o que é transparente fica preto.
            var converter = factory.CreateFormatConverter();
            owned.Add(converter);
            converter.Initialize(source, PixelFormat.Format32bppPBGRA, BitmapDitherType.None, null, 0, BitmapPaletteType.Custom);
            uint stride = w * 4;
            var pixels = new byte[(long)stride * h];
            fixed (byte* p = pixels)
                converter.CopyPixels(new Vortice.Mathematics.RectI(0, 0, (int)w, (int)h), stride, (uint)pixels.Length, (nint)p);
            return (pixels, (int)w, (int)h);
        }
        finally
        {
            foreach (var o in owned) o.Dispose();
        }
    }

    // Salva BGRA (opaco) em JPG (qualidade padrão do Windows, 90; ".jpg"/".jpeg") ou PNG (sem perda, o resto), pelo WIC.
    public static unsafe void Save(string path, byte[] bgra, int w, int h)
    {
        bool jpeg = Path.GetExtension(path).ToLowerInvariant() is ".jpg" or ".jpeg";
        using var factory = new IWICImagingFactory();
        string part = path + ".part";
        using (var file = File.Create(part))
        using (var stream = factory.CreateStream(file))
            Encode(factory, stream, jpeg ? ContainerFormat.Jpeg : ContainerFormat.Png, bgra, w, h);
        File.Move(part, path, overwrite: true);
    }

    // BGRA (opaco) → PNG na memória (para enviar ao ComfyUI sem passar pelo disco).
    public static byte[] EncodePng(byte[] bgra, int w, int h)
    {
        using var factory = new IWICImagingFactory();
        using var memory = new MemoryStream();
        using (var stream = factory.CreateStream(memory))
            Encode(factory, stream, ContainerFormat.Png, bgra, w, h);
        return memory.ToArray();
    }

    // O codificador escolhe o formato que aceita (o PNG não aceita "32 bits sem alfa" e troca por 24 bits): os pixels
    // vão no formato que ele devolveu. Antes iam sempre em 32 bits — no PNG, cada linha saía deslocada (listras
    // verticais e cor errada).
    private static unsafe void Encode(IWICImagingFactory factory, IWICStream stream, ContainerFormat container, byte[] bgra, int w, int h)
    {
        using var encoder = factory.CreateEncoder(container);
        encoder.Initialize(stream, BitmapEncoderCacheOption.NoCache);
        using var frame = encoder.CreateNewFrame(out var options);
        frame.Initialize(options);
        frame.SetSize((uint)w, (uint)h);
        var format = PixelFormat.Format32bppBGR;
        frame.SetPixelFormat(ref format);
        byte[] data = bgra;
        int stride = w * 4;
        if (format == PixelFormat.Format24bppBGR)
        {
            stride = (w * 3 + 3) & ~3; // linhas alinhadas em 4 bytes
            data = new byte[(long)stride * h];
            var packed = data;
            Parallel.For(0, h, y =>
            {
                int s = y * w * 4, d = y * stride;
                for (int x = 0; x < w; x++, s += 4, d += 3)
                    (packed[d], packed[d + 1], packed[d + 2]) = (bgra[s], bgra[s + 1], bgra[s + 2]);
            });
        }
        else if (format == PixelFormat.Format32bppBGRA || format == PixelFormat.Format32bppPBGRA)
        {
            data = (byte[])bgra.Clone();
            for (int i = 3; i < data.Length; i += 4) data[i] = 255; // opaco
        }
        else if (format != PixelFormat.Format32bppBGR)
            throw new NotSupportedException($"O codificador pediu um formato de pixel inesperado ({format})");
        fixed (byte* p = data) frame.WritePixels((uint)h, (uint)stride, (uint)data.Length, p);
        frame.Commit();
        encoder.Commit();
        options.Dispose();
    }

    // Imagem na memória (PNG/JPG/WebP...) → BGRA pré-multiplicado, no tamanho original.
    public static unsafe (byte[] pixels, int width, int height) DecodePixels(byte[] data)
    {
        using var factory = new IWICImagingFactory();
        using var memory = new MemoryStream(data);
        using var stream = factory.CreateStream(memory);
        using var decoder = factory.CreateDecoderFromStream(stream, DecodeOptions.CacheOnLoad);
        using var frame = decoder.GetFrame(0);
        frame.GetSize(out uint w, out uint h);
        using var converter = factory.CreateFormatConverter();
        converter.Initialize(frame, PixelFormat.Format32bppPBGRA, BitmapDitherType.None, null, 0, BitmapPaletteType.Custom);
        var pixels = new byte[(long)w * h * 4];
        fixed (byte* p = pixels)
            converter.CopyPixels(new Vortice.Mathematics.RectI(0, 0, (int)w, (int)h), w * 4, (uint)pixels.Length, (nint)p);
        return (pixels, (int)w, (int)h);
    }

    // --encodetest <foto> <pasta>: grava PNG (memória e arquivo) e JPG e lê de volta. Diferença média por canal: PNG = 0.
    public static int EncodeTest(string input, string folder)
    {
        var (pixels, w, h) = LoadPixels(input, 2048);
        for (int i = 3; i < pixels.Length; i += 4) pixels[i] = 255;
        double Diff(byte[] other)
        {
            long sum = 0;
            for (int i = 0; i < pixels.Length; i += 4)
                sum += Math.Abs(pixels[i] - other[i]) + Math.Abs(pixels[i + 1] - other[i + 1]) + Math.Abs(pixels[i + 2] - other[i + 2]);
            return sum / (pixels.Length / 4 * 3.0);
        }
        Directory.CreateDirectory(folder);
        var memory = DecodePixels(EncodePng(pixels, w, h)).pixels;
        string png = Path.Combine(folder, "encodetest.png"), jpg = Path.Combine(folder, "encodetest.jpg");
        Save(png, pixels, w, h);
        Save(jpg, pixels, w, h);
        double dm = Diff(memory), dp = Diff(LoadPixels(png).pixels), dj = Diff(LoadPixels(jpg).pixels);
        Console.WriteLine($"{w}x{h}  PNG na memória: {dm:0.00}  PNG arquivo: {dp:0.00}  JPG: {dj:0.00}");
        return dm < 0.01 && dp < 0.01 && dj < 6 ? 0 : 1;
    }

    // Ícone pequeno (favicon PNG/ICO) em BGRA com transparência, até "max" pixels de lado.
    public static unsafe ThumbnailCache.Thumb? DecodeSmall(byte[] data, int max = 32)
    {
        try
        {
            using var factory = new IWICImagingFactory();
            using var memory = new MemoryStream(data);
            using var stream = factory.CreateStream(memory);
            using var decoder = factory.CreateDecoderFromStream(stream, DecodeOptions.CacheOnLoad);
            using var frame = decoder.GetFrame(0);
            frame.GetSize(out uint w, out uint h);
            IWICBitmapSource source = frame;
            IWICBitmapScaler? scaler = null;
            if (w > max || h > max)
            {
                double scale = Math.Min((double)max / w, (double)max / h);
                (w, h) = ((uint)Math.Max(1, w * scale), (uint)Math.Max(1, h * scale));
                scaler = factory.CreateBitmapScaler();
                scaler.Initialize(frame, w, h, BitmapInterpolationMode.Fant);
                source = scaler;
            }
            using var converter = factory.CreateFormatConverter();
            converter.Initialize(source, PixelFormat.Format32bppPBGRA, BitmapDitherType.None, null, 0, BitmapPaletteType.Custom);
            var pixels = new byte[w * h * 4];
            fixed (byte* p = pixels)
                converter.CopyPixels(new Vortice.Mathematics.RectI(0, 0, (int)w, (int)h), w * 4, (uint)pixels.Length, (nint)p);
            scaler?.Dispose();
            return new ThumbnailCache.Thumb(pixels, (int)w, (int)h);
        }
        catch { return null; }
    }

    private static int Orientation(IWICBitmapFrameDecode frame)
    {
        try
        {
            using var reader = frame.MetadataQueryReader;
            foreach (var query in new[] { "/app1/ifd/{ushort=274}", "/ifd/{ushort=274}", "System.Photo.Orientation" })
            {
                try
                {
                    var value = reader.GetMetadataByName(query);
                    if (value.Value is { } v) return Convert.ToInt32(v);
                }
                catch { } // essa forma de guardar não existe neste formato
            }
        }
        catch { } // formato sem metadados
        return 1;
    }
}
