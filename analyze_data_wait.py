# -*- coding: utf-8 -*-
"""量化 16mm/s 轮: ①X_COMPLETE→DETECT_3D_OK 数据等待 ②移动下一检测位耗时([Send]PTP→GRAB)"""
import re
from statistics import median

LOG = r"C:\Users\Administrator\Desktop\RBLAOI\bin\x64\Debug\Logs\log_20260825_101923.txt"
START_LINE = 40948   # 16mm/s 设置行
END_LINE = 46553     # 16 轮汇总行

ts_pat = re.compile(r"^(\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3})")
scan_pat = re.compile(r"\[Scan3DToAndWaitOK\] 发送: SCAN3D")
xc_pat = re.compile(r"^\s*\[Recv\] X_COMPLETE")
d3d_pat = re.compile(r"\[Recv3d\] DETECT_3D_OK:")
move_pat = re.compile(r"^\s*\[Send\] X\d+,Y\d+,Z\d+")   # PTP 移动（带 Z）
grab_pat = re.compile(r"\[Send2d\] GRAB")
e_pat = re.compile(r"\[⏱\] 检测位\d+ E\(3D结果获取\)")

def to_ms(t):
    h, m, s = t[11:13], t[14:16], t[17:23]
    return int(h)*3600000 + int(m)*60000 + int(s[:2])*1000 + int(s[3:6])

events = []   # (kind, line, ts)
last_ts = None
with open(LOG, encoding="utf-8-sig", errors="replace") as f:
    for ln, line in enumerate(f, 1):
        if ln < START_LINE: continue
        if ln > END_LINE: break
        m = ts_pat.match(line)
        if m:
            last_ts = m.group(1)
        if last_ts is None: continue
        # 事件与时间戳可能同行（[Recv3d]/[Send2d]/[INFO]）也可能独立行（[Recv]/[Send]），统一全行搜索
        if scan_pat.search(line): events.append(("scan", ln, to_ms(last_ts)))
        elif xc_pat.match(line): events.append(("xcomp", ln, to_ms(last_ts)))
        elif d3d_pat.search(line): events.append(("d3d", ln, to_ms(last_ts)))
        elif move_pat.match(line): events.append(("move", ln, to_ms(last_ts)))
        elif grab_pat.search(line): events.append(("grab", ln, to_ms(last_ts)))
        elif e_pat.search(line): events.append(("e", ln, to_ms(last_ts)))

# 每个 SCAN3D → 其后第一个 X_COMPLETE → 其后第一个 DETECT_3D_OK（边界：下一次 scan）
data_waits, scan_times = [], []
n_scan = 0
for i, (k, ln, t) in enumerate(events):
    if k != "scan": continue
    n_scan += 1
    xc = next((e for e in events[i+1:] if e[0] == "xcomp"), None)
    d3 = next((e for e in events[i+1:] if e[0] == "d3d"), None)
    if xc and d3 and d3[1] > xc[1]:
        scan_times.append(d3[2] - t)      # SCAN3D 发送→数据到达（总）
        data_waits.append(d3[2] - xc[2])  # X_COMPLETE→数据到达（纯等待）

# 移动耗时: 每次 [Send] PTP → 其后第一个 GRAB（边界: 下一次 move）
move_times = []
for i, (k, ln, t) in enumerate(events):
    if k != "move": continue
    g = next((e for e in events[i+1:] if e[0] == "grab"), None)
    if g and g[1] < t + 20000:   # 20s 内
        move_times.append(g[2] - t)

def dist(vals):
    v = sorted(vals)
    p = lambda q: v[min(len(v)-1, int(q*len(v)))]
    return f"n={len(v)} min={v[0]} p25={p(0.25)} 中位={p(0.5)} p75={p(0.75)} p90={p(0.9)} max={v[-1]}"

print(f"SCAN3D 次数: {n_scan}")
print(f"\n① X_COMPLETE→DETECT_3D_OK 纯数据等待(可异步化部分):\n   {dist(data_waits)}")
print(f"\n② SCAN3D发送→数据到达 总延迟:\n   {dist(scan_times)}")
print(f"\n③ [Send]PTP移动→GRAB 移动耗时(隐藏窗口):\n   {dist(move_times)}")
if data_waits and move_times:
    mw, mm = median(data_waits), median(move_times)
    print(f"\n>>> 中位数据等待 {mw}ms vs 中位移动 {mm}ms → 若异步化, 每拍隐藏约 {min(mw, mm)}ms")
    print(f">>> 理论收益: 单拍 4625 → {4625 - min(mw, mm)}ms ({(min(mw, mm)/4625)*100:.1f}%), 40位排 {190*1000 - min(mw, mm)*39:.0f}ms 内省 {min(mw, mm)*39:.0f}ms")
