# Icon

The sources of `src/BlenderLauncher.App/Assets/Icons/app.ico` and `blend-file.ico`: an isometric box (a Blender build) whose orange (`#e87d0d`) lid is lifting open, showing a warm glow inside. The box sides are blue (`#3d7cc0`, `#265787`). It is a nod to Blender's default cube, not its logo.

| File | Used for |
|---|---|
| `app.svg` | The app icon at 32 px and above, and the README logo (`.github/images/logo.svg` is a copy) |
| `app-small.svg` | The app icon at 16, 20 and 24 px (flat colours, no inner walls or lid highlight) |
| `blend-file.svg` | The `.blend` association's icon at 32 px and above: a light page with a folded corner and the box on its lower part |
| `blend-file-small.svg` | The `.blend` icon at 16, 20 and 24 px (a heavier page outline and a larger, flat box) |

Each `.ico` holds PNG images at 16, 20, 24, 32, 40, 48, 64 and 256 px, each rendered from the SVG for its size. The app also shows the icon inside its own windows from `app-32.png` (title bar), `app-64.png` (first-run wizard step list) and `app-128.png` (Settings → About, the wizard's welcome page), rendered from `app.svg`.

All of them come from one script. After changing an SVG, run it from the repository root and commit the regenerated files:

```
uv run --no-project --with resvg-py==0.5.0 --with pillow==13.0.0 scripts/render-icons.py --preview artifacts/icon-preview
```

Check the preview sheets (each size at 1:1 and enlarged, on dark and on light) before committing, especially the small sizes.

If you change `app.svg`, copy it to `.github/images/logo.svg` too.
