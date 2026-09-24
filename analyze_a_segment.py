# -*- coding: utf-8 -*-
"""对比异步化前后 A 段构成: 文件就绪→A行 间隔(=复制+ReadImage[前]/仅复制[后]) + A段/移动/等待耗时"""
import re, sys
from statistics import median

def parse(log, start, end, tag):
    ts_pat = re.compile(r"^(\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3})")
    ready_pat = re.compile(r"文件就绪: .*等待耗时: (\d+)ms")
    a_pat = re.compile(r"\[⏱\] 检测位(\d+) A\(移动\+拍照\)=(\d+)ms")
    move_pat = re.compile(r"^\s*\[Send\] X\d+,Y\d+,Z\d+")
    xc_pat = re.compile(r"^\s*\[Recv\] X_COMPLETE")

    def to_ms(t):
        h, m, s = t[11:13], t[14:16], t[17:23]
        return int(h)*3600000 + int(m)*60000 + int(s[:2])*1000 + int(s[3:6])

    last_ts, rows, cur_a, cur_ready = None, [], [], []
    A = {}   # pos -> {a_ms, ready_to_a:[]}
    with open(log, encoding="utf-8-sig", errors="replace") as f:
        for ln, line in enumerate(f, 1):
            if ln < start or ln > end: continue
            m = ts_pat.match(line)
            if m: last_ts = m.group(1)
            if last_ts is None: continue
            rm = ready_pat.search(line)
            if rm:
                cur_ready.append((to_ms(last_ts), int(rm.group(1))))
                continue
            am = a_pat.search(line)
            if am:
                pos, a_ms = int(am.group(1)), int(am.group(2))
                ready_to_a = []
                for rt, w in cur_ready:
                    ready_to_a.append((to_ms(last_ts) - rt, w))
                A[pos] = {"a": a_ms, "ready_to_a": ready_to_a}
                cur_ready = []
    return A

A_am = parse(r"C:\Users\Administrator\Desktop\RBLAOI\bin\x64\Debug\Logs\log_20260825_101923.txt", 40948, 46553, "上午")
A_pm = parse(r"C:\Users\Administrator\Desktop\RBLAOI\bin\x64\Debug\Logs\log_20260825_141716.txt", 9687, 15074, "下午")

def stats(vals):
    v = sorted(vals)
    p = lambda q: v[min(len(v)-1, int(q*len(v)))]
    return f"n={len(v)} 中位={p(0.5)} p90={p(0.9)} max={v[-1]}"

print("=== A段总耗时 ===")
print(f"上午(同步ReadImage): {stats([a['a'] for a in A_am.values()])}")
print(f"下午(异步ReadImage): {stats([a['a'] for a in A_pm.values()])}")

print("\n=== 文件就绪→A行 间隔(含复制; 上午含同步ReadImage, 下午不含) ===")
print(f"上午: {stats([d[0] for a in A_am.values() for d in a['ready_to_a']])}")
print(f"下午: {stats([d[0] for a in A_pm.values() for d in a['ready_to_a']])}")

print("\n=== 文件就绪等待耗时(日志自带) ===")
print(f"上午: {stats([d[1] for a in A_am.values() for d in a['ready_to_a']])}")
print(f"下午: {stats([d[1] for a in A_pm.values() for d in a['ready_to_a']])}")

# 同位置对比
print("\n=== 同位置 A段对比 ===")
common = sorted(set(A_am) & set(A_pm))
diffs = [A_pm[p]['a'] - A_am[p]['a'] for p in common]
print(f"位数: {len(common)}, Δ中位: {median(diffs):+.0f}ms")
for p in common[:12]:
    print(f"  位{p}: 上午{A_am[p]['a']} → 下午{A_pm[p]['a']} ({A_pm[p]['a']-A_am[p]['a']:+d}ms)")
