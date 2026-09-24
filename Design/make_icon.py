# -*- coding: utf-8 -*-
"""生成顶部栏 PNG 资源：
- Assets/AppIcon.png  64x64，顶部栏 Image 用
源：Design/preview/5_scan_badge.png（用户选定的扫描徽章版）

注意：正式 RBLAOI.ico 请用 rebuild_icon.ps1 生成！
Pillow 生成的 ICO 无法被 WPF 的 WIC IconBitmapDecoder 解码
（Window.Icon 会抛 XamlParseException: TypeConverterMarkupExtension），
必须用 System.Drawing 的标准 32bpp ARGB DIB 帧（2026-08-18 已踩坑修复）。
"""
import os
from PIL import Image

HERE = os.path.dirname(os.path.abspath(__file__))
SRC = os.path.join(HERE, "preview", "5_scan_badge.png")
ROOT = os.path.dirname(HERE)

src = Image.open(SRC).convert("RGB")

# 顶部栏 PNG（64x64）
assets = os.path.join(ROOT, "Assets")
os.makedirs(assets, exist_ok=True)
png_path = os.path.join(assets, "AppIcon.png")
src.resize((64, 64), Image.LANCZOS).save(png_path, format="PNG")
print("saved", png_path, os.path.getsize(png_path), "bytes")
