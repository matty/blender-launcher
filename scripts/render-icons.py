"""Renders the app's icons from the SVG sources in docs/icon.

Writes, into src/BlenderLauncher.App/Assets/Icons:
  app.ico, blend-file.ico   PNG images at 16, 20, 24, 32, 40, 48, 64 and 256 px; the -small SVG for 24 px and below
  app-32.png                the title bar's 16 px icon (2x, so it stays sharp at 200 %)
  app-64.png                the first-run wizard's 22 px step-list icon
  app-128.png               the 52 and 64 px icons (Settings → About, the wizard's welcome page)
and, with --preview DIR, a preview sheet per icon: every size at 1:1 and enlarged, on dark and on light.

Run from the repository root (uv fetches resvg-py and Pillow into a throwaway environment):
  uv run --no-project --with resvg-py==0.5.0 --with pillow==13.0.0 scripts/render-icons.py [--preview DIR]
"""

import argparse
import io
import pathlib

import resvg_py
from PIL import Image

REPO = pathlib.Path(__file__).resolve().parent.parent
SOURCES = REPO / "docs" / "icon"
ICONS = REPO / "src" / "BlenderLauncher.App" / "Assets" / "Icons"
ICO_SIZES = [16, 20, 24, 32, 40, 48, 64, 256]
SMALL_MAX = 24  # sizes up to this use the -small SVG
APP_PNGS = [32, 64, 128]


def render(name: str, size: int) -> Image.Image:
    svg = SOURCES / (f"{name}-small.svg" if size <= SMALL_MAX else f"{name}.svg")
    png = bytes(resvg_py.svg_to_bytes(svg_path=str(svg), width=size, height=size))
    return Image.open(io.BytesIO(png)).convert("RGBA")


def preview(name: str, images: list[Image.Image], folder: pathlib.Path) -> None:
    width = sum(max(s * 4, 64) + 16 for s in ICO_SIZES) + 16
    half = 256 + 16 + 64 * 4
    sheet = Image.new("RGBA", (width, 2 * half))
    for row, background in enumerate([(32, 32, 32, 255), (243, 243, 243, 255)]):
        top = row * half
        sheet.paste(Image.new("RGBA", (width, half), background), (0, top))
        x = 16
        for size, image in zip(ICO_SIZES, images):
            if size < 256:
                sheet.alpha_composite(image, (x, top + 16))
                sheet.alpha_composite(image.resize((size * 4, size * 4), Image.NEAREST), (x, top + 16 + 64 + 8))
            else:
                sheet.alpha_composite(image, (x, top + 16))
            x += max(size * 4, 64) + 16
    folder.mkdir(parents=True, exist_ok=True)
    sheet.save(folder / f"{name}-preview.png")


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--preview", type=pathlib.Path, help="folder for the preview sheets")
    args = parser.parse_args()

    for name in ["app", "blend-file"]:
        images = [render(name, s) for s in ICO_SIZES]
        images[-1].save(ICONS / f"{name}.ico", format="ICO", sizes=[(s, s) for s in ICO_SIZES],
                        append_images=images[:-1], bitmap_format="png")
        print("wrote", (ICONS / f"{name}.ico").relative_to(REPO))
        if args.preview:
            preview(name, images, args.preview)

    for size in APP_PNGS:
        path = ICONS / f"app-{size}.png"
        render("app", size).save(path, optimize=True)
        print("wrote", path.relative_to(REPO))


if __name__ == "__main__":
    main()
