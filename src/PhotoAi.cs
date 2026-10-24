using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace MKVision.Desktop;

// IA de fotos (Ajustes → Fotos): amplia a foto 4x com um modelo pesado, uma vez por foto (não é tempo real).
// Três modelos (créditos em Models\photo\CREDITOS.txt): Real-ESRGAN (natural), UltraSharp (nítida) e RealWebPhoto DAT2
// (máxima, a mais lenta). Roda na placa pelo TensorRT (o caminho comum, CUDA, deixava partes dos modelos de
// "atenção" no processador: dezenas de segundos por pedaço), em pedaços sempre do mesmo tamanho (256 + borda de contexto de 16): um motor só,
// otimizado uma vez e guardado. O resultado fica só na memória.
internal static class PhotoAi
{
    // Tile: tamanho do pedaço que vai à IA (os pesados em pedaços menores: o TensorRT pode ficar sem memória ao
    // montar). Fp16: meia precisão no TensorRT (mais rápida; modelos de "atenção" podem estourar nela).
    // Ensemble: cada pedaço também espelhado, e a média das duas (Máxima+; o dobro do tempo). Face: treinado só com
    // rostos limpos (pode passar antes pela limpeza da compressão).
    public sealed record Model(string Name, string File, string Label, int Tile = 256, bool Fp16 = true, bool Ensemble = false, bool Face = false);

    // 0 = desligado.
    public static readonly Model[] Models =
    [
        new("", "", "Desligada"),
        new("Real-ESRGAN", "RealESRGAN_x4plus.onnx", "Natural"),
        new("UltraSharp", "4x-UltraSharp.onnx", "Nítida"),
        // O Nomos HAT-L (só existe em fp16) devolvia valores inválidos — foto preta; este, do mesmo autor, é de 2024,
        // feito para fotos da internet (compressão JPEG/WebP, redução, desfoque, ruído) e em precisão completa.
        new("RealWebPhoto DAT2", "4xRealWebPhoto_v4_dat2.onnx", "Máxima", Tile: 128, Fp16: Fp16Dat2),
        // Treinados só com rostos em close (FaceUp): pele, cílios e cabelo; em outras fotos podem sair piores.
        new("FaceUp DAT", "4xFaceUpDAT.onnx", "Rosto", Tile: 128, Fp16: false, Face: true), // em fp16 estoura (valores inválidos)
        new("FaceUp Sharp DAT", "4xFaceUpSharpDAT.onnx", "Rosto nítido", Tile: 128, Fp16: false, Face: true),
        // A Máxima, cada pedaço também espelhado (média das duas): menos defeitos inventados, o dobro do tempo.
        new("RealWebPhoto DAT2 (dupla)", "4xRealWebPhoto_v4_dat2.onnx", "Máxima+", Tile: 128, Fp16: Fp16Dat2, Ensemble: true),
    ];

    public const int Scale = 4;
    private static readonly bool Fp16Dat2 = Environment.GetEnvironmentVariable("MKV_DAT2_FP16") != "0";
    private const int Pad = 16; // borda de contexto em volta de cada pedaço (o que vai à IA: pedaço + 2x borda, múltiplo de 16 para o HAT)
    private static readonly string Folder = Path.Combine(AppContext.BaseDirectory, "Models", "photo");
    private static readonly object Lock = new();
    // Até duas sessões carregadas (por arquivo: a Máxima e a Máxima+ usam a mesma): o rosto com a limpeza usa a Máxima
    // e a de rosto em toda foto — trocando uma pela outra, cada foto perderia ~45 s carregando. A menos usada sai.
    private static readonly List<(string file, InferenceSession session)> _sessions = [];
    private const int MaxSessions = 2;

    // Limpeza da compressão antes das IAs de rosto: a Máxima (treinada com fotos de internet comprimidas), reduzida
    // de volta ao tamanho original. Direto, as de rosto viam os blocos do JPEG como rugas (riscos na bochecha).
    private const int CleanModel = 3;
    private const double CleanShare = 0.45; // parte do tempo que a limpeza leva (para a barra de progresso)

    public static bool Cleans(int model, bool clean) => clean && Models[model].Face && Available(CleanModel);

    // Já carregado (o primeiro uso de cada modelo monta o motor do TensorRT: alguns minutos, uma vez só; depois, segundos).
    public static bool Loaded(int model, bool clean = false)
    {
        lock (_sessions)
            return _sessions.Any(s => s.file == Models[model].File) && (!Cleans(model, clean) || _sessions.Any(s => s.file == Models[CleanModel].File));
    }

    // Descarrega as IAs carregadas (libera a memória da placa, ex.: para o ComfyUI na tecla G). O próximo uso carrega
    // de novo (do cache do TensorRT: segundos). Só com nenhuma foto sendo ampliada. false = não havia nada.
    public static bool Release()
    {
        lock (Lock)
        lock (_sessions)
        {
            if (_sessions.Count == 0) return false;
            foreach (var (_, session) in _sessions) session.Dispose();
            _sessions.Clear();
        }
        Log.Write("Fotos: IAs descarregadas (memória da placa livre).");
        return true;
    }

    public static bool Available(int model) => model > 0 && model < Models.Length && File.Exists(Path.Combine(Folder, Models[model].File));

    // Amplia "bgra" (w x h, pré-multiplicado) 4x — ou 2x com "half" (fotos grandes em resolução cheia: a IA roda na
    // foto inteira e cada 2x2 da saída vira um). "progress" recebe 0–1. Uma foto de cada vez. "clean": nas IAs de
    // rosto, passa antes pela limpeza da compressão (o dobro do tempo, mais ou menos).
    public static byte[] Upscale(int model, byte[] bgra, int w, int h, Action<double>? progress, CancellationToken token, bool half = false, bool clean = false)
    {
        if (!Cleans(model, clean)) return UpscaleOne(model, bgra, w, h, progress, token, half);
        lock (Lock)
        {
            int factor = half ? Scale / 2 : Scale;
            var cleaned = UpscaleOne(CleanModel, bgra, w, h, progress is null ? null : p => progress(p * CleanShare), token, half);
            cleaned = ShrinkBy(cleaned, w * factor, h * factor, factor);
            return UpscaleOne(model, cleaned, w, h, progress is null ? null : p => progress(CleanShare + p * (1 - CleanShare)), token, half);
        }
    }

    //
    // Pedaços: cada um vai à IA com uma borda de contexto de 16 px; as bordas de pedaços vizinhos se sobrepõem e são
    // misturadas em rampa (o peso de um cai enquanto o do vizinho sobe, somando sempre 1) — cortados e colados lado a
    // lado, a junção dava um degrau ~2x a diferença normal entre pixels (uma grade sutil em pele lisa). Sem custo a
    // mais: a borda de contexto já ia à IA e era descartada. A saída se acumula numa faixa de linhas que avança com
    // os pedaços (a memória não cresce com fotos grandes).
    // Modelo com "Ensemble" (Máxima+): cada pedaço vai também espelhado, e a média das duas respostas reduz os
    // defeitos que a IA inventa (o dobro do tempo).
    private static byte[] UpscaleOne(int model, byte[] bgra, int w, int h, Action<double>? progress, CancellationToken token, bool half)
    {
        lock (Lock)
        {
            var session = Session(model);
            string inputName = session.InputMetadata.Keys.First();
            bool fp16 = session.InputMetadata[inputName].ElementDataType == TensorElementType.Float16;
            int Tile = Models[model].Tile, Window = Tile + 2 * Pad, div = half ? 2 : 1;
            int ow = w * Scale / div, oh = h * Scale / div, ramp = 2 * Pad * Scale, span = Window * Scale;
            var output = new byte[(long)ow * oh * 4];
            int tilesX = (w + Tile - 1) / Tile, tilesY = (h + Tile - 1) / Tile, done = 0;
            int bandRows = span / div + 2, bandStart = 0;
            var band = new float[(long)bandRows * ow * 3];
            // Peso de uma posição da janela (0…span) ao longo de um eixo: rampa nas bordas que têm vizinho.
            bool hardCut = Environment.GetEnvironmentVariable("MKV_NO_BLEND") == "1"; // autoteste: o corte seco de antes
            float Weight(int p, bool before, bool after) => hardCut
                ? (before && p < Pad * Scale || after && p >= span - Pad * Scale ? 0f : 1f)
                : (before ? Math.Clamp((p + 0.5f) / ramp, 0f, 1f) : 1f) * (after ? Math.Clamp((span - p - 0.5f) / ramp, 0f, 1f) : 1f);
            var weightX = new float[span];
            var weightY = new float[span];
            for (int ty = 0; ty < tilesY; ty++)
            {
                for (int p = 0; p < span; p++) weightY[p] = Weight(p, ty > 0, ty < tilesY - 1);
                for (int tx = 0; tx < tilesX; tx++)
                {
                    token.ThrowIfCancellationRequested();
                    for (int p = 0; p < span; p++) weightX[p] = Weight(p, tx > 0, tx < tilesX - 1);
                    int wx0 = tx * Tile - Pad, wy0 = ty * Tile - Pad;
                    var data = Run(session, inputName, fp16, bgra, w, h, wx0, wy0, Window, flip: false);
                    if (done == 0)
                    {
                        // Primeiro pedaço: valores inválidos (NaN) = o modelo estoura nesta precisão (o HAT-L em fp16 dava a
                        // foto toda preta). Melhor avisar do que trocar a foto por uma preta.
                        int invalid = 0;
                        foreach (float v in data) if (float.IsNaN(v) || float.IsInfinity(v)) invalid++;
                        if (invalid > data.Length / 100) throw new InvalidOperationException($"a IA {Models[model].Name} devolveu valores inválidos nesta placa");
                    }
                    if (Models[model].Ensemble)
                    {
                        var mirrored = Run(session, inputName, fp16, bgra, w, h, wx0, wy0, Window, flip: true);
                        int plane = span * span;
                        for (int c = 0; c < 3; c++)
                        for (int y = 0; y < span; y++)
                        {
                            int row = c * plane + y * span;
                            for (int x = 0; x < span; x++) data[row + x] = 0.5f * (data[row + x] + mirrored[row + span - 1 - x]);
                        }
                    }
                    // Acumula na faixa (na resolução final), com o peso da rampa.
                    int planeSize = span * span;
                    for (int y = 0; y < span; y++)
                    {
                        int Y = wy0 * Scale + y;
                        if (Y < 0 || Y >= h * Scale) continue;
                        int fy = Y / div - bandStart;
                        if (fy < 0 || fy >= bandRows) continue;
                        float wy = weightY[y] / (div * div);
                        long rowBase = (long)fy * ow * 3;
                        for (int x = 0; x < span; x++)
                        {
                            int X = wx0 * Scale + x;
                            if (X < 0 || X >= w * Scale) continue;
                            float wgt = wy * weightX[x];
                            long b = rowBase + (X / div) * 3;
                            int s = y * span + x;
                            band[b] += wgt * data[2 * planeSize + s];     // B
                            band[b + 1] += wgt * data[planeSize + s];     // G
                            band[b + 2] += wgt * data[s];                 // R
                        }
                    }
                    progress?.Invoke(++done / (double)(tilesX * tilesY));
                }
                // Linhas que nenhum pedaço seguinte toca: prontas — vão para a saída e a faixa avança.
                int ready = ty == tilesY - 1 ? oh : Math.Min(oh, ((ty + 1) * Tile - Pad) * Scale / div);
                for (int fy = bandStart; fy < ready; fy++)
                {
                    long src = (long)(fy - bandStart) * ow * 3, dst = (long)fy * ow * 4;
                    for (int x = 0; x < ow; x++)
                    {
                        output[dst + x * 4] = ToByte(band[src + x * 3]);
                        output[dst + x * 4 + 1] = ToByte(band[src + x * 3 + 1]);
                        output[dst + x * 4 + 2] = ToByte(band[src + x * 3 + 2]);
                        output[dst + x * 4 + 3] = 255;
                    }
                }
                int shift = ready - bandStart;
                if (shift > 0)
                {
                    long keep = (long)(bandRows - shift) * ow * 3;
                    if (keep > 0) Array.Copy(band, (long)shift * ow * 3, band, 0, keep);
                    Array.Clear(band, (int)Math.Max(0, keep), (int)(band.LongLength - Math.Max(0, keep)));
                    bandStart = ready;
                }
            }
            return output;
        }
    }

    // Um pedaço (janela de "window" a partir de x0,y0; fora da foto repete a borda), espelhado se "flip" (quem pede
    // espelhado desfaz). Devolve a saída da IA em planos R, G, B (4x a janela de lado cada).
    private static float[] Run(InferenceSession session, string inputName, bool fp16, byte[] bgra, int w, int h, int x0, int y0, int window, bool flip)
    {
        var input = new float[3 * window * window];
        int plane = window * window;
        for (int y = 0; y < window; y++)
        {
            int sy = Math.Clamp(y0 + y, 0, h - 1);
            for (int x = 0; x < window; x++)
            {
                int sx = Math.Clamp(x0 + (flip ? window - 1 - x : x), 0, w - 1), i = (sy * w + sx) * 4, o = y * window + x;
                input[o] = bgra[i + 2] / 255f;
                input[plane + o] = bgra[i + 1] / 255f;
                input[2 * plane + o] = bgra[i] / 255f;
            }
        }
        long[] shape = [1, 3, window, window];
        using var value = fp16 ? OrtValue.CreateTensorValueFromMemory(input.Select(v => (Float16)v).ToArray(), shape) : OrtValue.CreateTensorValueFromMemory(input, shape);
        // As opções ficam numa variável até o fim: soltas na chamada, o coletor de lixo podia liberá-las com a IA ainda
        // rodando (o programa fechava sozinho, mais vezes na Máxima+, que roda o dobro).
        using var runOptions = new RunOptions();
        using var results = session.Run(runOptions, new Dictionary<string, OrtValue> { [inputName] = value }, session.OutputNames);
        return fp16 ? results[0].GetTensorDataAsSpan<Float16>().ToArray().Select(v => (float)v).ToArray() : results[0].GetTensorDataAsSpan<float>().ToArray();
    }

    private static byte ToByte(float v) => (byte)Math.Clamp((int)MathF.Round(v * 255f), 0, 255);

    // Até duas sessões (cada modelo ocupa memória da placa); uma terceira descarrega a usada há mais tempo.
    private static InferenceSession Session(int model)
    {
        string file = Models[model].File;
        lock (_sessions)
        {
            int index = _sessions.FindIndex(s => s.file == file);
            if (index >= 0)
            {
                var hit = _sessions[index];
                _sessions.RemoveAt(index);
                _sessions.Add(hit);
                return hit.session;
            }
            while (_sessions.Count >= MaxSessions)
            {
                _sessions[0].session.Dispose();
                _sessions.RemoveAt(0);
            }
        }
        Rife.LoadNvidiaLibraries();
        var options = new SessionOptions { GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL };
        string name = Path.GetFileNameWithoutExtension(Models[model].File);
        string cache = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MKVisionDesktop", "trt-cache", "photo-" + name);
        Directory.CreateDirectory(cache);
        int window = Models[model].Tile + 2 * Pad;
        var trt = new OrtTensorRTProviderOptions();
        trt.UpdateOptions(new Dictionary<string, string>
        {
            ["device_id"] = "0",
            ["trt_fp16_enable"] = Models[model].Fp16 ? "1" : "0",
            ["trt_engine_cache_enable"] = "1",
            ["trt_engine_cache_path"] = cache,
            ["trt_timing_cache_enable"] = "1",
            ["trt_timing_cache_path"] = cache,
            ["trt_builder_optimization_level"] = "3",
            ["trt_max_workspace_size"] = (2L << 30).ToString(), // 2 GB para montar (a placa tem 8)
            ["trt_profile_min_shapes"] = $"input:1x3x{window}x{window}",
            ["trt_profile_opt_shapes"] = $"input:1x3x{window}x{window}",
            ["trt_profile_max_shapes"] = $"input:1x3x{window}x{window}"
        });
        if (Environment.GetEnvironmentVariable("MKV_PHOTO_EP") != "cuda") options.AppendExecutionProvider_Tensorrt(trt); // autoteste: só CUDA
        options.AppendExecutionProvider_CUDA(0); // o que o TensorRT não suportar
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var session = new InferenceSession(Path.Combine(Folder, file), options);
        lock (_sessions) _sessions.Add((file, session));
        Log.Write($"Fotos: IA {Models[model].Name} pronta ({clock.Elapsed.TotalSeconds:0.0} s).");
        return session;
    }

    public static string Describe(int model)
    {
        lock (Lock)
        {
            var session = Session(model);
            string Dims(NodeMetadata m) => string.Join("x", m.Dimensions.Select(d => d < 0 ? "?" : d.ToString()));
            return string.Join("; ", session.InputMetadata.Select(i => $"entrada {i.Key} {i.Value.ElementDataType} {Dims(i.Value)}")) + " | " +
                   string.Join("; ", session.OutputMetadata.Select(o => $"saída {o.Key} {o.Value.ElementDataType} {Dims(o.Value)}"));
        }
    }

    // --detailtest <foto> <pasta> [modelo]: amplia com a IA (padrão: Natural) e aplica o realce de textura em 0, 40 e
    // 80 (e 40 com grão 30); mede o tempo do realce numa imagem do tamanho de uma foto de Instagram ampliada.
    public static int DetailTest(string photo, string folder, int model)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        Directory.CreateDirectory(folder);
        var (pixels, w, h) = ImageFile.LoadPixels(photo, 512);
        var raw = Upscale(model, pixels, w, h, null, CancellationToken.None);
        int ow = w * Scale, oh = h * Scale;
        foreach (var (texture, grain) in new[] { (0f, 0f), (0.4f, 0f), (0.8f, 0f), (0.4f, 0.3f) })
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var output = PhotoDetail.Apply(raw, ow, oh, texture, grain);
            Console.WriteLine($"realce {texture * 100:0}, grão {grain * 100:0}: {clock.ElapsedMilliseconds} ms ({ow}x{oh})");
            InterpTest.Png(Path.Combine(folder, $"realce-{texture * 100:0}-grao-{grain * 100:0}.png"), output, ow, 0, 0, ow, oh);
        }
        // Pele × fundo: realce 60 com e sem a máscara de pele.
        InterpTest.Png(Path.Combine(folder, "pele-tudo.png"), PhotoDetail.Apply(raw, ow, oh, 0.6f, 0), ow, 0, 0, ow, oh);
        InterpTest.Png(Path.Combine(folder, "pele-so.png"), PhotoDetail.Apply(raw, ow, oh, 0.6f, 0, skin: true), ow, 0, 0, ow, oh);
        InterpTest.Png(Path.Combine(folder, "pele-ia.png"), raw, ow, 0, 0, ow, oh);
        // O grão sobrevive à redução para a tela? Desvio do brilho numa área lisa, reduzida 4x, com e sem grão.
        var flat = new byte[1024 * 1024 * 4];
        for (int i = 0; i < flat.Length; i += 4) (flat[i], flat[i + 1], flat[i + 2], flat[i + 3]) = (150, 160, 200, 255);
        foreach (float g in new[] { 0f, 0.25f, 0.5f, 1f })
        {
            var grained = PhotoDetail.Apply(flat, 1024, 1024, 0, g);
            double sum = 0, sq = 0;
            int count = 0;
            for (int y = 0; y < 1024; y += 4)
            for (int x = 0; x < 1024; x += 4)
            {
                double l = 0;
                for (int dy = 0; dy < 4; dy++) for (int dx = 0; dx < 4; dx++) l += grained[((y + dy) * 1024 + x + dx) * 4 + 1];
                l /= 16;
                sum += l; sq += l * l; count++;
            }
            double mean = sum / count;
            Console.WriteLine($"grão {g * 100:0}: variação visível na tela (desvio, em níveis de 255) {Math.Sqrt(Math.Max(0, sq / count - mean * mean)):0.00}");
        }
        var big = new byte[4320 * 5400 * 4];
        var c2 = System.Diagnostics.Stopwatch.StartNew();
        PhotoDetail.Apply(big, 4320, 5400, 0.4f, 0.3f);
        Console.WriteLine($"realce numa ampliada de 4320x5400: {c2.ElapsedMilliseconds} ms");
        return 0;
    }

    // --cleantest <foto> <pasta> [modelo]: IA de rosto (padrão: Rosto nítido) direto e com a limpeza da compressão antes.
    public static int CleanTest(string photo, string folder, int model)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        Directory.CreateDirectory(folder);
        var (pixels, w, h) = ImageFile.LoadPixels(photo, 512);
        int ow = w * Scale, oh = h * Scale;
        foreach (bool clean in new[] { false, true })
        {
            Upscale(model, pixels, w, h, null, CancellationToken.None, clean: clean); // carrega
            var clock = System.Diagnostics.Stopwatch.StartNew();
            double last = 0;
            bool ordered = true;
            var output = Upscale(model, pixels, w, h, p => { ordered &= p >= last; last = p; }, CancellationToken.None, clean: clean);
            Console.WriteLine($"{(clean ? "com limpeza" : "direto")}: {clock.Elapsed.TotalSeconds:0.0} s, progresso {(ordered && last == 1 ? "ok" : "fora de ordem")}");
            InterpTest.Png(Path.Combine(folder, (clean ? "limpa" : "direto") + ".png"), output, ow, 0, 0, ow, oh);
        }
        return 0;
    }

    // Reduz "factor" vezes (média de cada bloco factor x factor).
    internal static byte[] ShrinkBy(byte[] src, int w, int h, int factor)
    {
        int ow = w / factor, oh = h / factor;
        var dst = new byte[ow * oh * 4];
        Parallel.For(0, oh, y =>
        {
            for (int x = 0; x < ow; x++)
                for (int c = 0; c < 4; c++)
                {
                    int sum = 0;
                    for (int dy = 0; dy < factor; dy++)
                        for (int dx = 0; dx < factor; dx++) sum += src[((y * factor + dy) * w + x * factor + dx) * 4 + c];
                    dst[(y * ow + x) * 4 + c] = (byte)((sum + factor * factor / 2) / (factor * factor));
                }
        });
        return dst;
    }

    // --photoaitest <foto> <pasta>: cada modelo amplia a foto (reduzida a no máximo 512 de lado); mostra o formato,
    // o tempo e salva o resultado em PNG.
    public static int Test(string photo, string folder)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        Directory.CreateDirectory(folder);
        var (pixels, w, h) = ImageFile.LoadPixels(photo, 512);
        Console.WriteLine($"foto: {w}x{h}");
        InterpTest.Png(Path.Combine(folder, "0-original.png"), pixels, w, 0, 0, w, h);
        for (int model = 1; model < Models.Length; model++)
        {
            if (Environment.GetEnvironmentVariable("MKV_PHOTO_ONLY") is { } only && only != model.ToString()) continue;
            if (!Available(model)) { Console.WriteLine($"{Models[model].Name}: arquivo não encontrado"); continue; }
            try
            {
                Console.WriteLine($"{Models[model].Name}: {Describe(model)}");
                var clock = System.Diagnostics.Stopwatch.StartNew();
                var output = Upscale(model, pixels, w, h, null, CancellationToken.None);
                Console.WriteLine($"  {w}x{h} → {w * Scale}x{h * Scale} em {clock.Elapsed.TotalSeconds:0.0} s (1ª vez: inclui otimizar para a placa)");
                clock.Restart();
                output = Upscale(model, pixels, w, h, null, CancellationToken.None);
                int tile = Models[model].Tile, tiles = ((w + tile - 1) / tile) * ((h + tile - 1) / tile);
                Console.WriteLine($"  de novo: {clock.Elapsed.TotalSeconds:0.0} s ({clock.Elapsed.TotalSeconds / tiles:0.00} s por pedaço de {tile}x{tile}; ~{clock.Elapsed.TotalSeconds / (w * h) * 1080 * 1350:0} s numa foto de 1080x1350)");
                InterpTest.Png(Path.Combine(folder, $"{model}-{Models[model].File.Replace(".onnx", "")}.png"), output, w * Scale, 0, 0, w * Scale, h * Scale);
            }
            catch (Exception error) { Console.WriteLine($"  falhou: {error.Message.Split('\n')[0]}"); }
        }
        return 0;
    }
}
