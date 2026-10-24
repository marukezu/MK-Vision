"""Gera os ícones do MK Vision (16/24/32/48/64/256 px): mk-lente.ico e mk-globo.ico, e o app.ico que o app usa.

Cada tamanho é desenhado direto na sua resolução (com superamostragem), não reduzido do 256: nos pequenos
os detalhes finos saem (reflexo da lente, linhas do globo), o fundo ocupa o quadro todo e o MK cresce e encaixa
na grade de pixels; em 16 px as letras são desenhadas pixel a pixel, porque o vetor borra o V do M.

Uso: python gerar_ico.py [lente|globo] [pasta-png]
  grava mk-lente.ico e mk-globo.ico ao lado deste arquivo e copia o escolhido (padrão: lente) para app.ico,
  que é o que o .csproj usa; com pasta-png, grava também as PNGs de cada tamanho para conferir.
"""
import io, math, os, shutil, struct, sys
import numpy as np
from PIL import Image, ImageDraw

HERE = os.path.dirname(os.path.abspath(__file__))
SIZES = (16, 24, 32, 48, 64, 256)
WHITE, CORE = (255, 255, 255), (0x17, 0x18, 0x1B)

# Letras em 16 px, da linha `top` em diante: # branco, + meio-tom.
MK16 = ["..#....#..#..#..",
        "..##..##..#.#...",
        "..#+##+#..##....",
        "..#.++.#..#+....",
        "..#....#..#+#...",
        "..#....#..#.+#..",
        "..#....#..#..#..",
        "..#....#..#..#.."]
MK16_BOLD = [".##...##.##..##.",
             ".###.###.##.##..",
             ".#######.####...",
             ".##.#.##.###....",
             ".##...##.####...",
             ".##...##.##.##..",
             ".##...##.##..##.",
             ".##...##.##..##."]

# Cada desenho: medidas na grade de 256 (as do SVG em icones-opcoes/) e, por tamanho, o que muda.
# letras: mk=(cx, cy, H, w) como no SVG, ou px=(hastes do M, haste do K, ponta dos braços do K, topo, base,
# fundo do V, espessura) em pixels daquele tamanho, para cair na grade.
LENTE_FULL = dict(ring=122, core=100, glint=True, mk=(128, 130, 84, 22))
GLOBO_FULL = dict(box=8, radius=56, lines=6, line_alpha=0.3, mk=(128, 128, 100, 27))
DESIGNS = {
    "lente": {
        256: LENTE_FULL,
        64: LENTE_FULL,
        48: dict(LENTE_FULL, glint=False, core=102, mk=(128, 130, 92, 24)),
        32: dict(ring=127, core=116, glint=False, px=(6, 14, 18.5, 26, 11.4, 20.6, 17.6, 2.5)),
        24: dict(ring=128, core=115, glint=False, px=(4, 10, 14, 20, 8, 16, 13, 2)),
        16: dict(ring=128, core=114, glint=False, bitmap=(MK16, 4)),
    },
    "globo": {
        256: GLOBO_FULL,
        64: dict(GLOBO_FULL, lines=8),
        48: dict(GLOBO_FULL, box=4, radius=52, lines=8, line_alpha=0.28, mk=(128, 128, 108, 28)),
        32: dict(box=0, radius=56, lines=8, line_alpha=0.22, simple=True, px=(5.5, 13.5, 18.5, 26.5, 10.5, 21.5, 17, 3)),
        24: dict(box=0, radius=56, lines=0, px=(4, 10, 14, 20, 7, 17, 13.4, 2)),
        16: dict(box=0, radius=52, lines=0, bitmap=(MK16_BOLD, 4)),
    },
}
COLORS = {"lente": ((0x8A, 0xB4, 0xF8), (0x7B, 0x5C, 0xF0)), "globo": ((0x22, 0xB5, 0xA0), (0x1F, 0x6F, 0xA8))}


def mk_lines(cx, cy, H, w):
    """Mesma geometria das letras do SVG: linhas centrais do M e do K."""
    Wm, Wk, g = 0.80 * H, 0.52 * H, w * 1.55
    xL = cx - (Wm + g + Wk) / 2
    xR, yT, yB = xL + Wm, cy - H / 2 + w / 2, cy + H / 2 - w / 2
    xK = xR + g
    yV, yM = yT + (yB - yT) * 0.66, (yT + yB) / 2
    return [[(xL, yB), (xL, yT), ((xL + xR) / 2, yV), (xR, yT), (xR, yB)],
            [(xK, yT), (xK, yB)],
            [(xK + Wk, yT), (xK + w * 0.1, yM), (xK + Wk, yB)]]


def mk_px(size, xL, xR, xK, xA, yT, yB, yV, w):
    u = 256 / size
    yM = (yT + yB) / 2
    lines = [[(xL, yB), (xL, yT), ((xL + xR) / 2, yV), (xR, yT), (xR, yB)], [(xK, yT), (xK, yB)],
             [(xA, yT), (xK + w * 0.1, yM), (xA, yB)]]
    return [[(x * u, y * u) for x, y in p] for p in lines], w * u


def render(design, size):
    s = DESIGNS[design][size]
    ss = max(4, 1024 // size)
    n, k = size * ss, size * ss / 256
    out = Image.new("RGBA", (n, n), (0, 0, 0, 0))

    def layer(draw_mask, color, opacity=1.0):
        nonlocal out
        mask = Image.new("L", (n, n), 0)
        draw_mask(ImageDraw.Draw(mask))
        if opacity < 1:
            mask = mask.point(lambda v: round(v * opacity))
        fill = color.copy() if isinstance(color, Image.Image) else Image.new("RGBA", (n, n), color + (255,))
        fill.putalpha(mask)
        out = Image.alpha_composite(out, fill)

    def circle(d, cx, cy, r):
        d.ellipse([(cx - r) * k, (cy - r) * k, (cx + r) * k, (cy + r) * k], fill=255)

    def polyline(d, pts, w):
        for (x0, y0), (x1, y1) in zip(pts, pts[1:]):
            dx, dy = x1 - x0, y1 - y0
            ln = math.hypot(dx, dy) or 1
            ox, oy = -dy / ln * w / 2, dx / ln * w / 2
            d.polygon([((x0 + ox) * k, (y0 + oy) * k), ((x1 + ox) * k, (y1 + oy) * k),
                       ((x1 - ox) * k, (y1 - oy) * k), ((x0 - ox) * k, (y0 - oy) * k)], fill=255)
        for x, y in pts:
            circle(d, x, y, w / 2)

    def arc(cx, cy, r, x0, y0, x1, y1):
        a0, a1 = math.atan2(y0 - cy, x0 - cx), math.atan2(y1 - cy, x1 - cx)
        if a1 < a0: a1 += 2 * math.pi
        return [(cx + r * math.cos(a0 + (a1 - a0) * i / 40), cy + r * math.sin(a0 + (a1 - a0) * i / 40)) for i in range(41)]

    def ellipse_line(d, cx, cy, rx, ry, w):
        d.ellipse([(cx - rx - w / 2) * k, (cy - ry - w / 2) * k, (cx + rx + w / 2) * k, (cy + ry + w / 2) * k],
                  outline=255, width=max(1, round(w * k)))

    def gradient(x0, x1):
        # diagonal, como o linearGradient 0,0 → 1,1 do SVG sobre a caixa x0..x1
        a, b = COLORS[design]
        yy, xx = np.mgrid[0:n, 0:n] / k
        t = np.clip(((xx - x0) + (yy - x0)) / (2 * (x1 - x0)), 0, 1)[..., None]
        g = (np.array(a) * (1 - t) + np.array(b) * t).round().astype(np.uint8)
        return Image.fromarray(np.dstack([g, np.full((n, n), 255, np.uint8)]), "RGBA")

    if design == "lente":
        r = s["ring"]
        layer(lambda d: circle(d, 128, 128, r), gradient(128 - r, 128 + r))
        layer(lambda d: circle(d, 128, 128, s["core"]), CORE)
        if s["glint"]:
            layer(lambda d: polyline(d, arc(128, 128, 92, 44, 92, 92, 44), 7), WHITE, 0.22)
            layer(lambda d: polyline(d, arc(128, 128, 101, 30, 104, 64, 50), 7), WHITE, 0.75)
    else:
        b, w = s["box"], s["lines"]
        layer(lambda d: d.rounded_rectangle([b * k, b * k, (256 - b) * k - 1, (256 - b) * k - 1], s["radius"] * k, fill=255),
              gradient(b, 256 - b))
        if w:
            def globe(d):
                ellipse_line(d, 128, 128, 98, 98, w)
                ellipse_line(d, 128, 128, 44, 98, w)
                polyline(d, [(30, 128), (226, 128)], w)
                if not s.get("simple"):
                    polyline(d, [(44, 80), (212, 80)], w)
                    polyline(d, [(44, 176), (212, 176)], w)
            layer(globe, WHITE, s["line_alpha"])

    if "bitmap" not in s:
        lines, w = mk_px(size, *s["px"]) if "px" in s else (mk_lines(*s["mk"]), s["mk"][3])
        layer(lambda d: [polyline(d, p, w) for p in lines], WHITE)
    # média em alfa pré-multiplicado, para a borda não escurecer
    out = out.convert("RGBa").reduce(ss).convert("RGBA")
    if "bitmap" in s:
        rows, top = s["bitmap"]
        px = out.load()
        for y, row in enumerate(rows, start=top):
            for x, c in enumerate(row):
                a = {"#": 1.0, "+": 0.45}.get(c, 0)
                if a:
                    r_, g_, b_, al = px[x, y]
                    px[x, y] = (round(r_ + (255 - r_) * a), round(g_ + (255 - g_) * a), round(b_ + (255 - b_) * a), al)
    return out


def write_ico(path, images):
    blobs = []
    for im in images:
        b = io.BytesIO()
        im.save(b, "PNG", optimize=True)
        blobs.append(b.getvalue())
    head = struct.pack("<HHH", 0, 1, len(images))
    offset = 6 + 16 * len(images)
    entries = b""
    for im, data in zip(images, blobs):
        w = im.width if im.width < 256 else 0
        entries += struct.pack("<BBBBHHII", w, w, 0, 0, 1, 32, len(data), offset)
        offset += len(data)
    with open(path, "wb") as f:
        f.write(head + entries + b"".join(blobs))


if __name__ == "__main__":
    chosen = sys.argv[1] if len(sys.argv) > 1 else "lente"
    png_dir = sys.argv[2] if len(sys.argv) > 2 else None
    for design in DESIGNS:
        images = [render(design, size) for size in SIZES]
        write_ico(os.path.join(HERE, f"mk-{design}.ico"), images)
        if png_dir:
            os.makedirs(png_dir, exist_ok=True)
            for im in images:
                im.save(os.path.join(png_dir, f"{design}-{im.width}.png"))
    shutil.copyfile(os.path.join(HERE, f"mk-{chosen}.ico"), os.path.join(HERE, "app.ico"))
    print("ok: app.ico =", f"mk-{chosen}.ico")
