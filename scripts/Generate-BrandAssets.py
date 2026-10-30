"""Render the original LandBar capsule mark at each Windows icon size. Requires Pillow."""
from io import BytesIO
from pathlib import Path
import struct
from PIL import Image, ImageDraw

ROOT = Path(__file__).resolve().parent.parent
BRAND = ROOT / "src/LandBar/Assets/Brand"
SIZES = (16, 20, 24, 32, 40, 48, 64, 128, 256)


def render(size):
    # Draw every target independently: the 16px wave retains its complete three-bar silhouette.
    scale = 8
    canvas = Image.new("RGBA", (size * scale, size * scale))
    draw = ImageDraw.Draw(canvas)
    unit = size * scale / 16
    box = tuple(round(v * unit) for v in (0.5, 2.5, 15.5, 13.5))
    draw.rounded_rectangle(box, radius=5.5 * unit, fill="#BEDDC9", outline="#24352C", width=max(1, round(unit)))
    for x, top, bottom in ((4.5, 6, 10), (7.25, 4.5, 11.5), (10, 5.5, 10.5)):
        draw.rounded_rectangle(tuple(round(v * unit) for v in (x, top, x + 1.5, bottom)),
                               radius=0.75 * unit, fill="#18231D")
    return canvas.resize((size, size), Image.Resampling.LANCZOS)


def main():
    BRAND.mkdir(parents=True, exist_ok=True)
    payloads = []
    for size in SIZES:
        buffer = BytesIO()
        render(size).save(buffer, format="PNG")
        payloads.append((size, buffer.getvalue()))
    header = struct.pack("<HHH", 0, 1, len(payloads))
    offset = 6 + 16 * len(payloads)
    entries = bytearray()
    for size, data in payloads:
        entries.extend(struct.pack("<BBBBHHII", size if size < 256 else 0,
                                   size if size < 256 else 0, 0, 0, 1, 32, len(data), offset))
        offset += len(data)
    (BRAND / "LandBar.ico").write_bytes(header + entries + b"".join(data for _, data in payloads))
    for name, size in (("Square44x44Logo.png", 44), ("Square150x150Logo.png", 150), ("StoreLogo.png", 50)):
        render(size).save(ROOT / "packaging/Assets" / name)
    (BRAND / "LandBar.svg").write_text('''<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 16 16">
  <rect x="0.5" y="2.5" width="15" height="11" rx="5.5" fill="#BEDDC9" stroke="#24352C" stroke-width="1"/>
  <g fill="#18231D"><rect x="4.5" y="6" width="1.5" height="4" rx="0.75"/>
  <rect x="7.25" y="4.5" width="1.5" height="7" rx="0.75"/>
  <rect x="10" y="5.5" width="1.5" height="5" rx="0.75"/></g>
</svg>
''', encoding="utf-8")
    print("Generated LandBar icon frames: " + ", ".join(map(str, SIZES)))


if __name__ == "__main__":
    main()
