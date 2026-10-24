using Vortice.Direct3D11;
using Vortice.MediaFoundation;

namespace MKVision.Desktop;

// Lê os quadros de um arquivo de vídeo com o Media Foundation do Windows: decodificação na placa de vídeo
// (DXVA) e conversão de cor também na placa; cada quadro chega como textura Direct3D, com o tempo exato do
// arquivo. Formatos: os que o Windows decodifica (H.264, HEVC, VP9, AV1 — com as extensões da Store).
// Só a imagem (o áudio não é lido). Uma thread por vez.
internal sealed class VideoFile : IDisposable
{
    private static readonly Guid EnableHardwareTransforms = new("a634a91c-822b-41b9-a494-4de4643612b0"); // MF_READWRITE_ENABLE_HARDWARE_TRANSFORMS
    private static readonly object StartLock = new();
    private static bool _started;

    private readonly IMFDXGIDeviceManager _manager;
    private readonly IMFSourceReader _reader;

    public string FilePath { get; }
    public int Width { get; private set; }
    public int Height { get; private set; }
    public double Duration { get; }        // segundos (0 = desconhecida)
    public double FrameDuration { get; }   // segundos por quadro, pelo arquivo (1/30 se ele não informar)

    private readonly Stream? _stream;          // vídeo da internet (HttpRangeStream): o leitor lê dele
    private readonly MFByteStream? _byteStream;

    // "stream": em vez do arquivo "path", lê deste fluxo (path vira só o nome mostrado).
    public VideoFile(string path, ID3D11Device device, Stream? stream = null, string? contentType = null)
    {
        lock (StartLock)
        {
            if (!_started) MediaFactory.MFStartup(true).CheckError();
            _started = true;
        }
        FilePath = path;
        _manager = MediaFactory.MFCreateDXGIDeviceManager();
        _manager.ResetDevice(device).CheckError();
        using (var attributes = MediaFactory.MFCreateAttributes(3))
        {
            attributes.Set(SourceReaderAttributeKeys.D3DManager, _manager).CheckError();
            attributes.Set(SourceReaderAttributeKeys.EnableAdvancedVideoProcessing, true).CheckError(); // cor (YUV → RGB) na placa
            attributes.Set(EnableHardwareTransforms, 1u).CheckError();                                 // decodificador da placa
            if (stream is null) _reader = MediaFactory.MFCreateSourceReaderFromURL(path, attributes);
            else
            {
                _stream = stream;
                _byteStream = new MFByteStream(stream, false);
                // Sem endereço, o Windows descobre o formato pelo conteúdo; o tipo e o nome ajudam.
                try
                {
                    using var info = _byteStream.QueryInterface<IMFAttributes>();
                    if (contentType is not null) info.Set(ByteStreamAttributeKeys.ContentType, contentType);
                    info.Set(ByteStreamAttributeKeys.OriginName, path);
                }
                catch { }
                _reader = MediaFactory.MFCreateSourceReaderFromByteStream(_byteStream, attributes);
            }
        }
        _reader.SetStreamSelection(SourceReaderIndex.AllStreams, false);
        _reader.SetStreamSelection(SourceReaderIndex.FirstVideoStream, true);
        using (var type = MediaFactory.MFCreateMediaType())
        {
            type.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video).CheckError();
            type.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.Argb32).CheckError();
            _reader.SetCurrentMediaType(SourceReaderIndex.FirstVideoStream, type);
        }
        ReadFormat();

        FrameDuration = 1 / 30.0;
        using (var current = _reader.GetCurrentMediaType(SourceReaderIndex.FirstVideoStream))
        {
            if (current.GetUInt64(MediaTypeAttributeKeys.FrameRate, out ulong rate).Success && (uint)rate != 0 && rate >> 32 != 0)
                FrameDuration = (uint)rate / (double)(rate >> 32); // taxa = numerador/denominador
        }
        try
        {
            var duration = _reader.GetPresentationAttribute(SourceReaderIndex.MediaSource, PresentationDescriptionAttributeKeys.Duration);
            Duration = Convert.ToDouble(duration.Value) / 1e7; // unidades de 100 ns
        }
        catch
        {
            Duration = 0;
        }
    }

    // O que o arquivo diz da cor (faixa, matriz, cores, curva) — para conferir a conversão (--colorcheck).
    public string ColorInfo
    {
        get
        {
            try
            {
                using var native = _reader.GetNativeMediaType(SourceReaderIndex.FirstVideoStream, 0);
                return ColorCheck.Describe(native);
            }
            catch (Exception error) { return "cor: " + error.Message; }
        }
    }

    private void ReadFormat()
    {
        using var current = _reader.GetCurrentMediaType(SourceReaderIndex.FirstVideoStream);
        ulong size = current.GetUInt64(MediaTypeAttributeKeys.FrameSize);
        Width = (int)(size >> 32);
        Height = (int)(size & 0xffffffff);
    }

    // Próximo quadro: "copy" recebe a textura decodificada (e o índice dela, se for um array) para copiar.
    // false = fim do vídeo.
    // "want": se der false para o tempo do quadro, ele não é copiado (pular rápido até um ponto).
    public bool ReadFrame(Action<ID3D11Texture2D, uint> copy, out double time, Func<double, bool>? want = null)
    {
        while (true)
        {
            var sample = _reader.ReadSample(SourceReaderIndex.FirstVideoStream, SourceReaderControlFlag.None, out _, out var flags, out long timestamp);
            try
            {
                if ((flags & SourceReaderFlag.EndOfStream) != 0)
                {
                    time = 0;
                    return false;
                }
                if ((flags & SourceReaderFlag.Error) != 0) throw new InvalidOperationException("erro ao decodificar o vídeo");
                if ((flags & SourceReaderFlag.CurrentMediaTypeChanged) != 0) ReadFormat();
                if (sample is null) continue; // aviso sem imagem (ex.: lacuna no vídeo)
                time = timestamp / 1e7;
                if (want is not null && !want(time)) return true;
                using var buffer = sample.GetBufferByIndex(0);
                using var dxgi = buffer.QueryInterface<IMFDXGIBuffer>();
                using var texture = new ID3D11Texture2D(dxgi.GetResource(typeof(ID3D11Texture2D).GUID));
                copy(texture, dxgi.SubresourceIndex);
                return true;
            }
            finally
            {
                sample?.Dispose();
            }
        }
    }

    // Vai para "seconds" (o Windows volta ao quadro-chave anterior; quem chama descarta até chegar lá).
    public void Seek(double seconds) => _reader.SetCurrentPosition((long)(Math.Max(0, seconds) * 1e7));

    public void Dispose()
    {
        _reader.Dispose();
        _manager.Dispose();
        _byteStream?.Dispose();
        _stream?.Dispose();
    }
}
