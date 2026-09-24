# -*- coding: utf-8 -*-
"""复现 MainViewModel.DetectionFlow.cs 的 3D-2D 匈牙利匹配,分析 PinIdx=8 为何被排除"""
import math

# validPins2D: (PinIndex, X, Y) —— 数组顺序即 pi 顺序
pins = [
    (0,  222.077, 291.340),
    (2,  224.698, 291.354),
    (3,  227.214, 291.290),
    (4,  229.733, 291.268),
    (5,  232.270, 291.237),
    (6,  237.369, 291.217),
    (7,  234.869, 291.251),
    (8,  239.643, 291.122),   # ← 问题 PIN
    (11, 222.011, 288.708),
    (12, 224.668, 288.736),
    (13, 227.177, 288.722),
    (14, 229.705, 288.641),
    (15, 232.291, 288.714),
    (16, 234.816, 288.643),
    (17, 237.343, 288.512),
    (18, 239.807, 288.578),
]

# candidatesAll board 坐标（[3D转换] 日志顺序）
cands = [
    (234.715, 287.206),  # 3D#0
    (229.613, 287.126),  # 3D#1
    (224.543, 287.202),  # 3D#2
    (231.599, 287.451),  # 3D#3
    (227.056, 287.187),  # 3D#4
    (239.709, 287.100),  # 3D#5
    (237.251, 287.074),  # 3D#6
    (237.256, 289.740),  # 3D#7
    (239.510, 289.627),  # 3D#8
    (234.731, 289.759),  # 3D#9
    (227.101, 289.753),  # 3D#10
    (229.615, 289.724),  # 3D#11
    (224.580, 289.815),  # 3D#12
    (232.169, 289.716),  # 3D#13
    (221.954, 289.759),  # 3D#14
    (221.942, 287.178),  # 3D#15
    (219.611, 287.192),  # 3D#16
    (219.537, 289.785),  # 3D#17
]

n2 = len(pins)
n3 = len(cands)
size = max(n2, n3)
INF = 1e9
maxMatchDist = 15.0

# ---- 与 C# 代码一致的代价矩阵 ----
hungCost = [[INF] * size for _ in range(size)]
for pi in range(size):
    for ci in range(size):
        if pi < n2 and ci < n3:
            px, py = pins[pi][1], pins[pi][2]
            bx, by = cands[ci]
            d = math.sqrt((px - bx) ** 2 + (py - by) ** 2)
            hungCost[pi][ci] = d if d <= maxMatchDist else INF

# ---- 与 C# 代码一致的 Hungarian ----
u = [0.0] * (size + 1)
v = [0.0] * (size + 1)
hungP = [0] * (size + 1)
way = [0] * (size + 1)

for hi in range(1, size + 1):
    hungP[0] = hi
    j0 = 0
    minv = [INF] * (size + 1)
    used = [False] * (size + 1)
    while True:
        used[j0] = True
        i0 = hungP[j0]
        delta = INF
        j1 = 0
        for j in range(1, size + 1):
            if used[j]:
                continue
            cur = hungCost[i0 - 1][j - 1] - u[i0] - v[j]
            if cur < minv[j]:
                minv[j] = cur
                way[j] = j0
            if minv[j] < delta:
                delta = minv[j]
                j1 = j
        for j in range(0, size + 1):
            if used[j]:
                u[hungP[j]] += delta
                v[j] -= delta
            else:
                minv[j] -= delta
        j0 = j1
        if hungP[j0] == 0:
            break
    j0_ = j0
    while True:
        j1 = way[j0_]
        hungP[j0_] = hungP[j1]
        j0_ = j1
        if j0_ == 0:
            break

assign = {}
dist_map = {}
for j in range(1, size + 1):
    if hungP[j] <= n2 and j <= n3:
        pi = hungP[j] - 1
        ci = j - 1
        px, py = pins[pi][1], pins[pi][2]
        bx, by = cands[ci]
        d = math.sqrt((px - bx) ** 2 + (py - by) ** 2)
        assign[pi] = ci
        dist_map[pi] = d

print("=== 匈牙利分配结果 ===")
for pi in range(n2):
    pin_idx = pins[pi][0]
    if pi in assign:
        ci = assign[pi]
        print(f"pi={pi} PinIdx={pin_idx} -> 3D#{ci} dist={dist_map[pi]:.3f}mm")
    else:
        print(f"pi={pi} PinIdx={pin_idx} -> 未分配")

print()
print("=== 未分配(跳过)的 3D 点 ===")
assigned_cis = set(assign.values())
for ci in range(n3):
    if ci not in assigned_cis:
        print(f"3D#{ci} 未被任何 2D 使用")

print()
print("=== PinIdx=8 (pi=7) 与各 3D 点距离 ===")
px, py = pins[7][1], pins[7][2]
for ci in range(n3):
    bx, by = cands[ci]
    d = math.sqrt((px - bx) ** 2 + (py - by) ** 2)
    flag = "<=15 可匹配" if d <= maxMatchDist else ">15 INF"
    print(f"3D#{ci}({bx:.3f},{by:.3f}) dist={d:.3f}mm {flag}")
