# -*- coding: utf-8 -*-
import re
from datetime import datetime
F = r"C:\Users\Administrator\Desktop\RBLAOI\bin\x64\Debug\Logs\log_20260821_162440.txt"
FMT = '%Y-%m-%d %H:%M:%S.%f'
patD = re.compile(r'(\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3}) .*\[⏱\] 检测位(\d+) D\(')
patE = re.compile(r'(\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3}) .*\[⏱\] 检测位(\d+) E\(')
patT = re.compile(r'(\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3}) .*检测位 (\d+) 3D数据超时，扫描成功但未收到数据')

def ts(s):
    return datetime.strptime(s, FMT)

d = {}
for line in open(F, encoding='utf-8', errors='ignore'):
    m = patD.search(line)
    if m:
        d[(m.group(2), 'D')] = ts(m.group(1))
    m = patE.search(line)
    if m:
        d[(m.group(2), 'E')] = ts(m.group(1))
    m = patT.search(line)
    if m:
        d[(m.group(2), 'T')] = ts(m.group(1))

gaps = []
for (pos, tag), t in d.items():
    if tag == 'E' and (pos, 'D') in d:
        gaps.append((pos, (t - d[(pos, 'D')]).total_seconds() * 1000))
gaps.sort(key=lambda x: x[1])
print("D(扫描完成)→E(结果获取) 间隔分布（ms）:")
print("  全部:", [round(g, 0) for _, g in gaps])
over2s = [(k, g) for k, g in gaps if g > 2000]
print("  超2s(疑似超时/重扫):", len(over2s), over2s)

timeouts = []
for (pos, tag), t in d.items():
    if tag == 'T' and (pos, 'D') in d:
        timeouts.append((pos, (t - d[(pos, 'D')]).total_seconds() * 1000))
print("超时告警 D→T(ms):", [(k, round(t, 0)) for k, t in timeouts])
