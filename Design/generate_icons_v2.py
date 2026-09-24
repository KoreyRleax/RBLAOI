# -*- coding: utf-8 -*-
"""RBLAOI 应用图标样图 v2 —— Rb 组合 + AOI 意象 (512x512 PNG + 对比总览图)"""
import os, math
from PIL import Image, ImageDraw, ImageFont

HERE = os.path.dirname(os.path.abspath(__file__))
OUT = os.path.join(HERE, "preview")
os.makedirs(OUT, exist_ok=True)
S = 512

DEEP   = "#0F4C81"
MID    = "#1863DA"
LIGHT  = "#A9C6E8"
WHITE  = "#FFFFFF"

ARIAL = r"C:\Windows\Fonts\arialbd.ttf"
MSYH  = r"C:\Windows\Fonts\msyh.ttc"

def font(size, path=ARIAL):
    return ImageFont.truetype(path, size)

def h2rgb(h):
    h = h.lstrip("#")
    return tuple(int(h[i:i+2], 16) for i in (0, 2, 4))

def rounded_gradient(size, radius, top, bottom):
    img = Image.new("RGB", (size, size), bottom)
    grad = Image.new("RGB", (1, size))
    for y in range(size):
        t = y / size
        c = tuple(int(top[i] + (bottom[i] - top[i]) * t) for i in range(3))
        grad.putpixel((0, y), c)
    grad = grad.resize((size, size))
    mask = Image.new("L", (size, size), 0)
    ImageDraw.Draw(mask).rounded_rectangle([0, 0, size-1, size-1], radius=radius, fill=255)
    img.paste(grad, (0, 0), mask)
    return img

def save(img, name):
    img.save(os.path.join(OUT, name))
    print("saved", name)

# ---------- 方案1 渐变经典 + 放大镜角标（检测） ----------
img = rounded_gradient(S, 110, h2rgb(MID), h2rgb(DEEP))
d = ImageDraw.Draw(img)
d.text((256, 238), "Rb", font=font(330), fill=WHITE, anchor="mm")
d.rounded_rectangle([146, 364, 366, 378], radius=7, fill=LIGHT)          # 光带
# 放大镜角标（浅蓝描边 + 手柄 + 高光点）
cx, cy, r = 402, 398, 52
d.ellipse([cx-r, cy-r, cx+r, cy+r], outline=LIGHT, width=14)
d.line([(cx+r-14, cy+r-14), (cx+r+34, cy+r+40)], fill=LIGHT, width=16)
d.ellipse([cx-22, cy-22, cx-6, cy-6], fill=LIGHT)                        # 镜片高光
save(img, "1_scope.png")

# ---------- 方案2 几何 R + 靶心 b（定位检测） ----------
img = Image.new("RGB", (S, S), DEEP)
d = ImageDraw.Draw(img)
d.rounded_rectangle([0, 0, S-1, S-1], radius=110, fill=DEEP)
# 左 R
d.rectangle([92, 133, 133, 379], fill=WHITE)
d.rectangle([92, 133, 225, 174], fill=WHITE)
d.pieslice([92, 133, 225, 266], 270, 90, fill=WHITE)
d.rectangle([92, 133, 225, 175], fill=WHITE)
d.polygon([(160, 263), (225, 263), (265, 379), (215, 379)], fill=WHITE)
# 右 b 竖线
d.rectangle([379, 133, 420, 379], fill=WHITE)
# 靶心：外圆 + 内圆 + 中心点 + 四向刻度
bx, by = 360, 339
d.ellipse([287, 266, 433, 412], outline=WHITE, width=10)
d.ellipse([310, 289, 410, 389], outline=WHITE, width=8)
d.ellipse([bx-9, by-9, bx+9, by+9], fill=WHITE)
d.line([(360, 246), (360, 266)], fill=WHITE, width=8)   # 上刻度
d.line([(360, 412), (360, 432)], fill=WHITE, width=8)   # 下刻度
d.line([(267, 339), (287, 339)], fill=WHITE, width=8)   # 左刻度
d.line([(433, 339), (453, 339)], fill=WHITE, width=8)   # 右刻度
save(img, "2_target.png")

# ---------- 方案3 药丸 R|b + 斜向扫描光束（光学扫描） ----------
img = Image.new("RGB", (S, S), DEEP)
d = ImageDraw.Draw(img)
d.rounded_rectangle([0, 0, S-1, S-1], radius=110, fill=DEEP)
# 背景斜向扫描光束（3 条平行浅蓝线，-28 度）
slope = -math.tan(math.radians(28))
for b0 in (351.7, 391.7, 431.7):
    x0, y0 = 0, b0
    x1, y1 = S, slope * S + b0
    d.line([(x0, y0), (x1, y1)], fill=LIGHT, width=10)
# 白色药丸（覆盖光束形成层次）
d.rounded_rectangle([72, 138, 440, 374], radius=118, fill=WHITE)
d.ellipse([96, 180, 152, 236], fill=LIGHT)               # 药丸上方悬浮阴影
# 左半深蓝胶囊 + R
d.pieslice([72, 138, 308, 374], 90, 270, fill=DEEP)
d.rectangle([72, 138, 256, 374], fill=DEEP)
d.text((164, 256), "R", font=font(210), fill=WHITE, anchor="mm")
# 右半白底 + b
d.text((348, 256), "b", font=font(210), fill=DEEP, anchor="mm")
save(img, "3_pill_scan.png")

# ---------- 方案4 简约描边 Rb + 十字准星（取景定位） ----------
img = Image.new("RGB", (S, S), LIGHT)
d = ImageDraw.Draw(img)
d.rounded_rectangle([0, 0, S-1, S-1], radius=110, fill=LIGHT)
d.text((256, 240), "Rb", font=font(310), fill=WHITE, anchor="mm",
       stroke_width=16, stroke_fill=MID)
d.text((256, 372), "AOI", font=font(72), fill=DEEP, anchor="mm")   # 底部小字 AOI
# 右上角十字准星
qx, qy = 410, 118
d.ellipse([qx-34, qy-34, qx+34, qy+34], outline=DEEP, width=10)
d.line([(374, qy), (446, qy)], fill=DEEP, width=8)
d.line([(qx, 82), (qx, 154)], fill=DEEP, width=8)
save(img, "4_crosshair.png")

# ---------- 方案5 扫描徽章 + 镜头光圈（光学镜头） ----------
img = Image.new("RGB", (S, S), DEEP)
d = ImageDraw.Draw(img)
d.rounded_rectangle([0, 0, S-1, S-1], radius=110, fill=DEEP)
d.ellipse([56, 56, 456, 456], fill=MID)
d.ellipse([86, 86, 426, 426], fill=DEEP)
d.arc([96, 96, 416, 416], start=200, end=340, fill=LIGHT, width=14)  # 扫描弧
for a in range(0, 360, 30):                                           # 刻度点
    rad = math.radians(a)
    cx = 256 + 203 * math.cos(rad)
    cy = 256 + 203 * math.sin(rad)
    d.ellipse([cx-7, cy-7, cx+7, cy+7], fill=LIGHT)
# 中央镜头光圈：外环 + 内环 + 6 叶片 + 中心点
d.ellipse([186, 186, 326, 326], outline=WHITE, width=12)
d.ellipse([210, 210, 302, 302], outline=LIGHT, width=8)
for a in range(0, 360, 60):                                           # 叶片短线
    rad = math.radians(a)
    x0, y0 = 256 + 46 * math.cos(rad), 256 + 46 * math.sin(rad)
    x1, y1 = 256 + 64 * math.cos(rad), 256 + 64 * math.sin(rad)
    d.line([(x0, y0), (x1, y1)], fill=WHITE, width=8)
d.ellipse([247, 247, 265, 265], fill=WHITE)
# 底部 Rb 字
d.text((256, 420), "Rb", font=font(130), fill=WHITE, anchor="mm")
save(img, "5_lens_badge.png")

# ---------- 对比总览图 ----------
names = ["1_scope.png", "2_target.png", "3_pill_scan.png", "4_crosshair.png", "5_lens_badge.png"]
labels = ["方案1 渐变 + 放大镜", "方案2 几何 + 靶心", "方案3 药丸 + 扫描", "方案4 简约 + 准星", "方案5 徽章 + 镜头"]
TH, GAP, PAD, LAB_H = 256, 16, 16, 40
W = PAD * 2 + len(names) * TH + (len(names) - 1) * GAP
H = PAD + TH + 6 + LAB_H + PAD
sheet = Image.new("RGB", (W, H), "#F5F7FA")
sd = ImageDraw.Draw(sheet)
for i, (n, lab) in enumerate(zip(names, labels)):
    x0 = PAD + i * (TH + GAP)
    thumb = Image.open(os.path.join(OUT, n)).resize((TH, TH), Image.LANCZOS)
    sheet.paste(thumb, (x0, PAD))
    sd.text((x0 + TH / 2, PAD + TH + 6 + LAB_H / 2), lab, font=font(24, MSYH),
            fill="#1E1E1E", anchor="mm")
sheet.save(os.path.join(OUT, "ALL_compare.png"))
print("saved ALL_compare.png")
print("done ->", OUT)
