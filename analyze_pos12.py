# -*- coding: utf-8 -*-
"""检测位12 精确分析：固定行号范围，2D针与3D点全配对偏差"""
import re, math

F = r"C:\Users\Administrator\Desktop\RBLAOI\bin\x64\Debug\Logs\log_20260821_172511.txt"
START, END = 4331, 4560  # 检测位12 的拍范围

pat_ok = re.compile(r'\[3D匹配\] PinIdx=(\d+) 2D\(([0-9.-]+),([0-9.-]+)\).*OK 匹配3D#(\d+) dist=([0-9.]+) board\(([0-9.-]+),([0-9.-]+)\)')
pat_no = re.compile(r'\[3D匹配\] PinIdx=(\d+) 2D\(([0-9.-]+),([0-9.-]+)\).*NO 无匹配')
pat_3d = re.compile(r'\[3D转换\] 3D#(\d+): vm\([0-9.-]+,[0-9.-]+\) => board\(([0-9.-]+),([0-9.-]+)\)')

pins2d = {}   # pin -> (x,y)
ok_pairs = [] # (pin, 3d#)
pts3d = {}    # 3d# -> (x,y)

with open(F, encoding='utf-8', errors='ignore') as f:
    for i, line in enumerate(f, 1):
        if i < START or i > END:
            continue
        m = pat_ok.search(line)
        if m:
            pins2d[int(m.group(1))] = (float(m.group(2)), float(m.group(3)))
            ok_pairs.append((int(m.group(1)), int(m.group(4))))
            continue
        m = pat_no.search(line)
        if m:
            pins2d[int(m.group(1))] = (float(m.group(2)), float(m.group(3)))
        m = pat_3d.search(line)
        if m:
            pts3d[int(m.group(1))] = (float(m.group(2)), float(m.group(3)))

print(f"检测位12: 2D针 {len(pins2d)} 个, 3D点 {len(pts3d)} 个, OK匹配 {len(ok_pairs)} 个")
ok_set = {p for p, _ in ok_pairs}

print("\n=== 每个2D针 → X方向最近排的按序对应3D点（正确对位）偏差 ===")
# 按X分组配对：2D针排序后与3D点排序后按位置对应
p2_sorted = sorted(pins2d.items(), key=lambda kv: (kv[1][0], kv[1][1]))
p3_sorted = sorted(pts3d.items(), key=lambda kv: (kv[1][0], kv[1][1]))
print(f"2D按(X,Y)排序: {[(p, round(x,2), round(y,2)) for p,(x,y) in p2_sorted]}")
print(f"3D按(X,Y)排序: {[(t, round(x,2), round(y,2)) for t,(x,y) in p3_sorted]}")

if len(p2_sorted) == len(p3_sorted):
    print("\n数量相等，按排序一一对应（正确对位）:")
    dxs, dys, dists = [], [], []
    for (pin, p2), (tid, p3) in zip(p2_sorted, p3_sorted):
        dx, dy = p3[0]-p2[0], p3[1]-p2[1]
        d = math.hypot(dx, dy)
        dxs.append(dx); dys.append(dy); dists.append(d)
        mark = "OK " if pin in ok_set else "无匹配"
        print(f"  Pin{pin:2d}({p2[0]:7.2f},{p2[1]:7.2f}) ↔ 3D#{tid}({p3[0]:7.2f},{p3[1]:7.2f})  dx={dx:+.3f} dy={dy:+.3f} dist={d:.3f} [{mark}]")
    print(f"\n正确对位平均: dx={sum(dxs)/len(dxs):+.3f}  dy={sum(dys)/len(dys):+.3f}  dist={sum(dists)/len(dists):.3f}")
    print(f"OK针的dist平均: {sum(dists[i] for i in range(len(dists)) if p2_sorted[i][0] in ok_set)/max(len(ok_set),1):.3f}")
