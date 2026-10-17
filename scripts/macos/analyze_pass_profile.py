#!/usr/bin/env python3
# Copyright (C) 2026 SharpEmu Emulator Project
# SPDX-License-Identifier: GPL-2.0-or-later
"""Aggregate SharpEmu Metal V2 [METAL-V2][PASS-PROF] encoder lines from a game.log.

Usage: python3 -I analyze_pass_profile.py <game.log | run-dir> [--from-frame N] [--to-frame M] [--top N]
"""
import argparse
import os
import re
import statistics
import sys
from collections import Counter, defaultdict

ENC_RE = re.compile(
    r'\[METAL-V2\]\[PASS-PROF\] f=(\d+) s=(\d+) i=(\d+) k=(\S+) us=([\d.]+) gap_us=([\d.]+) '
    r'(?:vs_us=[\d.]+ fs_us=[\d.]+ )?draws=(\d+) tg=(\d+) ld_mb=([\d.]+) st_mb=([\d.]+) '
    r'end=(\S+) a=\[(.*?)\] sh=\[(.*?)\] after=\[(.*?)\] n_after=(\d+)')
RES_RE = re.compile(r'^(\d+x\d+)')


def shape_key(a):
    """Resolution plus attachment formats, e.g. '1280x720 R8G8B8A8Unorm,d:D32Sfloat'."""
    m = RES_RE.match(a)
    res = m.group(1) if m else '?'
    fmts = []
    cm = re.search(r'c=\[([^\]]*)\]', a)
    if cm and cm.group(1):
        fmts += [x.split(':', 1)[0] for x in cm.group(1).split(',') if x]
    dm = re.search(r'd=([^:\s\]]+)', a)
    if dm:
        fmts.append('d:' + dm.group(1))
    return f"{res} {','.join(fmts) or '-'}"


def median(vals):
    return statistics.median(vals) if vals else 0.0


def pctl(vals, p):
    if not vals:
        return 0.0
    s = sorted(vals)
    return s[min(len(s) - 1, int(round((len(s) - 1) * p)))]


def mode(vals):
    return Counter(vals).most_common(1)[0][0] if vals else '-'


def trunc(s, n):
    return s if len(s) <= n else s[:n - 3] + '...'


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument('path', help='game.log, or a run directory containing game.log')
    ap.add_argument('--from-frame', type=int, default=None)
    ap.add_argument('--to-frame', type=int, default=None)
    ap.add_argument('--top', type=int, default=15)
    args = ap.parse_args()

    path = args.path
    if os.path.isdir(path):
        path = os.path.join(path, 'game.log')
    if not os.path.isfile(path):
        print(f"error: no such log: {path}", file=sys.stderr)
        return 2

    encs = []
    with open(path, encoding='utf-8', errors='replace') as fh:
        for line in fh:
            if 'PASS-PROF] f=' not in line:
                continue
            m = ENC_RE.search(line)
            if not m:
                continue
            f = int(m.group(1))
            if args.from_frame is not None and f < args.from_frame:
                continue
            if args.to_frame is not None and f > args.to_frame:
                continue
            after_raw = m.group(14).strip()
            after = [] if after_raw in ('', '-') else [x.strip() for x in after_raw.split(',') if x.strip()]
            encs.append({
                'f': f, 'k': m.group(4), 'us': float(m.group(5)), 'gap': float(m.group(6)),
                'draws': int(m.group(7)), 'ld': float(m.group(9)), 'st': float(m.group(10)),
                'end': m.group(11), 'a': m.group(12), 'sh': m.group(13), 'after': after,
            })

    if not encs:
        print('no PASS-PROF encoder lines in selection')
        return 0

    frames = defaultdict(lambda: dict(us=0.0, gap=0.0, render=0, compute=0, ld=0.0, st=0.0, draws=0))
    for e in encs:
        F = frames[e['f']]
        F['us'] += e['us']
        F['gap'] += e['gap']
        F['ld'] += e['ld']
        F['st'] += e['st']
        F['draws'] += e['draws']
        if e['k'] == 'render':
            F['render'] += 1
        elif e['k'] == 'compute':
            F['compute'] += 1
    frame_ids = sorted(frames)
    n = len(frame_ids)
    renders = [e for e in encs if e['k'] == 'render']
    computes = [e for e in encs if e['k'] == 'compute']
    total_render_us = sum(e['us'] for e in renders) or 1.0
    total_compute_us = sum(e['us'] for e in computes) or 1.0
    top = args.top

    # A. Frame summary
    print(f"A. {n} frames (f{frame_ids[0]}..f{frame_ids[-1]}), {len(encs)} encoders")
    print(f"   {'metric':<14}{'median':>12}{'p90':>12}")
    for label, key in [('enc us', 'us'), ('gap us', 'gap'), ('render enc', 'render'),
                       ('compute enc', 'compute'), ('ld_mb', 'ld'), ('st_mb', 'st'), ('draws', 'draws')]:
        vals = [frames[f][key] for f in frame_ids]
        print(f"   {label:<14}{median(vals):>12.1f}{pctl(vals, 0.9):>12.1f}")
    count = 15
    while True:
        step = max(1, -(-n // count))
        parts = [f"f{f}={(frames[f]['us'] + frames[f]['gap']) / 1000:.1f}" for f in frame_ids[::step]]
        line = 'GPU ms/frame (every %d): ' % step + ' '.join(parts)
        if len(line) < 200 or count <= 2:
            break
        count -= 1
    print('   ' + line)

    # B. End causes for render encoders
    print('\nB. Render end causes')
    print(f"   {'end':<26}{'count':>8}{'/frame':>9}{'sum_us':>13}{'ld_MB':>11}{'st_MB':>11}{'share':>8}")
    by_end = defaultdict(list)
    for e in renders:
        by_end[e['end']].append(e)
    for end, es in sorted(by_end.items(), key=lambda kv: -len(kv[1])):
        su = sum(e['us'] for e in es)
        print(f"   {trunc(end, 26):<26}{len(es):>8}{len(es) / n:>9.2f}{su:>13.0f}"
              f"{sum(e['ld'] for e in es):>11.1f}{sum(e['st'] for e in es):>11.1f}"
              f"{100 * su / total_render_us:>7.1f}%")

    # C. Attachment shape for render encoders
    print('\nC. Render attachment shapes (resolution + formats)')
    print(f"   {'shape':<44}{'enc/frm':>9}{'med_us':>10}{'share':>8}{'(ld+st)MB/frm':>15}")
    by_shape = defaultdict(list)
    for e in renders:
        by_shape[shape_key(e['a'])].append(e)
    rows = sorted(by_shape.items(), key=lambda kv: -sum(e['us'] for e in kv[1]))[:top]
    for key, es in rows:
        su = sum(e['us'] for e in es)
        mb = sum(e['ld'] + e['st'] for e in es)
        print(f"   {trunc(key, 44):<44}{len(es) / n:>9.2f}{median([e['us'] for e in es]):>10.0f}"
              f"{100 * su / total_render_us:>7.1f}%{mb / n:>15.1f}")

    # D. Top render passes by GPU time
    print('\nD. Top render passes (shape, shader hashes)')
    print(f"   {'enc/frm':>8}{'med_us':>9}{'share':>8}  {'typ end':<22}{'med ld':>8}{'med st':>8}  shape / shaders")
    by_pass = defaultdict(list)
    for e in renders:
        by_pass[(shape_key(e['a']), e['sh'])].append(e)
    rows = sorted(by_pass.items(), key=lambda kv: -sum(e['us'] for e in kv[1]))[:top]
    for (shape, sh), es in rows:
        su = sum(e['us'] for e in es)
        print(f"   {len(es) / n:>8.2f}{median([e['us'] for e in es]):>9.0f}{100 * su / total_render_us:>7.1f}%"
              f"  {trunc(mode([e['end'] for e in es]), 22):<22}"
              f"{median([e['ld'] for e in es]):>8.1f}{median([e['st'] for e in es]):>8.1f}"
              f"  {trunc(shape, 40)} {trunc(sh, 80)}")

    # E. Top compute dispatch groups
    print('\nE. Top compute dispatch groups (by sh string)')
    print(f"   {'enc/frm':>8}{'med_us':>10}{'share':>8}  sh")
    by_disp = defaultdict(list)
    for e in computes:
        by_disp[e['sh']].append(e)
    rows = sorted(by_disp.items(), key=lambda kv: -sum(e['us'] for e in kv[1]))[:top]
    for sh, es in rows:
        su = sum(e['us'] for e in es)
        print(f"   {len(es) / n:>8.2f}{median([e['us'] for e in es]):>10.0f}{100 * su / total_compute_us:>7.1f}%"
              f"  {trunc(sh, 100)}")

    # F. Transfer-ended render passes and what they caused
    print('\nF. Transfer-ended render passes (MB = st_mb + next render ld_mb if same shape)')
    by_cat = defaultdict(lambda: [0, 0.0])
    by_cause = defaultdict(lambda: [0, 0.0])
    for idx, e in enumerate(renders):
        if not e['end'].startswith('transfer:'):
            continue
        nxt = renders[idx + 1] if idx + 1 < len(renders) else None
        if nxt is not None and nxt['f'] != e['f']:
            nxt = None
        mb = e['st'] + (nxt['ld'] if nxt is not None and nxt['a'] == e['a'] else 0.0)
        cat = ' '.join(e['after'][0].split()[:2]) if e['after'] else '(none)'
        by_cat[cat][0] += 1
        by_cat[cat][1] += mb
        by_cause[e['end']][0] += 1
        by_cause[e['end']][1] += mb
    print(f"   {'first after category':<32}{'count':>8}{'MB':>12}")
    for cat, (c, mb) in sorted(by_cat.items(), key=lambda kv: -kv[1][1])[:top]:
        print(f"   {trunc(cat, 32):<32}{c:>8}{mb:>12.1f}")
    print(f"   {'end cause':<32}{'count':>8}{'MB':>12}")
    for cause, (c, mb) in sorted(by_cause.items(), key=lambda kv: -kv[1][1])[:top]:
        print(f"   {trunc(cause, 32):<32}{c:>8}{mb:>12.1f}")

    # G. Gaps
    print('\nG. Gaps')
    gap_frames = [frames[f]['gap'] for f in frame_ids]
    print(f"   gap us/frame: median {median(gap_frames):.0f}  p90 {pctl(gap_frames, 0.9):.0f}")
    big = defaultdict(lambda: [0, 0.0])
    big_total = 0.0
    for idx in range(1, len(encs)):
        e, prev = encs[idx], encs[idx - 1]
        if e['gap'] > 500 and prev['f'] == e['f']:
            key = f"{prev['k']}/{prev['end']}"
            big[key][0] += 1
            big[key][1] += e['gap']
            big_total += e['gap']
    print(f"   gaps > 500us: {sum(v[0] for v in big.values())} totalling {big_total:.0f} us; "
          f"preceding encoder kind/end:")
    print(f"   {'prev kind/end':<32}{'count':>8}{'gap_us':>14}{'share':>8}")
    for key, (c, gu) in sorted(big.items(), key=lambda kv: -kv[1][1])[:top]:
        print(f"   {trunc(key, 32):<32}{c:>8}{gu:>14.0f}{100 * gu / (big_total or 1.0):>7.1f}%")
    return 0


if __name__ == '__main__':
    sys.exit(main())
