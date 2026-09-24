#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""1) 8/21 40位置连续 vs 8/24 41位置连续 对比  2) XYZ到达顺序统计"""
import re, os, statistics, datetime

LOG_DIR = r"C:\Users\Administrator\Desktop\RBLAOI\bin\x64\Debug\Logs"
F_821 = os.path.join(LOG_DIR, "log_20260821_172511.txt")
F_824 = os.path.join(LOG_DIR, "log_20260824_170535.txt")

TS_RE = re.compile(r"^(\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3})")
POS_RE = re.compile(r"\[⏱\] 检测位(\d+) (A\(移动\+拍照\)=(\d+)ms \| 总=(\d+)ms|B\+C\(2D启动\+移到起点\)=(\d+)ms|D\(3D扫描完成\)=(\d+)ms|E\(3D结果获取\)=(\d+)ms \| 总=(\d+)ms|总耗时=(\d+)ms)")

def parse_positions(path, start_line=None):
    """返回 {pos: {A,BC,D,E,total,ts_list}}，仅统计 ≥10 位置的连续轮"""
    # 先找检测位0 的 A 之后连续到 max 的轮次
    rounds = []
    cur = None
    lines = open(path, encoding="utf-8-sig", errors="ignore").read().splitlines()
    for ln in lines:
        tm = TS_RE.match(ln)
        m = POS_RE.search(ln)
        if not m:
            continue
        ts = tm.group(1)
        pos = int(m.group(1))
        a, atot, bc, d, e, etot, total = m.group(3), m.group(4), m.group(5), m.group(6), m.group(7), m.group(8), m.group(9)
        if pos == 0 and a is not None:
            cur = {"pos": {}, "start_ts": ts}
            rounds.append(cur)
        if cur is None:
            continue
        rec = cur["pos"].setdefault(pos, {})
        if a is not None: rec["A"] = int(a)
        if bc is not None: rec["BC"] = int(bc)
        if d is not None: rec["D"] = int(d)
        if e is not None: rec["E"] = int(e)
        if total is not None: rec["total"] = int(total); rec["ts"] = ts
    # 找最长连续轮
    best = None
    for rd in rounds:
        n = len(rd["pos"])
        if best is None or n > len(best["pos"]):
            best = rd
    return best

def t2ms(ts):
    t = datetime.datetime.strptime(ts, "%Y-%m-%d %H:%M:%S.%f")
    return t

def summarize(name, rd):
    poss = sorted(rd["pos"].keys())
    print(f"\n===== {name}: {len(poss)} 位置连续 (起 {rd['start_ts']}) =====")
    totals = [rd["pos"][p]["total"] for p in poss if "total" in rd["pos"][p]]
    As = [rd["pos"][p]["A"] for p in poss if "A" in rd["pos"][p]]
    Ds = [rd["pos"][p]["D"] for p in poss if "D" in rd["pos"][p]]
    if len(totals) != len(poss):
        print("  数据不完整"); return
    end_ts = rd["pos"][poss[-1]]["ts"]
    span = (t2ms(end_ts) - t2ms(rd["start_ts"])).total_seconds()
    def q(x, name_):
        x = sorted(x)
        return (f"{name_}: min={min(x)} P25={x[len(x)//4]} med={statistics.median(x)} "
                f"P75={x[3*len(x)//4]} max={max(x)} avg={statistics.mean(x):.0f}")
    print(f"  总耗时 {q(totals, 'total')}")
    print(f"  A段   {q(As, 'A')}")
    print(f"  D段   {q(Ds, 'D')}")
    print(f"  连续跨度: {span:.1f}s")
    # 逐位置
    row = []
    for p in poss:
        r = rd["pos"][p]
        row.append(f"{p}:{r.get('total')}")
    print("  " + " ".join(row))

rd821 = parse_positions(F_821)
rd824 = parse_positions(F_824)
summarize("8/21 172511 最长连续轮", rd821)
summarize("8/24 170535 最长连续轮", rd824)

# ============ XYZ 到达顺序统计 ============
print("\n\n########## XYZ 到达顺序分析 (8/24 日志) ##########")
lines = open(F_824, encoding="utf-8-sig", errors="ignore").read().splitlines()
# 记录 Send 移动指令 和 Recv COMPLETE 的时序
events = []  # (line_idx, ts, kind)
for i, ln in enumerate(lines):
    tm = TS_RE.match(ln)
    if not tm:
        continue
    ts = tm.group(1)
    if "[Send]" in ln:
        m = re.search(r"\[Send\] (X\d+,\s*Y\d+(?:,\s*Z\d+)?)", ln)
        if m:
            events.append((i, ts, "MOVE", m.group(1).replace(" ", "")))
        m = re.search(r"\[Send\] (SCAN3D\s*X\d+,\s*Y\d+)", ln)
        if m:
            events.append((i, ts, "SCAN3D", m.group(1)))
    if "[Recv]" in ln:
        m = re.search(r"\[Recv\] ((?:X|Y|Z|H)_COMPLETE)", ln)
        if m:
            events.append((i, ts, "COMPLETE", m.group(1)))
        m = re.search(r"\[Recv\] (POS:.*)", ln)
        if m:
            events.append((i, ts, "POS", m.group(1)))

# 按 MOVE/SCAN3D 分组，统计其后的 COMPLETE 顺序
def ts2ms(ts):
    t = datetime.datetime.strptime(ts, "%Y-%m-%d %H:%M:%S.%f")
    return t

groups = []
cur = None
for e in events:
    kind = e[2]
    if kind in ("MOVE", "SCAN3D"):
        cur = {"cmd": e[3], "ts": e[1], "done": []}
        groups.append(cur)
    elif kind == "COMPLETE" and cur is not None:
        cur["done"].append((e[1], e[3]))
    elif kind == "POS" and cur is not None:
        # 遇到 POS 或新指令前的空档结束本组？POS 表示到达终点，终止收集
        cur["pos"] = e[3]

# 统计顺序
order_stat = {"X_first": 0, "Y_first": 0, "Z_first": 0, "X_only": 0, "Y_only": 0, "none": 0}
x_before_y = []
samples = []
for g in groups:
    done = g["done"]
    if not done:
        order_stat["none"] += 1
        continue
    # 只取一次移动指令后、到下一个移动指令前的 COMPLETE
    seq = [d[1] for d in done]
    if "Z_COMPLETE" in seq and len(seq) >= 3:
        order_stat["Z_first"] += 1
    if "X_COMPLETE" in seq and "Y_COMPLETE" in seq:
        x_t = [ts2ms(d[0]) for d in done if d[1] == "X_COMPLETE"][0]
        y_t = [ts2ms(d[0]) for d in done if d[1] == "Y_COMPLETE"][0]
        gap = (x_t - y_t).total_seconds() * 1000
        x_before_y.append(gap)
        if x_t < y_t:
            order_stat["X_first"] += 1
        else:
            order_stat["Y_first"] += 1
        samples.append((g["cmd"], seq, round(gap)))
    elif "X_COMPLETE" in seq:
        order_stat["X_only"] += 1
    elif "Y_COMPLETE" in seq:
        order_stat["Y_only"] += 1

print(f"移动指令总数: {len(groups)}")
print(f"顺序统计: {order_stat}")
if x_before_y:
    pos = [s for s in x_before_y if s > 0]
    neg = [s for s in x_before_y if s <= 0]
    print(f"X先到次数: {len(pos)} (X-Y间隔 中位 {statistics.median(pos):.0f}ms)")
    print(f"Y先到次数: {len(neg)} (X-Y间隔 中位 {statistics.median(neg):.0f}ms, 负=Y先)")
    print(f"X/Y间隔绝对值中位: {statistics.median([abs(s) for s in x_before_y]):.0f}ms")
print("\n样本(前20, cmd | 到达顺序 | X相对Y的ms):")
for s in samples[:20]:
    print(f"  {s[0]:<22} {' '.join(s[1]):<30} {s[2]:>6}ms")
