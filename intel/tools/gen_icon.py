"""Generate EveDeck Intel's launcher icon from the shared EveDeck mark.

EveDeck Intel is a sibling of the desktop app, not a separate brand, so the icon is the EveDeck
mark with an INTEL plate under it. That plate is the only thing distinguishing the two in a
launcher, so it has to stay legible at 48dp.

Everything is laid out against Android's adaptive-icon geometry: the canvas is 108dp and only the
central 66dp is guaranteed to survive masking. A circular mask is the most aggressive one shipped,
so the plate is sized against the circle's chord at its own baseline rather than against the
square -- see `_chord_half_width`. Get this wrong and the plate's bottom corners get clipped on
exactly the devices that use circular icons.

Usage:
    python tools/gen_icon.py
"""

from __future__ import annotations

import math
import os
import sys

from PIL import Image, ImageChops, ImageDraw, ImageFont

HERE = os.path.dirname(os.path.abspath(__file__))
RES = os.path.join(HERE, "..", "android", "src", "main", "res")
MARK = os.path.join(RES, "drawable", "evedeck_mark.png")

# Adaptive icon geometry, in pixels at xxxhdpi (4px per dp).
DP = 4
CANVAS = 108 * DP          # 432
SAFE_DIAMETER = 66 * DP    # 264
CENTER = CANVAS / 2
SAFE_RADIUS = SAFE_DIAMETER / 2

ACCENT = (41, 182, 246, 255)     # EveDeck cyan
PLATE_TEXT = (6, 16, 24, 255)    # near-black, reads as a cut-out on the cyan

FONT_CANDIDATES = [
    ("C:/Windows/Fonts/bahnschrift.ttf", "Bold Condensed"),
    ("C:/Windows/Fonts/arialbd.ttf", None),
    ("/usr/share/fonts/truetype/dejavu/DejaVuSans-Bold.ttf", None),
]


def _load_font(size: int) -> ImageFont.FreeTypeFont:
    """First available candidate, asking for a condensed bold cut where the face offers one."""
    for path, variation in FONT_CANDIDATES:
        if not os.path.exists(path):
            continue
        font = ImageFont.truetype(path, size)
        if variation:
            try:
                font.set_variation_by_name(variation)
            except (OSError, AttributeError):
                pass  # Static face, or Pillow built without variable-font support.
        return font
    raise SystemExit("no usable font found; add one to FONT_CANDIDATES")


def _chord_half_width(y: float) -> float:
    """Half-width of the safe circle at a given y. Zero outside it."""
    dy = abs(y - CENTER)
    if dy >= SAFE_RADIUS:
        return 0.0
    return math.sqrt(SAFE_RADIUS**2 - dy**2)


def _trim_alpha(image: Image.Image) -> Image.Image:
    """Crop to visible pixels.

    Crops on the alpha band specifically: `Image.getbbox()` on an RGBA image counts colour data
    sitting under zero alpha, which leaves the generated mark off-centre.
    """
    bbox = image.getchannel("A").getbbox()
    return image.crop(bbox) if bbox else image


def _fit(image: Image.Image, box: int) -> Image.Image:
    scale = box / max(image.size)
    size = (max(1, round(image.width * scale)), max(1, round(image.height * scale)))
    return image.resize(size, Image.LANCZOS)


def build_foreground() -> Image.Image:
    canvas = Image.new("RGBA", (CANVAS, CANVAS), (0, 0, 0, 0))

    mark = _fit(_trim_alpha(Image.open(MARK).convert("RGBA")), 180)
    mark_center_y = 176
    canvas.alpha_composite(
        mark,
        (round(CENTER - mark.width / 2), round(mark_center_y - mark.height / 2)),
    )

    # Plate, sized so its lowest corners still sit inside the circular mask.
    plate_top, plate_bottom = 272, 320
    half = min(_chord_half_width(plate_top), _chord_half_width(plate_bottom)) - 2
    plate = (CENTER - half, plate_top, CENTER + half, plate_bottom)

    draw = ImageDraw.Draw(canvas)
    draw.rounded_rectangle(plate, radius=(plate_bottom - plate_top) / 2, fill=ACCENT)

    _draw_tracked_text(draw, "INTEL", plate, available=half * 2 - 26)
    return canvas


def _draw_tracked_text(draw: ImageDraw.ImageDraw, text: str, plate, available: float) -> None:
    """Letter-spaced text, scaled down until it fits the plate. Tracking earns legibility at 48dp."""
    size, tracking = 44, 5
    while size > 10:
        font = _load_font(size)
        widths = [draw.textlength(ch, font=font) for ch in text]
        total = sum(widths) + tracking * (len(text) - 1)
        if total <= available:
            break
        size -= 1
    else:
        raise SystemExit("INTEL will not fit the plate")

    ascent, descent = font.getmetrics()
    x = (plate[0] + plate[2]) / 2 - total / 2
    y = (plate[1] + plate[3]) / 2 - (ascent + descent) / 2
    for ch, width in zip(text, widths):
        draw.text((x, y), ch, font=font, fill=PLATE_TEXT)
        x += width + tracking


def build_monochrome(foreground: Image.Image) -> Image.Image:
    """Themed-icon layer (Android 13+): one flat colour, the system supplies the hue.

    Keying off the mark's outline gives a shapeless blob, because a flat fill throws away all the
    interior structure that makes the mark recognisable. So the silhouette is cut from *luminance*
    instead: the mark is lit, and its bright parts are exactly its features -- the cube, the orbital
    ring, the star and the card edges. Everything dim falls away and the shape survives.

    The plate is handled separately, as a solid lozenge with INTEL punched back out as negative
    space, so the wording stays readable at any tint.
    """
    rgb = foreground.convert("RGB")
    luminance = rgb.convert("L")
    opaque = foreground.getchannel("A").point(lambda v: 255 if v > 90 else 0)
    lit = luminance.point(lambda v: 255 if v > 104 else 0)
    mask = Image.composite(lit, Image.new("L", foreground.size, 0), opaque)

    mono = Image.new("RGBA", foreground.size, (255, 255, 255, 0))
    mono.putalpha(mask)

    # Redraw the plate over whatever luminance made of it: solid, with the text cut out.
    plate_solid = _plate_mask(foreground, ACCENT[:3])
    plate_text = _plate_mask(foreground, PLATE_TEXT[:3])
    mask = mono.getchannel("A")
    mask.paste(255, (0, 0), plate_solid)
    mask.paste(0, (0, 0), plate_text)
    mono.putalpha(mask)
    return mono


def _plate_mask(image: Image.Image, colour: tuple[int, int, int]) -> Image.Image:
    """Mask of pixels painted in exactly `colour` -- the plate is drawn flat, so this is exact."""
    r, g, b, a = image.split()
    matches = [
        band.point(lambda v, target=value: 255 if v == target else 0)
        for band, value in ((r, colour[0]), (g, colour[1]), (b, colour[2]))
    ]
    combined = matches[0]
    for extra in matches[1:]:
        combined = ImageChops.multiply(combined, extra)
    return ImageChops.multiply(combined, a.point(lambda v: 255 if v > 200 else 0))


def main() -> int:
    foreground = build_foreground()
    out_fg = os.path.join(RES, "drawable", "ic_launcher_foreground.png")
    foreground.save(out_fg)
    print("wrote", os.path.relpath(out_fg, HERE))

    mono = build_monochrome(foreground)
    out_mono = os.path.join(RES, "drawable", "ic_launcher_monochrome.png")
    mono.save(out_mono)
    print("wrote", os.path.relpath(out_mono, HERE))

    preview = Image.new("RGBA", (CANVAS, CANVAS), (11, 15, 20, 255))
    preview.alpha_composite(foreground)
    out_preview = os.path.join(HERE, "icon-preview.png")
    preview.save(out_preview)
    print("wrote", os.path.relpath(out_preview, HERE))
    return 0


if __name__ == "__main__":
    sys.exit(main())
