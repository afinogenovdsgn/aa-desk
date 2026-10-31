"""Generate app/tray PNG and multi-resolution ICO directly from Assets/aa-desk.svg.

Requires CairoSVG and Pillow. Install locally with:
python -m pip install --target .tools/python cairosvg pillow
The resulting PNG/ICO files are committed; Python is not needed for normal builds.
"""
from io import BytesIO
from pathlib import Path
import re
import sys

ROOT = Path(__file__).resolve().parents[1]
LOCAL_PACKAGES = ROOT / ".tools" / "python"
if LOCAL_PACKAGES.is_dir():
    sys.path.insert(0, str(LOCAL_PACKAGES))

import cairosvg
from PIL import Image

ASSETS = ROOT / "src" / "DownloadsStack" / "Assets"
SVG = (ASSETS / "aa-desk.svg").read_text(encoding="utf-8")
SIZES = [(16, 16), (24, 24), (32, 32), (40, 40), (48, 48), (64, 64), (256, 256)]


def render(active=False):
    svg = SVG
    if active:
        colors = {"#142630": "#5adbc5", "#f3faf8": "#142630", "#5adbc5": "#f3faf8"}
        svg = re.sub(r"#[0-9a-fA-F]{6}", lambda match: colors.get(match.group().lower(), match.group()), svg)
    data = cairosvg.svg2png(bytestring=svg.encode("utf-8"), output_width=512, output_height=512)
    return Image.open(BytesIO(data)).convert("RGBA").resize((256, 256), Image.Resampling.LANCZOS)


for stem, active in (("app", False), ("tray", False), ("tray-active", True)):
    icon = render(active)
    icon.save(ASSETS / f"{stem}.ico", format="ICO", sizes=SIZES)
    if stem != "tray-active":
        icon.save(ASSETS / f"{stem}.png")