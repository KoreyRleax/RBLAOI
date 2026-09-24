# -*- coding: utf-8 -*-
"""RBLAOI 应用图标样图生成器 —— 5 款 Rb 组合风格 (512x512 PNG)"""
import os
from PIL import Image, ImageDraw, ImageFont

OUT = os.path.join(os.path.dirname(os.path.abspath(__file__)), "preview")
os.makedirs(OUT, exist_ok=True)
S = 512

# 项目配色
DEEP   = "#0F4C81"   # 深蓝
MID    = "#1863DA"   # 中蓝
LIGHT  = "#A9C6E8"   # 浅蓝
WHITE  = "#FFFFFF"
INK    = "#1E1E1E"

FONT_PATH = r"C:\Windows\Fonts\arialbd.ttf"

def font(size):
    return ImageFont.truetype(FONT_PATH, size)

def rounded_gradient(size, radius, top, bottom):
    """圆角矩形 + 垂直渐变"""
    img = Image.new("RGB", (size, size), bottom)
    grad = Image.new("RGB", (1, size))
    for y in range(size):
        t = y / size
        c = tuple(int(top[i] + (bottom[i] - top[i]) * t) for i in range(3))
        grad.putpixel((0, y), c)
    grad = grad.resize((size, size))
    mask = Image.new("L", (size, size), 0)
    ImageDraw.Draw(mask).rounded_rectangle([0, 0, size - 1, size - 1], radius=radius, fill=255)
    img.paste(grad, (0, 0), mask)
    return img

def center_text(draw, text, f, fill, stroke=0, stroke_fill=None, y_off=0):
    draw.text((S / 2, S / 2 + y_off), text, font=f, fill=fill, anchor="mm",
              stroke_width=stroke, stroke_fill=stroke_fill)

def hex2rgb(h):
    h = h.lstrip("#")
    return tuple(int(h[i:i + 2], 16) for i in (0, 2, 4))

def save(img, name):
    img.save(os.path.join(OUT, name))
    print("saved", name)

# ---------- 方案1：渐变经典（白字 Rb + 底部光带） ----------
img = rounded_gradient(S, 110, hex2rgb(MID), hex2rgb(DEEP))
d = ImageDraw.Draw(img)
center_text(d, "Rb", font(320), WHITE, y_off=-10)
d.rounded_rectangle([150, 372, 362, 386], radius=7, fill=LIGHT)   # 底部浅蓝光带
save(img, "1_gradient_classic.png")

# ---------- 方案2：几何拼接（R | b 抽象对称，共享中线） ----------
img = Image.new("RGB", (S, S), DEEP)
d = ImageDraw.Draw(img)
d.rounded_rectangle([0, 0, S - 1, S - 1], radius=110, fill=DEEP)
# 左侧 R：竖条 + 顶横 + 右上弧 + 斜腿
d.rectangle([92, 133, 133, 379], fill=WHITE)
d.rectangle([92, 133, 225, 174], fill=WHITE)
d.pieslice([92, 133, 225, 266], 270, 90, fill=WHITE)              # 右上圆弧
d.rectangle([92, 133, 225, 175], fill=WHITE)                      # 补平弧顶
d.polygon([(160, 263), (225, 263), (265, 379), (215, 379)], fill=WHITE)  # 斜腿
# 右侧 b：竖条 + 右下圆
d.rectangle([379, 133, 420, 379], fill=WHITE)
d.ellipse([287, 266, 433, 412], fill=WHITE)
save(img, "2_geo_symmetry.png")

# ---------- 方案3：药丸左右分割（左R右b） ----------
img = Image.new("RGB", (S, S), DEEP)
d = ImageDraw.Draw(img)
d.rounded_rectangle([0, 0, S - 1, S - 1], radius=110, fill=DEEP)
# 白色药丸外框
d.rounded_rectangle([72, 138, 440, 374], radius=118, fill=WHITE)
# 左半胶囊（深蓝）
d.pieslice([72, 138, 308, 374], 90, 270, fill=DEEP)
d.rectangle([72, 138, 256, 374], fill=DEEP)
d.text((164, 256), "R", font=font(210), fill=WHITE, anchor="mm")   # 左半中心
d.text((348, 256), "b", font=font(210), fill=DEEP, anchor="mm")    # 右半中心
save(img, "3_pill_split.png")

# ---------- 方案4：浅蓝简约（描边字 + 圆点） ----------
img = Image.new("RGB", (S, S), LIGHT)
d = ImageDraw.Draw(img)
d.rounded_rectangle([0, 0, S - 1, S - 1], radius=110, fill=LIGHT)
center_text(d, "Rb", font(310), WHITE, stroke=16, stroke_fill=MID, y_off=-10)
d.ellipse([380, 380, 424, 424], fill=DEEP)
save(img, "4_minimal_outline.png")

# ---------- 方案5：扫描徽章（圆底 + 扫描弧 + 刻度） ----------
img = Image.new("RGB", (S, S), DEEP)
d = ImageDraw.Draw(img)
d.rounded_rectangle([0, 0, S - 1, S - 1], radius=110, fill=DEEP)
d.ellipse([56, 56, 456, 456], fill=MID)                           # 圆底
d.ellipse([76, 76, 436, 436], fill=DEEP)                          # 内圆（环效果）
d.arc([96, 96, 416, 416], start=200, end=340, fill=LIGHT, width=14)   # 扫描弧
for a in range(0, 360, 30):                                       # 刻度点
    import math
    r0, r1 = 196, 210
    rad = math.radians(a)
    cx = S / 2 + (r0 + r1) / 2 * math.cos(rad)
    cy = S / 2 + (r0 + r1) / 2 * math.sin(rad)
    d.ellipse([cx - 7, cy - 7, cx + 7, cy + 7], fill=LIGHT)
center_text(d, "Rb", font(230), WHITE, y_off=6)
save(img, "5_scan_badge.png")

print("done ->", OUT)
