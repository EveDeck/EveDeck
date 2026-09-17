"""Generate the daemon's Windows icon and tray images from the shared launcher artwork.

The tray and the taskbar are the only places the daemon is ever seen, so both come from the same
source as the tablet's launcher icon (`gen_icon.build_foreground`) -- the desktop daemon and the
tablet app are one product and should not look like two.

Android's adaptive canvas carries a lot of mask padding that a desktop icon does not need, so the
artwork is trimmed to its own ink and re-margined. The tray renders at 16px on a 100% display, far
below what the INTEL plate survives, so the tray image is the mark alone.

Usage:
    python tools/gen_desktop_icon.py
"""

from __future__ import annotations

import os
import sys

from PIL import Image

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)

from gen_icon import MARK, _fit, _trim_alpha, build_foreground  # noqa: E402

PACKAGING = os.path.join(HERE, "..", "packaging")
RESOURCES = os.path.join(HERE, "..", "daemon", "src", "main", "resources")

# Windows renders .ico at every one of these; leaving a size out makes Explorer upscale a smaller
# one and the plate turns to mush.
ICO_SIZES = [16, 24, 32, 48, 64, 128, 256]


def _square(art: Image.Image, box: int, margin: float) -> Image.Image:
    """Centre `art` on a transparent `box` canvas, occupying `1 - 2 * margin` of it."""
    canvas = Image.new("RGBA", (box, box), (0, 0, 0, 0))
    fitted = _fit(art, round(box * (1 - 2 * margin)))
    canvas.alpha_composite(
        fitted,
        ((box - fitted.width) // 2, (box - fitted.height) // 2),
    )
    return canvas


def main() -> int:
    full = _square(_trim_alpha(build_foreground()), 512, margin=0.06)
    out_ico = os.path.join(PACKAGING, "eveintel.ico")
    full.save(out_ico, sizes=[(size, size) for size in ICO_SIZES])
    print("wrote", os.path.relpath(out_ico, HERE))

    # The tray drops the INTEL plate: at 16px it is a grey smear, and the mark alone still reads.
    mark = _trim_alpha(Image.open(MARK).convert("RGBA"))
    for size in (16, 20, 24, 32, 40, 48, 64):
        out_tray = os.path.join(RESOURCES, f"tray-{size}.png")
        _square(mark, size, margin=0.0).save(out_tray)
    print(f"wrote {len(os.listdir(RESOURCES))} tray images to daemon resources")

    out_window = os.path.join(RESOURCES, "icon-256.png")
    _square(_trim_alpha(build_foreground()), 256, margin=0.06).save(out_window)
    print("wrote", os.path.relpath(out_window, HERE))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
