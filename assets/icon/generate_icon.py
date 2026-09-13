"""
生成 GithubReleaseWatch 应用图标（PNG 多分辨率 + ICO 多分辨率）。

设计：纯蓝底（垂直渐变）+ 白色 "GRW" 字样，无其他装饰。
"""
from PIL import Image, ImageDraw, ImageFont
from pathlib import Path

SIZES_PNG = [16, 24, 32, 48, 64, 128, 256]
ICO_SIZES = [(16, 16), (24, 24), (32, 32), (48, 48), (64, 64), (128, 128), (256, 256)]

OUT_DIR = Path(__file__).resolve().parent
PNG_BASE = OUT_DIR / "GithubReleaseWatch"
ICO_OUT = OUT_DIR / "GithubReleaseWatch.ico"

# 纯蓝底配色（去掉徽章 / 渐变就用一种）
BG_COLOR = (37, 99, 235, 255)  # #2563EB —— 与界面主色保持一致
FG_WHITE = (255, 255, 255, 255)


def _draw_solid_bg(img: Image.Image):
    w, h = img.size
    px = img.load()
    for y in range(h):
        for x in range(w):
            px[x, y] = BG_COLOR


def _safe_font(size: int) -> ImageFont.FreeTypeFont:
    candidates = [
        r"C:\Windows\Fonts\segoeuib.ttf",   # Segoe UI Bold
        r"C:\Windows\Fonts\arialbd.ttf",
        r"C:\Windows\Fonts\calibrib.ttf",
    ]
    for c in candidates:
        try:
            return ImageFont.truetype(c, size)
        except OSError:
            continue
    return ImageFont.load_default()


def _make_base(size: int) -> Image.Image:
    s = max(size, 32)
    img = Image.new("RGBA", (s, s), (0, 0, 0, 0))
    _draw_solid_bg(img)

    # 圆角蒙版
    mask = Image.new("L", (s, s), 0)
    mdraw = ImageDraw.Draw(mask)
    mdraw.rounded_rectangle((0, 0, s - 1, s - 1), radius=int(s * 0.22), fill=255)
    out = Image.new("RGBA", (s, s), (0, 0, 0, 0))
    out.paste(img, (0, 0), mask=mask)

    draw = ImageDraw.Draw(out)

    # 全部尺寸统一用 "GRW"，字号按可用宽度反推，保证左右各留 ~8% 边距不裁切
    text = "GRW"
    avail = s * 0.84          # 可用宽度（左右各留 8%）
    size = int(s * 0.5)
    font = _safe_font(size)
    while size > 5:
        bbox = draw.textbbox((0, 0), text, font=font)
        if (bbox[2] - bbox[0]) <= avail:
            break
        size = int(size * 0.92)
        font = _safe_font(size)

    bbox = draw.textbbox((0, 0), text, font=font)
    tw, th = bbox[2] - bbox[0], bbox[3] - bbox[1]
    tx = (s - tw) // 2 - bbox[0]
    ty = (s - th) // 2 - bbox[1]
    draw.text((tx, ty), text, fill=FG_WHITE, font=font)

    return out


def main():
    OUT_DIR.mkdir(parents=True, exist_ok=True)
    png_images = []
    for s in SIZES_PNG:
        img = _make_base(s)
        png_path = PNG_BASE.parent / f"{PNG_BASE.stem}_{s}.png"
        img.save(png_path)
        png_images.append(img)
        print(f"saved {png_path} ({s}x{s})")

    ICO_OUT.parent.mkdir(parents=True, exist_ok=True)
    png_images[-1].save(
        ICO_OUT,
        format="ICO",
        sizes=ICO_SIZES,
        append_images=png_images[:-1],
    )
    print(f"saved {ICO_OUT}")


if __name__ == "__main__":
    main()
