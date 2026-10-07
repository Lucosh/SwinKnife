"""Genera il logo di SwinKnife: un coltellino svizzero con il logo di Windows.

Uso: python tools/icon/make_icon.py
Scrive src/SwinKnife/Assets/swinknife.png (256 px) e swinknife.ico (16-256 px).
"""
import math
from pathlib import Path

import numpy as np
from PIL import Image, ImageDraw, ImageFilter

S = 8                     # supersampling: si disegna a 2048 px e poi si riduce
W = 256 * S
ASSETS = Path(__file__).resolve().parents[2] / "src" / "SwinKnife" / "Assets"


def hexc(h):
    h = h.lstrip("#")
    return tuple(int(h[i:i + 2], 16) for i in (0, 2, 4))


def bezier(p0, p1, p2, n=40):
    return [((1 - t) ** 2 * p0[0] + 2 * (1 - t) * t * p1[0] + t * t * p2[0],
             (1 - t) ** 2 * p0[1] + 2 * (1 - t) * t * p1[1] + t * t * p2[1])
            for t in (i / n for i in range(n + 1))]


def place(points, pivot, angle_deg):
    """Ruota i punti (coordinate locali dell'attrezzo) attorno al perno."""
    a = math.radians(angle_deg)
    c, s = math.cos(a), math.sin(a)
    return [((pivot[0] + x * c - y * s) * S, (pivot[1] + x * s + y * c) * S) for x, y in points]


def capsule(x0, y0, x1, y1):
    r = (y1 - y0) / 2
    cy = y0 + r
    pts = [(x1 - r + r * math.cos(math.radians(a)), cy + r * math.sin(math.radians(a))) for a in range(-90, 91, 3)]
    pts += [(x0 + r + r * math.cos(math.radians(a)), cy + r * math.sin(math.radians(a))) for a in range(90, 271, 3)]
    return [(x * S, y * S) for x, y in pts]


def mask_of(poly):
    m = Image.new("L", (W, W), 0)
    ImageDraw.Draw(m).polygon(poly, fill=255)
    return m


def gradient(p0, p1, c0, c1):
    """Gradiente lineare da p0 (colore c0) a p1 (colore c1), coordinate a 256."""
    yy, xx = np.mgrid[0:W, 0:W].astype(np.float32) / S
    d = np.array(p1, np.float32) - np.array(p0, np.float32)
    t = ((xx - p0[0]) * d[0] + (yy - p0[1]) * d[1]) / float(d @ d)
    t = np.clip(t, 0, 1)[..., None]
    rgb = np.array(hexc(c0), np.float32) * (1 - t) + np.array(hexc(c1), np.float32) * t
    a = np.full((W, W, 1), 255, np.float32)
    return Image.fromarray(np.concatenate([rgb, a], 2).astype(np.uint8), "RGBA")


def fill(img, poly, fill_img, outline=None, width=0):
    img.paste(fill_img, (0, 0), mask_of(poly))
    if outline:
        ImageDraw.Draw(img).line(poly + [poly[0]], fill=outline, width=width * S, joint="curve")


def steel(pivot, angle, across):
    """Gradiente acciaio perpendicolare all'attrezzo."""
    a = math.radians(angle)
    nx, ny = -math.sin(a) * across, math.cos(a) * across
    return gradient((pivot[0] - nx, pivot[1] - ny), (pivot[0] + nx, pivot[1] + ny), "#FFFFFF", "#9AA5B3")


def build():
    img = Image.new("RGBA", (W, W), (0, 0, 0, 0))
    edge = hexc("#5E6B7A")

    # --- lama grande: perno a sinistra, punta in alto a sinistra
    piv_l, ang_l = (68, 178), -114
    blade = [(-8, -12), (104, -12)] + bezier((104, -12), (140, -12), (156, -4)) \
        + bezier((156, -4), (120, 14), (60, 13))[1:] + [(-8, 13)]
    blade = place(blade, piv_l, ang_l)
    fill(img, blade, steel(piv_l, ang_l, 13), edge, 2)
    # filo della lama (fascia chiara lungo il tagliente)
    bevel = place(bezier((150, -3), (118, 9), (60, 9)) + bezier((60, 13), (120, 14), (156, -4)), piv_l, ang_l)
    img.paste(Image.new("RGBA", (W, W), hexc("#E9EEF4") + (255,)), (0, 0), mask_of(bevel))

    # --- lama piccola, quasi chiusa, per dare profondità
    piv_m, ang_m = (188, 178), -32
    small = [(-6, -7), (70, -7)] + bezier((70, -7), (92, -6), (100, -1)) + bezier((100, -1), (76, 8), (40, 7))[1:] + [(-6, 7)]
    small = place(small, piv_m, ang_m)
    fill(img, small, steel(piv_m, ang_m, 7), edge, 2)

    # --- cacciavite / apribottiglie: perno a destra, punta in alto a destra
    piv_r, ang_r = (188, 178), -66
    tool = [(-8, -9), (82, -9), (96, -3), (96, 3), (82, 9), (40, 9), (36, 3), (28, 3), (24, 9), (-8, 9)]
    tool = place(tool, piv_r, ang_r)
    fill(img, tool, steel(piv_r, ang_r, 9), edge, 2)

    # --- manico blu Windows con ombra
    handle = capsule(40, 150, 216, 206)
    shadow = Image.new("RGBA", (W, W), (0, 0, 0, 0))
    shadow.paste(Image.new("RGBA", (W, W), (0, 30, 70, 110)), (0, 0), mask_of([(x, y + 5 * S) for x, y in handle]))
    shadow = shadow.filter(ImageFilter.GaussianBlur(5 * S))
    img = Image.alpha_composite(shadow, img)
    fill(img, handle, gradient((0, 150), (0, 206), "#3AA0F5", "#0058B8"), hexc("#00468F"), 2)
    # riflesso sulla metà superiore
    gloss = capsule(48, 154, 208, 176)
    img = Image.alpha_composite(img, _tinted(gloss, (255, 255, 255, 46)))

    # --- perni
    d = ImageDraw.Draw(img)
    for cx, cy in (piv_l, piv_r):
        r = 6
        d.ellipse([(cx - r) * S, (cy - r) * S, (cx + r) * S, (cy + r) * S], fill=hexc("#DCE3EA"), outline=hexc("#00468F"), width=S * 2)

    # --- logo di Windows (quattro quadrati) al centro del manico
    cx, cy, q, g = 128, 178, 15, 3
    for ox in (-1, 1):
        for oy in (-1, 1):
            x0 = cx + (g / 2 if ox > 0 else -g / 2 - q)
            y0 = cy + (g / 2 if oy > 0 else -g / 2 - q)
            d.rectangle([x0 * S, y0 * S, (x0 + q) * S - 1, (y0 + q) * S - 1], fill=(255, 255, 255))

    # centra il disegno nel quadrato con un piccolo margine
    bbox = img.getbbox()
    crop = img.crop(bbox)
    side = int(max(crop.size) * 1.1)
    out = Image.new("RGBA", (side, side), (0, 0, 0, 0))
    out.paste(crop, ((side - crop.width) // 2, (side - crop.height) // 2))
    return out


def _tinted(poly, rgba):
    layer = Image.new("RGBA", (W, W), (0, 0, 0, 0))
    layer.paste(Image.new("RGBA", (W, W), rgba), (0, 0), mask_of(poly))
    return layer


def main():
    big = build()
    png = big.resize((256, 256), Image.LANCZOS)
    png.save(ASSETS / "swinknife.png")
    sizes = [16, 20, 24, 32, 40, 48, 64, 128, 256]
    png.save(ASSETS / "swinknife.ico", sizes=[(s, s) for s in sizes])
    print("ok")


if __name__ == "__main__":
    main()
