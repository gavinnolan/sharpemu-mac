#!/usr/bin/env python3
# Copyright (C) 2026 SharpEmu Emulator Project
# SPDX-License-Identifier: GPL-2.0-or-later
"""Turns one run-game.sh run directory into the performance table every perf change is judged by.

Usage:
  python3 -I scripts/macos/perf_report.py <run-dir> [--from 150] [--to 480 | --segment NAME] [--json out.json]
                                          [--baseline other-run-dir] [--md out.md]
  python3 -I scripts/macos/perf_report.py --selftest

All times are seconds since launch (game.tlog prefixes, footprint.log, and frame-times.txt placed
through run.env's launch_epoch). The window is --from .. --to (default 150 .. end of the run, the
SH cutscene). Inputs, every one optional except what a section needs:
  frame-times.txt   one line per guest flip (SHARPEMU_FRAME_TIMES, see src/SharpEmu.Libs/VideoOut/FrameTimeLog.cs).
                    Gives the frame-time table, per-30 s fps, the regime histogram and the spike list.
  game.tlog         game.log with a "[+seconds]" prefix per line (run-game.sh); without it only the
                    frame and footprint sections are filled.
    [PERF][RENDER_MS]        SHARPEMU_PROFILE_RENDER=1: exclusive worker ms per phase per window.
    [METAL-V2][PERF/CHURN/TBDR/WAITS]  always printed every ~2 s: draws, passes, GPU busy ms, uploads, writebacks.
                    The image cache's uploads/writebacks/evictions and the submission counts on PERF are running
                    totals since launch; they are turned into per-window deltas here. Every other field is per window.
    [METAL-V2][PASS-PROF]    SHARPEMU_METAL_V2_PROFILE_PASSES=1: per-encoder GPU time (analyze_pass_profile.py).
    [METAL-V2][SLOW-EOP], OverlapEvicted warnings, [METAL-PRESENT], [MEM]/[ALLOC-PROFILE] GC pauses: spike causes.
  footprint.log     "seconds footprint_mb rss_mb" per second: peak footprint.
  result.txt        peak_footprint_mb / peak_rss_mb of the whole run.

--segment NAME takes the window from markers.tsv: NAME-start .. NAME-end (plan steps t:mark:NAME-start, see input-plan.py;
the end of the run when NAME-end is missing). --baseline <run-dir> analyses that run with the same window and prints Before / After / Delta%.
Loading gaps: a spike whose [METAL-V2][PERF] windows show (almost) no submissions or passes is the guest
loading, not rendering; it is classed `loading (no GPU submissions)`, left out of the cause list and the top 10, and the
frame-time table is given with and without those gaps. frame-times.txt is placed on the game.tlog clock through its
t0_unix_us header and run.env's launch_epoch.
Spike attribution: a flip interval longer than 2x the window median is a spike. Causes come from the log
lines inside [flip before - slack, flip + slack]; SLOW-EOP and OverlapEvicted are point events, while
compile, drop_budget, adopt_blocked (a [METAL-PRESENT] window that published nothing) and GC are
2-5 s window counters, so they mark every spike inside that window (coarse).
"""
import argparse
import bisect
import importlib.util
import json
import os
import re
import sys
import tempfile
from collections import Counter, defaultdict

HERE = os.path.dirname(os.path.abspath(__file__))
TLOG = re.compile(r'^\[\+\s*([\d.]+)\]\s?(.*)$')
KV = re.compile(r'(\w+)=(-?\d+(?:\.\d+)?)(?=[\s,\]]|$)')
PHASE_MS = re.compile(r'(\w+)=([\d.]+)ms/n(\d+)')

# [METAL-V2][PERF] fields that are running totals since launch (image cache counters, submission counts).
PERF_CUMULATIVE = ('uploads', 'writebacks', 'evictions', 'submissions', 'guest_submissions', 'slices')
# [METAL-V2][CHURN] per-window counters reported per frame.
CHURN_KEYS = ('up_new', 'up_cpu', 'up_gpu', 'up_other', 'up_alias', 'wb_bind', 'wb_store', 'wb_read', 'wb_copy',
              'wb_overlap', 'wb_release', 'wb_drain', 'wb_fenced', 'drop_overlap', 'drop_budget',
              'alias_kept', 'alias_published', 'alias_reused')
TBDR_END_KEYS = ('end_clear', 'end_target', 'end_transfer', 'end_compute', 'end_flush', 'end_present',
                 'end_other', 'end_indirect')
SPIKE_FACTOR = 2.0
REGIME_BINS = ((0, 1.0, '<1'), (1.0, 1.5, '1-1.5'), (1.5, 2.0, '1.5-2'), (2.0, 3.0, '2-3'),
               (3.0, 5.0, '3-5'), (5.0, float('inf'), '>=5'))


def percentile(values, p):
    """Linear-interpolated percentile (p in 0..100) of an unsorted list; None when empty."""
    if not values:
        return None
    s = sorted(values)
    if len(s) == 1:
        return float(s[0])
    pos = (len(s) - 1) * p / 100.0
    lo = int(pos)
    hi = min(lo + 1, len(s) - 1)
    return s[lo] + (s[hi] - s[lo]) * (pos - lo)


def r(value, digits=2):
    return None if value is None else round(value, digits)


# ---------------------------------------------------------------- input readers

def read_env(path):
    env = {}
    if os.path.exists(path):
        for line in open(path, errors='replace'):
            key, sep, value = line.rstrip('\n').partition('=')
            if sep:
                env[key] = value
    return env


def read_flips(path, launch_epoch):
    """Flip times in seconds since launch."""
    if not os.path.exists(path) or launch_epoch is None:
        return []
    origin, times = None, []
    for line in open(path, errors='replace'):
        if line.startswith('#'):
            m = re.search(r't0_unix_us=(\d+)', line)
            if m:
                origin = int(m.group(1)) / 1e6 - launch_epoch
            continue
        parts = line.split()
        if len(parts) == 2 and origin is not None and parts[1].isdigit():
            times.append(origin + int(parts[1]) / 1e6)
    times.sort()
    return times


def read_markers(path, launch_epoch):
    """(seconds since launch, name) per line of markers.tsv (epoch<TAB>name, written by input-plan.py)."""
    rows = []
    if os.path.exists(path) and launch_epoch is not None:
        for line in open(path, errors='replace'):
            epoch, _, name = line.rstrip('\n').partition('\t')
            try:
                rows.append((float(epoch) - launch_epoch, name))
            except ValueError:
                pass
    return sorted(rows)


def segment_window(run, name):
    """(lo, hi) seconds since launch of segment `name`: markers <name>-start and <name>-end of markers.tsv
    (hi is None, the end of the run, when the end marker was never written)."""
    env = read_env(os.path.join(run, 'run.env'))
    launch = float(env['launch_epoch']) if env.get('launch_epoch') else None
    marks = read_markers(os.path.join(run, 'markers.tsv'), launch)
    starts = [t for t, n in marks if n == name + '-start']
    if not starts:
        raise SystemExit(f'perf_report: no {name}-start marker in {run}/markers.tsv')
    lo = starts[0]
    ends = [t for t, n in marks if n == name + '-end' and t >= lo]
    return lo, (ends[0] if ends else None)


def read_tlog(path):
    """[(seconds, text)] in file order; lines without a prefix (stack traces) inherit the previous time."""
    rows = []
    if not os.path.exists(path):
        return rows
    last = 0.0
    for line in open(path, errors='replace'):
        m = TLOG.match(line)
        if m:
            last = float(m.group(1))
            rows.append((last, m.group(2)))
        else:
            rows.append((last, line.rstrip('\n')))
    return rows


def read_footprint(path):
    rows = []
    if os.path.exists(path):
        for line in open(path, errors='replace'):
            parts = line.split()
            if len(parts) == 3 and all(p.isdigit() for p in parts):
                rows.append((float(parts[0]), int(parts[1]), int(parts[2])))
    return rows


def kv(text):
    return {k: float(v) for k, v in KV.findall(text)}


# ---------------------------------------------------------------- frame times

LOADING = 'loading (no GPU submissions)'


def interval_stats(intervals):
    """Frame-time statistics of (start, end) flip intervals; fps is intervals per second of interval time."""
    ms = [(b - a) * 1000.0 for a, b in intervals]
    out = {'intervals': len(ms)}
    if not ms:
        return out
    median = percentile(ms, 50)
    out.update({
        'fps': r(len(ms) / (sum(ms) / 1000.0), 3) if sum(ms) > 0 else None,
        'p50_ms': r(median, 1), 'p90_ms': r(percentile(ms, 90), 1), 'p99_ms': r(percentile(ms, 99), 1),
        'max_ms': r(max(ms), 1), 'mean_ms': r(sum(ms) / len(ms), 1),
        'over_2x_median': sum(1 for v in ms if v > SPIKE_FACTOR * median),
    })
    return out


def frame_section(flips, lo, hi):
    """Frame-time statistics for the flip intervals that end inside [lo, hi]."""
    pts = [t for t in flips if lo <= t <= hi]
    intervals = [(a, b) for a, b in zip(pts, pts[1:])]
    out = {'flips': len(pts)}
    out.update(interval_stats(intervals))
    return out, intervals


def is_loading(a, b, perf):
    """True when the guest submitted (almost) nothing to the GPU for most of the flip gap (a, b): the
    [METAL-V2][PERF] windows (each is the `seconds` before its tick) with fewer than 20 submissions
    and 50 passes (a frame is ~500 passes; a loading screen trickles a few submissions) cover at least 60% of the gap. Windows that straddle the gap's edges may hold
    the work just before or after it, so only the idle share has to dominate."""
    idle = 0.0
    for t, d in perf:
        sec = d.get('seconds', 0.0)
        if sec <= 0:
            continue
        overlap = min(t, b) - max(t - sec, a)
        if overlap > 0 and d.get('submissions', 0) < 20 and d.get('passes', 0) < 50:
            idle += overlap
    return b > a and idle >= 0.6 * (b - a)


def windowed_fps(flips, lo, hi, width):
    """[(start, end, flips, fps, p50_ms, max_ms)] for consecutive windows of `width` seconds."""
    rows = []
    start = lo
    while start < hi:
        end = min(start + width, hi)
        pts = [t for t in flips if start <= t < end]
        ms = [(b - a) * 1000.0 for a, b in zip(pts, pts[1:])]
        rows.append((start, end, len(pts), len(pts) / (end - start) if end > start else 0.0,
                     percentile(ms, 50), max(ms) if ms else None))
        start = end
    return rows


def regime_histogram(flips, lo, hi, width=10.0, step=5.0):
    """Share of rolling `width`-second windows (every `step` s) whose fps falls in each REGIME_BINS bin."""
    counts = Counter()
    total = 0
    start = lo
    while start + width <= hi + 1e-9:
        n = bisect.bisect_left(flips, start + width) - bisect.bisect_left(flips, start)
        fps = n / width
        for low, high, name in REGIME_BINS:
            if low <= fps < high:
                counts[name] += 1
                break
        total += 1
        start += step
    return {name: (counts[name] / total if total else None) for _, _, name in REGIME_BINS}, total


# ---------------------------------------------------------------- log counters

def parse_ticks(tlog):
    """Every periodic counter line, keyed by family, as (t, fields) with cumulative PERF fields as deltas."""
    fam = defaultdict(list)
    totals = {}
    for t, text in tlog:
        if text.startswith('[METAL-V2][PERF]'):
            d = kv(text)
            for k in PERF_CUMULATIVE:
                if k in d:
                    total = d[k]
                    d[k] = max(0.0, total - totals.get(k, 0.0))
                    totals[k] = total
            fam['perf'].append((t, d))
        elif text.startswith('[METAL-V2][CHURN]'):
            fam['churn'].append((t, kv(text)))
        elif text.startswith('[METAL-V2][TBDR]'):
            fam['tbdr'].append((t, kv(text)))
        elif text.startswith('[METAL-PRESENT]'):
            fam['present'].append((t, kv(text)))
        elif text.startswith('[PERF][RENDER_MS]'):
            d = kv(text)
            d['phases'] = {name: (float(ms), int(n)) for name, ms, n in PHASE_MS.findall(text)}
            fam['render'].append((t, d))
        elif text.startswith('[MEM] dotnet'):
            m = re.search(r'pause_ms=\d+\(\+(\d+)\)', text)
            if m:
                fam['gc'].append((t, {'pause_ms': float(m.group(1))}))
        elif text.startswith('[ALLOC-PROFILE]'):
            m = re.search(r'pause_ms=(\d+)', text)
            if m:
                fam['gc'].append((t, {'pause_ms': float(m.group(1))}))
    return fam


def in_window(rows, lo, hi):
    return [(t, d) for t, d in rows if lo < t <= hi]


def render_section(rows, lo, hi):
    sel = in_window(rows, lo, hi)
    frames = sum(d.get('frames', 0) for _, d in sel)
    if not sel or frames <= 0:
        return None
    ms = defaultdict(float)
    entries = defaultdict(int)
    for _, d in sel:
        for name, (m, n) in d['phases'].items():
            ms[name] += m
            entries[name] += n
    worker = sum(v for k, v in ms.items() if not k.startswith('Idle'))
    draws = entries.get('DrawExecutor', 0)
    phases = {k: r(v / frames, 2) for k, v in sorted(ms.items(), key=lambda kv_: -kv_[1])}
    return {
        'windows': len(sel), 'frames': frames, 'seconds': r(sum(d.get('window_s', 0) for _, d in sel), 1),
        'draws_per_frame': r(draws / frames, 0), 'worker_ms_per_frame': r(worker / frames, 1),
        'worker_us_per_draw': r(worker * 1000.0 / draws, 1) if draws else None,
        'phase_ms_per_frame': phases,
        'phase_entries_per_frame': {k: r(entries[k] / frames, 1) for k in phases},
    }


def counters_section(fam, flips, lo, hi):
    """Per-frame draws, passes, GPU time, upload/writeback deltas and pass-end causes over the window."""
    perf = in_window(fam['perf'], lo, hi)
    if not perf:
        return None
    first_t, last_t = perf[0][0], perf[-1][0]
    start = first_t - perf[0][1].get('seconds', 0.0)
    frames = bisect.bisect_right(flips, last_t) - bisect.bisect_right(flips, start)
    seconds = sum(d.get('seconds', 0.0) for _, d in perf)

    def total(rows, key):
        return sum(d.get(key, 0.0) for _, d in rows)

    def near(rows):
        return [(t, d) for t, d in rows if first_t - 0.05 <= t <= last_t + 0.05]

    out = {'perf_windows': len(perf), 'seconds': r(seconds, 1), 'frames': frames}
    if frames <= 0:
        return out
    for key, name in (('draws', 'draws_per_frame'), ('passes', 'passes_per_frame'),
                      ('gpu_busy_ms', 'gpu_busy_ms_per_frame'), ('kernel_ms', 'gpu_kernel_ms_per_frame'),
                      ('uploads', 'uploads_per_frame'), ('writebacks', 'writebacks_per_frame'),
                      ('evictions', 'image_evictions_per_frame'), ('pipeline_ms', 'pipeline_ms_per_frame')):
        out[name] = r(total(perf, key) / frames, 2)
    churn = near(fam['churn'])
    if churn:
        for key in CHURN_KEYS:
            if any(key in d for _, d in churn):
                out['churn_' + key + '_per_frame'] = r(total(churn, key) / frames, 2)
    tbdr = near(fam['tbdr'])
    if tbdr:
        passes = total(tbdr, 'passes')
        out['tbdr_load_mb_per_frame'] = r(total(tbdr, 'load_mb') / frames, 1)
        out['tbdr_store_mb_per_frame'] = r(total(tbdr, 'store_mb') / frames, 1)
        out['pass_end_per_frame'] = {k[4:]: r(total(tbdr, k) / frames, 2) for k in TBDR_END_KEYS
                                     if total(tbdr, k) > 0}
        out['pass_end_share'] = {k[4:]: r(total(tbdr, k) / passes, 3) for k in TBDR_END_KEYS
                                 if passes and total(tbdr, k) > 0}
    return out


def load_pass_module():
    spec = importlib.util.spec_from_file_location('analyze_pass_profile', os.path.join(HERE, 'analyze_pass_profile.py'))
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def passprof_section(tlog, lo, hi):
    """Per-frame encoder GPU time and render-pass end causes from SHARPEMU_METAL_V2_PROFILE_PASSES lines."""
    enc_lines = [(t, text) for t, text in tlog if lo < t <= hi and text.startswith('[METAL-V2][PASS-PROF] f=')]
    if not enc_lines:
        return None
    mod = load_pass_module()
    frames = defaultdict(lambda: {'us': 0.0, 'gap': 0.0, 'render': 0, 'compute': 0, 'draws': 0})
    ends = defaultdict(lambda: [0, 0.0])
    gpu_ms = defaultdict(float)
    for _, text in enc_lines:
        m = mod.ENC_RE.search(text)
        if not m:
            continue
        f = frames[int(m.group(1))]
        f['us'] += float(m.group(5))
        f['gap'] += float(m.group(6))
        f['draws'] += int(m.group(7))
        kind = m.group(4)
        if kind == 'render':
            f['render'] += 1
            ends[m.group(11)][0] += 1
            ends[m.group(11)][1] += float(m.group(5))
        elif kind == 'compute':
            f['compute'] += 1
    for t, text in tlog:
        if lo < t <= hi and text.startswith('[METAL-V2][PASS-PROF] sub f='):
            m = re.search(r'sub f=(\d+) .*? gpu_ms=([\d.]+)', text)
            if m:
                gpu_ms[int(m.group(1))] += float(m.group(2))
    if not frames:
        return None
    n = len(frames)
    enc_ms = [(f['us'] + f['gap']) / 1000.0 for f in frames.values()]
    render_total = sum(v[1] for v in ends.values()) or 1.0
    out = {
        'frames': n,
        'render_encoders_per_frame': r(sum(f['render'] for f in frames.values()) / n, 1),
        'compute_encoders_per_frame': r(sum(f['compute'] for f in frames.values()) / n, 1),
        'encoder_gpu_ms_per_frame_mean': r(sum(enc_ms) / n, 1),
        'encoder_gpu_ms_per_frame_p50': r(percentile(enc_ms, 50), 1),
        'encoder_gpu_ms_per_frame_p90': r(percentile(enc_ms, 90), 1),
        'pass_end_causes': {k: {'per_frame': r(v[0] / n, 2), 'gpu_share': r(v[1] / render_total, 3)}
                            for k, v in sorted(ends.items(), key=lambda kv_: -kv_[1][0])[:10]},
    }
    if gpu_ms:
        vals = list(gpu_ms.values())
        out['submission_gpu_ms_per_frame_mean'] = r(sum(vals) / len(vals), 1)
        out['submission_gpu_ms_per_frame_p50'] = r(percentile(vals, 50), 1)
    return out


def footprint_section(rows, lo, hi, result):
    out = {}
    if rows:
        peak = max(rows, key=lambda x: x[1])
        out['peak_footprint_mb'] = peak[1]
        out['peak_footprint_at_s'] = peak[0]
        inw = [x for x in rows if lo <= x[0] <= hi]
        if inw:
            out['window_peak_footprint_mb'] = max(x[1] for x in inw)
            out['window_peak_rss_mb'] = max(x[2] for x in inw)
        out['peak_rss_mb'] = max(x[2] for x in rows)
    for key in ('peak_footprint_mb', 'peak_rss_mb'):
        if key in result and key not in out:
            out[key] = int(result[key])
    return out or None


# ---------------------------------------------------------------- spikes

def attribute_spikes(intervals, median_ms, tlog, fam, slack, slow_eop_min_ms=100.0):
    """One record per interval longer than 2x the median: {t, ms, causes: {name: detail}}."""
    spikes = [(a, b) for a, b in intervals if (b - a) * 1000.0 > SPIKE_FACTOR * median_ms]
    times = [t for t, _ in tlog]
    sources = {'SLOW-EOP': 0, 'OverlapEvicted': 0, 'compile': len(fam['perf']), 'drop_budget': len(fam['churn']),
               'adopt_blocked': len(fam['present']), 'GC': len(fam['gc'])}
    out = []
    for a, b in spikes:
        lo, hi = a - slack, b + slack
        causes = {}
        if is_loading(a, b, fam['perf']):
            out.append({'t': a, 'ms': (b - a) * 1000.0, 'causes': {LOADING: {'n': 1}}, 'loading': True})
            continue
        for i in range(bisect.bisect_left(times, lo), bisect.bisect_right(times, hi)):
            text = tlog[i][1]
            if text.startswith('[METAL-V2][SLOW-EOP]'):
                sources['SLOW-EOP'] += 1
                m = re.search(r'latency_ms=([\d.]+)', text)
                latency = float(m.group(1)) if m else 0.0
                if latency >= slow_eop_min_ms:
                    c = causes.setdefault('SLOW-EOP', {'n': 0, 'max_ms': 0.0})
                    c['n'] += 1
                    c['max_ms'] = max(c['max_ms'], latency)
            elif 'cause=OverlapEvicted' in text:
                causes.setdefault('OverlapEvicted', {'n': 0})['n'] += 1
        # Window counters: the window a tick closes is (tick - seconds, tick].
        for family in ('perf', 'churn', 'present', 'gc'):
            rows = fam[family]
            for idx, (t, d) in enumerate(rows):
                start = t - d['seconds'] if d.get('seconds') else (rows[idx - 1][0] if idx else 0.0)
                if t < lo or start > hi:
                    continue
                if family == 'perf':
                    if (d.get('compile_ms', 0) >= 20 or d.get('library_wait_ms', 0) >= 10
                            or d.get('pipeline_ms', 0) >= 50 or d.get('draws_skipped_compiling', 0) > 0):
                        c = causes.setdefault('compile', {'n': 0, 'ms': 0.0})
                        c['n'] += 1
                        c['ms'] += d.get('compile_ms', 0) + d.get('library_wait_ms', 0) + d.get('pipeline_ms', 0)
                elif family == 'churn' and d.get('drop_budget', 0) > 0:
                    c = causes.setdefault('drop_budget', {'n': 0})
                    c['n'] += int(d['drop_budget'])
                elif family == 'present' and d.get('published', 1) == 0 and d.get('adopt_blocked', 0) > 0:
                    c = causes.setdefault('adopt_blocked', {'n': 0})
                    c['n'] += int(d['adopt_blocked'])
                elif family == 'gc' and d.get('pause_ms', 0) >= 50:
                    c = causes.setdefault('GC', {'n': 0, 'ms': 0.0})
                    c['n'] += 1
                    c['ms'] += d['pause_ms']
        out.append({'t': a, 'ms': (b - a) * 1000.0, 'causes': causes})
    return out


def spike_section(intervals, median_ms, tlog, fam, slack):
    if not intervals or median_ms is None:
        return None
    spikes = attribute_spikes(intervals, median_ms, tlog, fam, slack) if tlog else \
        [{'t': a, 'ms': (b - a) * 1000.0, 'causes': {}} for a, b in intervals
         if (b - a) * 1000.0 > SPIKE_FACTOR * median_ms]
    loading = [s for s in spikes if s.get('loading')]
    spikes = [s for s in spikes if not s.get('loading')]
    counts = Counter()
    if loading:
        counts[LOADING] = len(loading)
    for s in spikes:
        for name in s['causes']:
            counts[name] += 1
        if not s['causes']:
            counts['(none)'] += 1
    top = sorted(spikes, key=lambda s: -s['ms'])[:10]
    return {
        'threshold_ms': r(SPIKE_FACTOR * median_ms, 1), 'count': len(spikes) + len(loading),
        'count_excluding_loading': len(spikes),
        'loading_gaps': [{'at_s': r(s['t'], 2), 'ms': r(s['ms'], 0)} for s in sorted(loading, key=lambda s: s['t'])],
        'loading_seconds': r(sum(s['ms'] for s in loading) / 1000.0, 1),
        'ms_total_over_median': r(sum(s['ms'] - median_ms for s in spikes), 0),
        'cause_counts': dict(counts.most_common()),
        'top': [{'at_s': r(s['t'], 2), 'ms': r(s['ms'], 0), 'causes': s['causes']} for s in top],
        'sources_present': {k: bool(fam[f]) for k, f in
                            (('compile', 'perf'), ('drop_budget', 'churn'), ('adopt_blocked', 'present'),
                             ('GC', 'gc'))},
    }


# ---------------------------------------------------------------- driver

def analyse(run, lo, hi, slack=0.25):
    """The full metrics dict for one run directory."""
    env = read_env(os.path.join(run, 'run.env'))
    result = read_env(os.path.join(run, 'result.txt'))
    launch = float(env['launch_epoch']) if env.get('launch_epoch') else None
    flips = read_flips(os.path.join(run, 'frame-times.txt'), launch)
    tlog = read_tlog(os.path.join(run, 'game.tlog'))
    if hi is None:
        ends = [flips[-1]] if flips else []
        ends += [tlog[-1][0]] if tlog else []
        hi = max(ends) if ends else 0.0
    fam = parse_ticks(tlog) if tlog else defaultdict(list)
    m = {'run': os.path.abspath(run), 'window_s': [lo, round(hi, 1)],
         'tag': os.path.basename(os.path.normpath(run)), 'commit': env.get('commit'),
         'app': env.get('app'), 'result': open(os.path.join(run, 'result.txt')).readline().strip()
         if os.path.exists(os.path.join(run, 'result.txt')) else None,
         'env': {k: v for k, v in env.items() if k.startswith('SHARPEMU_')}}
    frames, intervals = frame_section(flips, lo, hi)
    m['frames'] = frames
    if frames.get('p50_ms') is not None:
        limit = SPIKE_FACTOR * frames['p50_ms'] / 1000.0
        loading = {iv for iv in intervals if iv[1] - iv[0] > limit and is_loading(iv[0], iv[1], fam['perf'])}
        m['frames_no_loading'] = {'loading_gaps': len(loading),
                                  'loading_seconds': r(sum(b - a for a, b in loading), 1)}
        m['frames_no_loading'].update(interval_stats([iv for iv in intervals if iv not in loading]))
    if frames.get('p50_ms') is not None:
        m['windows_30s'] = [{'from': a, 'to': b, 'flips': n, 'fps': r(f, 3), 'p50_ms': r(p50, 0), 'max_ms': r(mx, 0)}
                            for a, b, n, f, p50, mx in windowed_fps(flips, lo, hi, 30.0)]
        shares, total = regime_histogram(flips, lo, hi)
        m['regimes_10s_fps'] = {k: r(v, 3) for k, v in shares.items()}
        m['regime_windows'] = total
    m['render'] = render_section(fam['render'], lo, hi) if tlog else None
    m['counters'] = counters_section(fam, flips, lo, hi) if tlog else None
    m['pass_profile'] = passprof_section(tlog, lo, hi) if tlog else None
    if frames.get('p50_ms') is not None:
        m['spikes'] = spike_section(intervals, frames['p50_ms'], tlog, fam, slack)
    gc = in_window(fam['gc'], lo, hi) if tlog else []
    if gc:
        m['gc_pause_ms_total'] = r(sum(d['pause_ms'] for _, d in gc), 0)
    m['footprint'] = footprint_section(read_footprint(os.path.join(run, 'footprint.log')), lo, hi, result)
    return m


# ---------------------------------------------------------------- output

def fmt(v, digits=None):
    if v is None:
        return 'n/a'
    if isinstance(v, float) and digits is not None:
        return f'{v:.{digits}f}'
    return str(v)


def table(headers, rows):
    out = ['| ' + ' | '.join(headers) + ' |', '|' + '|'.join('---' for _ in headers) + '|']
    out += ['| ' + ' | '.join(str(c) for c in row) + ' |' for row in rows]
    return out


def cause_text(causes):
    parts = []
    for name, d in causes.items():
        extra = ''
        if 'max_ms' in d:
            extra = f' x{d["n"]} max {d["max_ms"]:.0f}ms'
        elif 'ms' in d:
            extra = f' {d["ms"]:.0f}ms'
        elif d.get('n', 1) > 1:
            extra = f' x{d["n"]}'
        parts.append(name + extra)
    return ', '.join(parts) or '(none found)'


def render_markdown(m):
    L = [f'## Performance report: {m["tag"]}', '',
         f'{"Segment " + m["segment"] + ", w" if m.get("segment") else "W"}indow {m["window_s"][0]:g}-{m["window_s"][1]:g} s since launch. Commit {m.get("commit")}. {m.get("result") or ""}']
    if m['env']:
        L.append('Env: ' + ' '.join(f'{k}={v}' for k, v in m['env'].items()))
    f = m['frames']
    L += ['', '### Frame time', '']
    if f.get('p50_ms') is None:
        L.append('No frame intervals in the window (frame-times.txt missing, or fewer than two flips).')
    else:
        nl = m.get('frames_no_loading') or {}
        rows = [['all', f['flips'], fmt(f['fps']), f['p50_ms'], f['p90_ms'], f['p99_ms'], f['max_ms'], f['over_2x_median']]]
        if nl.get('p50_ms') is not None:
            rows.append([f'without {nl["loading_gaps"]} loading gaps ({nl["loading_seconds"]} s)', nl['intervals'] + 1,
                         fmt(nl['fps']), nl['p50_ms'], nl['p90_ms'], nl['p99_ms'], nl['max_ms'], nl['over_2x_median']])
        L += table(['intervals', 'flips', 'fps', 'p50 ms', 'p90 ms', 'p99 ms', 'max ms', 'frames > 2x median'], rows)
        L += ['', 'fps per 30 s window:', '']
        L += table(['window s', 'flips', 'fps', 'p50 ms', 'max ms'],
                   [[f'{w["from"]:g}-{w["to"]:g}', w['flips'], fmt(w['fps']), fmt(w['p50_ms']), fmt(w['max_ms'])]
                    for w in m['windows_30s']])
        L += ['', f'Regimes: share of the {m["regime_windows"]} rolling 10 s windows (5 s step) by fps:', '']
        L += table(list(m['regimes_10s_fps']), [[fmt(v) if v is None else f'{v * 100:.0f}%'
                                                for v in m['regimes_10s_fps'].values()]])
    c, rp, pp = m['counters'], m['render'], m['pass_profile']
    L += ['', '### Per-frame work', '']
    rows = []
    if rp:
        rows += [['draws / frame (RENDER)', fmt(rp['draws_per_frame'])],
                 ['worker us / draw', fmt(rp['worker_us_per_draw'])],
                 ['worker ms / frame', fmt(rp['worker_ms_per_frame'])]]
    if c and 'draws_per_frame' in c:
        rows += [['draws / frame (PERF)', fmt(c['draws_per_frame'])], ['passes / frame', fmt(c['passes_per_frame'])],
                 ['GPU busy ms / frame', fmt(c['gpu_busy_ms_per_frame'])],
                 ['GPU kernel ms / frame', fmt(c['gpu_kernel_ms_per_frame'])],
                 ['uploads / frame', fmt(c['uploads_per_frame'])], ['writebacks / frame', fmt(c['writebacks_per_frame'])]]
        for key in ('up_alias', 'wb_overlap', 'drop_overlap', 'drop_budget', 'alias_published'):
            if 'churn_' + key + '_per_frame' in c:
                rows.append([key.replace('_', ' ') + ' / frame', fmt(c['churn_' + key + '_per_frame'])])
        if 'tbdr_load_mb_per_frame' in c:
            rows += [['TBDR load / store MB / frame', f'{c["tbdr_load_mb_per_frame"]} / {c["tbdr_store_mb_per_frame"]}']]
    if pp:
        rows += [['render encoders / frame (PASS-PROF)', fmt(pp['render_encoders_per_frame'])],
                 ['encoder GPU ms / frame mean / p50 / p90 (PASS-PROF)',
                  f'{pp["encoder_gpu_ms_per_frame_mean"]} / {pp["encoder_gpu_ms_per_frame_p50"]} / {pp["encoder_gpu_ms_per_frame_p90"]}']]
        if 'submission_gpu_ms_per_frame_mean' in pp:
            rows.append(['submission gpu_ms / frame mean / p50',
                         f'{pp["submission_gpu_ms_per_frame_mean"]} / {pp["submission_gpu_ms_per_frame_p50"]}'])
    L += table(['metric', 'value'], rows) if rows else ['No render profile or [METAL-V2][PERF] lines in the window.']
    if c and c.get('pass_end_per_frame'):
        L += ['', 'Pass-end causes (TBDR, per frame / share of passes):', '']
        L += table(['cause', 'per frame', 'share'],
                   [[k, v, f'{c["pass_end_share"][k] * 100:.0f}%'] for k, v in
                    sorted(c['pass_end_per_frame'].items(), key=lambda kv_: -kv_[1])])
    if pp and pp.get('pass_end_causes'):
        L += ['', 'Render-encoder end causes (PASS-PROF):', '']
        L += table(['end', 'per frame', 'GPU time share'],
                   [[k, v['per_frame'], f'{v["gpu_share"] * 100:.0f}%'] for k, v in pp['pass_end_causes'].items()])
    if rp:
        L += ['', f'Worker phases, exclusive ms per frame ({rp["frames"]} frames, {rp["windows"]} windows):', '']
        L += table(['phase', 'ms / frame', 'entries / frame'],
                   [[k, v, rp['phase_entries_per_frame'][k]] for k, v in list(rp['phase_ms_per_frame'].items())[:20]])
    s = m.get('spikes')
    if s:
        L += ['', f'### Spikes (> {s["threshold_ms"]} ms = 2x median): {s["count"]}, '
                  f'{s["count_excluding_loading"]} outside loading, {s["ms_total_over_median"]:.0f} ms above the median in total', '']
        L += table(['cause', 'spikes'], [[k, v] for k, v in s['cause_counts'].items()]) or []
        if s['loading_gaps']:
            L += ['', f'Loading gaps (under 20 submissions and 50 passes per 2 s in the gap), {s["loading_seconds"]} s in total: ' +
                  ', '.join(f'{g["at_s"]:g} s +{g["ms"] / 1000:.1f} s' for g in s['loading_gaps'][:12])]
        L += ['', 'Top 10 outside loading:', '']
        L += table(['at s', 'ms', 'causes'], [[t['at_s'], t['ms'], cause_text(t['causes'])] for t in s['top']])
        missing = [k for k, v in s['sources_present'].items() if not v]
        if missing:
            L += ['', 'No log lines for: ' + ', '.join(missing) + '.']
        L += ['', 'compile, drop_budget, adopt_blocked and GC are 2-5 s window counters (coarse); adopt_blocked is a symptom '
              '(the host had no new guest frame to present), not a cause of its own.']
    if m.get('gc_pause_ms_total') is not None:
        L += ['', f'GC pause in window: {m["gc_pause_ms_total"]:.0f} ms']
    fp = m.get('footprint')
    L += ['', '### Footprint', '']
    if fp:
        L += table(['peak footprint MB', 'at s', 'window peak MB', 'peak RSS MB'],
                   [[fp.get('peak_footprint_mb', 'n/a'), fp.get('peak_footprint_at_s', 'n/a'),
                     fp.get('window_peak_footprint_mb', 'n/a'), fp.get('peak_rss_mb', 'n/a')]])
    else:
        L.append('No footprint.log.')
    return '\n'.join(L) + '\n'


HEADLINE = (
    ('frames.fps', 'fps'), ('frames.p50_ms', 'frame p50 ms'), ('frames.p90_ms', 'frame p90 ms'),
    ('frames.p99_ms', 'frame p99 ms'), ('frames.max_ms', 'frame max ms'),
    ('frames.over_2x_median', 'frames > 2x median'), ('spikes.count', 'spikes'),
    ('frames_no_loading.fps', 'fps without loading gaps'), ('frames_no_loading.p50_ms', 'p50 ms without loading'),
    ('frames_no_loading.p90_ms', 'p90 ms without loading'), ('frames_no_loading.p99_ms', 'p99 ms without loading'),
    ('frames_no_loading.max_ms', 'max ms without loading'),
    ('frames_no_loading.over_2x_median', 'frames > 2x median without loading'),
    ('spikes.count_excluding_loading', 'spikes outside loading'),
    ('render.draws_per_frame', 'draws / frame (RENDER)'), ('render.worker_us_per_draw', 'worker us / draw'),
    ('render.worker_ms_per_frame', 'worker ms / frame'), ('counters.draws_per_frame', 'draws / frame (PERF)'),
    ('counters.passes_per_frame', 'passes / frame'), ('counters.gpu_busy_ms_per_frame', 'GPU busy ms / frame'),
    ('counters.gpu_kernel_ms_per_frame', 'GPU kernel ms / frame'),
    ('counters.uploads_per_frame', 'uploads / frame'), ('counters.writebacks_per_frame', 'writebacks / frame'),
    ('counters.churn_up_alias_per_frame', 'alias uploads / frame'),
    ('counters.churn_wb_overlap_per_frame', 'overlap writebacks / frame'),
    ('counters.churn_drop_budget_per_frame', 'drop_budget / frame'),
    ('pass_profile.render_encoders_per_frame', 'render encoders / frame (PASS-PROF)'),
    ('pass_profile.encoder_gpu_ms_per_frame_mean', 'encoder GPU ms / frame (PASS-PROF)'),
    ('footprint.peak_footprint_mb', 'peak footprint MB'),
)


def dig(d, path):
    for part in path.split('.'):
        if not isinstance(d, dict) or part not in d:
            return None
        d = d[part]
    return d if isinstance(d, (int, float)) else None


def delta_pct(before, after):
    if before is None or after is None:
        return 'n/a'
    if before == 0:
        return '0%' if after == 0 else 'new'
    return f'{(after - before) / abs(before) * 100:+.1f}%'


def compare_markdown(before, after):
    rows = [[label, fmt(dig(before, p)), fmt(dig(after, p)), delta_pct(dig(before, p), dig(after, p))]
            for p, label in HEADLINE if dig(before, p) is not None or dig(after, p) is not None]
    L = [f'## Before {before["tag"]} / After {after["tag"]}', '']
    L += table(['metric', 'Before', 'After', 'Delta'], rows)
    names = {}
    for side in (before, after):
        for k, v in ((side.get('render') or {}).get('phase_ms_per_frame') or {}).items():
            names[k] = max(names.get(k, 0), v)
    if names:
        top = sorted(names, key=lambda k: -names[k])[:15]
        pb = (before.get('render') or {}).get('phase_ms_per_frame') or {}
        pa = (after.get('render') or {}).get('phase_ms_per_frame') or {}
        L += ['', 'Worker phase ms per frame:', '']
        L += table(['phase', 'Before', 'After', 'Delta'],
                   [[k, fmt(pb.get(k)), fmt(pa.get(k)), delta_pct(pb.get(k), pa.get(k))] for k in top])
    return '\n'.join(L) + '\n'


# ---------------------------------------------------------------- self test

def selftest():
    failures = []

    def check(name, got, want):
        if got != want:
            failures.append(f'{name}: got {got!r}, want {want!r}')

    # percentiles
    vals = [10, 20, 30, 40, 50]
    check('p50', percentile(vals, 50), 30.0)
    check('p90', percentile(vals, 90), 46.0)
    check('p0/p100', (percentile(vals, 0), percentile(vals, 100)), (10.0, 50.0))
    check('empty', percentile([], 50), None)
    check('single', percentile([7], 99), 7.0)

    # cumulative PERF counters become deltas, per-window ones stay as they are
    tlog = [(10.0, '[METAL-V2][PERF] seconds=2.00 submissions=10 draws=100 passes=5 uploads=50 writebacks=20 gpu_busy_ms=30.0'),
            (12.0, '[METAL-V2][PERF] seconds=2.00 submissions=25 draws=40 passes=2 uploads=80 writebacks=20 gpu_busy_ms=10.0'),
            (14.0, '[METAL-V2][PERF] seconds=2.00 submissions=30 draws=60 passes=3 uploads=81 writebacks=35 gpu_busy_ms=20.0')]
    fam = parse_ticks(tlog)
    check('uploads delta', [d['uploads'] for _, d in fam['perf']], [50.0, 30.0, 1.0])
    check('writebacks delta', [d['writebacks'] for _, d in fam['perf']], [20.0, 0.0, 15.0])
    check('draws untouched', [d['draws'] for _, d in fam['perf']], [100.0, 40.0, 60.0])
    flips = [10.0 + 0.5 * i for i in range(9)]  # 2 fps over 10..14
    c = counters_section(fam, flips, 11.0, 14.0)  # selects the windows ending at 12 and 14 (span 10..14, 8 flips after 10)
    check('frames in span', c['frames'], 8)
    check('uploads per frame', c['uploads_per_frame'], r(31 / 8, 2))
    check('writebacks per frame', c['writebacks_per_frame'], r(15 / 8, 2))
    check('draws per frame', c['draws_per_frame'], r(100 / 8, 2))
    check('gpu per frame', c['gpu_busy_ms_per_frame'], r(30 / 8, 2))

    # frame section: 3 s at 100 ms with one 1 s hitch
    pts = [0.1 * i for i in range(31)]
    pts = pts[:15] + [t + 0.9 for t in pts[15:]]
    f, ivs = frame_section(pts, 0, 10)
    check('median', f['p50_ms'], 100.0)
    check('over 2x', f['over_2x_median'], 1)
    check('max', f['max_ms'], 1000.0)

    # regimes and per-window fps
    fl = [i * 0.5 for i in range(200)]  # 2 fps
    shares, total = regime_histogram(fl, 0, 60)
    check('regime 2fps', shares['2-3'], 1.0)
    check('windows', total, 11)
    check('win fps', windowed_fps(fl, 0, 60, 30.0)[0][3], 2.0)

    # spike attribution
    sp_tlog = [(5.0, '[METAL-V2][SLOW-EOP] serial=1 latency_ms=340.0 gpu_ms=340.0 noticed_ms=0.0'),
               (5.2, '[METAL-V2][WARN] A cached image is dropped and recreated: cause=OverlapEvicted cached=0x1'),
               (5.3, '[METAL-V2][SLOW-EOP] serial=2 latency_ms=20.0 gpu_ms=20.0'),
               (6.0, '[METAL-V2][PERF] seconds=2.00 submissions=30 passes=60 draws=500 pipeline_ms=120.0 library_wait_ms=0.0'),
               (6.0, '[METAL-V2][CHURN] up_new=1 drop_budget=7'),
               (6.0, '[METAL-PRESENT] guest_frames=0 seconds=2.000 published=0 adopt_blocked=200'),
               (20.0, '[METAL-V2][SLOW-EOP] serial=3 latency_ms=500.0')]
    sfam = parse_ticks(sp_tlog)
    spikes = attribute_spikes([(4.0, 6.0), (10.0, 10.1)], 100.0, sp_tlog, sfam, slack=0.25)
    check('only the long interval is a spike', len(spikes), 1)
    causes = spikes[0]['causes']
    check('slow-eop counted once, min 100 ms', causes['SLOW-EOP']['n'], 1)
    check('slow-eop max', causes['SLOW-EOP']['max_ms'], 340.0)
    check('overlap', causes['OverlapEvicted']['n'], 1)
    check('compile', 'compile' in causes, True)
    check('drop_budget', causes['drop_budget']['n'], 7)
    check('adopt_blocked', causes['adopt_blocked']['n'], 200)
    check('far SLOW-EOP not attributed', causes['SLOW-EOP']['n'], 1)

    # loading gaps: no submissions across the gap
    lt = [(10.0, '[METAL-V2][PERF] seconds=2.00 submissions=100 draws=50 passes=5'),
          (12.0, '[METAL-V2][PERF] seconds=2.00 submissions=100 draws=0 passes=0'),
          (14.0, '[METAL-V2][PERF] seconds=2.00 submissions=100 draws=0 passes=0'),
          (16.0, '[METAL-V2][PERF] seconds=2.00 submissions=130 draws=40 passes=4')]
    lfam = parse_ticks(lt)
    check('loading gap detected', is_loading(10.5, 15.0, lfam['perf']), True)
    check('busy gap is not loading', is_loading(9.0, 10.5, lfam['perf']), False)
    check('gap without ticks is not loading', is_loading(30.0, 35.0, lfam['perf']), False)
    check('gap ending when work resumes', is_loading(11.0, 17.0, lfam['perf']), False)
    lspikes = attribute_spikes([(10.5, 15.0)], 100.0, lt, lfam, slack=0.25)
    check('loading cause', list(lspikes[0]['causes']), [LOADING])
    lflips = [10.0 + 0.1 * i for i in range(5)] + [15.0 + 0.1 * i for i in range(5)]
    ls = spike_section([(a, b) for a, b in zip(lflips, lflips[1:])], 100.0, lt, lfam, 0.25)
    check('spike counts', (ls['count'], ls['count_excluding_loading'], ls['loading_gaps'][0]['ms']), (1, 0, 4600.0))
    st = interval_stats([(0.0, 0.1), (0.1, 0.2), (0.2, 0.3), (0.3, 5.0)])
    check('stats with gap', (st['p50_ms'], st['max_ms'], st['over_2x_median']), (100.0, 4700.0, 1))

    # end to end on a synthetic run directory, then compare against itself
    with tempfile.TemporaryDirectory() as d:
        launch = 1_000_000.0
        with open(os.path.join(d, 'run.env'), 'w') as fh:
            fh.write(f'commit=abc\nlaunch_epoch={launch}\nSHARPEMU_PROFILE_RENDER=1\n')
        with open(os.path.join(d, 'frame-times.txt'), 'w') as fh:
            fh.write(f'# sharpemu-frame-times v1 clock=monotonic t0_unix_us={int(launch * 1e6)}\n')
            for i in range(1, 101):
                fh.write(f'{i} {int(i * 0.5 * 1e6)}\n')
        with open(os.path.join(d, 'game.tlog'), 'w') as fh:
            fh.write('[+  20.000] [PERF][RENDER_MS] window_s=5.0 frames=10 CommandStream=1000.00ms/n50 '
                     'DrawExecutor=100.00ms/n5000 IdleNoQueuedWork=3000.00ms/n1\n')
            fh.write('[+  20.000] [METAL-V2][PERF] seconds=2.00 draws=1000 passes=40 uploads=10 writebacks=4 gpu_busy_ms=400.0\n')
        with open(os.path.join(d, 'footprint.log'), 'w') as fh:
            fh.write('10 1000 500\n20 4000 900\n30 3000 900\n')
        with open(os.path.join(d, 'markers.tsv'), 'w') as fh:
            fh.write(f'{launch + 15:.3f}\tseg-start\n{launch + 40:.3f}\tseg-end\n')
        check('segment window', segment_window(d, 'seg'), (15.0, 40.0))
        m = analyse(d, 15, 40)
        check('selftest fps', m['frames']['fps'], 2.0)
        check('render worker ms', m['render']['worker_ms_per_frame'], 110.0)
        check('render us per draw', m['render']['worker_us_per_draw'], 220.0)
        check('footprint peak', m['footprint']['peak_footprint_mb'], 4000)
        check('draws per frame (PERF)', m['counters']['draws_per_frame'], r(1000 / 4, 2))
        text = render_markdown(m)
        check('markdown has frame table', '### Frame time' in text, True)
        check('compare self is 0%', '+0.0%' in compare_markdown(m, m), True)
        json.dumps(m)
    if failures:
        print('SELFTEST FAILED\n  ' + '\n  '.join(failures))
        return 1
    print('selftest ok')
    return 0


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument('run', nargs='?', help='run-game.sh run directory (artifacts/runs/<ts>-sh-<tag>)')
    ap.add_argument('--from', dest='lo', type=float, default=150.0, help='window start, s since launch (default 150)')
    ap.add_argument('--to', dest='hi', type=float, default=None, help='window end, s since launch (default end of run)')
    ap.add_argument('--segment', help='window = markers NAME-start .. NAME-end of markers.tsv (plan t:mark: steps); '
                    'replaces --from/--to, and --baseline uses the same marker names in its own run')
    ap.add_argument('--json', help='write the metrics as JSON to this path')
    ap.add_argument('--md', help='also write the markdown to this path')
    ap.add_argument('--baseline', help='a second run directory; prints Before (it) / After (run) / Delta')
    ap.add_argument('--slack', type=float, default=0.25, help='seconds around a spike searched for causes (default 0.25)')
    ap.add_argument('--selftest', action='store_true')
    args = ap.parse_args()
    if args.selftest:
        return selftest()
    if not args.run or not os.path.isdir(args.run):
        ap.error('a run directory is required')
    lo, hi = segment_window(args.run, args.segment) if args.segment else (args.lo, args.hi)
    m = analyse(args.run, lo, hi, args.slack)
    if args.segment:
        m['segment'] = args.segment
    text = render_markdown(m)
    if args.baseline:
        blo, bhi = segment_window(args.baseline, args.segment) if args.segment else (lo, hi)
        base = analyse(args.baseline, blo, bhi, args.slack)
        text += '\n' + compare_markdown(base, m)
        m['baseline'] = base
    print(text)
    if args.json:
        with open(args.json, 'w') as fh:
            json.dump(m, fh, indent=2)
    if args.md:
        with open(args.md, 'w') as fh:
            fh.write(text)
    return 0


if __name__ == '__main__':
    sys.exit(main())
