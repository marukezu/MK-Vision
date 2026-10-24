using Vortice.MediaFoundation;

namespace MKVision.Desktop;

// --tsprobe <arquivo>: faixas do arquivo (tipo, formato) e quantas amostras cada uma entrega.
internal static class TsProbe
{
    public static int Run(string path)
    {
        MediaFactory.MFStartup(true).CheckError();
        using var reader = MediaFactory.MFCreateSourceReaderFromURL(path, null);
        for (uint i = 0; i < 16; i++)
        {
            try
            {
                using var type = reader.GetNativeMediaType((SourceReaderIndex)i, 0);
                type.GetGUID(MediaTypeAttributeKeys.MajorType, out var major);
                type.GetGUID(MediaTypeAttributeKeys.Subtype, out var sub);
                reader.SetStreamSelection(SourceReaderIndex.AllStreams, false);
                reader.SetStreamSelection((SourceReaderIndex)i, true);
                reader.SetCurrentPosition(0);
                int samples = 0;
                long first = -1;
                for (int n = 0; n < 200; n++)
                {
                    using var s = reader.ReadSample((SourceReaderIndex)i, SourceReaderControlFlag.None, out _, out var flags, out long ts);
                    if ((flags & SourceReaderFlag.EndOfStream) != 0) break;
                    if (s is not null) { samples++; if (first < 0) first = ts; }
                }
                Console.WriteLine($"faixa {i}: {(major == MediaTypeGuids.Video ? "vídeo" : major == MediaTypeGuids.Audio ? "áudio" : major.ToString())} {sub} — {samples} amostras, a primeira em {first / 1e7:0.00} s");
            }
            catch (Exception e) when (i > 0) { if (!e.Message.Contains("C00D36B3") && !e.Message.Contains("InvalidStreamNumber")) Console.WriteLine($"faixa {i}: {e.Message.Split('\n')[0]}"); break; }
        }
        return 0;
    }
}
