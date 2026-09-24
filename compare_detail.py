#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""逐位置对比 8/21 vs 8/24 同口径（方案215）"""
import re, os, statistics

LOG_DIR = r"C:\Users\Administrator\Desktop\RBLAOI\bin\x64\Debug\Logs"
LINE_RE = re.compile(
    r"^(\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3}) .*?\[⏱\] 检测位(\d+) (A\(移动\+拍照\)=(\d+)ms \| 总=(\d+)ms|B\+C\(2D启动\+移到起点\)=(\d+)ms|D\(3D扫描完成\)=(\d+)ms|E\(3D结果获取\)=(\d+)ms \| 总=(\d+)ms|总耗时=(\d+)ms)"
)

def parse_log(path):
    rounds = []
    cur = None
    for ln in open(path, encoding="utf-8-sig", errors="ignore"):
        m = LINE_RE.match(ln)
        if not m:
            continue
        ts, pos, _, a, atotal, bc, d, e, etotal, total = m.groups()
        pos = int(pos)
        if pos == 0 and a is not None:
            cur = {"pos": {}}
            rounds.append(cur)
        if cur is None:
            continue
        rec = cur["pos"].setdefault(pos, {})
        if a is not None: rec["A"] = int(a)
        if bc is not None: rec["BC"] = int(bc)
        if d is not None: rec["D"] = int(d)
        if e is not None: rec["E"] = int(e)
        if total is not None: rec["total"] = int(total)
    return rounds

def show(path, round_idx, label):
    rounds = parse_log(path)
    if round_idx >= len(rounds):
        print(f"{label}: 轮{round_idx} 不存在 (共{len(rounds)}轮)")
        return
    rd = rounds[round_idx]
    poss = sorted(rd["pos"].keys())
    print(f"--- {label} 轮{round_idx} ({len(poss)}位置) ---")
    print(f"{'pos':>3} {'A':>6} {'BC':>6} {'D':>6} {'E':>6} {'total':>6}")
    for p in poss:
        r = rd["pos"][p]
        print(f"{p:>3} {r.get('A','-'):>6} {r.get('BC','-'):>6} {r.get('D','-'):>6} {r.get('E','-'):>6} {r.get('total','-'):>6}")
    totals = [rd["pos"][p]["total"] for p in poss if "total" in rd["pos"][p]]
    As = [rd["pos"][p]["A"] for p in poss if "A" in rd["pos"][p]]
    Ds = [rd["pos"][p]["D"] for p in poss if "D" in rd["pos"][p]]
    if totals:
        print(f"total: 中位={statistics.median(totals)} 均值={statistics.mean(totals):.0f} 最小={min(totals)} 最大={max(totals)}")
    if As:
        print(f"A段:   中位={statistics.median(As)} 均值={statistics.mean(As):.0f}")
    if Ds:
        print(f"D段:   中位={statistics.median(Ds)} 均值={statistics.mean(Ds):.0f}")
    print()

base113621 = os.path.join(LOG_DIR, "log_20260821_113621.txt")
base172511 = os.path.join(LOG_DIR, "log_20260821_172511.txt")
opt0824   = os.path.join(LOG_DIR, "log_20260824_170535.txt")

print("======= 8/21 基线（优化前）=======")
show(base113621, 19, "8/21 113621")   # 8位置完整排
show(base113621, 21, "8/21 113621")   # 9位置(取前8)
show(base172511, 8,  "8/21 172511")   # 40位置连续（取统计）

print("======= 8/24 优化后 =======")
show(opt0824, 1, "8/24 170535")
show(opt0824, 2, "8/24 170535")
