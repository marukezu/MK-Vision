using Vortice.Direct3D11;
using static MKVision.Desktop.PlayerNative;

namespace MKVision.Desktop;

// Anotações (F3): um painel na lateral esquerda com pincel e retângulo, as cores, três espessuras, desfazer e limpar.
// Desenha-se com o botão esquerdo sobre a imagem (foto ou quadro do vídeo — o vídeo pausa ao abrir o painel). Os
// traços ficam presos à imagem (acompanham zoom, movimento, giro e espelho; a roda continua dando zoom) e entram na
// imagem salva pelo Ctrl+S. Fechar o painel (F3 ou Esc) mantém os traços; outra foto, outro quadro do vídeo ou "Limpar"
// os apagam. Ctrl+Z desfaz o último. Os traços também vão na imagem que o G manda ao ComfyUI; a resposta dele é outra
// versão da imagem: os traços somem dela e voltam ao voltar à original (T). Desenhar na resposta começa traços novos.
// Contornos fechados (retângulo, ou um traço à mão que volta perto do começo) são a área que o ComfyUI deve editar: o
// prompt ganha a instrução (editar só ali e tirar o contorno) e, na resposta, fora da área volta a imagem original,
// sem o desenho (ComfyArea).
internal sealed partial class Player
{
    private static readonly uint[] AnnoColors = [0xFF3B30, 0xFFCC00, 0x34C759, 0x0A84FF, 0xFFFFFF, 0x111111];
    private static readonly string[] AnnoColorNames = ["vermelho", "amarelo", "verde", "azul", "branco", "preto"];
    private static readonly int[] AnnoSizes = [3, 6, 12]; // espessura na tela (px a 96 dpi), no zoom em que foi desenhado
    private const string IconPen = "", IconRectangle = "", IconUndo = "", IconClear = "", IconCloseAnno = "";

    // Com _lock.
    private bool _annotating;
    private int _annoTool, _annoColor, _annoSize = 1, _annoHover = -1; // ferramenta: 0 pincel, 1 retângulo
    private readonly List<AnnoStroke> _strokes = [];
    private AnnoStroke? _drawingStroke;
    private (double x, double y) _rectStart;
    private string _annoKey = "", _annoShownKey = "";      // de que imagem (e versão: "imagem#versão") são os traços / o que está na tela
    private AnnotationLayer.Map? _annoMap;                  // onde a imagem estava no último desenho
    private bool _annoFull;                                 // refazer a camada inteira (desfazer, limpar)
    private (double u0, double v0, double u1, double v1, float width)? _annoDirty;
    private readonly List<(Native.RECT rect, int action)> _annoButtons = [];

    // Thread da tela.
    private AnnotationLayer? _annoLayer;
    private UiCanvas? _annoCanvas;
    private ID3D11ShaderResourceView? _annoToolbar;
    private string _annoToolbarKey = "";

    // Ações dos botões do painel.
    private const int ActPen = 0, ActRect = 1, ActColor = 10, ActSize = 20, ActUndo = 30, ActClear = 31, ActClose = 32;

    // F3: abre/fecha o painel.
    private void ToggleAnnotate()
    {
        bool open, video, playing, media;
        lock (_lock) (open, video, playing, media) = (_annotating, _file is not null && !_image, _playing, _file is not null || _image);
        if (!open && !media)
        {
            Notify("Abra uma foto ou um vídeo para anotar");
            return;
        }
        if (!open && video && playing) TogglePlay(); // anota no quadro parado
        lock (_lock)
        {
            _annotating = !open;
            _drawingStroke = null;
            _annoHover = -1;
            _dirty = true;
        }
        if (!open) ReleaseCapture();
        Notify(open ? "Anotações: painel fechado (F3 abre de novo)" : "Anotações: desenhe com o mouse · Ctrl+Z desfaz · F3 ou Esc fecha");
        _renderWake.Set();
    }

    // Teclas com o painel aberto: Esc fecha, Ctrl+Z desfaz. true = usada.
    private bool AnnotateKey(int key)
    {
        if (key == VK_ESCAPE) { ToggleAnnotate(); return true; }
        if (key == 'Z' && KeyDown(VK_CONTROL)) { UndoStroke(); return true; }
        return false;
    }

    private void UndoStroke()
    {
        lock (_lock)
        {
            if (_strokes.Count == 0) return;
            _strokes.RemoveAt(_strokes.Count - 1);
            (_annoFull, _dirty) = (true, true);
        }
        _renderWake.Set();
    }

    private void ClearStrokes()
    {
        lock (_lock)
        {
            _strokes.Clear();
            _drawingStroke = null;
            (_annoFull, _dirty) = (true, true);
        }
        _renderWake.Set();
    }

    // ---------- Mouse (thread principal) ----------

    // Botão no ponto (com _lock): índice em _annoButtons, ou -1.
    private int AnnoButtonAt(int x, int y) => _annoButtons.FindIndex(b => Inside(b.rect, x, y));

    // Apertou o botão esquerdo: um botão do painel, ou começa um traço sobre a imagem. true = usado.
    private bool AnnotateMouseDown(int x, int y, UiPart part)
    {
        int action = -1;
        lock (_lock)
        {
            if (!_annotating) return false;
            int button = AnnoButtonAt(x, y);
            if (button >= 0) action = _annoButtons[button].action;
            else
            {
                if (part != UiPart.Video || _annoMap is not { } map) return false;
                var (u, v) = map.ToImage(x, y);
                if (_strokes.Count > 0 && _annoKey != _annoShownKey)
                {
                    // Traços de outra versão desta imagem (escondidos): desenhar nesta começa do zero.
                    _strokes.Clear();
                    _annoFull = true;
                }
                if (_strokes.Count == 0) _annoKey = _annoShownKey; // os traços passam a ser desta imagem
                // Espessura escolhida (na tela) → fração da largura da imagem, no zoom de agora.
                float width = (float)(U(AnnoSizes[_annoSize]) / map.Scale / map.Fw);
                _drawingStroke = new AnnoStroke { Color = AnnoColors[_annoColor], Width = width, Rect = _annoTool == 1 };
                _drawingStroke.Points.Add(((float)u, (float)v));
                if (_annoTool == 1) _drawingStroke.Points.AddRange([((float)u, (float)v), ((float)u, (float)v), ((float)u, (float)v)]);
                _rectStart = (x, y);
                _strokes.Add(_drawingStroke);
                MarkDirty(u, v, u, v, width);
            }
        }
        if (action >= 0) AnnotateAction(action);
        else SetCapture(Hwnd);
        _renderWake.Set();
        return true;
    }

    // Mouse andando: continua o traço (ou estica o retângulo); sobre o painel, acende o botão. true = desenhando.
    private bool AnnotateMouseMove(int x, int y)
    {
        lock (_lock)
        {
            if (!_annotating) return false;
            int hover = AnnoButtonAt(x, y);
            if (hover != _annoHover) (_annoHover, _dirty) = (hover, true);
            if (_drawingStroke is not { } stroke || _annoMap is not { } map) return false;
            if (stroke.Rect)
            {
                // Retângulo como você vê na tela (os 4 cantos na imagem: com giro, continua alinhado à tela).
                var (sx, sy) = _rectStart;
                var corners = new[] { map.ToImage(sx, sy), map.ToImage(x, sy), map.ToImage(x, y), map.ToImage(sx, y) };
                var old = stroke.Points.ToArray();
                for (int i = 0; i < 4; i++) stroke.Points[i] = ((float)corners[i].u, (float)corners[i].v);
                var all = old.Concat(stroke.Points).ToArray();
                MarkDirty(all.Min(p => p.u), all.Min(p => p.v), all.Max(p => p.u), all.Max(p => p.v), stroke.Width);
            }
            else
            {
                var (u, v) = map.ToImage(x, y);
                var last = stroke.Points[^1];
                if (Math.Abs((u - last.u) * map.Fw * map.Scale) + Math.Abs((v - last.v) * map.Fh * map.Scale) < 1.5) return true; // menos de ~1 px
                stroke.Points.Add(((float)u, (float)v));
                MarkDirty(Math.Min(u, last.u), Math.Min(v, last.v), Math.Max(u, last.u), Math.Max(v, last.v), stroke.Width);
            }
            _dirty = true;
        }
        _renderWake.Set();
        return true;
    }

    // Soltou: o traço termina. true = estava desenhando.
    private bool AnnotateMouseUp()
    {
        lock (_lock)
        {
            if (_drawingStroke is null) return false;
            _drawingStroke = null;
        }
        ReleaseCapture();
        return true;
    }

    // Com _lock: a região que mudou (junta com a que ainda não foi desenhada).
    private void MarkDirty(double u0, double v0, double u1, double v1, float width)
    {
        if (_annoDirty is { } d)
            (u0, v0, u1, v1, width) = (Math.Min(u0, d.u0), Math.Min(v0, d.v0), Math.Max(u1, d.u1), Math.Max(v1, d.v1), Math.Max(width, d.width));
        _annoDirty = (u0, v0, u1, v1, width);
        _dirty = true;
    }

    private void AnnotateAction(int action)
    {
        switch (action)
        {
            case ActPen or ActRect:
                lock (_lock) (_annoTool, _dirty) = (action == ActRect ? 1 : 0, true);
                break;
            case >= ActColor and < ActColor + 6:
                lock (_lock) (_annoColor, _dirty) = (action - ActColor, true);
                break;
            case >= ActSize and < ActSize + 3:
                lock (_lock) (_annoSize, _dirty) = (action - ActSize, true);
                break;
            case ActUndo: UndoStroke(); break;
            case ActClear: ClearStrokes(); break;
            case ActClose: ToggleAnnotate(); break;
        }
        _renderWake.Set();
    }

    // WM_SETCURSOR: mira sobre a imagem com o painel aberto. true = trocou.
    private bool AnnotateCursor()
    {
        lock (_lock)
        {
            if (!_annotating || _annoHover >= 0) return false;
            if (_hover != UiPart.Video && _drawingStroke is null) return false;
        }
        SetCursor(LoadCursor(IntPtr.Zero, (IntPtr)32515 /* IDC_CROSS */));
        return true;
    }

    // ---------- Tela (thread da tela) ----------

    // Depois de desenhar a imagem: a camada dos traços e, com o painel aberto, o painel. "shown" = o que está na tela
    // (quadro, tamanho, giro e espelho já resolvidos como a imagem foi desenhada).
    private void DrawAnnotations(ID3D11RenderTargetView target, int cw, int ch, int frameW, int frameH, int rotation, (float x, float y) flip,
        (double x, double y, double w, double h) crop, string shownKey)
    {
        (int x0, int y0, int x1, int y1) clip = (0, 0, cw, ch);
        AnnotationLayer.Map? map = null;
        AnnotationLayer.Shape[] shapes;
        bool full, annotating, hidden;
        (double u0, double v0, double u1, double v1, float width)? dirty;
        lock (_lock)
        {
            _annoShownKey = shownKey;
            if (_strokes.Count > 0 && shownKey.Split('#')[0] != _annoKey.Split('#')[0])
            {
                // Outra foto, outro quadro do vídeo: os traços eram da anterior.
                _strokes.Clear();
                _drawingStroke = null;
                _annoFull = true;
            }
            hidden = _strokes.Count > 0 && shownKey != _annoKey; // outra versão da imagem (a do ComfyUI ou a original)
            if (frameW > 0 && frameH > 0)
            {
                // Onde a imagem está: a mesma conta do Layout (caixa girada, corte, encaixe, zoom e movimento). Com corte,
                // a imagem inteira fica além da parte mostrada; os traços só aparecem nela.
                var (bw, bh) = Box(frameW, frameH, rotation);
                var (rx, ry, rw, rh) = Fit(cw, ch, CropSize(bw, crop.w), CropSize(bh, crop.h));
                double ix = rx + _zoomX * rw, iy = ry + _zoomY * rh, iw = rw * _zoom, ih = rh * _zoom;
                clip = ((int)Math.Max(0, Math.Round(ix)), (int)Math.Max(0, Math.Round(iy)), (int)Math.Min(cw, Math.Round(ix + iw)), (int)Math.Min(ch, Math.Round(iy + ih)));
                double radians = rotation * Math.PI / 180;
                map = new AnnotationLayer.Map(frameW, frameH, Math.Cos(radians), Math.Sin(radians), flip.x < 0 ? -1 : 1, flip.y < 0 ? -1 : 1,
                    bw, bh, ix - crop.x / crop.w * iw, iy - crop.y / crop.h * ih, iw / crop.w, ih / crop.h);
            }
            _annoMap = map;
            shapes = _strokes.Select(s => new AnnotationLayer.Shape(s.Color, s.Width, s.Rect, [.. s.Points])).ToArray();
            (full, dirty, annotating) = (_annoFull, _annoDirty, _annotating);
            (_annoFull, _annoDirty) = (false, null);
        }
        if (map is { } m && !hidden && (shapes.Length > 0 || full))
        {
            _annoLayer ??= new AnnotationLayer();
            var layer = _annoLayer.Render(_gpu, cw, ch, m, shapes, full, dirty, clip);
            if (shapes.Length > 0) _gpu.DrawOverlay(target, 0, 0, cw, ch, layer);
        }
        if (annotating) DrawAnnoToolbar(target, cw, ch);
    }

    // O painel: uma coluna na lateral esquerda, no meio da altura.
    private void DrawAnnoToolbar(ID3D11RenderTargetView target, int cw, int ch)
    {
        int tool, color, size, hover;
        lock (_lock) (tool, color, size, hover) = (_annoTool, _annoColor, _annoSize, _annoHover);
        int w = U(52), cell = U(40), gap = U(10);
        int height = 2 * cell + gap + 6 * U(32) + gap + 3 * U(32) + gap + 3 * cell + U(16);
        int x = U(12), y = Math.Max(U(8), (ch - height) / 2);
        string key = $"{cw}|{ch}|{tool}|{color}|{size}|{hover}";
        var buttons = new List<(Native.RECT, int)>();
        if (key != _annoToolbarKey || _annoToolbar is null)
        {
            _annoToolbarKey = key;
            var c = _annoCanvas ??= new UiCanvas();
            c.Begin(w, height);
            c.Rect(0, 0, w, height, 0x202124, 0.94f);
            c.Rect(0, 0, w, 1, White, 0.12f);
            int top = U(8);
            void Button(int action, int h, Action<int> paint, bool on)
            {
                int index = buttons.Count;
                if (on) c.Rect(U(4), top, w - U(8), h, Accent, 0.35f);
                else if (hover == index) c.Rect(U(4), top, w - U(8), h, White, 0.12f);
                paint(top + h / 2);
                buttons.Add((new Native.RECT { Left = x + U(4), Top = y + top, Right = x + w - U(4), Bottom = y + top + h }, action));
                top += h;
            }
            void Glyph(string glyph, int cy) => c.Text(glyph, _fontIcons, (w - c.TextWidth(glyph, _fontIcons)) / 2, cy - c.TextHeight(_fontIcons) / 2, White, 0.95f);
            Button(ActPen, cell, cy => Glyph(IconPen, cy), tool == 0);
            Button(ActRect, cell, cy => Glyph(IconRectangle, cy), tool == 1);
            top += gap;
            for (int i = 0; i < AnnoColors.Length; i++)
            {
                int index = i;
                Button(ActColor + i, U(32), cy =>
                {
                    if (color == index) c.Circle(w / 2f, cy, U(12), White, 0.95f);
                    c.Circle(w / 2f, cy, U(9), AnnoColors[index], 1);
                }, false);
            }
            top += gap;
            for (int i = 0; i < AnnoSizes.Length; i++)
            {
                int index = i;
                Button(ActSize + i, U(32), cy => c.Circle(w / 2f, cy, Math.Max(U(2), U(AnnoSizes[index]) / 2f + U(1)), White, 0.9f), size == index);
            }
            top += gap;
            Button(ActUndo, cell, cy => Glyph(IconUndo, cy), false);
            Button(ActClear, cell, cy => Glyph(IconClear, cy), false);
            Button(ActClose, cell, cy => Glyph(IconCloseAnno, cy), false);
            _annoToolbar = c.Upload(_gpu);
            lock (_lock)
            {
                _annoButtons.Clear();
                _annoButtons.AddRange(buttons);
            }
        }
        _gpu.DrawOverlay(target, x, y, w, height, _annoToolbar);
    }

    // ---------- Salvar (Ctrl+S) ----------

    // Os traços na imagem que vai ser salva (BGRA, w×h), se forem desta imagem. true = tinha traços.
    private bool BurnAnnotations(byte[] pixels, int w, int h)
    {
        AnnotationLayer.Shape[] shapes;
        lock (_lock)
        {
            if (_strokes.Count == 0 || _annoKey != _annoShownKey) return false;
            shapes = _strokes.Select(s => new AnnotationLayer.Shape(s.Color, s.Width, s.Rect, [.. s.Points])).ToArray();
        }
        foreach (var shape in shapes)
            AnnotationLayer.Draw(pixels, w, h, bgra: true, (0, 0, w, h), shape.Color, shape.Width * w,
                [.. shape.Points.Select(p => ((double)p.u * w, (double)p.v * h))], shape.Rect);
        return true;
    }

    // Onde um ponto da imagem cai na imagem que o G manda (ScreenPixels: w×h, na orientação da tela — girada, espelhada
    // e cortada —, sem o zoom). Com _lock.
    private AnnotationLayer.Map SentMap(Frame frame, int w, int h)
    {
        int rotation = frame.Baked ? 0 : _rotation;
        (float x, float y) flip = frame.Baked ? (1, 1) : (_flipH ? -1 : 1, _flipV ? -1 : 1);
        var (bw, bh) = Box(frame.Width, frame.Height, rotation);
        var crop = CropFor(frame);
        double radians = rotation * Math.PI / 180, iw = w / crop.w, ih = h / crop.h;
        return new AnnotationLayer.Map(frame.Width, frame.Height, Math.Cos(radians), Math.Sin(radians), flip.x < 0 ? -1 : 1, flip.y < 0 ? -1 : 1,
            bw, bh, -crop.x * iw, -crop.y * ih, iw, ih);
    }

    // A imagem que o G manda (ScreenPixels, BGRA): os traços entram nela, com a mesma conta da tela. Com _lock.
    private void BurnAnnotationsShown(byte[] pixels, int w, int h, Frame frame)
    {
        if (_strokes.Count == 0 || _annoKey != _annoShownKey) return;
        var map = SentMap(frame, w, h);
        foreach (var stroke in _strokes)
            AnnotationLayer.Draw(pixels, w, h, bgra: true, (0, 0, w, h), stroke.Color, stroke.Width * map.Fw * map.Scale,
                [.. stroke.Points.Select(p => map.ToScreen(p.u, p.v))], stroke.Rect);
    }

    // A área de edição para o G: a máscara (no tamanho w×h da imagem enviada, na orientação dela) dos contornos fechados
    // e as cores deles; e as cores dos traços abertos (indicações). null = sem anotações nesta imagem. Com _lock.
    private (float[]? mask, string[] areaColors, string[] markColors)? EditArea(Frame frame, int w, int h)
    {
        if (_strokes.Count == 0 || _annoKey != _annoShownKey) return null;
        var map = SentMap(frame, w, h);
        float[]? mask = null;
        var areas = new List<string>();
        var marks = new List<string>();
        double feather = Math.Max(4, Math.Min(w, h) * 0.015); // borda suave entre a edição e a original
        foreach (var stroke in _strokes)
        {
            var points = stroke.Points.Select(p => map.ToScreen(p.u, p.v)).ToArray();
            double half = stroke.Width * map.Fw * map.Scale / 2;
            string color = ComfyArea.ColorName(stroke.Color);
            if (!stroke.Rect && !ComfyArea.IsClosed(points, half))
            {
                marks.Add(color);
                continue;
            }
            mask ??= new float[w * h];
            ComfyArea.AddRegion(mask, w, h, points, half, feather);
            areas.Add(color);
        }
        return (mask, [.. areas.Distinct()], [.. marks.Distinct()]);
    }

    // O que vai junto do prompt, em inglês (o Qwen entende melhor): editar só dentro da área contornada e tirar o
    // contorno; traços soltos são indicações e saem da resposta.
    private static string AreaInstruction(string[] areaColors, string[] markColors)
    {
        static string Join(string[] colors) => colors.Length == 1 ? colors[0] : string.Join(", ", colors[..^1]) + " and " + colors[^1];
        var text = new List<string>();
        if (areaColors.Length > 0)
            text.Add($"Apply the edit only inside the area outlined in {Join(areaColors)} on the image; keep everything outside that area exactly the same. Remove the {Join(areaColors)} outline from the result.");
        if (markColors.Length > 0)
            text.Add($"The {Join(markColors)} marks drawn on the image are only guides: do not keep them in the result.");
        return string.Join(" ", text);
    }

    // Autoteste: o que o G mandaria agora (a imagem com o desenho, a limpa, a máscara e o texto que vai junto do prompt).
    public (byte[] sent, byte[] clean, float[]? mask, int w, int h, string instruction)? TestComfyArea()
    {
        lock (_gpuLock)
        lock (_lock)
        {
            if (ScreenFrame() is not { } frame) return null;
            var shown = _still is not null && _stillShowAi && _stillSerial == frame.Serial ? _still : frame;
            var (sent, w, h) = ScreenPixels(shown, ComfyMaxSide);
            var clean = ScreenPixels(shown, ComfyMaxSide, annotations: false).pixels;
            var area = EditArea(shown, w, h);
            return (sent, clean, area?.mask, w, h, area is { } a ? AreaInstruction(a.areaColors, a.markColors) : "");
        }
    }

    // Autoteste.
    public bool TestAnnotating { get { lock (_lock) return _annotating; } }
    public int TestStrokeCount { get { lock (_lock) return _strokes.Count; } }
    public (int x, int y) TestAnnoButton(int action) { lock (_lock) { var r = _annoButtons.First(b => b.action == action).rect; return ((r.Left + r.Right) / 2, (r.Top + r.Bottom) / 2); } }
    public (double x, double y) TestImageToScreen(double u, double v) { lock (_lock) return _annoMap!.Value.ToScreen(u, v); }
    public void TestUndoStroke() => UndoStroke();
    public void TestClearStrokes() => ClearStrokes();
    public void TestAnnoAction(int action) => AnnotateAction(action);
}
