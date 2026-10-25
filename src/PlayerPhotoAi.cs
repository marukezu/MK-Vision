using System.Diagnostics;
using Vortice.Direct3D11;

namespace MKVision.Desktop;

// Versões da imagem da tela (a foto aberta, ou o quadro do vídeo pausado): a original é o próprio quadro; por cima dela
// podem existir a do ComfyUI (tecla G) e as melhoradas pela IA de fotos (tecla I, Ajustes → Fotos) — uma para a
// original e uma para a do ComfyUI. Tudo só na memória, nunca no disco (até o Ctrl+S).
//   T  base: original ↔ ComfyUI          U  melhoria (do I) ligada ↔ desligada, na base escolhida
//   I  melhora a base escolhida (mesmo modelo de antes: só liga a que já existe)
//   Y  luz e cor (ToneAi) por cima da versão da tela; de novo: liga ↔ desliga (mudou a força no painel: refaz)
// Trocar o modelo no painel tira as melhorias (a base continua). O play, pular ou abrir outra foto descarta tudo.
internal sealed partial class Player
{
    private const int PhotoAiMaxSide = 2048; // maior que isso: reduz antes (a saída, 4x, ainda passa de 8000 px)
    private const int PhotoAiFullSide = 4096; // "resolução cheia": até aqui sem reduzir, e a saída sai 2x (até 8192)

    private CancellationTokenSource? _photoJob;
    private string _photoStatus = "";
    // As versões (com _lock e _gpuLock), todas do quadro "_stillSerial". _still = a que a tela desenha no lugar do
    // quadro (null = a original); calculada por ShowLayers.
    private Frame? _comfyLayer, _aiOriginal, _aiComfy;
    private int _aiOriginalModel, _aiComfyModel; // modelo da IA de fotos que fez cada melhorada
    private bool _showComfy, _showAi;
    // Luz e cor (Y): por versão de origem (o quadro, a do ComfyUI ou uma melhorada), a ajustada e a força usada.
    private readonly Dictionary<Frame, (Frame layer, float exposure)> _tone = [];
    private bool _showTone;
    private int _toneJobs; // Interlocked
    private Frame? _still;
    private bool _stillShowAi;
    private int _stillJobs; // IAs (I ou G) trabalhando no quadro pausado (Interlocked)
    private long _stillSerial = -1;
    private double _stillTime;
    private long _photoAiSerial = -1; // foto sendo melhorada agora (Serial; -1 = nenhuma)
    private bool _bypassForAi; // os efeitos do player foram desligados pelo I (e voltam quando a IA termina). Thread da janela.
    private (float texture, float grain, bool skin) _detailShown = (-1, -1, false);
    private CancellationTokenSource? _detailJob;

    // Situação para o painel (aba Fotos).
    private string PhotoStatus()
    {
        int model = _prefs.PhotoAi;
        if (model == 0) return "Desligada: as fotos aparecem como são (com a nitidez e a Cor e luz).";
        if (!PhotoAi.Available(model)) return "O arquivo desta IA não está na pasta Models\\photo.";
        lock (_lock) return _photoStatus.Length > 0 ? _photoStatus : $"Pronta: I melhora a imagem da tela ({PhotoAi.Models[model].Name}).";
    }

    // Com _lock: há versões guardadas (de algum quadro).
    private bool HasLayers => _comfyLayer is not null || _aiOriginal is not null || _aiComfy is not null || _tone.Count > 0;

    // Com _lock: a imagem da tela agora — vídeo pausado: o quadro; foto: ela. null = nada (ou vídeo tocando).
    private Frame? ScreenFrame()
    {
        if (_file is not null && !_image) return !_playing && _queue.Count > 0 ? _queue[0] : null;
        return _queue.FirstOrDefault(f => f.Image);
    }

    // Com _lock e _gpuLock: as versões passam a ser deste quadro (as de outro saem).
    private void LayersFor(Frame frame)
    {
        if (HasLayers && _stillSerial != frame.Serial) DisposeStill();
        (_stillSerial, _stillTime) = (frame.Serial, frame.FileTime);
    }

    // Com _lock: a versão escolhida pelo T e pelo U (null = o próprio quadro), antes da luz e cor.
    private Frame? ChosenLayer()
    {
        var ai = _showComfy ? _aiComfy : _aiOriginal;
        return _showAi && ai is not null ? ai : _showComfy ? _comfyLayer : null;
    }

    // Com _lock: escolhe a versão da tela pelas escolhas do T, do U e do Y.
    private void ShowLayers()
    {
        var chosen = ChosenLayer();
        if (_showTone && (chosen ?? ScreenFrame()) is { } source && _tone.TryGetValue(source, out var toned)) chosen = toned.layer;
        (_still, _stillShowAi) = (chosen, chosen is not null);
        if (_image && _queue.FirstOrDefault(f => f.Image) is { } photo)
            (_videoW, _videoH) = chosen is null ? (photo.Width, photo.Height) : (chosen.Width, chosen.Height);
        _dirty = true;
    }

    // Com _lock: o nome da versão da tela (para os avisos e o Ctrl+S).
    private string ShownName()
    {
        var shown = _still is not null && _stillShowAi ? _still : null;
        string Name(Frame? f) => f == _comfyLayer && f is not null ? "ComfyUI" : f == _aiComfy && f is not null ? "ComfyUI + IA" :
            f == _aiOriginal && f is not null ? "IA 4x" : "original";
        foreach (var (source, toned) in _tone)
            if (toned.layer == shown) return Name(source) + " + luz";
        return Name(shown);
    }

    private Frame NewLayer(byte[] pixels, int w, int h, bool baked = false)
    {
        var texture = ImageFile.CreateTexture(_gpu.Device, pixels, w, h);
        return new Frame { Texture = texture, View = _gpu.Device.CreateShaderResourceView(texture), Width = w, Height = h, Image = true, Analyzed = true, Baked = baked };
    }

    // Com _lock e _gpuLock: a versão sai (e a luz e cor feita em cima dela também).
    private void DisposeLayer(ref Frame? layer)
    {
        if (layer is not null && _tone.Remove(layer, out var toned)) DisposeFrame(toned.layer);
        DisposeFrame(layer);
        layer = null;
    }

    private static void DisposeFrame(Frame? frame)
    {
        frame?.Processed?.Dispose();
        frame?.View.Dispose();
        frame?.Texture.Dispose();
    }

    // Tecla Y: luz e cor (ToneAi) na versão da tela. Já feita com a força do painel: liga ↔ desliga; com outra: refaz.
    private void ToggleTone()
    {
        if (!ToneAi.Available)
        {
            Notify("As IAs de luz e cor não estão na pasta Models\\tone");
            return;
        }
        float exposure = _settings.ToneExposure;
        byte[] pixels;
        int w, h;
        long serial;
        Frame source;
        bool video;
        lock (_gpuLock)
        lock (_lock)
        {
            video = _file is not null && !_image;
            var frame = ScreenFrame();
            if (frame is null)
            {
                Notify(video ? "Pause o vídeo e aperte Y para ajustar a luz e a cor do quadro" : "Abra uma foto para ajustar a luz e a cor");
                return;
            }
            LayersFor(frame);
            source = ChosenLayer() ?? frame;
            if (_tone.TryGetValue(source, out var toned) && Math.Abs(toned.exposure - exposure) < 0.001f)
            {
                _showTone = !_showTone;
                ShowLayers();
                Notify($"Luz e cor (Y): {(_showTone ? "ligada" : "desligada")} · tela: {ShownName()}");
                _renderWake.Set();
                return;
            }
            if (Volatile.Read(ref _toneJobs) > 0)
            {
                Notify("Aguarde: a luz e a cor ainda estão sendo ajustadas");
                return;
            }
            if (source.Texture.Description.Format is not (Vortice.DXGI.Format.B8G8R8A8_UNorm or Vortice.DXGI.Format.B8G8R8X8_UNorm))
            {
                Notify("Este vídeo usa um formato de cor que a IA ainda não lê");
                return;
            }
            (w, h, serial) = (source.Width, source.Height, frame.Serial);
            pixels = ReadPixels(source.Texture, w, h);
        }
        Interlocked.Increment(ref _toneJobs);
        if (video) Interlocked.Increment(ref _stillJobs); // o clique no vídeo não dá play enquanto ajusta
        Notify(exposure > 0.001f ? $"Luz e cor: ajustando (exposição {exposure * 100:0}%)..." : "Luz e cor: ajustando a cor...");
        var thread = new Thread(() =>
        {
            var clock = Stopwatch.StartNew();
            try
            {
                var layer = NewLayer(ToneAi.Apply(pixels, w, h, exposure), w, h, source.Baked);
                bool used = false;
                lock (_gpuLock)
                lock (_lock)
                {
                    // Ainda a mesma tela, e a versão de origem ainda existe (um G ou outro I pode ter trocado).
                    var frame = ScreenFrame();
                    if (frame is not null && frame.Serial == serial &&
                        (source == frame || source == _comfyLayer || source == _aiOriginal || source == _aiComfy))
                    {
                        LayersFor(frame);
                        if (_tone.Remove(source, out var old)) DisposeFrame(old.layer);
                        layer.Serial = ++_nextSerial;
                        _tone[source] = (layer, exposure);
                        _showTone = true;
                        ShowLayers();
                        used = true;
                    }
                }
                if (!used)
                {
                    DisposeFrame(layer);
                    return;
                }
                string shown;
                lock (_lock) shown = ShownName();
                Notify($"Luz e cor ajustadas em {clock.Elapsed.TotalSeconds:0.0} s · Y liga/desliga · tela: {shown}");
                Log.Write($"Luz e cor: {w}x{h} em {clock.Elapsed.TotalSeconds:0.0} s (exposição {exposure * 100:0}%).");
                _renderWake.Set();
            }
            catch (Exception error)
            {
                Log.Write($"Luz e cor: falhou — {error.Message}");
                Notify("Luz e cor: falhou (" + error.Message.Split('\n')[0] + ")");
            }
            finally
            {
                Interlocked.Decrement(ref _toneJobs);
                if (video) Interlocked.Decrement(ref _stillJobs);
            }
        }) { IsBackground = true, Priority = ThreadPriority.BelowNormal, Name = "Luz e cor" };
        thread.Start();
    }

    // Tecla T: base original ↔ a do ComfyUI (a melhoria do U segue ligada/desligada).
    private void ToggleComfy()
    {
        string text;
        lock (_gpuLock)
        lock (_lock)
        {
            var frame = ScreenFrame();
            if (frame is null || _comfyLayer is null || _stillSerial != frame.Serial)
                text = frame is null && _file is not null && !_image ? "Pause o vídeo: T alterna o quadro com o do ComfyUI" : "Nenhuma imagem do ComfyUI nesta tela: aperte G";
            else
            {
                _showComfy = !_showComfy;
                ShowLayers();
                text = $"Tela: {ShownName()}";
            }
        }
        Notify(text);
        _renderWake.Set();
    }

    // Tecla U: liga/desliga a melhoria do I na base escolhida.
    private void ToggleAi()
    {
        string text;
        lock (_gpuLock)
        lock (_lock)
        {
            var frame = ScreenFrame();
            var ai = _showComfy ? _aiComfy : _aiOriginal;
            if (frame is null || ai is null || _stillSerial != frame.Serial) text = "Esta imagem ainda não tem melhoria: aperte I";
            else
            {
                _showAi = !_showAi;
                ShowLayers();
                text = $"Melhoria (I): {(_showAi ? "ligada" : "desligada")} · tela: {ShownName()}";
            }
        }
        Notify(text);
        _renderWake.Set();
    }

    // Tecla I: melhora com a IA de fotos a base escolhida (original ou a do ComfyUI). Já melhorada com este modelo: só liga.
    private void EnhanceShown()
    {
        if (NeedNvidiaPack()) return; // sem as bibliotecas da NVIDIA: pergunta se baixa (PlayerNvidia)
        int model = _prefs.PhotoAi;
        if (model == 0)
        {
            Notify("Escolha a IA em Ajustes → Fotos (o I melhora a imagem da tela)");
            return;
        }
        if (!PhotoAi.Available(model))
        {
            Notify("O arquivo desta IA não está na pasta Models\\photo");
            return;
        }
        bool video;
        lock (_lock) video = _file is not null && !_image;
        if (Volatile.Read(ref _stillJobs) > 0 || Interlocked.Read(ref _photoAiSerial) != -1)
        {
            Notify("Aguarde: ainda tem uma IA trabalhando nesta imagem");
            return;
        }
        byte[] pixels;
        int w, h;
        long serial;
        double time;
        Frame? comfyBase;
        lock (_gpuLock)
        lock (_lock)
        {
            var frame = ScreenFrame();
            if (frame is null)
            {
                Notify(video ? "Pause o vídeo e aperte I para melhorar o quadro com a IA" : "Abra uma foto para melhorar");
                return;
            }
            LayersFor(frame);
            comfyBase = _showComfy ? _comfyLayer : null;
            var done = comfyBase is not null ? _aiComfy : _aiOriginal;
            int doneModel = comfyBase is not null ? _aiComfyModel : _aiOriginalModel;
            if (done is not null && doneModel == model)
            {
                _showAi = true;
                ShowLayers();
                Notify($"Melhoria ligada (já estava pronta) · tela: {ShownName()}");
                _renderWake.Set();
                return;
            }
            var source = comfyBase ?? frame;
            if (source.Texture.Description.Format is not (Vortice.DXGI.Format.B8G8R8A8_UNorm or Vortice.DXGI.Format.B8G8R8X8_UNorm))
            {
                Notify("Este vídeo usa um formato de cor que a IA de fotos ainda não lê");
                return;
            }
            (w, h, serial, time) = (source.Width, source.Height, frame.Serial, frame.FileTime);
            pixels = ReadPixels(source.Texture, w, h);
        }
        PauseEffectsForAi();
        EnhancePixels(model, pixels, w, h, video, serial, comfyBase);
    }

    // I: enquanto a IA de fotos trabalha, os efeitos do player saem (como no B) e voltam quando ela termina. Já sem
    // efeitos (B apertado antes): fica como está. Thread da janela.
    private void PauseEffectsForAi()
    {
        if (_settings.Bypass) return;
        (_settings.Bypass, _bypassForAi) = (true, true);
        _panel?.Refresh();
        OnSettingsChanged();
    }

    // A IA terminou (ou falhou, ou foi cancelada): os efeitos voltam — se foi o I que tirou e ninguém mexeu no B depois.
    private void RestoreEffectsAfterAi()
    {
        if (!_bypassForAi) return;
        _bypassForAi = false;
        if (!_settings.Bypass) return;
        _settings.Bypass = false;
        _panel?.Refresh();
        OnSettingsChanged();
    }

    // A IA de fotos nos pixels da base; o resultado vira a melhorada dela (se a tela ainda for a mesma e a base do
    // ComfyUI, se era ela, não tiver sido trocada por um G novo).
    private void EnhancePixels(int model, byte[] pixels, int w, int h, bool video, long serial, Frame? comfyBase)
    {
        bool half = _settings.PhotoFullRes && Math.Max(w, h) > PhotoAiMaxSide;
        int limit = _settings.PhotoFullRes ? PhotoAiFullSide : PhotoAiMaxSide;
        if (Math.Max(w, h) > limit) (pixels, w, h) = Shrink(pixels, w, h, limit);
        int scale = half ? PhotoAi.Scale / 2 : PhotoAi.Scale;
        bool clean = PhotoAi.Cleans(model, _settings.PhotoFaceClean);
        _photoJob?.Cancel();
        var job = _photoJob = new CancellationTokenSource();
        var token = job.Token;
        string name = PhotoAi.Models[model].Name, what = comfyBase is not null ? "a imagem do ComfyUI" : video ? "o quadro" : "a foto";
        if (video) Interlocked.Increment(ref _stillJobs);
        else Interlocked.Exchange(ref _photoAiSerial, serial);
        var thread = new Thread(() =>
        {
            var clock = Stopwatch.StartNew();
            try
            {
                string preparing = PhotoAi.Loaded(model, clean) ? "" : " (preparando a IA para a placa: na 1ª vez de cada modo leva uns minutos)";
                lock (_lock) _photoStatus = $"{name}{(clean ? " com limpeza" : "")}: melhorando {what} {w}x{h} → {w * scale}x{h * scale}...{preparing}";
                Notify(preparing.Length > 0 ? $"IA de fotos ({name}): preparando — na 1ª vez leva uns minutos" : $"IA de fotos ({name}): melhorando {what}...");
                double shown = -1;
                var output = PhotoAi.Upscale(model, pixels, w, h, p =>
                {
                    if (p - shown < 0.05 && p < 1) return;
                    shown = p;
                    double left = p > 0.02 ? clock.Elapsed.TotalSeconds * (1 - p) / p : -1;
                    string text = $"IA de fotos ({name}): {p * 100:0}%" + (left >= 0 ? $" · falta ~{Math.Ceiling(left):0} s" : "");
                    lock (_lock) _photoStatus = text;
                    if (!token.IsCancellationRequested) Notify(text);
                }, token, half, clean);
                if (token.IsCancellationRequested) return;
                int ow = w * scale, oh = h * scale;
                var layer = NewLayer(Detailed(output, ow, oh), ow, oh, comfyBase?.Baked == true);
                layer.Raw = (output, ow, oh);
                bool used = false;
                lock (_gpuLock)
                lock (_lock)
                {
                    var frame = ScreenFrame();
                    if (frame is not null && frame.Serial == serial && (comfyBase is null || comfyBase == _comfyLayer))
                    {
                        LayersFor(frame);
                        layer.Serial = ++_nextSerial;
                        if (comfyBase is not null)
                        {
                            DisposeLayer(ref _aiComfy);
                            (_aiComfy, _aiComfyModel, _showComfy) = (layer, model, true);
                        }
                        else
                        {
                            DisposeLayer(ref _aiOriginal);
                            (_aiOriginal, _aiOriginalModel, _showComfy) = (layer, model, false);
                        }
                        _showAi = true;
                        ShowLayers();
                        _photoStatus = $"{name}: {what} melhorada para {ow}x{oh} em {clock.Elapsed.TotalSeconds:0} s. U liga/desliga; Ctrl+S salva.";
                        used = true;
                    }
                }
                if (!used)
                {
                    Frame? unused = layer;
                    DisposeLayer(ref unused);
                    return;
                }
                Notify($"Melhorada pela IA ({name}) · U liga/desliga · Ctrl+S salva");
                Log.Write($"Fotos: {what} ampliada pela IA {name} para {ow}x{oh} em {clock.Elapsed.TotalSeconds:0.0} s.");
                _renderWake.Set();
            }
            catch (OperationCanceledException) { }
            catch (Exception error)
            {
                Log.Write($"Fotos: a IA falhou — {error.Message}");
                lock (_lock) _photoStatus = "A IA de fotos falhou: " + error.Message.Split('\n')[0];
                if (!token.IsCancellationRequested) Notify($"A IA de fotos falhou ({what} continua como estava)");
            }
            finally
            {
                if (video) Interlocked.Decrement(ref _stillJobs);
                else Interlocked.CompareExchange(ref _photoAiSerial, -1, serial);
                // Os efeitos voltam pela thread da janela (o painel de ajustes é dela).
                _uiQueue.Enqueue((_ => RestoreEffectsAfterAi(), null));
                PlayerNative.PostMessage(Hwnd, WM_RUN, IntPtr.Zero, IntPtr.Zero);
            }
        }) { IsBackground = true, Priority = ThreadPriority.BelowNormal, Name = "Fotos: IA na imagem da tela" };
        thread.Start();
    }

    // Pausado com uma versão guardada (ou sendo feita): o clique no vídeo não dá play — o play descarta as versões, e
    // é fácil clicar só para "ver" o resultado. Espaço e o botão de play continuam valendo.
    private bool StillGuard()
    {
        lock (_lock) return !_playing && (HasLayers || Volatile.Read(ref _stillJobs) > 0 || (_activeTab is { } tab && ComfyJobOf(tab) is not null)); // o ComfyUI desta aba: o quadro fica
    }

    // Com _lock e _gpuLock (ou sem ninguém desenhando): descarta todas as versões.
    private void DisposeStill()
    {
        DisposeLayer(ref _comfyLayer);
        DisposeLayer(ref _aiOriginal);
        DisposeLayer(ref _aiComfy);
        foreach (var (_, toned) in _tone) DisposeFrame(toned.layer); // a do quadro original
        _tone.Clear();
        (_still, _stillShowAi, _showComfy, _showAi, _showTone, _stillSerial) = (null, false, false, false, false, -1);
        _cropBaked = FullCrop; // o corte era da resposta do ComfyUI
        if (_image && _queue.FirstOrDefault(f => f.Image) is { } photo) (_videoW, _videoH) = (photo.Width, photo.Height);
    }

    // A ampliada com o realce de textura e o grão escolhidos.
    private byte[] Detailed(byte[] raw, int w, int h)
    {
        var (texture, grain, skin) = (_settings.PhotoTexture, _settings.PhotoGrain, _settings.PhotoSkin);
        lock (_lock) _detailShown = (texture, grain, skin);
        return PhotoDetail.Apply(raw, w, h, texture, grain, skin);
    }

    // Mexeu no realce ou no grão: refaz as melhoradas a partir da saída pura da IA. Só o último pedido conta
    // (arrastando o controle, os do meio são descartados).
    private void PhotoDetailChanged()
    {
        lock (_lock)
            if (_detailShown == (_settings.PhotoTexture, _settings.PhotoGrain, _settings.PhotoSkin)) return;
        _detailJob?.Cancel();
        var job = _detailJob = new CancellationTokenSource();
        var token = job.Token;
        Task.Run(async () =>
        {
            await Task.Delay(150, token); // espera o controle parar
            Frame?[] layers;
            lock (_lock) layers = [_aiOriginal, _aiComfy];
            foreach (var target in layers)
            {
                if (target?.Raw is not { } r || token.IsCancellationRequested) continue;
                var pixels = Detailed(r.pixels, r.w, r.h);
                if (token.IsCancellationRequested) return;
                var texture = ImageFile.CreateTexture(_gpu.Device, pixels, r.w, r.h);
                var view = _gpu.Device.CreateShaderResourceView(texture);
                bool used = false;
                lock (_gpuLock)
                lock (_lock)
                {
                    if ((target == _aiOriginal || target == _aiComfy) && !token.IsCancellationRequested)
                    {
                        target.View.Dispose();
                        target.Texture.Dispose();
                        (target.Texture, target.View) = (texture, view);
                        target.ProcessedKey = "";
                        _dirty = true;
                        used = true;
                    }
                }
                if (!used)
                {
                    view.Dispose();
                    texture.Dispose();
                }
            }
            _renderWake.Set();
        }, token);
    }

    // Com o lock da GPU: os pixels BGRA de uma textura.
    private byte[] ReadPixels(ID3D11Texture2D texture, int w, int h)
    {
        using var staging = _gpu.Device.CreateTexture2D(new Texture2DDescription(texture.Description.Format, (uint)w, (uint)h, 1, 1,
            BindFlags.None, ResourceUsage.Staging, CpuAccessFlags.Read));
        _gpu.Context.CopySubresourceRegion(staging, 0, 0, 0, 0, texture, 0, new Vortice.Mathematics.Box(0, 0, 0, w, h, 1));
        var map = _gpu.Context.Map(staging, 0, MapMode.Read);
        var pixels = new byte[(long)w * h * 4];
        for (int y = 0; y < h; y++) System.Runtime.InteropServices.Marshal.Copy(map.DataPointer + (nint)((long)y * map.RowPitch), pixels, y * w * 4, w * 4);
        _gpu.Context.Unmap(staging, 0);
        for (int i = 3; i < pixels.Length; i += 4) pixels[i] = 255; // quadro de vídeo: opaco
        return pixels;
    }

    // Reduz pela média da área (vídeo 4K → até "max" de lado antes da IA).
    private static (byte[] pixels, int w, int h) Shrink(byte[] src, int w, int h, int max)
    {
        double scale = (double)max / Math.Max(w, h);
        int nw = Math.Max(1, (int)(w * scale)), nh = Math.Max(1, (int)(h * scale));
        var dst = new byte[(long)nw * nh * 4];
        Parallel.For(0, nh, y =>
        {
            int y0 = y * h / nh, y1 = Math.Max(y0 + 1, (y + 1) * h / nh);
            for (int x = 0; x < nw; x++)
            {
                int x0 = x * w / nw, x1 = Math.Max(x0 + 1, (x + 1) * w / nw);
                int b = 0, g = 0, r = 0, n = 0;
                for (int sy = y0; sy < y1; sy++)
                for (int sx = x0; sx < x1; sx++)
                {
                    int i = (sy * w + sx) * 4;
                    b += src[i]; g += src[i + 1]; r += src[i + 2]; n++;
                }
                int o = (y * nw + x) * 4;
                (dst[o], dst[o + 1], dst[o + 2], dst[o + 3]) = ((byte)(b / n), (byte)(g / n), (byte)(r / n), 255);
            }
        });
        return (dst, nw, nh);
    }

    // Ctrl+S: salva a versão da tela (do ComfyUI e/ou melhorada) — "Salvar como", na pasta da foto (ou em
    // Imagens\MK Vision, para fotos da internet), com o nome dela + a versão ("(ComfyUI)", "(IA 4x)"...). PNG (sem
    // perda, o padrão) ou JPG (menor). No vídeo: com o nome do vídeo e o tempo do quadro.
    private void SavePhotoAi()
    {
        byte[]? pixels = null;
        int w = 0, h = 0;
        string tag = "";
        string? stillName = null;
        bool video;
        // O corte (F4) da versão salva, com o giro e o espelho em que foi feito (ele é da imagem da tela).
        (double x, double y, double w, double h) crop;
        int rotation;
        (float x, float y) flip;
        lock (_gpuLock)
        lock (_lock)
        {
            video = _file is not null && !_image;
            (crop, rotation, flip) = (_crop, _rotation, (_flipH ? -1 : 1, _flipV ? -1 : 1));
            var frame = ScreenFrame();
            if (frame is not null && _still is { } still && _stillShowAi && _stillSerial == frame.Serial)
            {
                (w, h) = (still.Width, still.Height);
                pixels = ReadPixels(still.Texture, w, h);
                if (still.Baked) (crop, rotation, flip) = (_cropBaked, 0, (1, 1));
                tag = ShownName();
                if (video)
                {
                    var t = TimeSpan.FromSeconds(_stillTime);
                    stillName = $"{Path.GetFileNameWithoutExtension(_fileName)} ({(t.TotalHours >= 1 ? $"{(int)t.TotalHours}h" : "")}{t.Minutes}m{t.Seconds:00}s)";
                }
            }
            else
            {
                // Sem versão da IA/ComfyUI: o quadro da tela (vídeo, tocando ou pausado) ou a foto como é.
                var shownFrame = video ? (_queue.Count > 0 ? _queue[0] : null) : frame;
                if (shownFrame is not null &&
                    shownFrame.Texture.Description.Format is Vortice.DXGI.Format.B8G8R8A8_UNorm or Vortice.DXGI.Format.B8G8R8X8_UNorm)
                {
                    (w, h) = (shownFrame.Width, shownFrame.Height);
                    pixels = ReadPixels(shownFrame.Texture, w, h);
                    tag = video ? "quadro" : "cópia";
                    if (video)
                    {
                        var t = TimeSpan.FromSeconds(shownFrame.FileTime);
                        stillName = $"{Path.GetFileNameWithoutExtension(_fileName)} ({(t.TotalHours >= 1 ? $"{(int)t.TotalHours}h" : "")}{t.Minutes}m{t.Seconds:00}s)";
                    }
                }
            }
        }
        if (pixels is null)
        {
            Notify(video ? "Este vídeo usa um formato de cor que ainda não dá para salvar como imagem" : "Abra uma foto ou um vídeo para salvar");
            return;
        }
        if (BurnAnnotations(pixels, w, h)) tag += ", anotada"; // os traços do F3 entram na imagem salva
        if (CropPixels(pixels, w, h, crop, rotation, flip) is { } cut) // e só a parte cortada (F4)
        {
            (pixels, w, h) = cut;
            tag += ", cortada";
        }
        string source, shown;
        lock (_lock) (source, shown) = (_file is not null ? _file.FilePath : _photoPath, stillName ?? _fileName);
        bool local = File.Exists(source) && !source.StartsWith(WebCache, StringComparison.OrdinalIgnoreCase) &&
                     !source.StartsWith(ComfyVideoFolder, StringComparison.OrdinalIgnoreCase);
        string folder = SaveFolder(local ? Path.GetDirectoryName(source)! :
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "MK Vision"));
        string baseName = string.Concat((stillName ?? Path.GetFileNameWithoutExtension(shown)).Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        if (baseName.Length == 0) baseName = "foto";
        var thread = new Thread(() =>
        {
            Directory.CreateDirectory(folder);
            string? path = AskSavePath(folder, $"{baseName} ({tag}).png");
            if (path is null) return;
            RememberSaveFolder(path);
            try
            {
                Notify("Salvando...");
                ImageFile.Save(path, pixels, w, h);
                long size = new FileInfo(path).Length;
                Notify($"Salva: {Path.GetFileName(path)} ({size / 1048576.0:0.0} MB, {w}x{h})");
                Log.Write($"Fotos: salva a versão \"{tag}\" ({w}x{h}, {size / 1048576.0:0.0} MB).");
            }
            catch (Exception error)
            {
                Notify("Não consegui salvar: " + error.Message);
                Log.Write($"Fotos: não consegui salvar — {error.Message}");
            }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
    }

    // Pasta onde o "Salvar como" (Ctrl+S, Shift+S) abre: a última onde você salvou, foto ou vídeo; se ela não existe
    // mais (apagada, pendrive tirado) ou nunca salvou, a de sempre ("normal": a do arquivo, ou Imagens/Vídeos\MK Vision).
    private string SaveFolder(string normal) =>
        _prefs.LastSaveFolder is { Length: > 0 } last && Directory.Exists(last) ? last : normal;

    // Salvou (thread do "Salvar como"): a pasta fica para a próxima vez. Os ajustes são gravados na thread da janela.
    private void RememberSaveFolder(string path)
    {
        if (Path.GetDirectoryName(path) is not { Length: > 0 } folder) return;
        PostUi(() =>
        {
            _prefs.LastSaveFolder = folder;
            _prefs.Save();
        });
    }

    public string TestSaveFolder(string normal) => SaveFolder(normal);
    public string TestLastSaveFolder => _prefs.LastSaveFolder;
    public string TestOsdText { get { lock (_lock) return _osdText; } }

    // "Salvar como": devolve o caminho escolhido (com a extensão do tipo escolhido) ou null.
    private string? AskSavePath(string folder, string name)
    {
        string? test = Environment.GetEnvironmentVariable("MKV_SAVE_AS"); // autoteste: sem janela
        if (test is not null) return test;
        const int max = 2048;
        var buffer = System.Runtime.InteropServices.Marshal.AllocHGlobal(max * 2);
        try
        {
            var chars = (name + "\0").ToCharArray();
            System.Runtime.InteropServices.Marshal.Copy(chars, 0, buffer, chars.Length);
            var dialog = new PlayerNative.OPENFILENAME
            {
                lStructSize = System.Runtime.InteropServices.Marshal.SizeOf<PlayerNative.OPENFILENAME>(),
                hwndOwner = Hwnd,
                lpstrFilter = "PNG (sem perda, maior)\0*.png\0JPG (menor)\0*.jpg\0\0",
                nFilterIndex = 1,
                lpstrFile = buffer,
                nMaxFile = max,
                lpstrInitialDir = folder,
                lpstrTitle = "MK Vision — salvar a foto melhorada pela IA",
                lpstrDefExt = "png",
                Flags = PlayerNative.OFN_EXPLORER | PlayerNative.OFN_OVERWRITEPROMPT | PlayerNative.OFN_PATHMUSTEXIST | PlayerNative.OFN_NOCHANGEDIR
            };
            if (!PlayerNative.GetSaveFileName(ref dialog)) return null;
            string path = System.Runtime.InteropServices.Marshal.PtrToStringUni(buffer)!;
            string wanted = dialog.nFilterIndex == 2 ? ".jpg" : ".png";
            string ext = Path.GetExtension(path).ToLowerInvariant();
            if (ext is not (".jpg" or ".jpeg" or ".png")) path += wanted;
            else if (ext != wanted && !(wanted == ".jpg" && ext == ".jpeg")) path = Path.ChangeExtension(path, wanted);
            return path;
        }
        finally { System.Runtime.InteropServices.Marshal.FreeHGlobal(buffer); }
    }

    // Vídeos do ComfyUI (Wan, tecla G): temporários, apagados ao fechar o app (até o Shift+S salvar onde você quiser).
    private static readonly string ComfyVideoFolder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MKVisionDesktop", "comfy-videos");

    // Shift+S: salva o vídeo aberto inteiro — "Salvar como" (vídeo do ComfyUI ou da internet: em Vídeos\MK Vision; o
    // resto: na pasta dele). O Ctrl+S salva só o quadro da tela.
    private void SaveVideo()
    {
        string? source, name;
        lock (_lock) (source, name) = (_file is not null && !_image ? _file.FilePath : null, _fileName);
        if (source is null || !File.Exists(source))
        {
            Notify("Abra um vídeo para salvar (Shift+S salva o vídeo; Ctrl+S, o quadro)");
            return;
        }
        bool temp = source.StartsWith(ComfyVideoFolder, StringComparison.OrdinalIgnoreCase) || source.StartsWith(WebCache, StringComparison.OrdinalIgnoreCase);
        string folder = SaveFolder(temp ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "MK Vision") : Path.GetDirectoryName(source)!);
        string ext = Path.GetExtension(source).ToLowerInvariant() is { Length: > 0 } e ? e : ".mp4";
        string baseName = string.Concat(Path.GetFileNameWithoutExtension(name ?? "video").Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        var thread = new Thread(() =>
        {
            Directory.CreateDirectory(folder);
            string? path = Environment.GetEnvironmentVariable("MKV_SAVE_AS") ?? AskVideoPath(folder, baseName + ext, ext);
            if (path is null) return;
            RememberSaveFolder(path);
            try
            {
                Notify("Salvando o vídeo...");
                File.Copy(source, path, overwrite: true);
                Notify($"Vídeo salvo: {Path.GetFileName(path)} ({new FileInfo(path).Length / 1048576.0:0.0} MB)");
                Log.Write($"Player: vídeo salvo ({new FileInfo(path).Length / 1048576.0:0.0} MB).");
            }
            catch (Exception error)
            {
                Notify("Não consegui salvar o vídeo: " + error.Message);
                Log.Write($"Player: não consegui salvar o vídeo — {error.Message}");
            }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
    }

    // "Salvar como" para vídeo (a extensão do arquivo aberto).
    private string? AskVideoPath(string folder, string name, string ext)
    {
        const int max = 2048;
        var buffer = System.Runtime.InteropServices.Marshal.AllocHGlobal(max * 2);
        try
        {
            var chars = (name + "\0").ToCharArray();
            System.Runtime.InteropServices.Marshal.Copy(chars, 0, buffer, chars.Length);
            var dialog = new PlayerNative.OPENFILENAME
            {
                lStructSize = System.Runtime.InteropServices.Marshal.SizeOf<PlayerNative.OPENFILENAME>(),
                hwndOwner = Hwnd,
                lpstrFilter = $"Vídeo ({ext})\0*{ext}\0\0",
                nFilterIndex = 1,
                lpstrFile = buffer,
                nMaxFile = max,
                lpstrInitialDir = folder,
                lpstrTitle = "MK Vision — salvar o vídeo",
                lpstrDefExt = ext.TrimStart('.'),
                Flags = PlayerNative.OFN_EXPLORER | PlayerNative.OFN_OVERWRITEPROMPT | PlayerNative.OFN_PATHMUSTEXIST | PlayerNative.OFN_NOCHANGEDIR
            };
            if (!PlayerNative.GetSaveFileName(ref dialog)) return null;
            string path = System.Runtime.InteropServices.Marshal.PtrToStringUni(buffer)!;
            return Path.GetExtension(path).Equals(ext, StringComparison.OrdinalIgnoreCase) ? path : path + ext;
        }
        finally { System.Runtime.InteropServices.Marshal.FreeHGlobal(buffer); }
    }

    // Trocou de modelo no painel: as melhoradas (do modelo antigo) saem; a base da tela (original ou ComfyUI) fica.
    // O I melhora de novo com o modelo novo.
    private void PhotoAiChanged()
    {
        _photoJob?.Cancel();
        bool had;
        lock (_gpuLock)
        lock (_lock)
        {
            _photoStatus = "";
            had = _aiOriginal is not null || _aiComfy is not null;
            DisposeLayer(ref _aiOriginal);
            DisposeLayer(ref _aiComfy);
            _showAi = false;
            ShowLayers();
        }
        _renderWake.Set();
        if (_prefs.PhotoAi != 0) Notify($"IA de fotos: {PhotoAi.Models[_prefs.PhotoAi].Name}" + (had ? " · a melhoria anterior saiu; aperte I" : " · aperte I para melhorar"));
    }
}
