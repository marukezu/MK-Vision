using Vortice.MediaFoundation;

namespace MKVision.Desktop;

// Som do player: o tocador de mídia do Windows (Media Engine) toca só o áudio do arquivo — sem decodificar a
// imagem, que é do player — com volume, mudo e velocidade (com correção de tom). O relógio é o da imagem: o som
// começa, pausa, pula e muda de velocidade junto com ela e, se desviar mais de 0,15 s, é reposicionado.
// Abaixo de 0.5x fica mudo (câmera lenta é para a imagem). Thread principal.
internal sealed class PlayerAudio : IDisposable
{
    private const double MinAudibleSpeed = 0.5, MaxDrift = 0.15;

    private readonly IMFMediaEngineClassFactory? _factory;
    private readonly IMFMediaEngine? _engine;
    private readonly MediaEngineNotifyDelegate _notify; // referência fixa: o Windows chama por ela
    private volatile bool _ready, _hasAudio;
    private double _lastResync;

    public bool HasAudio => _hasAudio;
    // Duração que o tocador conhece (de um arquivo que ainda está baixando, só a parte que já chegou).
    public double Duration { get { try { return _ready ? _engine?.Duration ?? 0 : 0; } catch { return 0; } } }
    public double Position { get { try { return _engine?.CurrentTime ?? 0; } catch { return 0; } } } // para o autoteste

    public PlayerAudio()
    {
        _notify = OnEvent;
        try
        {
            MediaFactory.MFStartup(true).CheckError();
            _factory = new IMFMediaEngineClassFactory();
            using var attributes = MediaFactory.MFCreateAttributes(1);
            _engine = _factory.CreateInstance(MediaEngineCreateFlags.AudioOnly, attributes, _notify);
            _engine.AutoPlay = false;
            _engine.Loop = false;
        }
        catch (Exception error)
        {
            Log.Write($"Player: som indisponível — {error.Message}");
        }
    }

    private void OnEvent(MediaEngineEvent mediaEvent, UIntPtr param1, int param2)
    {
        switch (mediaEvent)
        {
            case MediaEngineEvent.LoadedMetadata:
                _hasAudio = _engine?.HasAudio() == true;
                _ready = true;
                break;
            case MediaEngineEvent.Error:
                _ready = false;
                _hasAudio = false;
                break;
        }
    }

    public void Open(string path)
    {
        if (_engine is null) return;
        _ready = _hasAudio = false;
        try
        {
            _engine.Pause();
            _engine.SetSource(path).CheckError();
            _engine.Load();
        }
        catch (Exception error)
        {
            Log.Write($"Player: não consegui abrir o som de {Path.GetFileName(path)} — {error.Message}");
        }
    }

    private MFByteStream? _byteStream;
    private Stream? _stream;

    // Som de um vídeo da internet (HttpRangeStream / arquivo crescendo); "name" com a extensão ajuda a achar o
    // formato. O fluxo passa a ser desta classe.
    public void Open(Stream stream, string name)
    {
        if (_engine is null) { stream.Dispose(); return; }
        _ready = _hasAudio = false;
        try
        {
            _engine.Pause();
            var old = (_byteStream, _stream);
            _stream = stream;
            _byteStream = new MFByteStream(stream, false);
            try
            {
                using var info = _byteStream.QueryInterface<IMFAttributes>();
                string ext = Path.GetExtension(name).ToLowerInvariant();
                info.Set(ByteStreamAttributeKeys.ContentType, ext switch { ".webm" => "audio/webm", ".m4a" => "audio/mp4", ".ts" => "video/mp2t", _ => "video/mp4" });
                info.Set(ByteStreamAttributeKeys.OriginName, name);
            }
            catch { }
            using var ex = _engine.QueryInterface<IMFMediaEngineEx>();
            ex.SetSourceFromByteStream(_byteStream, name);
            _engine.Load();
            old._byteStream?.Dispose();
            old._stream?.Dispose();
        }
        catch (Exception error)
        {
            Log.Write($"Player: não consegui abrir o som de {name} — {error.Message}");
        }
    }

    // Segue a imagem: chamado ~10x por segundo com o momento do arquivo na tela, se está tocando e a velocidade.
    public void Sync(double fileTime, bool playing, double speed, float volume, bool muted)
    {
        if (_engine is null || !_ready) return;
        try
        {
            _engine.Volume = Math.Clamp(volume, 0, 1);
            _engine.Muted = muted;
            bool audible = playing && _hasAudio && speed >= MinAudibleSpeed;
            if (!audible)
            {
                if (!_engine.IsPaused) _engine.Pause();
                return;
            }
            if (Math.Abs(_engine.PlaybackRate - speed) > 0.001)
            {
                _engine.DefaultPlaybackRate = speed;
                _engine.PlaybackRate = speed;
            }
            double drift = _engine.CurrentTime - fileTime;
            double now = Environment.TickCount64 / 1000.0;
            if (_engine.IsPaused || (Math.Abs(drift) > MaxDrift && !_engine.IsSeeking && now - _lastResync > 0.5))
            {
                _engine.CurrentTime = fileTime; // começou, pulou ou desviou: reposiciona
                _lastResync = now;
            }
            if (_engine.IsPaused) _engine.Play();
        }
        catch
        {
            // O som nunca pode atrapalhar a imagem.
        }
    }

    public void Dispose()
    {
        try
        {
            _engine?.Shutdown();
        }
        catch { }
        _byteStream?.Dispose();
        _stream?.Dispose();
        _engine?.Dispose();
        _factory?.Dispose();
    }
}
