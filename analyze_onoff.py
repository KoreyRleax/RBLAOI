import re, glob, os

def analyze(path):
    events = []
    lines = open(path, encoding='utf-8', errors='ignore').read().splitlines()
    last_ts = None
    for ln in lines:
        m = re.match(r'^(\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d+) \[DEBUG\]\s*$', ln)
        if m:
            last_ts = m.group(1)
            continue
        s = re.match(r'^\s*\[Send\]\s+(CAMERA3D_ON|CAMERA3D_OFF)\s*$', ln)
        if s and last_ts:
            events.append(('S', s.group(1), last_ts))
            last_ts = None
            continue
        r = re.match(r'^\s*\[Recv\]\s+(CAMERA3D_ON_OK|CAMERA3D_OFF_OK)\s*$', ln)
        if r and last_ts:
            events.append(('R', r.group(1), last_ts))
            last_ts = None
            continue
        if ln.strip():
            last_ts = None
    from datetime import datetime
    def to_ms(ts):
        return datetime.strptime(ts, '%Y-%m-%d %H:%M:%S.%f').timestamp() * 1000
    pairs = {'CAMERA3D_ON': [], 'CAMERA3D_OFF': []}
    pending = {}
    for kind, cmd, ts in events:
        if kind == 'S':
            pending[cmd] = to_ms(ts)
        else:
            key = 'CAMERA3D_ON' if 'ON_OK' in cmd else 'CAMERA3D_OFF'
            if key in pending:
                pairs[key].append(to_ms(ts) - pending[key])
                del pending[key]
            else:
                pairs[key].append(None)
    return pairs

def stat(vals):
    vals = [v for v in vals if v is not None]
    if not vals: return None
    vals.sort()
    n = len(vals)
    return (n, vals[n//2], vals[int(n*0.9)], vals[-1], sum(vals)/n)

files = sorted(glob.glob(r'C:/Users/Administrator/Desktop/RBLAOI/bin/x64/Debug/Logs/log_202608*.txt'))
for f in files:
    base = os.path.basename(f)
    try:
        pairs = analyze(f)
    except Exception as e:
        print(base, 'ERR', e); continue
    for key in ('CAMERA3D_ON', 'CAMERA3D_OFF'):
        s = stat(pairs[key])
        if s and s[0] > 0:
            print(f"{base} {key}: n={s[0]} 中位={s[1]:.0f}ms p90={s[2]:.0f}ms max={s[3]:.0f}ms 均值={s[4]:.0f}ms")
