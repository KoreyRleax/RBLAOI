# -*- coding: utf-8 -*-
"""轮次耗时对比（A/B+C/D/E 分段 + 同位置 D 对比）
用法: python analyze_135_dspeed.py [日志路径]  （默认 log_20260825_101923.txt）
"""
import re
import sys
from collections import defaultdict
from statistics import median

LOG = sys.argv[1] if len(sys.argv) > 1 else r"C:\Users\Administrator\Desktop\RBLAOI\bin\x64\Debug\Logs\log_20260825_101923.txt"

pat_speed = re.compile(r"已设置3D扫描速度: ([\d.]+) mm/s")
pat_A  = re.compile(r"检测位(\d+) A\(移动\+拍照\)=(\d+)ms")
pat_BC = re.compile(r"检测位(\d+) B\+C\(2D启动\+移到起点\)=(\d+)ms")
pat_D  = re.compile(r"检测位(\d+) D\(3D扫描完成\)=(\d+)ms")
pat_E  = re.compile(r"检测位(\d+) E\(3D结果获取\)=(\d+)ms")
pat_T  = re.compile(r"检测位(\d+) 总耗时=(\d+)ms")

# 轮次: 按设置速度行切分, 每段存 (speed, line_no, A{}, BC{}, D{}, E{}, T{})
rounds = []
cur = None
with open(LOG, encoding="utf-8", errors="replace") as f:
    for line_no, line in enumerate(f, 1):
        m = pat_speed.search(line)
        if m:
            cur = {"speed": float(m.group(1)), "start": line_no, "A": {}, "BC": {}, "D": {}, "E": {}, "T": {}}
            rounds.append(cur)
        if cur is None:
            continue
        for pat, key in ((pat_A, "A"), (pat_BC, "BC"), (pat_D, "D"), (pat_E, "E"), (pat_T, "T")):
            m = pat.search(line)
            if m:
                cur[key][int(m.group(1))] = int(m.group(2))

def stat(d):
    if not d:
        return None
    vals = list(d.values())
    return len(vals), min(vals), median(vals), max(vals), sum(vals) // len(vals)

print("=" * 78)
print(f"{'轮次':<6}{'速度':<8}{'位数':<6}{'A中位':<8}{'B+C中位':<9}{'D中位':<8}{'D范围':<16}{'E中位':<8}{'总中位':<8}{'总均值'}")
print("=" * 78)
for r in rounds:
    if not r["T"]:
        continue
    n, _, dm, _, _ = stat(r["D"])
    dmin, dmax = (min(r["D"].values()), max(r["D"].values())) if r["D"] else (0, 0)
    eStat = stat(r["E"])
    eStr = f"{eStat[2]:<8.0f}" if eStat else "—       "   # E行日志 2026-08-25 已移除（异步化），兼容旧日志
    print(f"{r['start']:<6}{r['speed']:<8.1f}{n:<6}{stat(r['A'])[2]:<8.0f}"
          f"{stat(r['BC'])[2]:<9.0f}{dm:<8.0f}{f'{dmin}~{dmax}':<16}"
          f"{eStr}{stat(r['T'])[2]:<8.0f}{stat(r['T'])[4]}")

# 取 12mm/s 完整轮 与 13.5mm/s 轮做同位置 D 对比（仅当日志中存在时）
try:
    r12 = next(r for r in rounds if abs(r["speed"] - 12.0) < 1e-6 and len(r["D"]) >= 30)
    r135 = next(r for r in rounds if abs(r["speed"] - 13.5) < 1e-6)
    print("\n同位置 D 段对比（12mm/s 轮 vs 13.5mm/s 轮）:")
    print(f"{'位':<5}{'12mm/s D':<12}{'13.5 D':<12}{'Δms':<8}{'Δ%'}")
    for pos in sorted(set(r12["D"]) & set(r135["D"])):
        d12, d135 = r12["D"][pos], r135["D"][pos]
        print(f"{pos:<5}{d12:<12}{d135:<12}{d135-d12:<8}{100*(d135-d12)/d12:+.1f}%")
except StopIteration:
    print("\n(日志中无 12/13.5mm/s 完整轮，跳过同位置对比)")

# 各轮 D 段分布（分位）
for r in rounds:
    if not r["D"]:
        continue
    vals = sorted(r["D"].values())
    if len(vals) < 10:
        continue
    p = lambda q: vals[min(len(vals)-1, int(q*len(vals)))]
    print(f"\n速度 {r['speed']}mm/s ({len(vals)}位) D段分布: min={vals[0]} p25={p(0.25)} 中位={p(0.5)} p75={p(0.75)} p90={p(0.9)} max={vals[-1]}")
