"""生成 README 样板图用的演示海报（全部虚构片名，不含任何真实影视素材）。"""

import os
import random
from PIL import Image, ImageDraw, ImageFont

OUT = os.path.join(os.path.dirname(os.path.abspath(__file__)), "posters")

TITLES = [
    "雾港谜案", "山海长歌", "星轨之外", "夜行者档案", "晨昏之间",
    "第九个夏天", "玻璃回声", "南方来信", "冬泳计划", "折光之城",
    "无声剧场", "白鲸之约", "十字路口的风", "慢速飞行", "深蓝回声",
    "旧城手记", "第七码头", "雾散之前", "林间信号", "海雾档案",
    "漫长的告白", "空房间", "月光路线", "北纬四十度", "倒数三十天",
    "浮岛记事", "雨后第二街", "光年外卖", "空白档期", "远山与海",
]

PALETTES = [
    ((32, 58, 122), (94, 168, 230)),
    ((18, 78, 84), (86, 190, 168)),
    ((88, 32, 78), (226, 132, 160)),
    ((36, 36, 48), (120, 124, 160)),
    ((112, 56, 24), (232, 158, 104)),
    ((22, 46, 36), (110, 178, 120)),
    ((48, 30, 96), (150, 122, 232)),
    ((96, 26, 40), (222, 110, 118)),
]

FONT_BOLD = "C:/Windows/Fonts/msyhbd.ttc"
FONT_REG = "C:/Windows/Fonts/msyh.ttc"


def lerp(a, b, t):
    return tuple(int(round(a[i] + (b[i] - a[i]) * t)) for i in range(3))


def gradient(size, c1, c2, vertical=True):
    w, h = size
    small = Image.new("RGB", (1, h) if vertical else (w, 1))
    px = small.load()
    n = h if vertical else w
    for i in range(n):
        c = lerp(c1, c2, i / max(1, n - 1))
        if vertical:
            px[0, i] = c
        else:
            px[i, 0] = c
    return small.resize(size, Image.BILINEAR)


def make_poster(idx, title, aspect):
    w = 420
    h = int(w * aspect)

    l, r = random.Random(idx + 7)._randbelow(len(PALETTES)), None
    palette = PALETTES[idx % len(PALETTES)]
    img = gradient((w, h), palette[0], palette[1]).convert("RGBA")

    d = ImageDraw.Draw(img, "RGBA")

    # 几何装饰：大圆 + 细线，避免纯色单调
    rng = random.Random(idx * 31 + 5)
    for _ in range(3):
        cx, cy = rng.randint(-60, w + 60), rng.randint(-40, h + 40)
        rad = rng.randint(70, 190)
        d.ellipse([cx - rad, cy - rad, cx + rad, cy + rad],
                  outline=(255, 255, 255, 26), width=rng.choice([1, 2, 3]))

    # 顶部对角光带
    band = Image.new("RGBA", (w, h), (0, 0, 0, 0))
    bd = ImageDraw.Draw(band, "RGBA")
    bd.polygon([(0, h * 0.30), (w, h * 0.04), (w, h * 0.20), (0, h * 0.48)],
               fill=(255, 255, 255, 20))
    img = Image.alpha_composite(img, band)
    d = ImageDraw.Draw(img, "RGBA")

    # 底部压暗，便于压字
    shade = gradient((w, int(h * 0.55)), (0, 0, 0), (0, 0, 0)).convert("RGBA")
    shade = gradient((w, int(h * 0.55)), (10, 14, 24), (10, 14, 24)).convert("RGBA")
    mask = Image.new("L", (w, int(h * 0.55)), 0)
    mp = mask.load()
    hh = int(h * 0.55)
    for y in range(hh):
        t = y / max(1, hh - 1)
        v = int(190 * (t ** 1.5))
        for x in range(w):
            mp[x, y] = v
    shade.putalpha(mask)
    img.alpha_composite(shade, (0, h - hh))
    d = ImageDraw.Draw(img, "RGBA")

    # 片名
    fs = 46 if len(title) <= 5 else 38
    font = ImageFont.truetype(FONT_BOLD, fs)
    small = ImageFont.truetype(FONT_REG, 17)

    tw = d.textlength(title, font=font)
    d.text(((w - tw) / 2, h - hh * 0.62), title, font=font,
           fill=(255, 255, 255, 245))

    meta = f"{2024 + idx % 3} · 演示"
    mw = d.textlength(meta, font=small)
    d.text(((w - mw) / 2, h - hh * 0.62 + fs + 10), meta, font=small,
           fill=(255, 255, 255, 165))

    # 左上角小标记（描边 + 白字）
    d.rounded_rectangle([18, 18, 92, 46], radius=6, outline=(255, 255, 255, 90), width=1)
    d.text((30, 23), "DEMO", font=ImageFont.truetype(FONT_BOLD, 16),
           fill=(255, 255, 255, 170))

    return img.convert("RGB")


def main():
    os.makedirs(OUT, exist_ok=True)
    rng = random.Random(2026)

    for i, title in enumerate(TITLES, start=1):
        aspect = round(rng.uniform(1.20, 1.78), 3)   # 让瀑布流真正参差
        img = make_poster(i, title, aspect)
        img.save(os.path.join(OUT, f"{i:03d}.jpg"), quality=88)

    print(f"{len(TITLES)} posters -> {OUT}")


if __name__ == "__main__":
    main()
