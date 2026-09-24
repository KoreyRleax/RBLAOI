#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""分析 41位轮: 解析3D → 下一检测位拍照完成 耗时构成（纯时间戳）"""
import re, datetime, statistics

F_824 = r"C:\Users\Administrator\Desktop\RBLAOI\bin\x64\Debug\Logs\log_20260824_170535.txt"
TS_RE = re.compile(r"^(\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3})")

def t2ms(ts):
    t = datetime.datetime.strptime(ts, "%Y-%m-%d %H:%M:%S.%f")
    return t.hour*3600000 + t.minute*60000 + t.second*1000 + t.microsecond//1000

# 事件序列: (t_ms, type, extra)
evs = []
cur_ts = None
lines = open(F_824, encoding="utf-8-sig", errors="ignore").read().splitlines()
for ln in lines:
    tm = TS_RE.match(ln)
    if tm:
        cur_ts = tm.group(1)
    if cur_ts is None:
        continue
    t = t2ms(cur_ts)
    m = re.search(r"\[⏱\] 检测位\d+ A\(移动\+拍照\)=(\d+)ms", ln)
    if m:
        evs.append((t, "A_END", int(m.group(1)))); continue
    m = re.search(r"\[⏱\] 检测位\d+ E\(3D结果获取\)=\d+ms \| 总=\d+ms", ln)
    if m:
        evs.append((t, "E_END", 0)); continue
    m = re.search(r"\[⏱\] 检测位\d+ 总耗时=(\d+)ms", ln)
    if m:
        evs.append((t, "TOTAL", int(m.group(1)))); continue
    m = re.search(r"\[Send2d\] GRAB", ln)
    if m:
        evs.append((t, "GRAB_SEND", 0)); continue
    m = re.search(r"文件就绪:.*等待耗时: (\d+)ms", ln)
    if m:
        evs.append((t, "FILE_READY", int(m.group(1)))); continue
    if "Send]" in ln:
        m = re.search(r"Send\]\s*(X\d+,\s*Y\d+(?:,\s*Z\d+)?)", ln)
        if m and "SCAN3D" not in m.group(1):
            evs.append((t, "MOVE", m.group(1))); continue
    if "Recv]" in ln:
        m = re.search(r"Recv\]\s*(X|Y|Z)_COMPLETE", ln)
        if m:
            evs.append((t, "COMP", m.group(1))); continue

# 只取 41位轮
base = t2ms("2026-08-24 17:25:59.000")
evs = [e for e in evs if e[0] >= base]

# 以 TOTAL 为锚: 每个 TOTAL 之后 = 解析完3D → 移动到下一检测位(拍照完成)
# 段 i: TOTAL[i] ~ A_END 中第一个大于 TOTAL[i] 的（=下一检测位A_END）
totals = [e for e in evs if e[1] == "TOTAL"]
print(f"TOTAL 事件: {len(totals)}")

seg = []
for i, te in enumerate(totals):
    t_total = te[0]
    # 找之后第一个 A_END
    nxt = [e for e in evs if e[0] > t_total and e[1] == "A_END"]
    if not nxt:
        continue
    t_end = nxt[0][0]
    sub = [e for e in evs if t_total < e[0] <= t_end]
    # 解析耗时: TOTAL 自身的 a 值? 用 TOTAL-E 对 (上一个 E_END)
    # 衔接: 首个在 TOTAL 后的 MOVE
    moves_after = [e for e in sub if e[1] == "MOVE"]
    comps_after = [e for e in sub if e[1] == "COMP"]
    grabs = [e for e in sub if e[1] == "GRAB_SEND"]
    frs = [e for e in sub if e[1] == "FILE_READY"]
    bridge = (moves_after[0][0] - t_total) if moves_after else None
    # 移动: 首MOVE→末COMP（含调Z）
    move = (comps_after[-1][0] - moves_after[0][0]) if moves_after and comps_after else None
    # GRAB: 每次 GRAB_SEND→对应 FILE_READY
    grab_times = []
    for g in grabs:
        rd = [f for f in frs if f[0] > g[0]]
        if rd:
            grab_times.append(rd[0][0] - g[0])
    grab_sum = sum(grab_times) if grab_times else None
    # 拍照收尾: 末FILE_READY→A_END
    tail = (t_end - frs[-1][0]) if frs else None
    seg_total = t_end - t_total
    seg.append({"seg_total": seg_total, "bridge": bridge, "move": move,
                "grab": grab_sum, "n_grab": len(grabs), "tail": tail,
                "gap_total_ms": te[2]})

print(f"\n{'i':>2} {'解析完→A_END':>12} {'衔接':>5} {'移动':>5} {'GRAB':>6} {'G次':>3} {'收尾':>5} {'A值':>5}")
for i, s in enumerate(seg):
    print(f"{i:>2} {s['seg_total']:>12} {str(s['bridge']):>5} {str(s['move']):>5} {str(s['grab']):>6} {s['n_grab']:>3} {str(s['tail']):>5} {s['gap_total_ms']:>5}")

print("\n===== 汇总（中位） =====")
def med(key):
    xs = [s[key] for s in seg if s[key] is not None]
    return statistics.median(xs) if xs else 0
print(f"解析完3D→下一检测位拍照完成(A_END): 中位 {med('seg_total')}ms")
print(f"  衔接(决策, TOTAL→首MOVE):       中位 {med('bridge')}ms")
print(f"  移动中心+调Z(首MOVE→末COMP):     中位 {med('move')}ms")
print(f"  GRAB拍照(GRAB_SEND→文件就绪):   中位 {med('grab')}ms  (GRAB次数中位 {statistics.median([s['n_grab'] for s in seg])})")
print(f"  收尾(末文件就绪→A_END):         中位 {med('tail')}ms")
