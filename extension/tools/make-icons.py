"""Generates the extension icons and Chrome Web Store images.

Usage (needs Pillow):  python extension/tools/make-icons.py
Writes:
  extension/icons/icon{16,32,48,128}.png        packaged with the extension (manifest "icons")
  extension/store-assets/store-icon-128.png     Web Store listing icon (96x96 artwork, 16px transparent padding)
  extension/store-assets/small-promo-440x280.png Web Store small promo tile
Design: white shield with a check mark on a navy rounded square.
"""
from pathlib import Path

from PIL import Image, ImageDraw, ImageFont

NAVY = (31, 78, 140, 255)
WHITE = (255, 255, 255, 255)
SCALE = 1024  # draw large, then downsample for clean edges

EXT_DIR = Path(__file__).resolve().parent.parent


def shield_points(cx, top, width, height):
    """Shield outline: flat top with rounded shoulders, sides tapering to a point."""
    half = width / 2
    return [
        (cx - half, top + height * 0.08),
        (cx, top),
        (cx + half, top + height * 0.08),
        (cx + half, top + height * 0.45),
        (cx + half * 0.72, top + height * 0.72),
        (cx, top + height),
        (cx - half * 0.72, top + height * 0.72),
        (cx - half, top + height * 0.45),
    ]


def draw_badge(size, padding=0):
    """Navy rounded square with the shield, `size` px, artwork inset by `padding` px (transparent)."""
    art = size - 2 * padding
    img = Image.new("RGBA", (SCALE, SCALE), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)
    d.rounded_rectangle([0, 0, SCALE - 1, SCALE - 1], radius=int(SCALE * 0.2), fill=NAVY)

    cx = SCALE / 2
    d.polygon(shield_points(cx, SCALE * 0.16, SCALE * 0.56, SCALE * 0.70), fill=WHITE)

    # Check mark in navy inside the shield.
    w = int(SCALE * 0.07)
    d.line([(SCALE * 0.37, SCALE * 0.47), (SCALE * 0.47, SCALE * 0.58), (SCALE * 0.65, SCALE * 0.38)],
           fill=NAVY, width=w, joint="curve")
    for x, y in [(SCALE * 0.37, SCALE * 0.47), (SCALE * 0.65, SCALE * 0.38)]:
        d.ellipse([x - w / 2, y - w / 2, x + w / 2, y + w / 2], fill=NAVY)

    art_img = img.resize((art, art), Image.LANCZOS)
    out = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    out.paste(art_img, (padding, padding), art_img)
    return out


def promo_tile(width=440, height=280):
    tile = Image.new("RGBA", (width, height), NAVY)
    badge = draw_badge(150)
    # White rounded backing so the navy badge stands out on the navy tile.
    back = Image.new("RGBA", (170, 170), (0, 0, 0, 0))
    ImageDraw.Draw(back).rounded_rectangle([0, 0, 169, 169], radius=34, fill=WHITE)
    tile.paste(back, (30, (height - 170) // 2), back)
    tile.paste(badge, (40, (height - 150) // 2), badge)

    d = ImageDraw.Draw(tile)
    try:
        font_big = ImageFont.truetype("DejaVuSans-Bold.ttf", 28)
        font_small = ImageFont.truetype("DejaVuSans.ttf", 17)
    except OSError:
        font_big = font_small = ImageFont.load_default()
    d.text((220, 105), "Chromebook", font=font_big, fill=WHITE)
    d.text((220, 140), "District device agent", font=font_small, fill=WHITE)
    return tile.convert("RGB")


def main():
    icons = EXT_DIR / "icons"
    store = EXT_DIR / "store-assets"
    icons.mkdir(exist_ok=True)
    store.mkdir(exist_ok=True)

    for size in (16, 32, 48, 128):
        draw_badge(size).save(icons / f"icon{size}.png", optimize=True)
    draw_badge(128, padding=16).save(store / "store-icon-128.png", optimize=True)
    promo_tile().save(store / "small-promo-440x280.png", optimize=True)
    print(f"Wrote icons to {icons} and store images to {store}")


if __name__ == "__main__":
    main()
