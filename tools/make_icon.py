"""生成 Huanying 应用图标（WinUI 3 / Fluent Design 风格）。

图形母题沿用播放器客户端：圆角方块 + 播放三角 + 环绕弧。
输出：
  Assets/Huanying.ico          多尺寸图标（exe / 窗口）
  Assets/Huanying.png          256px 主图标（README / 商店）
  Assets/Huanying-1024.png     1024px 大图（宣传用）
"""

import math
import os
from PIL import Image, ImageDraw, ImageFilter

OUT = os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))), "Assets")
S = 512          # 渲染基准尺寸
SS = 1024        # 实际超采样渲染尺寸


def lerp(a, b, t):
    return tuple(int(round(a[i] + (b[i] - a[i]) * t)) for i in range(3))


def make_gradient(size, top, bottom):
    """垂直渐变。"""
    img = Image.new("RGB", (1, size))
    px = img.load()
    for y in range(size):
        px[0, y] = lerp(top, bottom, y / (size - 1))
    return img.resize((size, size), Image.BILINEAR)


def radial(size, cx, cy, r, power=1.6):
    """径向渐变蒙版（左上高光 / 暗角用）。"""
    m = Image.new("L", (size, size), 0)
    px = m.load()
    for y in range(size):
        dy = y - cy
        for x in range(size):
            d = math.hypot(x - cx, dy) / r
            if d < 1.0:
                px[x, y] = int(255 * (1.0 - d) ** power)
    return m


def rounded_square_mask(size, radius_ratio=0.225):
    m = Image.new("L", (size, size), 0)
    d = ImageDraw.Draw(m)
    d.rounded_rectangle([0, 0, size - 1, size - 1], radius=int(size * radius_ratio), fill=255)
    return m


def rounded_polygon(points, radius, steps=16):
    """把折线多边形转成圆角多边形的密集点集。

    每个顶点取两条邻边方向，向内缩进 r / tan(θ/2) 得到切点，再用二次贝塞尔近似圆弧。
    直接填充这个点集即可得到干净的圆角，不会出现锐角外露。
    """
    n = len(points)
    out = []

    for i in range(n):
        prev = points[(i - 1) % n]
        cur = points[i]
        nxt = points[(i + 1) % n]

        v1 = (prev[0] - cur[0], prev[1] - cur[1])
        v2 = (nxt[0] - cur[0], nxt[1] - cur[1])
        l1 = math.hypot(*v1) or 1.0
        l2 = math.hypot(*v2) or 1.0
        d1 = (v1[0] / l1, v1[1] / l1)
        d2 = (v2[0] / l2, v2[1] / l2)

        cos_a = max(-1.0, min(1.0, d1[0] * d2[0] + d1[1] * d2[1]))
        angle = math.acos(cos_a)                 # 两邻边夹角
        half = angle / 2.0
        tan_half = math.tan(half)

        # 夹角过大时 tan 爆掉，退化成小圆角
        t = radius / tan_half if tan_half > 1e-6 else radius * 4
        t = min(t, l1 * 0.45, l2 * 0.45)

        p1 = (cur[0] + d1[0] * t, cur[1] + d1[1] * t)
        p2 = (cur[0] + d2[0] * t, cur[1] + d2[1] * t)

        # 二次贝塞尔 p1 -> cur -> p2
        for s in range(steps + 1):
            u = s / steps
            x = (1 - u) ** 2 * p1[0] + 2 * (1 - u) * u * cur[0] + u ** 2 * p2[0]
            y = (1 - u) ** 2 * p1[1] + 2 * (1 - u) * u * cur[1] + u ** 2 * p2[1]
            out.append((x, y))

    return out


def build(size=SS):
    u = size / 1000.0  # 以 1000 为设计基准

    # ---------- 底：圆角方块 + 渐变 ----------
    bg = make_gradient(size, (86, 197, 255), (13, 60, 168)).convert("RGBA")

    # 左上柔光：Fluent 的 acrylic 高光
    hi = Image.new("RGBA", (size, size), (255, 255, 255, 0))
    hi.putalpha(radial(size // 2, size * 0.34, size * 0.24, size * 0.62, 2.2)
                .resize((size, size), Image.BILINEAR).point(lambda v: int(v * 0.42)))
    bg = Image.alpha_composite(bg, hi)

    # 右下轻微压暗，增加体积
    lo = Image.new("RGBA", (size, size), (4, 18, 60, 0))
    lo.putalpha(radial(size // 2, size * 0.86, size * 0.92, size * 0.75, 2.0)
                .resize((size, size), Image.BILINEAR).point(lambda v: int(v * 0.30)))
    bg = Image.alpha_composite(bg, lo)

    # 圆角裁切
    mask = rounded_square_mask(size)
    canvas = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    canvas.paste(bg, (0, 0), mask)

    draw = ImageDraw.Draw(canvas, "RGBA")

    # 内描边：顶部亮、底部暗，模拟玻璃边缘
    inset = max(1, int(1.2 * u))
    draw.rounded_rectangle(
        [inset, inset, size - 1 - inset, size - 1 - inset],
        radius=int(size * 0.225) - inset,
        outline=(255, 255, 255, 64),
        width=max(1, int(1.6 * u)),
    )

    # ---------- 环绕弧 ----------
    cx, cy = size * 0.5, size * 0.5
    r = size * 0.335
    width = int(size * 0.026)
    box = [cx - r, cy - r, cx + r, cy + r]

    arc = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    ad = ImageDraw.Draw(arc, "RGBA")
    start, end = 34, 326          # 缺口朝向正右（播放方向）
    ad.arc(box, start=start, end=end, fill=(255, 255, 255, 54), width=width)


    canvas = Image.alpha_composite(canvas, arc)
    draw = ImageDraw.Draw(canvas, "RGBA")

    # ---------- 播放三角 ----------
    tri_raw = [
        (size * 0.372, size * 0.278),
        (size * 0.722, size * 0.500),
        (size * 0.372, size * 0.722),
    ]
    tri = rounded_polygon(tri_raw, size * 0.030)

    # 阴影：同形状下沉一层再模糊
    shadow = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    sd = ImageDraw.Draw(shadow, "RGBA")
    off = int(size * 0.014)
    sd.polygon([(x, y + off) for x, y in tri], fill=(6, 22, 64, 95))
    shadow = shadow.filter(ImageFilter.GaussianBlur(size * 0.014))
    canvas = Image.alpha_composite(canvas, shadow)
    draw = ImageDraw.Draw(canvas, "RGBA")

    # 三角本体
    draw.polygon(tri, fill=(255, 255, 255, 255))

    # 三角下半部极淡的冷色，避免纯白发死（用三角自身做蒙版）
    tri_mask = Image.new("L", (size, size), 0)
    ImageDraw.Draw(tri_mask).polygon(tri, fill=255)

    tint = radial(size // 2, size * 0.64, size * 0.86, size * 0.60, 1.3)
    tint_layer = Image.new("RGBA", (size, size), (146, 202, 255, 255))
    tint_layer.putalpha(tint.resize((size, size), Image.BILINEAR).point(lambda v: int(v * 0.22)))
    canvas.paste(Image.alpha_composite(canvas, tint_layer), (0, 0), tri_mask)

    return canvas


def main():
    os.makedirs(OUT, exist_ok=True)

    big = build()

    # 输出各尺寸
    def px(n):
        return big.resize((n, n), Image.LANCZOS)

    brand = os.path.join(os.path.dirname(OUT), "branding")
    os.makedirs(brand, exist_ok=True)
    px(1024).save(os.path.join(brand, "icon-1024.png"))
    px(256).save(os.path.join(OUT, "Huanying.png"))

    sizes = [16, 20, 24, 32, 40, 48, 64, 128, 256]
    px(256).save(os.path.join(OUT, "Huanying.ico"), format="ICO",
                 sizes=[(s, s) for s in sizes])


    print("icon ok ->", OUT)
    for f in sorted(os.listdir(OUT)):
        print("  ", f, os.path.getsize(os.path.join(OUT, f)), "bytes")


if __name__ == "__main__":
    main()
