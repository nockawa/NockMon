"""Generates NockMon's application icon (exe, installer, shortcuts) and a PNG for docs and packages.

Same look as the live taskbar icon (src/NockMon.App/Shell/BarIcon.cs): a dark square, a grey border and blue bars of a
CPU history. Small sizes are drawn pixel-exact like BarIcon (one-pixel border, one column per value); large sizes get
rounded corners and separate bars.

Usage: python tools/icon/make_icon.py   (needs Pillow)
"""
import math
from pathlib import Path

from PIL import Image, ImageDraw

BORDER = (0x6E, 0x6E, 0x6E, 255)
BACKGROUND = (0x14, 0x14, 0x14, 255)
BAR = (0x4C, 0xC2, 0xFF, 255)

ROOT = Path(__file__).resolve().parents[2]
ICO = ROOT / "src" / "NockMon.App" / "Assets" / "NockMon.ico"
PNG = ROOT / "docs" / "images" / "icon.png"


# A CPU history with ups and downs (rising bars alone read as a signal-strength meter), oldest first.
HISTORY = [0.34, 0.52, 0.40, 0.66, 0.48, 0.30, 0.58, 0.82, 0.56, 0.38, 0.72, 0.90, 0.62, 0.76]


def history(n):
    """HISTORY resampled to n values (linear interpolation)."""
    if n == 1:
        return [HISTORY[-1]]
    values = []
    for i in range(n):
        t = i * (len(HISTORY) - 1) / (n - 1)
        lo = math.floor(t)
        hi = min(lo + 1, len(HISTORY) - 1)
        values.append(HISTORY[lo] + (HISTORY[hi] - HISTORY[lo]) * (t - lo))
    return values


def small(size):
    """Pixel-exact, like BarIcon: one-pixel border, one bar per pixel column."""
    image = Image.new("RGBA", (size, size), BACKGROUND)
    pixels = image.load()
    for i in range(size):
        pixels[i, 0] = pixels[i, size - 1] = pixels[0, i] = pixels[size - 1, i] = BORDER
    inner = size - 2
    for x, value in enumerate(history(inner)):
        for y in range(round(value * inner)):
            pixels[1 + x, size - 2 - y] = BAR
    return image


def large(size):
    """Rounded square, border and separate bars, drawn at 4x then downsampled for clean edges."""
    scale = 4
    s = size * scale
    image = Image.new("RGBA", (s, s), (0, 0, 0, 0))
    draw = ImageDraw.Draw(image)
    radius = s * 0.16
    border = max(scale, round(s * 0.035))
    draw.rounded_rectangle((0, 0, s - 1, s - 1), radius=radius, fill=BORDER)
    draw.rounded_rectangle((border, border, s - 1 - border, s - 1 - border), radius=radius - border, fill=BACKGROUND)
    pad = s * 0.17
    bars = 9
    width = (s - 2 * pad) / bars
    gap = width * 0.22
    floor = s - pad
    for i, value in enumerate(history(bars)):
        left = pad + i * width + gap / 2
        top = floor - value * (s - 2 * pad)
        draw.rounded_rectangle((left, top, left + width - gap, floor), radius=(width - gap) * 0.18, fill=BAR)
    return image.resize((size, size), Image.LANCZOS)


def render(size):
    return small(size) if size < 40 else large(size)


def main():
    sizes = [16, 20, 24, 32, 40, 48, 64, 256]
    frames = [render(size) for size in sizes]
    ICO.parent.mkdir(parents=True, exist_ok=True)
    # Pillow writes one entry per size from the frames given (PNG-compressed for 256).
    frames[-1].save(ICO, format="ICO", sizes=[(s, s) for s in sizes], append_images=frames[:-1])
    render(256).save(PNG)
    print(f"{ICO}\n{PNG}")


if __name__ == "__main__":
    main()
