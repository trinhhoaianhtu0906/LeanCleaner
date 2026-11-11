"""Generates src/LeanCleaner/LeanCleaner.ico (16–256 px) and a 512 px preview PNG.

Design: rounded square with a blue→emerald gradient, a white leaf ("lean/clean")
and a small sparkle. Small sizes (<=24 px) drop the sparkle and leaf vein so the
silhouette stays crisp. Run: python installer/make-icon.py
"""
from pathlib import Path
from PIL import Image, ImageChops, ImageDraw

ROOT = Path(__file__).resolve().parent.parent
TOP, BOTTOM = (37, 99, 235), (16, 185, 129)   # blue-600 -> emerald-500
SS = 4                                          # supersampling factor


def gradient(n: int) -> Image.Image:
    """Diagonal top-left -> bottom-right gradient, n x n."""
    g = Image.new("RGB", (n, n))
    px = g.load()
    for y in range(n):
        for x in range(n):
            t = (x + y) / (2 * (n - 1))
            px[x, y] = tuple(round(a + (b - a) * t) for a, b in zip(TOP, BOTTOM))
    return g


def leaf_mask(n: int, detail: bool) -> Image.Image:
    """White leaf (intersection of two circles) rotated 45°, optional vein cut-out."""
    m = Image.new("L", (n, n), 0)
    # Leaf drawn upright around (cx, cy), then rotated 45° clockwise so it points to the top-right.
    r, off = n * 0.34, n * 0.18
    cx, cy = n * (0.53 if detail else 0.5), n * (0.47 if detail else 0.5)
    h = (r * r - off * off) ** 0.5                     # half-length of the leaf
    a = Image.new("L", (n, n), 0)
    b = Image.new("L", (n, n), 0)
    ImageDraw.Draw(a).ellipse((cx - off - r, cy - r, cx - off + r, cy + r), fill=255)
    ImageDraw.Draw(b).ellipse((cx + off - r, cy - r, cx + off + r, cy + r), fill=255)
    m.paste(ImageChops.multiply(a, b))
    d = ImageDraw.Draw(m)
    if detail:
        w = max(2, round(n * 0.024))
        d.line((cx, cy + h * 0.80, cx, cy - h * 0.45), fill=0, width=w)       # vein
        d.line((cx, cy + h * 0.80, cx, cy + h * 1.30), fill=255, width=round(n * 0.04))  # stem
    return m.rotate(-45, resample=Image.BICUBIC, center=(cx, cy))


def sparkle(draw: ImageDraw.ImageDraw, cx: float, cy: float, R: float) -> None:
    """Four-point star."""
    k = R * 0.28
    pts = [(cx, cy - R), (cx + k, cy - k), (cx + R, cy), (cx + k, cy + k),
           (cx, cy + R), (cx - k, cy + k), (cx - R, cy), (cx - k, cy - k)]
    draw.polygon(pts, fill=255)


def render(size: int) -> Image.Image:
    """One icon frame at `size` px."""
    n = size * SS
    detail = size > 24
    shape = Image.new("L", (n, n), 0)
    margin = round(n * (0.04 if size > 24 else 0.0))
    ImageDraw.Draw(shape).rounded_rectangle(
        (margin, margin, n - 1 - margin, n - 1 - margin), radius=round(n * 0.23), fill=255)

    icon = Image.new("RGBA", (n, n), (0, 0, 0, 0))
    icon.paste(gradient(n), (0, 0), shape)

    white = leaf_mask(n, detail)
    if detail:
        sparkle(ImageDraw.Draw(white), n * 0.27, n * 0.28, n * 0.10)
        sparkle(ImageDraw.Draw(white), n * 0.42, n * 0.17, n * 0.045)
    white = ImageChops.multiply(white, shape)
    icon.paste((255, 255, 255, 255), (0, 0), white)
    return icon.resize((size, size), Image.LANCZOS)


if __name__ == "__main__":
    sizes = [16, 20, 24, 32, 40, 48, 64, 128, 256]
    frames = [render(s) for s in sizes]
    ico = ROOT / "src" / "LeanCleaner" / "LeanCleaner.ico"
    frames[-1].save(ico, format="ICO", sizes=[(s, s) for s in sizes], append_images=frames[:-1])
    render(512).save(ROOT / "installer" / "icon-preview.png")
    print(ico)

