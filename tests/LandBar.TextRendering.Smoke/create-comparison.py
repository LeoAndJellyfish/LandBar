"""Build a nearest-neighbour enlargement from this test's native screenshots."""
import json
from pathlib import Path

from PIL import Image, ImageChops, ImageDraw, ImageFont

root = Path(__file__).resolve().parents[2]
primary = root / "artifacts/text-aa-check"
repeat = root / "artifacts/text-aa-check-repeat"
report = json.loads((primary / "report.json").read_text(encoding="utf-8"))
native = Image.open(primary / "desktop-native.png").convert("RGB")
later = Image.open(repeat / "desktop-native.png").convert("RGB")
stable = ImageChops.difference(native, later).getbbox() is None
canvas = Image.new("RGB", (1640, 315), "#080808")
draw = ImageDraw.Draw(canvas)
font = ImageFont.truetype("C:/Windows/Fonts/segoeui.ttf", 18)
draw.text((20, 12), "Noto Sans SC / native desktop, 100% DPI / enlarged 2x (nearest neighbour)", font=font, fill="#aaaaaa")
for col, variant in enumerate(["Ideal + Grayscale + Fixed", "Ideal + Grayscale + Animated (selected)"]):
    draw.text((20 + col * 810, 48), variant, font=font, fill="#f0f0f0")
    for row, size in enumerate([14, 15, 20]):
        sample = next(x for x in report["variants"] if x["variant"] == variant and x["size"] == size)
        rect = sample["rectangle"]
        crop = native.crop((rect["x"], rect["y"], rect["x"] + 390, rect["y"] + rect["height"]))
        canvas.paste(crop.resize((crop.width * 2, crop.height * 2), Image.Resampling.NEAREST), (20 + col * 810, 86 + row * 68))
canvas.save(root / "artifacts/text-aa-check.png")
summary = {"nativePixelStableAt650And2000ms": stable, "enlargement": "nearest neighbour 2x; no extra smoothing", "source": str(primary / "desktop-native.png")}
(primary / "repeat-check.json").write_text(json.dumps(summary, indent=2), encoding="utf-8")
print(json.dumps(summary))
if not stable:
    raise SystemExit("Native captures changed after idle.")
