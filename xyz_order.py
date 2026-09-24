#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""XYZ 到达顺序统计（处理 Send/Recv 换行格式）"""
import re, datetime, statistics, os

F_824 = r"C:\Users\Administrator\Desktop\RBLAOI\bin\x64\Debug\Logs\log_20260824_170535.txt"
TS_RE = re.compile(r"^(\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3})")

def ts2ms(ts):
    t = datetime.datetime.strptime(ts, "%Y-%m-%d %H:%M:%S.%f")
    return t

events = []
cur_ts = None
lines = open(F_824, encoding="utf-8-sig", errors="ignore").read().splitlines()
for i, ln in enumerate(lines):
    tm = TS_RE.match(ln)
    if tm:
        cur_ts = tm.group(1)
    if cur_ts is None:
        continue
    if "SCAN3D" in ln and "Send" in ln:
        m = re.search(r"Send\]\s*SCAN3D\s*(X\d+,\s*Y\d+)", ln)
        if m:
            events.append((i, cur_ts, "SCAN3D", m.group(1)))
            continue
    if "Send" in ln:
        m = re.search(r"Send\]\s*(X\d+,\s*Y\d+(?:,\s*Z\d+)?)", ln)
        if m and "SCAN3D" not in m.group(1):
            events.append((i, cur_ts, "MOVE", m.group(1).replace(" ", "")))
    if "Recv" in ln:
        m = re.search(r"Recv\]\s*((?:X|Y|Z|H)_COMPLETE)", ln)
        if m:
            events.append((i, cur_ts, "COMPLETE", m.group(1)))
            continue
        m = re.search(r"Recv\]\s*(POS:.*)", ln)
        if m:
            events.append((i, cur_ts, "POS", m.group(1)))

print(f"总事件: {len(events)}")
# 分组：MOVE/SCAN3D 开始一组，收集到下一个 MOVE/SCAN3D 前的 COMPLETE/POS
groups = []
cur = None
for e in events:
    if e[2] in ("MOVE", "SCAN3D"):
        if cur and cur["done"]:
            groups.append(cur)
        cur = {"cmd": e[3], "ts": e[1], "done": []}
    elif cur is not None and e[2] in ("COMPLETE", "POS"):
        cur["done"].append((e[1], e[2], e[3]))
if cur and cur["done"]:
    groups.append(cur)

print(f"移动指令组: {len(groups)}")
seq_stat = {"XY都到": 0, "仅X": 0, "仅Y": 0, "带Z": 0, "X先": 0, "Y先": 0, "同刻": 0}
gaps = []
samples = []
for g in groups:
    seq = [d[2] for d in g["done"]]
    xc = [d for d in g["done"] if d[2] == "X_COMPLETE"]
    yc = [d for d in g["done"] if d[2] == "Y_COMPLETE"]
    zc = [d for d in g["done"] if d[2] == "Z_COMPLETE"]
    has_pos = any(d[2] == "POS" for d in g["done"])
    if zc:
        seq_stat["带Z"] += 1
    if xc and yc:
        seq_stat["XY都到"] += 1
        gap = (ts2ms(xc[0][0]) - ts2ms(yc[0][0])).total_seconds() * 1000
        gaps.append(gap)
        if gap < -5:
            seq_stat["X先"] += 1
        elif gap > 5:
            seq_stat["Y先"] += 1
        else:
            seq_stat["同刻"] += 1
        samples.append((g["cmd"], [d[2] for d in g["done"]], round(gap), has_pos))
    elif xc:
        seq_stat["仅X"] += 1
    elif yc:
        seq_stat["仅Y"] += 1

print(f"顺序统计: {seq_stat}")
if gaps:
    xfirst = [g for g in gaps if g < -5]
    yfirst = [g for g in gaps if g > 5]
    print(f"\nX先到 {len(xfirst)} 次, 平均领先 {statistics.mean([abs(g) for g in xfirst]):.0f}ms, 中位 {statistics.median([abs(g) for g in xfirst]):.0f}ms" if xfirst else "无X先样本")
    print(f"Y先到 {len(yfirst)} 次, 平均领先 {statistics.mean([abs(g) for g in yfirst]):.0f}ms, 中位 {statistics.median([abs(g) for g in yfirst]):.0f}ms" if yfirst else "无Y先样本")
    all_gap = [abs(g) for g in gaps]
    print(f"X/Y间隔绝对值: 中位 {statistics.median(all_gap):.0f}ms, 平均 {statistics.mean(all_gap):.0f}ms, max {max(all_gap):.0f}ms")

print("\n样本(前25, 指令 | 到达序列 | X相对Yms | 有POS):")
for s in samples[:25]:
    print(f"  {s[0]:<20} {' '.join(s[1]):<36} {s[2]:>7}  {s[3]}")
