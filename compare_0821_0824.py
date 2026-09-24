#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""对比 8/21(优化前) 与 8/24(优化后) 检测单排耗时"""
import re, os, statistics, sys

LOG_DIR = r"C:\Users\Administrator\Desktop\RBLAOI\bin\x64\Debug\Logs"

# 行格式: 2026-08-21 11:37:20.961 [INFO] [⏱] 检测位0 总耗时=12655ms
LINE_RE = re.compile(
    r"^(\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3}) .*?\[⏱\] 检测位(\d+) (A\(移动\+拍照\)=(\d+)ms \| 总=(\d+)ms|B\+C\(2D启动\+移到起点\)=(\d+)ms|D\(3D扫描完成\)=(\d+)ms|E\(3D结果获取\)=(\d+)ms \| 总=(\d+)ms|总耗时=(\d+)ms)"
)

def parse_log(path):
    """返回: rounds = [ {pos: {A, BC, D, E, total, t_end}}, ... ] 按检测位0的A出现切轮"""
    entries = {}  # (round_idx, pos) -> dict
    rounds = []
    cur = None
    lines = open(path, encoding="utf-8-sig", errors="ignore").read().splitlines()
    for ln in lines:
        m = LINE_RE.match(ln)
        if not m:
            continue
        ts, pos, _, a, atotal, bc, d, e, etotal, total = m.groups()
        pos = int(pos)
        # 新排: 检测位0 的 A 段
        if pos == 0 and a is not None:
            cur = {"pos": {}}
            rounds.append(cur)
        if cur is None:
            continue
        rec = cur["pos"].setdefault(pos, {"t": ts})
        if a is not None:
            rec["A"] = int(a); rec["A_total"] = int(atotal); rec["tA"] = ts
        if bc is not None:
            rec["BC"] = int(bc)
        if d is not None:
            rec["D"] = int(d); rec["tD"] = ts
        if e is not None:
            rec["E"] = int(e); rec["E_total"] = int(etotal)
        if total is not None:
            rec["total"] = int(total); rec["t_end"] = ts
    # 输出带统计
    return rounds

def ts_to_ms(ts):
    h, m, s = ts.split(" ")[1].split(":")
    return int(h) * 3600000 + int(m) * 60000 + int(float(s) * 1000)

def fmt_round(idx, rd):
    poss = sorted(rd["pos"].keys())
    if not poss:
        return None
    n = len(poss)
    totals = [rd["pos"][p]["total"] for p in poss if "total" in rd["pos"][p]]
    durs = [rd["pos"][p]["D"] for p in poss if "D" in rd["pos"][p]]
    if not totals:
        return None
    # 排耗时 = 最后检测位结束 - 第一个检测位 A 开始
    first = rd["pos"][poss[0]]
    last = rd["pos"][poss[-1]]
    if "tA" in first and "t_end" in last:
        row_ms = ts_to_ms(last["t_end"]) - ts_to_ms(first["tA"])
    else:
        row_ms = None
    med = statistics.median(totals)
    avg = statistics.mean(totals)
    return {
        "idx": idx, "n": n, "poss": poss,
        "totals": totals, "med": med, "avg": avg,
        "row_ms": row_ms,
        "A_med": statistics.median([rd["pos"][p]["A"] for p in poss if "A" in rd["pos"][p]]) if any("A" in rd["pos"][p] for p in poss) else None,
        "D_med": statistics.median(durs) if durs else None,
    }

def main():
    targets = [
        ("8/21 基线 log_20260821_113621", os.path.join(LOG_DIR, "log_20260821_113621.txt")),
        ("8/21 基线 log_20260821_172511", os.path.join(LOG_DIR, "log_20260821_172511.txt")),
        ("8/24 优化后 log_20260824_170535", os.path.join(LOG_DIR, "log_20260824_170535.txt")),
    ]
    for name, path in targets:
        if not os.path.exists(path):
            print(f"{name}: 文件不存在")
            continue
        rounds = parse_log(path)
        print(f"\n===== {name} ({len(rounds)} 轮) =====")
        good = 0
        for i, rd in enumerate(rounds):
            info = fmt_round(i, rd)
            if info is None:
                continue
            flag = ""
            # 判定异常轮: 有D段但中位数D<1000(重试) 或 total 含 >11000
            if info["n"] >= 8 and max(info["totals"]) < 10000 and (info["D_med"] is None or info["D_med"] > 2500):
                good += 1
                flag = "  <-- 完整干净排"
            print(f"  轮{i}: {info['n']}位置 totals={info['totals']} 中位={info['med']} 均值={info['avg']} "
                  f"A中位={info['A_med']} D中位={info['D_med']} 排耗时={info['row_ms']}ms{flag}")
        print(f"  --> 完整干净排(>=8位置)数量: {good}")

if __name__ == "__main__":
    main()
