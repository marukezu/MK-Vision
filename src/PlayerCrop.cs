using Vortice.Direct3D11;
using static MKVision.Desktop.PlayerNative;

namespace MKVision.Desktop;

// Corte (F4), como no Fotos do Windows: a imagem inteira com uma moldura — cantos e lados ajustam, arrastar por dentro
// move, arrastar fora começa outra — o lado de fora escurecido, e embaixo as proporções, "Imagem inteira", Cancelar e
// Aceitar (Enter aceita, Esc cancela). Aceito, a tela mostra só a parte cortada, e é ela que vai no G (ComfyUI), no
// Ctrl+S e na área das anotações. O corte é da imagem como você a vê (com giro e espelho), em frações dela. A resposta
// do ComfyUI já vem cortada e tem o seu próprio corte. Outra foto ou vídeo, ou girar, desfaz o corte; espelhar o
// espelha junto. No vídeo, vale para o vídeo todo (pausa para ajustar).
internal sealed partial class Player
{
    private static readonly (double x, double y, double w, double h) FullCrop = (0, 0, 1, 1);
    private static readonly (string name, double ratio)[] CropRatios = [("Livre", 0), ("Original", -1), ("1:1", 1), ("4:3", 4 / 3.0), ("16:9", 16 / 9.0)];
    private const string IconAccept = "", IconCancelCrop = "";
    private enum CropGrip { None, Move, New, N, S, E, W, NE, NW, SE, SW }

    // Com _lock.
    private (double x, double y, double w, double h) _crop = FullCrop, _cropBaked = FullCrop; // da original / da resposta do ComfyUI
    private bool _cropping, _cropShownBaked, _cropPortrait;
    private (double x, double y, double w, double h) _cropSel = FullCrop;  // a moldura, em frações da imagem girada
    private int _cropRatio, _cropHover = -1;
    private CropGrip _cropDrag;
    private (double x, double y) _cropDragStart;                   // o mouse ao apertar (pixels da imagem)
    private (double x, double y, double w, double h) _cropDragFrom; // a moldura ao apertar
    private (int w, int h) _cropBox = (1, 1);                        // a imagem girada da tela (pixels)
    private (double x, double y, double w, double h) _cropImage;     // onde ela está na janela
    private readonly List<(Native.RECT rect, int action)> _cropButtons = [];

    // Thread da tela.
    private UiCanvas? _cropBarCanvas, _cropDarkCanvas, _cropLineCanvas, _cropGridCanvas;
    private ID3D11ShaderResourceView? _cropBar, _cropDark, _cropLine, _cropGrid;
    private string _cropBarKey = "";
    private int[]? _cropBarWidths;

    // Ações dos botões da barra.
    private const int CropActRatio = 0, CropActFull = 10, CropActCancel = 11, CropActAccept = 12;

    // Com _lock: o corte que vale para esta versão da imagem.
    private (double x, double y, double w, double h) CropFor(Frame frame) => frame.Baked ? _cropBaked : _crop;

    private static int CropSize(int full, double fraction) => Math.Max(1, (int)Math.Round(full * fraction));

    // Com _lock (no desenho): a imagem da tela, para o modo de corte (lá ela aparece inteira, sem zoom).
    private void NoteCropFrame(Frame frame, int rotation, int cw, int ch)
    {
        _cropShownBaked = frame.Baked;
        _cropBox = Box(frame.Width, frame.Height, rotation);
        _cropImage = Fit(cw, ch, _cropBox.w, _cropBox.h);
    }

    // F4: abre o modo de corte (de novo: cancela).
    private void ToggleCrop()
    {
        bool open, video, playing, media, annotating;
        lock (_lock) (open, video, playing, media, annotating) = (_cropping, _file is not null && !_image, _playing, _file is not null || _image, _annotating);
        if (open)
        {
            CancelCrop();
            return;
        }
        if (!media)
        {
            Notify("Abra uma foto ou um vídeo para cortar");
            return;
        }
        if (annotating) ToggleAnnotate(); // um modo de cada vez
        if (video && playing) TogglePlay(); // ajusta no quadro parado
        lock (_lock)
        {
            _cropping = true;
            _cropSel = _cropShownBaked ? _cropBaked : _crop; // começa no corte de agora (a imagem aparece inteira)
            (_cropRatio, _cropHover, _cropDrag) = (0, -1, CropGrip.None);
            _zoom = 1;
            _zoomX = _zoomY = 0;
            _dirty = true;
        }
        Notify("Cortar: ajuste a moldura · Enter aceita · Esc cancela");
        _renderWake.Set();
    }

    private void AcceptCrop()
    {
        string text;
        lock (_lock)
        {
            if (!_cropping) return;
            var sel = _cropSel;
            bool whole = sel.w > 0.999 && sel.h > 0.999;
            if (whole) sel = FullCrop;
            if (_cropShownBaked) _cropBaked = sel;
            else _crop = sel;
            (_cropping, _cropDrag, _dirty) = (false, CropGrip.None, true);
            text = whole ? "Corte: a imagem inteira" : $"Cortada: {CropSize(_cropBox.w, sel.w)}x{CropSize(_cropBox.h, sel.h)}";
        }
        ReleaseCapture();
        Notify(text);
        _renderWake.Set();
        UpdateTitle();
    }

    private void CancelCrop()
    {
        lock (_lock)
        {
            if (!_cropping) return;
            (_cropping, _cropDrag, _dirty) = (false, CropGrip.None, true);
        }
        ReleaseCapture();
        Notify("Corte cancelado");
        _renderWake.Set();
    }

    // Teclas no modo de corte: Enter aceita, Esc cancela; as outras ficam paradas (o corte é um modo). true = usada.
    private bool CropKey(int key)
    {
        lock (_lock)
            if (!_cropping) return false;
        if (key == VK_RETURN) AcceptCrop();
        else if (key == VK_ESCAPE) CancelCrop();
        return true;
    }

    private void CropAction(int action)
    {
        switch (action)
        {
            case >= CropActRatio and < CropActRatio + 5:
                lock (_lock)
                {
                    _cropRatio = action - CropActRatio;
                    var s = _cropSel;
                    double bw = _cropBox.w, bh = _cropBox.h, w = s.w * bw, h = s.h * bh;
                    _cropPortrait = h > w; // 4:3 numa moldura em pé vira 3:4
                    double ratio = CropRatioNow();
                    if (ratio > 0)
                    {
                        // A maior moldura da proporção dentro da de agora, no mesmo centro.
                        double cx = (s.x + s.w / 2) * bw, cy = (s.y + s.h / 2) * bh;
                        if (w / h > ratio) w = h * ratio;
                        else h = w / ratio;
                        _cropSel = CropFraction(cx - w / 2, cy - h / 2, cx + w / 2, cy + h / 2);
                    }
                    _dirty = true;
                }
                break;
            case CropActFull:
                lock (_lock) (_cropSel, _cropRatio, _dirty) = (FullCrop, 0, true);
                break;
            case CropActCancel: CancelCrop(); break;
            case CropActAccept: AcceptCrop(); break;
        }
        _renderWake.Set();
    }

    // Com _lock: largura/altura da proporção escolhida, em pixels da imagem (0 = livre).
    private double CropRatioNow()
    {
        double ratio = CropRatios[_cropRatio].ratio;
        if (ratio < 0) return (double)_cropBox.w / _cropBox.h; // a da imagem
        return ratio > 0 && _cropPortrait ? 1 / ratio : ratio;
    }

    // Com _lock: pixels da imagem → frações, dentro dela.
    private (double x, double y, double w, double h) CropFraction(double l, double t, double r, double b)
    {
        double bw = _cropBox.w, bh = _cropBox.h;
        l = Math.Clamp(l, 0, bw - 1);
        t = Math.Clamp(t, 0, bh - 1);
        r = Math.Clamp(r, l + 1, bw);
        b = Math.Clamp(b, t + 1, bh);
        return (l / bw, t / bh, (r - l) / bw, (b - t) / bh);
    }

    // ---------- Mouse (thread principal) ----------

    // Com _lock: janela → pixels da imagem (dentro dela).
    private (double x, double y) CropPoint(int x, int y)
    {
        var (ix, iy, iw, ih) = _cropImage;
        return (Math.Clamp((x - ix) / iw * _cropBox.w, 0, _cropBox.w), Math.Clamp((y - iy) / ih * _cropBox.h, 0, _cropBox.h));
    }

    // Com _lock: a moldura na janela.
    private (double l, double t, double r, double b) CropScreen()
    {
        var (ix, iy, iw, ih) = _cropImage;
        var s = _cropSel;
        return (ix + s.x * iw, iy + s.y * ih, ix + (s.x + s.w) * iw, iy + (s.y + s.h) * ih);
    }

    // Com _lock: o que o mouse pega no ponto (canto, lado, dentro = mover, fora = moldura nova).
    private CropGrip GripAt(int x, int y)
    {
        var (l, t, r, b) = CropScreen();
        double grab = U(14);
        bool nearL = Math.Abs(x - l) <= grab, nearR = Math.Abs(x - r) <= grab, nearT = Math.Abs(y - t) <= grab, nearB = Math.Abs(y - b) <= grab;
        bool alongX = x > l - grab && x < r + grab, alongY = y > t - grab && y < b + grab;
        if (nearT && nearL) return CropGrip.NW;
        if (nearT && nearR) return CropGrip.NE;
        if (nearB && nearL) return CropGrip.SW;
        if (nearB && nearR) return CropGrip.SE;
        if (nearT && alongX) return CropGrip.N;
        if (nearB && alongX) return CropGrip.S;
        if (nearL && alongY) return CropGrip.W;
        if (nearR && alongY) return CropGrip.E;
        return x > l && x < r && y > t && y < b ? CropGrip.Move : CropGrip.New;
    }

    private bool CropMouseDown(int x, int y)
    {
        int action = -1;
        lock (_lock)
        {
            if (!_cropping) return false;
            int button = _cropButtons.FindIndex(b => Inside(b.rect, x, y));
            if (button >= 0) action = _cropButtons[button].action;
            else
            {
                _cropDrag = GripAt(x, y);
                _cropDragStart = CropPoint(x, y);
                _cropDragFrom = _cropSel;
            }
            _dirty = true;
        }
        if (action >= 0) CropAction(action);
        else SetCapture(Hwnd);
        _renderWake.Set();
        return true;
    }

    // true = no modo de corte (o mouse é só dele: sem barras, lista ou painel).
    private bool CropMouseMove(int x, int y)
    {
        lock (_lock)
        {
            if (!_cropping) return false;
            (_mouseX, _mouseY, _mouseInside) = (x, y, true);
            int hover = _cropButtons.FindIndex(b => Inside(b.rect, x, y));
            if (hover != _cropHover) (_cropHover, _dirty) = (hover, true);
            if (_cropDrag == CropGrip.None) return true;
            _cropSel = CropDragged(CropPoint(x, y));
            _dirty = true;
        }
        _renderWake.Set();
        return true;
    }

    private bool CropMouseUp()
    {
        lock (_lock)
        {
            if (!_cropping) return false;
            _cropDrag = CropGrip.None;
        }
        ReleaseCapture();
        return true;
    }

    // Com _lock: a moldura com o mouse em "p" (pixels da imagem), conforme o que foi pego.
    private (double x, double y, double w, double h) CropDragged((double x, double y) p)
    {
        double bw = _cropBox.w, bh = _cropBox.h;
        var f = _cropDragFrom;
        double l = f.x * bw, t = f.y * bh, r = (f.x + f.w) * bw, b = (f.y + f.h) * bh;
        double min = Math.Min(Math.Min(bw, bh), U(24) * bw / Math.Max(1, _cropImage.w)); // ~24 pixels de tela
        double ratio = CropRatioNow();
        switch (_cropDrag)
        {
            case CropGrip.Move:
            {
                double dx = Math.Clamp(p.x - _cropDragStart.x, -l, bw - r), dy = Math.Clamp(p.y - _cropDragStart.y, -t, bh - b);
                return CropFraction(l + dx, t + dy, r + dx, b + dy);
            }
            case CropGrip.E or CropGrip.W:
            {
                // Um lado: o oposto fica; com proporção, a altura acompanha (no mesmo centro).
                double ax = _cropDrag == CropGrip.E ? l : r, dir = _cropDrag == CropGrip.E ? 1 : -1;
                double w = Math.Min(Math.Max(min, dir * (p.x - ax)), dir > 0 ? bw - ax : ax), cy = (t + b) / 2, h = b - t;
                if (ratio > 0)
                {
                    h = w / ratio;
                    double most = 2 * Math.Min(cy, bh - cy);
                    if (h > most) (h, w) = (most, most * ratio);
                }
                double x0 = dir > 0 ? ax : ax - w;
                return CropFraction(x0, cy - h / 2, x0 + w, cy + h / 2);
            }
            case CropGrip.N or CropGrip.S:
            {
                double ay = _cropDrag == CropGrip.S ? t : b, dir = _cropDrag == CropGrip.S ? 1 : -1;
                double h = Math.Min(Math.Max(min, dir * (p.y - ay)), dir > 0 ? bh - ay : ay), cx = (l + r) / 2, w = r - l;
                if (ratio > 0)
                {
                    w = h * ratio;
                    double most = 2 * Math.Min(cx, bw - cx);
                    if (w > most) (w, h) = (most, most / ratio);
                }
                double y0 = dir > 0 ? ay : ay - h;
                return CropFraction(cx - w / 2, y0, cx + w / 2, y0 + h);
            }
            default:
            {
                // Canto (o oposto fica) ou moldura nova (de onde apertou até o mouse).
                double ax, ay, dx, dy;
                if (_cropDrag == CropGrip.New)
                {
                    (ax, ay) = _cropDragStart;
                    (dx, dy) = (p.x >= ax ? 1 : -1, p.y >= ay ? 1 : -1);
                }
                else
                {
                    bool east = _cropDrag is CropGrip.NE or CropGrip.SE, south = _cropDrag is CropGrip.SE or CropGrip.SW;
                    (ax, ay, dx, dy) = (east ? l : r, south ? t : b, east ? 1 : -1, south ? 1 : -1);
                }
                double w = Math.Max(min, dx * (p.x - ax)), h = Math.Max(min, dy * (p.y - ay));
                double mostW = dx > 0 ? bw - ax : ax, mostH = dy > 0 ? bh - ay : ay;
                if (ratio > 0)
                {
                    if (w / h > ratio) w = h * ratio;
                    else h = w / ratio;
                }
                if (w > mostW) (w, h) = (mostW, ratio > 0 ? mostW / ratio : h);
                if (h > mostH) (h, w) = (mostH, ratio > 0 ? mostH * ratio : w);
                double x0 = dx > 0 ? ax : ax - w, y0 = dy > 0 ? ay : ay - h;
                return CropFraction(x0, y0, x0 + w, y0 + h);
            }
        }
    }

    // WM_SETCURSOR no modo de corte: setas de redimensionar nos cantos e lados, mover por dentro, mira fora. true = trocou.
    private bool CropCursor()
    {
        CropGrip grip;
        lock (_lock)
        {
            if (!_cropping || _cropHover >= 0) return false;
            grip = _cropDrag != CropGrip.None ? _cropDrag : GripAt(_mouseX, _mouseY);
        }
        int cursor = grip switch
        {
            CropGrip.NW or CropGrip.SE => 32642, // IDC_SIZENWSE
            CropGrip.NE or CropGrip.SW => 32643, // IDC_SIZENESW
            CropGrip.E or CropGrip.W => 32644,   // IDC_SIZEWE
            CropGrip.N or CropGrip.S => 32645,   // IDC_SIZENS
            CropGrip.Move => 32646,              // IDC_SIZEALL
            _ => 32515                           // IDC_CROSS
        };
        SetCursor(LoadCursor(IntPtr.Zero, (IntPtr)cursor));
        return true;
    }

    // ---------- Tela (thread da tela, com o lock da GPU) ----------

    // A moldura sobre a imagem inteira: fora escurecido, linhas dos terços, borda com as alças e a barra de botões.
    private void DrawCrop(ID3D11RenderTargetView target, int cw, int ch)
    {
        (double x, double y, double w, double h) image;
        (double l, double t, double r, double b) sel;
        int ratio, hover;
        lock (_lock)
        {
            if (!_cropping) return;
            (image, sel, ratio, hover) = (_cropImage, CropScreen(), _cropRatio, _cropHover);
        }
        ID3D11ShaderResourceView Solid(ref UiCanvas? canvas, ref ID3D11ShaderResourceView? view, uint rgb, float alpha)
        {
            if (view is not null) return view;
            canvas = new UiCanvas();
            canvas.Begin(2, 2);
            canvas.Rect(0, 0, 2, 2, rgb, alpha);
            return view = canvas.Upload(_gpu);
        }
        var dark = Solid(ref _cropDarkCanvas, ref _cropDark, 0x000000, 0.6f);
        var line = Solid(ref _cropLineCanvas, ref _cropLine, White, 0.95f);
        var grid = Solid(ref _cropGridCanvas, ref _cropGrid, White, 0.35f);
        void Fill(ID3D11ShaderResourceView view, double x0, double y0, double x1, double y1)
        {
            int a = (int)Math.Round(Math.Max(0, x0)), b = (int)Math.Round(Math.Max(0, y0));
            int c = (int)Math.Round(Math.Min(cw, x1)), d = (int)Math.Round(Math.Min(ch, y1));
            if (c > a && d > b) _gpu.DrawOverlay(target, a, b, c - a, d - b, view);
        }
        double right = image.x + image.w, bottom = image.y + image.h;
        Fill(dark, image.x, image.y, right, sel.t);
        Fill(dark, image.x, sel.b, right, bottom);
        Fill(dark, image.x, sel.t, sel.l, sel.b);
        Fill(dark, sel.r, sel.t, right, sel.b);
        for (int i = 1; i <= 2; i++)
        {
            double gx = sel.l + (sel.r - sel.l) * i / 3, gy = sel.t + (sel.b - sel.t) * i / 3;
            Fill(grid, gx, sel.t, gx + 1, sel.b);
            Fill(grid, sel.l, gy, sel.r, gy + 1);
        }
        double border = Math.Max(1, U(1)), grip = U(4), arm = U(22), half = grip / 2;
        Fill(line, sel.l, sel.t, sel.r, sel.t + border);
        Fill(line, sel.l, sel.b - border, sel.r, sel.b);
        Fill(line, sel.l, sel.t, sel.l + border, sel.b);
        Fill(line, sel.r - border, sel.t, sel.r, sel.b);
        // Alças: cantos em L e um traço no meio de cada lado.
        foreach (var (cx, cy, sx, sy) in new[] { (sel.l, sel.t, 1, 1), (sel.r, sel.t, -1, 1), (sel.l, sel.b, 1, -1), (sel.r, sel.b, -1, -1) })
        {
            Fill(line, Math.Min(cx - sx * half, cx + sx * arm), cy - half, Math.Max(cx - sx * half, cx + sx * arm), cy + half);
            Fill(line, cx - half, Math.Min(cy - sy * half, cy + sy * arm), cx + half, Math.Max(cy - sy * half, cy + sy * arm));
        }
        double mx = (sel.l + sel.r) / 2, my = (sel.t + sel.b) / 2, bar = arm * 0.6;
        Fill(line, mx - bar, sel.t - half, mx + bar, sel.t + half);
        Fill(line, mx - bar, sel.b - half, mx + bar, sel.b + half);
        Fill(line, sel.l - half, my - bar, sel.l + half, my + bar);
        Fill(line, sel.r - half, my - bar, sel.r + half, my + bar);
        DrawCropBar(target, cw, ch, ratio, hover);
    }

    // A barra embaixo: proporções · Imagem inteira · Cancelar · Aceitar.
    private void DrawCropBar(ID3D11RenderTargetView target, int cw, int ch, int ratio, int hover)
    {
        if (_fontMenu == IntPtr.Zero) return;
        var items = new List<(string? icon, string text, int action)>();
        for (int i = 0; i < CropRatios.Length; i++) items.Add((null, CropRatios[i].name, CropActRatio + i));
        items.Add((null, "Imagem inteira", CropActFull));
        items.Add((IconCancelCrop, "Cancelar", CropActCancel));
        items.Add((IconAccept, "Aceitar", CropActAccept));
        int pad = U(14), height = U(44), cell = U(34), gap = U(12), inner = U(5);
        var c = _cropBarCanvas ??= new UiCanvas();
        // Larguras: texto (+ ícone) e um respiro entre os grupos (proporções | inteira | cancelar e aceitar). Medidas uma
        // vez (a medida precisa da tela de desenho já criada).
        if (_cropBarWidths is null)
        {
            c.Begin(1, 1);
            _cropBarWidths = [.. items.Select(item => c.TextWidth(item.text, _fontMenu) + 2 * pad + (item.icon is null ? 0 : c.TextWidth(item.icon, _fontIcons) + U(8)))];
        }
        var widths = _cropBarWidths;
        int width = widths.Sum() + 2 * inner + 2 * gap;
        int x = (cw - width) / 2, y = ch - U(24) - height;
        string key = $"{cw}|{ch}|{ratio}|{hover}";
        var buttons = new List<(Native.RECT, int)>();
        int left = inner;
        for (int i = 0; i < items.Count; i++)
        {
            if (items[i].action is CropActFull or CropActCancel) left += gap;
            buttons.Add((new Native.RECT { Left = x + left, Top = y + (height - cell) / 2, Right = x + left + widths[i], Bottom = y + (height + cell) / 2 }, items[i].action));
            left += widths[i];
        }
        if (key != _cropBarKey || _cropBar is null)
        {
            _cropBarKey = key;
            c.Begin(width, height);
            c.Rect(0, 0, width, height, 0x202124, 0.94f);
            c.Rect(0, 0, width, 1, White, 0.12f);
            for (int i = 0; i < items.Count; i++)
            {
                var (icon, text, action) = items[i];
                var rect = buttons[i].Item1;
                float bx = rect.Left - x, by = rect.Top - y;
                bool accept = action == CropActAccept, chosen = action - CropActRatio == ratio && action < CropActFull;
                if (accept) c.Rect(bx, by, widths[i], cell, Accent, hover == i ? 1f : 0.85f);
                else if (chosen) c.Rect(bx, by, widths[i], cell, Accent, 0.35f);
                else if (hover == i) c.Rect(bx, by, widths[i], cell, White, 0.12f);
                int tx = (int)bx + pad;
                if (icon is not null)
                {
                    c.Text(icon, _fontIcons, tx, (int)by + (cell - c.TextHeight(_fontIcons)) / 2, White, 0.95f);
                    tx += c.TextWidth(icon, _fontIcons) + U(8);
                }
                c.Text(text, _fontMenu, tx, (int)by + (cell - c.TextHeight(_fontMenu)) / 2, White, 0.95f);
            }
            _cropBar = c.Upload(_gpu);
        }
        lock (_lock)
        {
            _cropButtons.Clear();
            _cropButtons.AddRange(buttons);
        }
        _gpu.DrawOverlay(target, x, y, width, height, _cropBar);
    }

    // ---------- Salvar (Ctrl+S) ----------

    // A parte cortada da imagem salva (na orientação do arquivo, w×h, BGRA): o corte (na imagem girada e espelhada) volta
    // para a orientação do arquivo; com giro fora de 90°, o retângulo que contém a moldura. null = sem corte.
    private static (byte[] pixels, int w, int h)? CropPixels(byte[] pixels, int w, int h, (double x, double y, double w, double h) crop, int rotation, (float x, float y) flip)
    {
        if (crop == FullCrop) return null;
        var (bw, bh) = Box(w, h, rotation);
        double radians = rotation * Math.PI / 180;
        var map = new AnnotationLayer.Map(w, h, Math.Cos(radians), Math.Sin(radians), flip.x < 0 ? -1 : 1, flip.y < 0 ? -1 : 1, bw, bh, 0, 0, bw, bh);
        var corners = new[] { (crop.x, crop.y), (crop.x + crop.w, crop.y), (crop.x, crop.y + crop.h), (crop.x + crop.w, crop.y + crop.h) }
            .Select(p => map.ToImage(p.Item1 * bw, p.Item2 * bh)).ToArray();
        int x0 = Math.Clamp((int)Math.Round(corners.Min(p => p.u) * w), 0, w - 1), x1 = Math.Clamp((int)Math.Round(corners.Max(p => p.u) * w), x0 + 1, w);
        int y0 = Math.Clamp((int)Math.Round(corners.Min(p => p.v) * h), 0, h - 1), y1 = Math.Clamp((int)Math.Round(corners.Max(p => p.v) * h), y0 + 1, h);
        int cw = x1 - x0, ch = y1 - y0;
        var output = new byte[cw * ch * 4];
        for (int y = 0; y < ch; y++) Buffer.BlockCopy(pixels, ((y0 + y) * w + x0) * 4, output, y * cw * 4, cw * 4);
        return (output, cw, ch);
    }

    // Autoteste.
    public bool TestCropping { get { lock (_lock) return _cropping; } }
    public (double x, double y, double w, double h) TestCrop { get { lock (_lock) return _crop; } }
    public (double x, double y, double w, double h) TestCropSelection { get { lock (_lock) return _cropSel; } }
    // Ponto da imagem girada (frações) → janela, no modo de corte.
    public (double x, double y) TestCropToScreen(double u, double v) { lock (_lock) return (_cropImage.x + u * _cropImage.w, _cropImage.y + v * _cropImage.h); }
    public (int x, int y) TestCropButton(int action)
    {
        lock (_lock)
        {
            var rect = _cropButtons.First(b => b.action == action).rect;
            return ((rect.Left + rect.Right) / 2, (rect.Top + rect.Bottom) / 2);
        }
    }
}
