"""Generate app and tray PNG/ICO from their separate SVG sources.

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
APP_SVG = (ASSETS / "aa-desk.svg").read_text(encoding="utf-8")
TRAY_SVG = (ASSETS / "downloads-tray.svg").read_text(encoding="utf-8")
SIZES = [(16, 16), (24, 24), (32, 32), (40, 40), (48, 48), (64, 64), (256, 256)]


def render(svg, active=False):
    if active:
        colors = {"#202020": "#7ed8f5", "#7ed8f5": "#202020"}
        svg = re.sub(r"#[0-9a-fA-F]{6}", lambda match: colors.get(match.group().lower(), match.group()), svg)
    data = cairosvg.svg2png(bytestring=svg.encode("utf-8"), output_width=512, output_height=512)
    return Image.open(BytesIO(data)).convert("RGBA").resize((256, 256), Image.Resampling.LANCZOS)


for stem, svg, active in (("app", APP_SVG, False), ("tray", TRAY_SVG, False), ("tray-active", TRAY_SVG, True)):
    icon = render(svg, active)
    icon.save(ASSETS / f"{stem}.ico", format="ICO", sizes=SIZES)
    if stem != "tray-active":
        icon.save(ASSETS / f"{stem}.png")
