# -*- coding: utf-8 -*-
"""
3D检测延迟分析工具 - 统计日志中 D(3D扫描完成) → E(3D结果获取) 的间隔分布

用法:
  python analyze_3d_delay.py [日志目录] [文件名关键字] [文件数量N]
    - 默认: 只分析 logs 目录下【最新】的1个日志文件
    - 关键字: 只分析文件名包含关键字的文件 (如 20260820)
    - 数量N: 0=全部, 1=最新1个(默认), 3=最新3个

注意: 本工具只读取已有日志, 不会触发检测。
      请在【运行一轮检测之后】再运行, 分析的就是最新那一轮。
"""
import re, glob, sys, datetime, os, collections

PAT_D = re.compile(r'(\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3}) .*\[⏱\] 检测位(\d+) D\(')
PAT_E = re.compile(r'(\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3}) .*\[⏱\] 检测位(\d+) E\(')
PAT_TIMEOUT = re.compile(r'(\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3}) .*3D数据超时')
# 真实延迟度量: 轴运动结束(X_COMPLETE) → VM推送(DETECT_3D_OK)
# D 日志本身包含 CAMERA3D_OFF 握手延迟(0~500ms)，D→E 会低估真实等待
PAT_XCOMPLETE = re.compile(r'(\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3}) .*\[Recv\] X_COMPLETE')
PAT_RECV3D = re.compile(r'(\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3}) .*\[Recv3d\] DETECT_3D_OK')
FMT = '%Y-%m-%d %H:%M:%S.%f'

def ts(s): return datetime.datetime.strptime(s, FMT)

def analyze(f):
    rows = []
    for line in open(f, encoding='utf-8', errors='ignore'):
        m = PAT_D.search(line)
        if m: rows.append(('D', m.group(2), ts(m.group(1))))
        m = PAT_E.search(line)
        if m: rows.append(('E', m.group(2), ts(m.group(1))))
    gaps, first_gap, to_count = [], None, 0
    for i, r in enumerate(rows):
        if r[0] == 'D':
            for j in range(i + 1, min(i + 4, len(rows))):
                if rows[j][0] == 'E':
                    g = round((rows[j][2] - r[2]).total_seconds() * 1000)
                    gaps.append(g)
                    if first_gap is None: first_gap = g
                    break
    for line in open(f, encoding='utf-8', errors='ignore'):
        if PAT_TIMEOUT.search(line): to_count += 1
    return gaps, first_gap, to_count

def analyze_motion_to_push(f):
    """真实链路延迟: 扫描运动结束 → VM推送非空DETECT_3D_OK → D日志
    锚点: 每拍一次 D 日志；运动结束 = D 前最近一次 X_COMPLETE（轮询空闲时也刷，
    但 D 前最后一批 X_COMPLETE+POS 即为该拍成功的扫描结束时刻）
    同时统计双扫描率（CAMERA3D_OFF 次数 / 成拍数；>1 说明存在失败重扫，每多1次≈多耗 Scan3DTimeout）
    返回 ([(运动→推送ms, 运动→D日志ms), ...], 双扫描次数, 成拍数)"""
    events = []  # (时间, 类型: X=运动结束 P=VM非空推送 O=发OFF D=D日志)
    last_ts = None
    for line in open(f, encoding='utf-8', errors='ignore'):
        m = re.match(r'(2026-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3})', line)
        if m:
            last_ts = m.group(1)
        if last_ts is None:
            continue
        if 'X_COMPLETE' in line:
            events.append((ts(last_ts), 'X'))
        elif '[Recv3d] DETECT_3D_OK' in line:
            events.append((ts(last_ts), 'E' if 'DETECT_3D_OK:|' in line else 'P'))
        elif '[Send] CAMERA3D_OFF' in line:
            events.append((ts(last_ts), 'O'))
        elif '[⏱]' in line and 'D(' in line:
            events.append((ts(last_ts), 'D'))
    out, n_off, n_d = [], 0, 0
    for i, (t, kind) in enumerate(events):
        if kind == 'O':
            n_off += 1
            continue
        if kind != 'D':
            continue
        n_d += 1
        xc = next((e for e in reversed(events[:i]) if e[1] == 'X'), None)
        if xc is None or (t - xc[0]).total_seconds() > 30:
            continue
        t_motion = xc[0]
        push = next((e for e in events if e[1] == 'P' and e[0] >= t_motion), None)
        if push is None:
            continue
        out.append((
            round((push[0] - t_motion).total_seconds() * 1000),
            round((t - t_motion).total_seconds() * 1000),
        ))
    return out, n_off, n_d

def report(f):
    gaps, first_gap, to_count = analyze(f)
    name = os.path.basename(f)
    mtime = datetime.datetime.fromtimestamp(os.path.getmtime(f))
    age_min = round((datetime.datetime.now() - mtime).total_seconds() / 60)
    print('=' * 72)
    print(f'文件: {name}   生成时间: {mtime.strftime("%Y-%m-%d %H:%M:%S")}  (约 {age_min} 分钟前)')
    print('=' * 72)
    if not gaps:
        print('该日志中暂无检测计时记录。')
        print('→ 请先在上位机上【运行一轮检测】，再重新运行本工具。')
        print('   (程序启动时就会生成日志文件，但没有检测记录时无法分析)')
        return
    over1s = [g for g in gaps if g > 1000]
    over5s = [g for g in gaps if g > 5000]
    stable = gaps[1:] if len(gaps) > 1 else []
    stable_over1s = len([g for g in stable if g > 1000])
    print(f'检测 {len(gaps)} 拍 | 首拍 D→E: {first_gap}ms | 后续拍 >1s: {stable_over1s}/{len(stable)}')
    print(f'全部间隔(ms): {" ".join(str(g) for g in gaps[:50])}{" ..." if len(gaps) > 50 else ""}')
    if over1s:
        print(f'⚠ {len(over1s)} 拍 >1s (max {max(over1s)}ms); >5s 的 {len(over5s)} 拍(疑似超时)')
    if to_count:
        print(f'⚠ 3D数据超时警告: {to_count} 次')
    m2p, n_off, n_d = analyze_motion_to_push(f)
    if m2p:
        pushes = [p for p, _ in m2p]
        print(f'真实链路(运动结束→VM推送): {" ".join(f"{p}ms" for p in pushes[:20])}'
              f'{" ..." if len(pushes) > 20 else ""}')
        print(f'  VM推送 min={min(pushes)}ms max={max(pushes)}ms avg={round(sum(pushes)/len(pushes))}ms '
              f'(这部分是VM侧计算耗时, 上位机优化无效)')
        slow_off = [d for _, d in m2p if d > 600]
        if slow_off:
            print(f'  ⚠ {len(slow_off)} 拍 运动结束→D日志 >600ms {slow_off[:10]} '
                  f'(含CAMERA3D_OFF握手/2D等待, 上位机可优化)')
    if n_d and n_off > n_d:
        print(f'  ⚠ 双扫描: SCAN3D结束(OFF) {n_off} 次 / 成拍 {n_d} 次 '
              f'→ 每拍多 {(n_off - n_d) / n_d:.1f} 次重扫, 每次重扫≈白等一个 Scan3DTimeout(当前4000ms)')
    if first_gap is not None and first_gap > 1000 and stable_over1s == 0 and len(gaps) >= 3:
        print('▶ 判定: 首拍预热型 (首拍慢, 后续均 <1s) → VM侧预热, 上位机优化无效')
    elif stable_over1s > 0:
        print('▶ 判定: 偶发慢/每拍慢型 → 需查 VM 推送延迟或运动层完成判定')
    else:
        print('▶ 判定: 正常型 (D→E 均在 1s 内)')

def main():
    base = os.path.dirname(os.path.abspath(__file__))
    logdir = sys.argv[1] if len(sys.argv) > 1 else os.path.join(base, 'bin', 'x64', 'Debug', 'logs')
    kw = sys.argv[2] if len(sys.argv) > 2 else ''
    n = int(sys.argv[3]) if len(sys.argv) > 3 and sys.argv[3].isdigit() else 1
    files = sorted(glob.glob(os.path.join(logdir, 'log_*.txt')),
                   key=os.path.getmtime, reverse=True)
    if kw:
        files = [f for f in files if kw in os.path.basename(f)]
    if n > 0:
        files = files[:n]
    if not files:
        print('未找到日志文件:', logdir); return
    print(f'日志目录: {logdir}')
    if n == 1:
        print(f'正在分析最新 1 个日志文件（如要分析最近 N 个，请加参数，例如: 分析3D延迟.bat 默认 / 全部: 传 0）')
    for f in files:
        report(f)
        print()

if __name__ == '__main__':
    main()
