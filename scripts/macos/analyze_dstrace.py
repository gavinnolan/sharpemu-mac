#!/usr/bin/env python3
# Copyright (C) 2026 SharpEmu Emulator Project
# SPDX-License-Identifier: GPL-2.0-or-later
"""Analyse a DSTRACE run (spec: INSTRUMENTATION.md section 10).

Usage
    python3 -I scripts/macos/analyze_dstrace.py <run dir | dstrace.log> [--out DIR] [--ab-settle S]
    python3 -I scripts/macos/analyze_dstrace.py --selftest

Inputs
    <run dir>/dstrace.log   (or the path in the "[DSTRACE] output=" line of game.log)
    <run dir>/game.log      [METAL-PRESENT], [METAL-V2][PERF], [PERF][GUEST_SCHED] windows (optional)

Outputs (<run dir>/dstrace-analysis/)
    frames.csv frames_summary.txt workers.csv wake_latency.txt fiber.txt memlock.txt
    hostcpu.txt gpu_roundtrip.csv metal.txt ab.txt timeline_median.txt timeline_p90.txt
    sanity.txt gamelog.txt skipped.txt

Behaviour
    * Python 3 standard library only, single streaming pass over dstrace.log.
    * "# SCHEMA" lines in the log are authoritative for field order; the built-in lists
      (spec sections 4-6) are used only for types without a SCHEMA line.
    * An absent event type never crashes the script. Every section that cannot run is
      listed in skipped.txt (and printed) with the reason. Unknown event types are
      skipped and counted. Without dstrace.log only the game.log summary is produced.
    * --selftest generates a synthetic dstrace.log + game.log into a temp dir that covers
      every event type, runs the whole pipeline and asserts the outputs.
    * Timestamps become seconds since the "# DSTRACE" header start_ts. Field values that are
      Stopwatch timestamps (enter_ts, start_ts, suspend_ts) are converted the same way.
      Duration fields keep their _ns unit.
Assumptions where the spec is silent (all documented in sanity.txt where they matter):
    HLE/FSW cpu_ns are per-call deltas; CPUPT cpu_ns is an absolute thread CPU clock (checked
    for monotonicity, ignored otherwise); WCPU packs two u32 per field, low half first.
"""
import sys
import os
import re
import math
import bisect
import csv
import random
import argparse
import tempfile
import traceback
import time
from collections import defaultdict, Counter
from operator import itemgetter

# --------------------------------------------------------------------------------------
# Constants
# --------------------------------------------------------------------------------------
SITE_SUSPEND, SITE_RESUME, SITE_MIGRATE = 0x80086d6cc, 0x80086f6d4, 0x80086f59b
SITE_NAMES = {SITE_SUSPEND: 'suspend', SITE_RESUME: 'resume', SITE_MIGRATE: 'migrate'}
GL_WRT, RT_WREQ, RT_WDONE, SJ_DEP = 0x800a64367, 0x800a654fb, 0x800a6548b, 0x800a7de1d
WAIT_FOR_COUNTER = 0x800867443
MAIN_LOOP = 0x800bc80da
ALT_LOOP = {0x8009df1b2, 0x8009df1c5}
YIELD_CALLERS = {0x8008e55e7, 0x8008e5718, 0x8008e5755, 0x8014c9379}
RA4_NAMES = {GL_WRT: 'GL_WRT(GameLoop WaitForRenderThread)', RT_WREQ: 'RT_WREQ(ProcessRenderThread state>=3)',
             RT_WDONE: 'RT_WDONE(ProcessRenderThread state==1)', SJ_DEP: 'SJ_DEP(SubmitJob deps)'}
CHAIN_RA4 = {GL_WRT, RT_WREQ, RT_WDONE, SJ_DEP}
LABEL_ADDRS = {0x800a793c0: 'RenderJob', 0x800a793d0: 'RenderJob', 0x800a65360: 'ProcessRenderThread',
               0x800bc9990: 'GameLoop', 0x800a7dda0: 'SubmitJob'}
CHAIN_LABELS = {'GameLoop', 'ProcessRenderThread', 'RenderJob', 'SubmitJob'}
LABEL_CHAR = {'GameLoop': 'G', 'ProcessRenderThread': 'T', 'RenderJob': 'R', 'SubmitJob': 'J'}
GF_KINDS = ['Blocked', 'Ready', 'Claimed', 'Scheduled', 'RunStarted', 'RunStopped', 'CallbackResumed',
            'ExecutorReleased', 'ClaimDeferred']
MLOCK_SITES = {1: 'TryRead shared', 2: 'TryRead exclusive', 3: 'TryWriteCore shared', 4: 'TryWriteCore exclusive',
               5: 'TryProtect', 6: 'SharedBacking TryWriteBackingCore', 7: 'SharedBacking ClearCore',
               8: 'temp protect/restore', 9: 'single-view read hit/miss', 10: 'single-view write hit/miss'}
WORKER_RE = re.compile(r'JobWorkerThread CPU(\d+)')
NWORKERS = 13
SUBMIT_FLIP_NAMES = ('sceAgcDriverSubmitDcb', 'sceAgcDcbSetFlip', 'sceAgcSuspendPoint')
PTHREAD_COND_NAMES = ('pthread_cond', 'scePthreadCond')

# Verbatim copy of the sink's schema table (src/SharpEmu.HLE/DsTrace/DsSchema.cs, diag-ds-trace 5ac4f003c).
# Suffix :x = hex, :s = STR id, otherwise signed decimal. Used as the fallback when a log lacks a SCHEMA line
# and by --selftest to generate SCHEMA lines in the sink's exact format.
SINK_SCHEMA = {
    'GF': 'kind guest:x name:s reason:s wake:s nid:s arg0:x arg1:x deadline',
    'BRA': 'guest:x nid:s ra1:x ra2:x ra3:x',
    'WAKE': 'enter_ts gate_wait_ns total_ns wake:s scanned woken first_woken:x pumped',
    'PUMPA': 'calls cas_lost cap_hit claimed wake_expired_ns',
    'USLA': 'ra1:x count req_us_sum actual_ns_sum pump_ns_sum sleep0_count',
    'USLP': 'ra1:x ra2:x req_us actual_ns pump_ns branch',
    'HLE': 'enter_ts dur_ns cpu_ns import:s rdi:x rsi:x rdx:x ra1:x ra2:x outcome rax:x',
    'HLEA': 'import:s count wall_sum_ns wall_max_ns n_ge_100us n_ge_1ms n_ge_10ms cpu_sum_ns cpu_n wall_sum_of_cpu_sampled_ns',
    'CPUPT': 'cpu_ns',
    'FSW': 'enter_ts total_ns gate_wait_ns body_ns prep_ns probe_code probe_wlock_wait_ns probe_mprotect_ns cpu_ns from:x to:x ra2:x ra1:x',
    'FSJ': 'job:x counter:x value target cond_flags aff_grp q08:x q10:x q58:x q60:x ra3:x ra4:x ra5:x',
    'MXQ': 'mutex:x waiter:x owner:x qlen spin_ns outcome ra1:x',
    'MXH': 'mutex:x from:x to:x to_parked qlen_after',
    'MXR': 'mutex:x guest:x wait_ns',
    'EVF': 'handle:x bits:x mode path total_ns loop_iters ra1:x',
    'EVS': 'handle:x bits:x woken',
    'APR': 'ra1:x ra2:x n_cmds total_ns pread_ns waitaddr_ns waitaddr_iters bytes',
    'MLW': 'site wait_ns hold_ns import:s thread_class in_rip_probe',
    'MLA': 'site tid count slow_count wait_sum_ns wait_max_ns hold_sum_ns hold_max_ns',
    'FLIP': 'seq rdi:x rsi:x rdx:x rcx:x r8:x ra1:x ra2:x',
    'SUBMIT': 'rdi:x dwords gate_wait_ns total_ns submission_id ra2:x',
    'SUSP': 'wait_ns boundaries_pending',
    'FLIPDONE': 'request_id buffer',
    'PRESENT': 'request_id',
    'QENQ': 'queue submission_id kind dwords',
    'SLICE': 'start_ts dur_ns queue submission_id packets outcome wait_addr:x cpu_ns',
    'CPS': 'queue kind addr:x ref:x mask:x func cur:x',
    'CPW': 'source woken oldest_blocked_ns',
    'WIDLE': 'start_ts dur_ns reason outstanding sample_addr:x oldest_age_us cpu_ns',
    'MCOMMIT': 'serial cause pending_labels submission_id',
    'MGPU': 'serial kstart_ns kend_ns gstart_ns gend_ns late',
    'LENC': 'serial addr:x value:x deferred kind',
    'LLAND': 'serial',
    'RRW': 'waited_serial committed_serial completed_serial wait_ns reason addr:x',
    'ROOTS': 'j:x s:x g:x s48:x s4c:x sb90 rtpp:x ctx:x disp:x ok_bits:x',
    'SRT': 'old new rt:x wait_accum',
    'SFC': 'i ctr label0:x label1:x exp0:x exp1:x lp0:x lp1:x',
    'SWK': 'w cur:x fiber:x q08:x q10:x q58:x q60:x flags_aff last_job_tsc mailbox:x',
    'SLK': 'ef8:x f78:x',
    'CSAT': 'job:x counter:x value target cond suspend_ts eligible_idle idle_mask:x',
    'JD': 'key:x chunk q0:x q1:x q2:x q3:x q4:x q5:x q6:x q7:x q8:x q9:x q10:x',
    'SCNT': 'c2_0_3:x c2_4_7:x c2_8_11:x c2_12:x c3_0_3:x c3_4_7:x c3_8_11:x c3_12:x',
    'SGRP': 'g sum_count waiting_full external_full',
    'SPOOL': 'ec8 ef0 ed0',
    'SFM': 'mode r34 r2c hz',
    'SSTAT': 'ticks max_gap_ns p99_gap_ns avg_tick_cost_ns',
    'THR': 'kind port thread_id guest:x name:s worker_idx',
    'TATTR': 'qos_apply qos_class relpri policy sched_prio managed_priority',
    'CLK': 'sw_mid tsc mat_ns ca_ns gap_ns',
    'PROC': 'user_ns sys_ns p_user_ns p_sys_ns runnable_ns instr cycles intr_wkups idle_wkups footprint load1x100 nthreads',
    'OTHER': 'others_cpu_ns pid1 cpu1 name1:s pid2 cpu2 name2:s pid3 cpu3 name3:s',
    'TCPU': 'thread_id user_us sys_us cpu_usage run_state flags name:s port',
    'WCPU': 'd0_1 d2_3 d4_5 d6_7 d8_9 d10_11 d12_13 running_mask',
    'GC': 'gen0 gen1 gen2 pause_total_ns alloc_bytes lock_contention threadpool_threads',
    'AB': 'phase window_idx',
    'WSTAT': 'written dropped max_occupancy writer_cpu_ns',
}
FALLBACK_SCHEMA = {k: [x.split(':')[0] for x in v.split()] for k, v in SINK_SCHEMA.items()}
# Field names whose value is a Stopwatch timestamp (converted to seconds since start_ts).
TS_FIELDS = {'HLE': ('enter_ts',), 'FSW': ('enter_ts',), 'WAKE': ('enter_ts',), 'SLICE': ('start_ts',),
             'WIDLE': ('start_ts',), 'CSAT': ('suspend_ts',)}
# Allow-list import names that sanity.txt checks.
EXPECTED_ROOTS = {'s48': 0x1fff, 's4c': 0xfffff5fe, 'sb90': 13}


class Missing(Exception):
    pass


# --------------------------------------------------------------------------------------
# Small helpers
# --------------------------------------------------------------------------------------
def pct(sv, p):
    """Linear-interpolated percentile of an already sorted list."""
    if not sv:
        return None
    if len(sv) == 1:
        return sv[0]
    k = (len(sv) - 1) * p / 100.0
    lo = int(math.floor(k))
    hi = min(lo + 1, len(sv) - 1)
    return sv[lo] + (sv[hi] - sv[lo]) * (k - lo)


def med(v):
    return pct(sorted(v), 50) if v else None


def mean(v):
    return sum(v) / len(v) if v else None


def fnum(x, nd=3):
    if x is None:
        return ''
    if isinstance(x, float):
        if math.isnan(x) or math.isinf(x):
            return ''
        return ('%.' + str(nd) + 'f') % x
    return str(x)


def ov(a, b, s, e):
    v = min(b, e) - max(a, s)
    return v if v > 0 else 0.0


def union_len(ivs, a, b):
    """Length of the union of (s, e) intervals clipped to [a, b]."""
    cl = sorted((max(a, s), min(b, e)) for s, e in ivs if e > a and s < b)
    tot, cur_s, cur_e = 0.0, None, None
    for s, e in cl:
        if cur_e is None or s > cur_e:
            if cur_e is not None:
                tot += cur_e - cur_s
            cur_s, cur_e = s, e
        else:
            cur_e = max(cur_e, e)
    if cur_e is not None:
        tot += cur_e - cur_s
    return tot


class IvIdx:
    """Interval index over tuples whose first two items are (start, end)."""

    def __init__(self, ivs):
        self.ivs = sorted(ivs, key=itemgetter(0))
        self.starts = [i[0] for i in self.ivs]
        self.maxlen = max((i[1] - i[0] for i in self.ivs), default=0.0)

    def q(self, a, b):
        lo = bisect.bisect_left(self.starts, a - self.maxlen)
        hi = bisect.bisect_right(self.starts, b)
        return [i for i in self.ivs[lo:hi] if i[1] > a and i[0] < b]


def linfit(xs, ys):
    """y = ym + a*(x-xm). Returns (a, xm, ym, rms, maxabs) or None."""
    n = len(xs)
    if n < 2:
        return None
    xm, ym = sum(xs) / n, sum(ys) / n
    sxx = sum((x - xm) ** 2 for x in xs)
    if sxx <= 0:
        return None
    a = sum((x - xm) * (y - ym) for x, y in zip(xs, ys)) / sxx
    res = [(y - ym) - a * (x - xm) for x, y in zip(xs, ys)]
    rms = math.sqrt(sum(r * r for r in res) / n)
    return (a, xm, ym, rms, max(abs(r) for r in res))


def fmt_us(us):
    if us >= 1e6:
        return '%gs' % (us / 1e6)
    if us >= 1e3:
        return '%gms' % (us / 1e3)
    return '%gus' % us


def hist_text(title, vals_s, indent='  '):
    """Log2 histogram, 1 us .. 1 s, of durations given in seconds."""
    lines = [title]
    if not vals_s:
        lines.append(indent + '(no samples)')
        return lines
    sv = sorted(vals_s)
    nb = 21
    counts = [0] * (nb + 2)  # 0: <1us, 1..nb: [2^(i-1), 2^i) us, nb+1: >= 2^20 us
    for v in sv:
        us = v * 1e6
        if us < 1:
            counts[0] += 1
        else:
            b = int(math.floor(math.log2(us))) + 1
            counts[min(b, nb + 1)] += 1
    n = len(sv)
    top = max(counts)
    for i, c in enumerate(counts):
        if c == 0:
            continue
        if i == 0:
            lab = '<1us'
        elif i == nb + 1:
            lab = '>=%s' % fmt_us(2 ** (nb - 1))
        else:
            lab = '[%s,%s)' % (fmt_us(2 ** (i - 1)), fmt_us(2 ** i))
        lines.append('%s%-18s %8d %5.1f%% %s' % (indent, lab, c, 100.0 * c / n, '#' * max(1, int(30 * c / top))))
    lines.append('%sn=%d p50=%s p90=%s p99=%s max=%s' % (indent, n, fmt_us(pct(sv, 50) * 1e6), fmt_us(pct(sv, 90) * 1e6),
                                                       fmt_us(pct(sv, 99) * 1e6), fmt_us(sv[-1] * 1e6)))
    return lines


# --------------------------------------------------------------------------------------
# dstrace.log parsing
# --------------------------------------------------------------------------------------
class Data:
    def __init__(self):
        self.recs = defaultdict(list)       # type -> [(ts_s, tid, v0, v1, ...)]
        self.schema = {}                    # type -> [field names]
        self.schema_src = {}                # type -> 'log' | 'fallback'
        self.colmap = {}
        self.strs = {}
        self.header = {}
        self.end = {}
        self.config = {}
        self.unknown = Counter()
        self.short = Counter()
        self.bad_lines = 0
        self.pairs = []                     # (FSW tuple, FSJ tuple)
        self.t0 = None
        self.freq = 1e9
        self.nlines = 0
        self._tsl = {}
        self.t_first = None
        self.t_last = 0.0

    def has(self, t):
        return bool(self.recs.get(t))

    def L(self, t):
        return self.recs.get(t, [])

    def col(self, t):
        """name -> tuple index for type t (KeyError if the type has no schema)."""
        c = self.colmap.get(t)
        if c is None:
            names = self.schema[t]
            c = {n: i + 2 for i, n in enumerate(names)}
            self.colmap[t] = c
        return c

    def S(self, i):
        if i == 0:
            return ''
        return self.strs.get(i, '#%d' % i)

    def tsl(self, t):
        v = self._tsl.get(t)
        if v is None:
            v = [r[0] for r in self.recs.get(t, [])]
            self._tsl[t] = v
        return v

    def rng(self, t, a, b):
        """Records of type t with a < ts <= b (records are ts sorted)."""
        tl = self.tsl(t)
        lo = bisect.bisect_right(tl, a)
        hi = bisect.bisect_right(tl, b)
        return self.recs[t][lo:hi] if t in self.recs else []

    def to_s(self, ticks):
        return (ticks - self.t0) / self.freq


_STR_ESC = re.compile(r'\\(.)')


def _toint(x):
    try:
        return int(x, 0)
    except ValueError:
        try:
            return int(x, 16)
        except ValueError:
            try:
                return int(float(x))
            except ValueError:
                return 0


def parse_log(path, D, progress=False):
    hdr_done = False
    schema_lines = {}
    tsidx = {}
    lastfsw = {}
    recs = D.recs
    pairs = D.pairs
    strs = D.strs
    unknown = D.unknown
    short = D.short
    tstart = time.time()
    with open(path, 'r', encoding='utf-8', errors='replace', buffering=1 << 20) as f:
        for line in f:
            D.nlines += 1
            c0 = line[0:1]
            if c0 == '#':
                if line.startswith('# DSTRACE'):
                    for tok in line.split()[3:]:
                        if '=' in tok:
                            k, v = tok.split('=', 1)
                            D.header[k] = v
                    try:
                        D.t0 = int(D.header.get('start_ts', ''))
                    except ValueError:
                        D.t0 = None
                    try:
                        D.freq = float(D.header.get('freq', '1e9'))
                    except ValueError:
                        D.freq = 1e9
                    hdr_done = True
                elif line.startswith('# SCHEMA'):
                    p = line.split()
                    if len(p) >= 3:
                        schema_lines[p[2]] = [x.split(':')[0] for x in p[3:]]
                elif line.startswith('# TSC'):
                    for tok in line.split()[2:]:
                        if tok.startswith('tsc_hz='):
                            D.header['tsc_hz'] = tok.split('=', 1)[1]
                            D.header['tsc_hz_late'] = '1'
                elif line.startswith('# CONFIG'):
                    for tok in line.split()[2:]:
                        if '=' in tok:
                            k, v = tok.split('=', 1)
                            D.config[k] = v
                elif line.startswith('# END'):
                    for tok in line.split()[2:]:
                        if '=' in tok:
                            k, v = tok.split('=', 1)
                            D.end[k] = v
                continue
            p = line.split()
            if len(p) < 3:
                D.bad_lines += 1
                continue
            typ = p[1]
            try:
                ts = int(p[0])
                tid = int(p[2])
            except ValueError:
                D.bad_lines += 1
                continue
            if D.t0 is None:
                D.t0 = ts
            if typ == 'STR':
                try:
                    parts = line.rstrip('\n').split(' ', 4)
                    sid = int(parts[3])
                    txt = parts[4] if len(parts) > 4 else ''
                    strs[sid] = _STR_ESC.sub(lambda m: {'n': '\n', 'r': '\r'}.get(m.group(1), m.group(1)), txt) if '\\' in txt else txt
                except (ValueError, IndexError):
                    D.bad_lines += 1
                continue
            names = D.schema.get(typ)
            if names is None:
                names = schema_lines.get(typ)
                if names is not None:
                    D.schema[typ] = names
                    D.schema_src[typ] = 'log'
                elif typ in FALLBACK_SCHEMA:
                    names = FALLBACK_SCHEMA[typ]
                    D.schema[typ] = names
                    D.schema_src[typ] = 'fallback'
                else:
                    unknown[typ] += 1
                    continue
                tsidx[typ] = [i + 2 for i, n in enumerate(names) if n in TS_FIELDS.get(typ, ())]
            try:
                vals = [int(x, 0) for x in p[3:]]
            except ValueError:
                vals = [_toint(x) for x in p[3:]]
            nn = len(names)
            if len(vals) < nn:
                short[typ] += 1
                vals.extend([0] * (nn - len(vals)))
            t_s = (ts - D.t0) / D.freq
            rec = [t_s, tid] + vals
            for i in tsidx[typ]:
                rec[i] = (rec[i] - D.t0) / D.freq
            rec = tuple(rec)
            recs[typ].append(rec)
            if t_s > D.t_last:
                D.t_last = t_s
            if typ == 'FSW':
                lastfsw[tid] = rec
            elif typ == 'FSJ':
                w = lastfsw.pop(tid, None)
                if w is not None:
                    pairs.append((w, rec))
    for t, lst in recs.items():
        lst.sort(key=itemgetter(0))
    pairs.sort(key=lambda wj: wj[0][0])
    D.parse_s = time.time() - tstart
    # types present in SCHEMA lines but never emitted still get a schema entry
    for t, names in schema_lines.items():
        D.schema.setdefault(t, names)
        D.schema_src.setdefault(t, 'log')
    return D


# --------------------------------------------------------------------------------------
# game.log parsing
# --------------------------------------------------------------------------------------
KV_RE = re.compile(r"(\w+)=('[^']*'|\S+)")


def _num(v):
    v = v.strip("'")
    m = re.match(r'^-?\d+(\.\d+)?', v)
    if not m:
        return v
    return float(m.group(0)) if '.' in m.group(0) else int(m.group(0))


def parse_gamelog(path):
    g = {'present': [], 'perf': [], 'sched': [], 'sched_windows': [], 'dstrace_lines': [], 'lines': 0}
    if not path or not os.path.exists(path):
        return g
    with open(path, 'r', encoding='utf-8', errors='replace') as f:
        for ln, line in enumerate(f):
            g['lines'] += 1
            if line.startswith('[METAL-PRESENT]'):
                d = {k: _num(v) for k, v in KV_RE.findall(line)}
                d['line'] = ln
                g['present'].append(d)
            elif line.startswith('[METAL-V2][PERF]'):
                d = {k: _num(v) for k, v in KV_RE.findall(line)}
                d['line'] = ln
                d['after_present'] = len(g['present'])
                g['perf'].append(d)
            elif line.startswith('[PERF][GUEST_SCHED]'):
                d = {k: _num(v) for k, v in KV_RE.findall(line)}
                if 'name' in d:
                    d['window_idx'] = len(g['sched_windows']) - 1
                    g['sched'].append(d)
                elif 'window' in d:
                    g['sched_windows'].append(d)
            elif '[DSTRACE]' in line:
                g['dstrace_lines'].append(line.rstrip())
    return g


def gamelog_dstrace_path(g):
    for ln in g['dstrace_lines']:
        m = re.search(r'output=(\S.*)$', ln)
        if m:
            return m.group(1).strip()
    return None


def gamelog_text(g):
    out = ['game.log summary (%d lines)' % g['lines']]
    pw = g['present']
    if pw:
        fr = sum(w.get('guest_frames', 0) for w in pw)
        secs = sum(w.get('seconds', 0) for w in pw)
        fps = sorted(w.get('guest_fps', 0) for w in pw if w.get('guest_fps', 0) > 0)
        hz = sorted(w.get('host_hz', 0) for w in pw)
        out.append('[METAL-PRESENT] windows=%d seconds=%.1f guest_frames=%d  guest_fps(nonzero windows) median=%s p90=%s  host_hz median=%s'
                   % (len(pw), secs, fr, fnum(pct(fps, 50), 2), fnum(pct(fps, 90), 2), fnum(pct(hz, 50), 1)))
    else:
        out.append('[METAL-PRESENT] none')
    pf = g['perf']
    if pf:
        secs = sum(w.get('seconds', 0) for w in pf)
        def tot(k):
            return sum(w.get(k, 0) for w in pf)
        out.append('[METAL-V2][PERF] windows=%d seconds=%.1f draws=%d uploads=%d writebacks=%d guest_submissions=%d gpu_busy_ms=%.0f idle_ms=%.0f host_wait_ms=%.0f'
                   % (len(pf), secs, tot('draws'), tot('uploads'), tot('writebacks'), tot('guest_submissions'),
                      tot('gpu_busy_ms'), tot('idle_ms'), tot('host_wait_ms')))
        if secs > 0:
            out.append('  per second: draws=%.1f uploads=%.1f writebacks=%.1f gpu_busy=%.0f ms/s'
                       % (tot('draws') / secs, tot('uploads') / secs, tot('writebacks') / secs, tot('gpu_busy_ms') / secs))
    else:
        out.append('[METAL-V2][PERF] none')
    sc = g['sched']
    if sc:
        agg = defaultdict(lambda: defaultdict(float))
        for d in sc:
            a = agg[str(d.get('name'))]
            a['n'] += 1
            for k in ('run_ms', 'blocked_ms', 'ready_ms', 'dispatch_ms', 'runs', 'blocked', 'resumed'):
                v = d.get(k)
                if isinstance(v, (int, float)):
                    a[k] += v
        out.append('[PERF][GUEST_SCHED] windows=%d thread-rows=%d; top threads by summed run_ms:' % (len(g['sched_windows']), len(sc)))
        for name, a in sorted(agg.items(), key=lambda kv: -kv[1]['run_ms'])[:16]:
            out.append('  %-34s run_ms=%9.1f blocked_ms=%8.1f ready_ms=%7.1f dispatch_ms=%7.1f runs=%d blocked=%d'
                       % (name, a['run_ms'], a['blocked_ms'], a['ready_ms'], a['dispatch_ms'], a['runs'], a['blocked']))
    else:
        out.append('[PERF][GUEST_SCHED] none')
    if g['dstrace_lines']:
        out.append('[DSTRACE] lines in game.log:')
        out.extend('  ' + l for l in g['dstrace_lines'][:20])
    return out


# --------------------------------------------------------------------------------------
# Derived structures shared by the output sections
# --------------------------------------------------------------------------------------
class Park:
    __slots__ = ('job', 'label', 'ra4', 'ra5', 's', 's_end', 'r', 'r_end', 'csat', 'idle', 'tid')

    def __init__(self, **k):
        for n in self.__slots__:
            setattr(self, n, k.get(n))


class Pair:
    __slots__ = ('t', 'end', 'tot', 'gate', 'body', 'prep', 'pc', 'wl', 'mp', 'cpu', 'frm', 'to', 'site', 'resumed',
                 'ra1', 'job', 'counter', 'tid', 'label', 'ra3', 'ra4', 'ra5', 'qs', 'aff')


class Ctx:
    def __init__(self, D, R, gl, settle):
        self.D, self.R, self.gl, self.settle = D, R, gl, settle
        self.label_cache = {}
        self.unknown_labels = Counter()
        self.jd = {}
        self.fits = {}
        self.flips = [r[0] for r in D.L('FLIP')]
        self.t_end = D.t_last
        self._build_threads()
        self._build_fits()
        self._build_ab()

    # ----- labels
    def label_q(self, q08, q10, q58, q60):
        key = (q08, q10, q58, q60)
        v = self.label_cache.get(key)
        if v is not None:
            return v
        lab = None
        for q in key:
            lab = LABEL_ADDRS.get(q)
            if lab:
                break
        if lab is None:
            cand = next((q for q in key if 0x800000000 <= q < 0x900000000), 0)
            lab = 'job@0x%x' % cand if cand else 'job@?'
            self.unknown_labels[lab] += 1
        self.label_cache[key] = lab
        return lab

    # ----- threads
    def _build_threads(self):
        D = self.D
        self.thr = {}
        self.w2tid, self.w2guest, self.tid2w, self.guest_name = {}, {}, {}, {}
        self.threadid2tid = {}
        if D.has('THR'):
            T = D.col('THR')
            for r in D.L('THR'):
                tid = r[1]
                name = D.S(r[T['name']])
                widx = r[T['worker_idx']]
                m = WORKER_RE.search(name)
                w = widx if 0 <= widx < NWORKERS else (int(m.group(1)) if m else None)
                self.thr[tid] = {'kind': r[T['kind']], 'thread_id': r[T['thread_id']], 'guest': r[T['guest']],
                                 'name': name, 'w': w, 'rec_ts': r[0]}
                self.threadid2tid[r[T['thread_id']]] = tid
                self.guest_name[r[T['guest']]] = name
                if w is not None and w < NWORKERS:
                    self.w2tid.setdefault(w, tid)
                    self.tid2w[tid] = w
                    if r[T['guest']]:
                        self.w2guest.setdefault(w, r[T['guest']])
        if D.has('GF'):
            G = D.col('GF')
            seen = set()
            for r in D.L('GF'):
                gh = r[G['guest']]
                if gh in seen:
                    continue
                seen.add(gh)
                name = D.S(r[G['name']])
                self.guest_name.setdefault(gh, name)
                m = WORKER_RE.search(name)
                if m:
                    w = int(m.group(1))
                    if w < NWORKERS:
                        self.w2guest.setdefault(w, gh)
                        self.w2tid.setdefault(w, r[1])
                        self.tid2w.setdefault(r[1], w)
        self.guest2w = {g: w for w, g in self.w2guest.items()}

    def tclass(self, tid):
        t = self.thr.get(tid)
        if t is None:
            return 'unknown'
        if t['w'] is not None:
            return 'worker'
        return {1: 'guest-other', 2: 'metal-worker', 3: 'metal-completion', 4: 'flip-pacer', 7: 'dstrace'}.get(t['kind'], 'other')

    def tname(self, tid):
        t = self.thr.get(tid)
        return t['name'] if t else 'tid%d' % tid

    # ----- clock fits
    def _build_fits(self):
        D = self.D
        self.fits = {}
        if not D.has('CLK'):
            return
        C = D.col('CLK')
        recs = D.L('CLK')
        gaps = sorted(r[C['gap_ns']] for r in recs)
        gcut = max(pct(gaps, 50) * 5, 1) if gaps else 0
        use = [r for r in recs if r[C['gap_ns']] <= gcut] or recs
        ys = [(r[C['sw_mid']] - D.t0) / D.freq for r in use]
        for dom in ('tsc', 'ca_ns', 'mat_ns'):
            f = linfit([float(r[C[dom]]) for r in use], ys)
            if f:
                self.fits[dom] = f
        try:
            hz = float(D.header.get('tsc_hz', 0))
        except ValueError:
            hz = 0.0
        self.tsc_hz = hz
        if 'tsc' not in self.fits and hz > 0 and use:
            r0 = use[0]
            self.fits['tsc'] = (1.0 / hz, float(r0[C['tsc']]), ys[0], 0.0, 0.0)   # slope from tsc_hz, no residual info

    def dom_to_s(self, dom, x):
        f = self.fits.get(dom)
        if f is None:
            return None
        a, xm, ym = f[0], f[1], f[2]
        return ym + a * (x - xm)

    # ----- A/B timeline
    def _build_ab(self):
        D = self.D
        self.ab = []
        if D.has('AB'):
            A = D.col('AB')
            self.ab = [(r[0], r[A['phase']], r[A['window_idx']]) for r in D.L('AB')]
        self.ab_ts = [x[0] for x in self.ab]

    def ab_at(self, t):
        if not self.ab:
            return 'NA', -1, None
        i = bisect.bisect_right(self.ab_ts, t) - 1
        if i < 0:
            return 'OFF', -1, None
        return ('ON' if self.ab[i][1] else 'OFF'), self.ab[i][2], self.ab[i][0]

    # ----- fiber pairs / parks
    def build_fiber(self):
        if hasattr(self, 'parks'):
            return
        D = self.D
        self.pairs_l, self.parks, self.park_s = [], [], []
        self.rj_ptrs, self.gl_ptrs, self.sj_ptrs = set(), set(), set()
        if not D.pairs:
            return
        FS, FJ = D.col('FSW'), D.col('FSJ')
        for w, j in D.pairs:
            p = Pair()
            ra2 = w[FS['ra2']]
            p.resumed = (ra2 >> 63) & 1
            p.site = ra2 & ((1 << 63) - 1)
            p.t = w[FS['enter_ts']]
            p.tot = w[FS['total_ns']] / 1e9
            p.end = p.t + p.tot
            p.gate, p.body, p.prep = w[FS['gate_wait_ns']], w[FS['body_ns']], w[FS['prep_ns']]
            p.pc, p.wl, p.mp, p.cpu = w[FS['probe_code']], w[FS['probe_wlock_wait_ns']], w[FS['probe_mprotect_ns']], w[FS['cpu_ns']]
            p.frm, p.to, p.ra1 = w[FS['from']], w[FS['to']], w[FS['ra1']]
            p.tid = w[1]
            p.job, p.counter = j[FJ['job']], j[FJ['counter']]
            p.qs = (j[FJ['q08']], j[FJ['q10']], j[FJ['q58']], j[FJ['q60']])
            p.aff = j[FJ['aff_grp']]
            p.ra3, p.ra4, p.ra5 = j[FJ['ra3']], j[FJ['ra4']], j[FJ['ra5']]
            p.label = self.label_q(*p.qs)
            self.pairs_l.append(p)
        self.pairs_l.sort(key=lambda p: p.t)
        pend = {}
        by_job = defaultdict(list)
        for p in self.pairs_l:
            if p.site == SITE_SUSPEND and p.job:
                pk = Park(job=p.job, label=p.label, ra4=p.ra4, ra5=p.ra5, s=p.t, s_end=p.end, tid=p.tid)
                pend[p.job] = pk
                self.parks.append(pk)
                by_job[p.job].append(pk)
            elif p.site == SITE_RESUME and p.job in pend:
                pk = pend.pop(p.job)
                pk.r, pk.r_end = p.t, p.end
        if D.has('CSAT'):
            C = D.col('CSAT')
            for r in D.L('CSAT'):
                lst = by_job.get(r[C['job']])
                if not lst:
                    continue
                for pk in reversed(lst):
                    if pk.s <= r[0] + 1e-6 and pk.csat is None:
                        if pk.r is None or r[0] <= pk.r + 1e-6:
                            pk.csat = r[0]
                            pk.idle = r[C['eligible_idle']]
                        break
        self.parks.sort(key=lambda k: k.s)
        self.park_s = [k.s for k in self.parks]
        for pk in self.parks:
            if pk.label == 'RenderJob':
                self.rj_ptrs.add(pk.job)

    def is_park(self, pk, kind):
        if kind == 'rj':
            return pk.label == 'RenderJob' or pk.job in self.rj_ptrs
        if kind == 'gl':
            return pk.ra4 == GL_WRT
        if kind == 'sj':
            return pk.ra4 == SJ_DEP or pk.label == 'SubmitJob'
        return False

    # ----- SRT timeline
    def build_srt(self):
        D = self.D
        self.srt_ts, self.srt_new, self.srt_old = [], [], []
        if D.has('SRT'):
            S = D.col('SRT')
            for r in D.L('SRT'):
                self.srt_ts.append(r[0])
                self.srt_new.append(r[S['new']])
                self.srt_old.append(r[S['old']])

    def dwell(self, a, b):
        res = {0: 0.0, 1: 0.0, 2: 0.0, 3: 0.0, 4: 0.0}
        ts, nw = self.srt_ts, self.srt_new
        i = bisect.bisect_right(ts, a) - 1
        cur = nw[i] if i >= 0 else None
        ct = a
        j = i + 1
        while j < len(ts) and ts[j] < b:
            if cur is not None:
                res[cur] = res.get(cur, 0.0) + (ts[j] - ct)
            cur, ct = nw[j], ts[j]
            j += 1
        if cur is not None:
            res[cur] = res.get(cur, 0.0) + (b - ct)
        return res

    # ----- SWK intervals
    def build_swk(self):
        D = self.D
        self.swk_by_w = defaultdict(list)      # w -> [(s, e, w, label, job)]
        self.swk_all = []
        if not D.has('SWK'):
            self.swk_idx = IvIdx([])
            return
        S = D.col('SWK')
        last = {}
        for r in D.L('SWK'):
            w = r[S['w']]
            prev = last.get(w)
            if prev is not None and prev[1] != 0:
                iv = (prev[0], r[0], w, prev[2], prev[1])
                self.swk_by_w[w].append(iv)
                self.swk_all.append(iv)
            cur = r[S['cur']]
            lab = self.label_q(r[S['q08']], r[S['q10']], r[S['q58']], r[S['q60']]) if cur else None
            last[w] = (r[0], cur, lab)
        for w, prev in last.items():
            if prev[1] != 0:
                iv = (prev[0], max(self.t_end, prev[0]), w, prev[2], prev[1])
                self.swk_by_w[w].append(iv)
                self.swk_all.append(iv)
        self.swk_idx = IvIdx(self.swk_all)
        self.rj_idx = IvIdx([i for i in self.swk_all if i[3] == 'RenderJob'])
        self.sj_idx = IvIdx([i for i in self.swk_all if i[3] == 'SubmitJob'])
        for iv in self.swk_all:
            if iv[3] == 'RenderJob':
                self.rj_ptrs.add(iv[4])

    # ----- CPU series
    def build_cpu(self):
        D = self.D
        self.cpu = defaultdict(list)           # tid -> [(t, cum_ns)]
        self.cpu_src = {}
        if D.has('TCPU') and self.threadid2tid:
            C = D.col('TCPU')
            for r in D.L('TCPU'):
                tid = self.threadid2tid.get(r[C['thread_id']])
                if tid is not None:
                    self.cpu[tid].append((r[0], (r[C['user_us']] + r[C['sys_us']]) * 1000))
            for tid in self.cpu:
                self.cpu_src[tid] = 'TCPU'
        self.cpupt_ok = None
        if D.has('CPUPT'):
            C = D.col('CPUPT')
            by = defaultdict(list)
            for r in D.L('CPUPT'):
                by[r[1]].append((r[0], r[C['cpu_ns']]))
            mono = tot = 0
            for lst in by.values():
                for i in range(1, len(lst)):
                    tot += 1
                    if lst[i][1] >= lst[i - 1][1]:
                        mono += 1
            self.cpupt_ok = (mono / tot >= 0.9) if tot else None
            if self.cpupt_ok:
                for tid, lst in by.items():
                    self.cpu[tid].extend(lst)
                    self.cpu_src[tid] = (self.cpu_src.get(tid, '') + '+CPUPT').lstrip('+')
        for tid in self.cpu:
            self.cpu[tid].sort()
        self._cpu_t = {tid: [x[0] for x in l] for tid, l in self.cpu.items()}

    def cpu_at(self, tid, t):
        l = self.cpu.get(tid)
        if not l:
            return None
        ts = self._cpu_t[tid]
        i = bisect.bisect_right(ts, t)
        if i == 0:
            return l[0][1]
        if i >= len(l):
            return l[-1][1]
        (t0, c0), (t1, c1) = l[i - 1], l[i]
        return c0 if t1 <= t0 else c0 + (c1 - c0) * (t - t0) / (t1 - t0)

    def cpu_delta_s(self, tid, a, b):
        x, y = self.cpu_at(tid, a), self.cpu_at(tid, b)
        if x is None or y is None:
            return None
        return max(0.0, (y - x) / 1e9)

    # ----- lock waits
    def build_locks(self):
        D = self.D
        self.mlw = defaultdict(list)           # tid -> [(ts, wait_s, site)]
        self.mla = defaultdict(list)           # tid -> [(ts, site, wait_sum_s, residual_s)]
        if D.has('MLW'):
            C = D.col('MLW')
            for r in D.L('MLW'):
                self.mlw[r[1]].append((r[0], r[C['wait_ns']] / 1e9, r[C['site']]))
        if D.has('MLA'):
            C = D.col('MLA')
            for r in D.L('MLA'):
                tid = r[C['tid']]
                ws = r[C['wait_sum_ns']] / 1e9
                got = 0.0
                for (t, w, s) in self.mlw.get(tid, ()):
                    if r[0] - 1.0 <= t <= r[0] and s == r[C['site']]:
                        got += w
                self.mla[tid].append((r[0], r[C['site']], ws, max(0.0, ws - got)))

    def lockwait_s(self, tid, a, b):
        tot = 0.0
        for (t, w, s) in self.mlw.get(tid, ()):
            if a <= t <= b:
                tot += w
        for (t, site, ws, resid) in self.mla.get(tid, ()):
            o = ov(a, b, t - 1.0, t)
            if o > 0:
                tot += resid * o
        return tot

    # ----- GF intervals
    def build_gf(self):
        D = self.D
        self.blocked_ivs = defaultdict(list)   # guest -> [(s,e)]
        self.ready_lat = []                    # (guest, lat_s)
        self.ready_ivs = defaultdict(list)
        if not D.has('GF'):
            return
        G = D.col('GF')
        blk, rdy = {}, {}
        for r in D.L('GF'):
            gh, k = r[G['guest']], r[G['kind']]
            if k == 0:
                blk[gh] = r[0]
            elif k == 1:
                if gh in blk:
                    self.blocked_ivs[gh].append((blk.pop(gh), r[0]))
                rdy[gh] = r[0]
            elif k == 4:
                t = rdy.pop(gh, None)
                if t is not None:
                    self.ready_lat.append((gh, r[0] - t))
                    self.ready_ivs[gh].append((t, r[0]))

    def blocked_s(self, guest, a, b):
        tot = 0.0
        for s, e in self.blocked_ivs.get(guest, ()):
            tot += ov(a, b, s, e)
        return tot


# --------------------------------------------------------------------------------------
# game.log window alignment (normalisers)
# --------------------------------------------------------------------------------------
def align_windows(C):
    """Place METAL-PRESENT / METAL-V2 PERF windows on the dstrace time axis.

    PRESENT windows are contiguous and carry guest_frames, so the cumulative frame count
    pins each window end to a FLIP timestamp. PERF windows are not contiguous; each is
    anchored to the end of the PRESENT window printed just before it."""
    g, flips = C.gl, C.flips
    info = {'t0': None, 'mad': None, 'perf': [], 'n_offsets': 0}
    pw = g['present']
    if not pw or not flips:
        return info
    cs = cf = 0.0
    ends, offs = [], []
    for w in pw:
        cs += w.get('seconds', 0)
        cf += w.get('guest_frames', 0)
        ends.append(cs)
        k = int(round(cf))
        if w.get('guest_frames', 0) > 0 and 0 < k <= len(flips):
            offs.append(flips[k - 1] - cs)
    if not offs:
        return info
    t0 = med(offs)
    info['t0'], info['n_offsets'] = t0, len(offs)
    info['mad'] = med([abs(o - t0) for o in offs])
    for w in g['perf']:
        j = w.get('after_present', 0)
        if j <= 0 or j > len(ends):
            continue
        e = t0 + ends[j - 1]
        info['perf'].append((e - w.get('seconds', 0), e, w))
    info['present_total_frames'] = int(round(cf))
    return info


def norm_in_frame(C, a, b, key):
    wins = C.align['perf']
    if not wins:
        return None
    tot, hit = 0.0, False
    for s, e, w in wins:
        o = ov(a, b, s, e)
        if o > 0 and e > s:
            hit = True
            tot += w.get(key, 0) * o / (e - s)
    return tot if hit else None


# --------------------------------------------------------------------------------------
# frames
# --------------------------------------------------------------------------------------
FRAME_COLS = [
    'frame', 'phase', 'ab_phase', 'ab_window', 'ab_settle', 't_start_s', 't_end_s', 'period_ms',
    't_state0_ms', 't_state3_ms', 't_state2_ms', 't_state1_ms', 'rt_cycles',
    'gl_wait_ms', 'gl_resume_hop_ms',
    'rj_queue_ms', 'rj_run_ms', 'rj_wall_ms', 'rj_parks', 'rj_park_ms', 'rj_dep_wait_ms', 'rj_hop_ms', 'rj_fence_ms',
    'label_land_ms', 'rj_cpu_ms', 'rj_lockwait_ms', 'rj_blocked_ms', 'rj_offcpu_resid_ms',
    'sj_count', 'sj_dep_wait_ms', 'sj_hop_ms', 'submit_and_flip_ms',
    'chain_switches', 'chain_switch_wall_ms', 'chain_switch_cpu_ms', 'chain_probe_wlock_ms', 'chain_probe_mprot_ms',
    'chain_gate_wait_ms', 'mutex_block_ms', 'mutex_convoy_max_ms',
    'metal_busy_ms', 'metal_cpu_ms', 'idle_nowork_ms', 'idle_allblocked_ms', 'rrwait_ms', 'gpu_exec_ms',
    'cp_wakes_retry', 'draws', 'uploads', 'writebacks', 'critical']
SEG_COLS = [('update', 't_state0_ms'), ('hopA', 't_state3_ms'), ('rj_queue', 'rj_queue_ms'), ('rj_compute', None),
            ('rj_lockwait', 'rj_lockwait_ms'), ('rj_dep_wait', 'rj_dep_wait_ms'), ('rj_hop', 'rj_hop_ms'),
            ('rj_fence', 'rj_fence_ms'), ('hopB', 't_state1_ms'), ('gl_resume_hop', 'gl_resume_hop_ms')]


def r3(x):
    return None if x is None else round(x, 4)


def build_frames(C):
    D, R = C.D, C.R
    flips = C.flips
    if not flips:
        raise Missing('FLIP records')
    armed = [D.recs[t][0][0] for t in ('GF', 'HLE', 'FSW', 'SRT', 'SWK', 'MLA', 'WAKE', 'SLICE', 'SFC') if D.has(t)]
    first_t = min(armed) if armed else 0.0
    bounds = [(min(first_t, flips[0]), flips[0])] + [(flips[i - 1], flips[i]) for i in range(1, len(flips))]

    have = {t: D.has(t) for t in ('SRT', 'SWK', 'SFC', 'FSW', 'CSAT', 'SLICE', 'WIDLE', 'HLE', 'MLW', 'MLA', 'RRW',
                                  'CPW', 'MGPU', 'LENC', 'MXR', 'GF', 'TCPU', 'CPUPT', 'THR')}
    have['FSW'] = bool(D.pairs)
    for t, why in (('SRT', 'state dwell columns'), ('SWK', 'RenderJob/SubmitJob run columns'), ('SFC', 'rj_fence/label_land'),
                   ('FSW', 'fiber/park/chain columns'), ('CSAT', 'dep-wait/hop columns'), ('SLICE', 'metal_busy/cpu'),
                   ('WIDLE', 'idle columns'), ('HLE', 'submit_and_flip_ms'), ('MLW', 'rj_lockwait (partial)'),
                   ('RRW', 'rrwait_ms'), ('MGPU', 'gpu_exec_ms/label_land'), ('CPW', 'cp_wakes_retry'),
                   ('MXR', 'mutex_block columns'), ('THR', 'worker->thread maps (rj_cpu/lockwait/blocked)')):
        if not have[t]:
            R.note('frames.csv: columns for %s left blank (no %s records)' % (why, t))
    if not (have['TCPU'] or have['CPUPT']):
        R.note('frames.csv: rj_cpu/rj_offcpu_resid left blank (no TCPU/CPUPT records)')
    if C.cpupt_ok is False:
        R.note('frames.csv: CPUPT cpu_ns is not monotonic per thread, so it was ignored (TCPU only)')

    # per-type interval indexes
    slice_idx = widle_idx = mgpu_ivs = None
    if have['SLICE']:
        S = D.col('SLICE')
        slice_idx = IvIdx([(r[S['start_ts']], r[S['start_ts']] + r[S['dur_ns']] / 1e9, r[S['cpu_ns']] / 1e9,
                            r[S['submission_id']]) for r in D.L('SLICE')])
    if have['WIDLE']:
        S = D.col('WIDLE')
        widle_idx = IvIdx([(r[S['start_ts']], r[S['start_ts']] + r[S['dur_ns']] / 1e9, r[S['reason']]) for r in D.L('WIDLE')])
    mgpu_by_serial = {}
    if have['MGPU'] and 'mat_ns' in C.fits:
        S = D.col('MGPU')
        ivs = []
        for r in D.L('MGPU'):
            if r[S['late']] == 2:          # never sampled, time fields are zero
                continue
            # MGPU times are on the host media clock (mach_absolute_time ns), i.e. the CLK mat_ns domain
            s = C.dom_to_s('mat_ns', r[S['gstart_ns']])
            e = C.dom_to_s('mat_ns', r[S['gend_ns']])
            ivs.append((s, e, r[S['serial']]))
            mgpu_by_serial[r[S['serial']]] = (s, e)
        mgpu_ivs = IvIdx(ivs)
        C.mgpu_by_serial = mgpu_by_serial
    elif have['MGPU']:
        R.note('frames.csv: gpu_exec/label_land blank (MGPU present but no usable CLK mat_ns fit)')
    lenc_by_addr = defaultdict(list)
    if have['LENC']:
        S = D.col('LENC')
        for r in D.L('LENC'):
            if r[S['deferred']] != 2:      # 2 = not encoded
                lenc_by_addr[r[S['addr']]].append((r[0], r[S['serial']]))
    hle_sub = []
    if have['HLE']:
        S = D.col('HLE')
        hle_sub = [(r[0], r[S['dur_ns']] / 1e9) for r in D.L('HLE') if D.S(r[S['import']]) in SUBMIT_FLIP_NAMES]
        hle_sub.sort()
    hle_sub_ts = [x[0] for x in hle_sub]
    rrw = []
    if have['RRW']:
        S = D.col('RRW')
        rrw = [(r[0], r[S['wait_ns']] / 1e9) for r in D.L('RRW')]
    cpw_retry = []
    if have['CPW']:
        S = D.col('CPW')
        cpw_retry = [(r[0], r[S['woken']]) for r in D.L('CPW') if r[S['source']] == 2]
    mxr = []
    if have['MXR']:
        S = D.col('MXR')
        mxr = [(r[0], r[1], r[S['wait_ns']] / 1e9) for r in D.L('MXR')]
    sfc = []
    if have['SFC']:
        S = D.col('SFC')
        for r in D.L('SFC'):
            i = r[S['i']]
            sfc.append((r[0], r[S['ctr']], i, r[S['label0']], r[S['label1']], r[S['exp0']], r[S['exp1']],
                        r[S['lp0']] if i == 0 else r[S['lp1']]))
    sfc_ts = [x[0] for x in sfc]
    pair_ts = [p.t for p in C.pairs_l]
    # phase helpers
    bink_tids = {tid for tid, t in C.thr.items() if 'bink' in t['name'].lower()}
    bink_ts = []
    if have['HLE'] and bink_tids:
        S = D.col('HLE')
        bink_ts = sorted(r[0] for r in D.L('HLE') if r[1] in bink_tids and any(
            D.S(r[S['import']]).startswith(pfx) for pfx in PTHREAD_COND_NAMES))
    worker_tids = set(C.tid2w)
    C.fences = []                      # (start, end, frame)
    C.frame_bounds = bounds
    rows = []
    for fi, (a, b) in enumerate(bounds):
        row = {c: None for c in FRAME_COLS}
        row['frame'] = fi
        row['t_start_s'], row['t_end_s'], row['period_ms'] = a, b, (b - a) * 1000
        mid = (a + b) / 2
        abp, abw, _ = C.ab_at(mid)
        row['ab_phase'], row['ab_window'] = abp, abw
        settle = False
        if C.ab and C.settle > 0:
            _, _, last_flip = C.ab_at(a)
            if last_flip is not None and a < last_flip + C.settle:
                settle = True
            if any(a < t <= b for t in C.ab_ts):
                settle = True
        row['ab_settle'] = 1 if settle else 0
        # ---- SRT dwell
        if have['SRT']:
            d = C.dwell(a, b)
            row['t_state0_ms'], row['t_state3_ms'] = d[0] * 1000, d[3] * 1000
            row['t_state2_ms'], row['t_state1_ms'] = d[2] * 1000, d[1] * 1000
            i0 = bisect.bisect_right(C.srt_ts, a)
            i1 = bisect.bisect_right(C.srt_ts, b)
            row['rt_cycles'] = sum(1 for i in range(i0, i1) if C.srt_old[i] == 3 and C.srt_new[i] == 2)
            t32 = next((C.srt_ts[i] for i in range(i0, i1) if C.srt_old[i] == 3 and C.srt_new[i] == 2), None)
        else:
            t32 = None
        # ---- parks
        pk_lo = bisect.bisect_right(C.park_s, a)
        pk_hi = bisect.bisect_right(C.park_s, b)
        frame_parks = C.parks[pk_lo:pk_hi]
        if have['FSW']:
            gl = [k for k in frame_parks if C.is_park(k, 'gl') and k.r is not None]
            row['gl_wait_ms'] = sum(k.r_end - k.s for k in gl) * 1000
            row['gl_resume_hop_ms'] = sum(k.r_end - k.csat for k in gl if k.csat is not None) * 1000 if have['CSAT'] else None
            sj = [k for k in frame_parks if C.is_park(k, 'sj') and k.r is not None]
            row['sj_dep_wait_ms'] = sum(k.r_end - k.s for k in sj if k.ra4 == SJ_DEP) * 1000
            row['sj_hop_ms'] = sum(k.r_end - k.csat for k in sj if k.csat is not None) * 1000 if have['CSAT'] else None
            rjp = [k for k in frame_parks if C.is_park(k, 'rj')]
            row['rj_parks'] = len(rjp)
            row['rj_park_ms'] = sum((k.r_end - k.s) for k in rjp if k.r is not None) * 1000
            if have['CSAT']:
                row['rj_dep_wait_ms'] = sum(k.csat - k.s for k in rjp if k.csat is not None) * 1000
                row['rj_hop_ms'] = sum(k.r_end - k.csat for k in rjp if k.csat is not None and k.r is not None) * 1000
            # chain fiber cost
            lo, hi = bisect.bisect_right(pair_ts, a), bisect.bisect_right(pair_ts, b)
            ch = [p for p in C.pairs_l[lo:hi] if p.label in CHAIN_LABELS or p.ra4 in CHAIN_RA4 or p.job in C.rj_ptrs]
            row['chain_switches'] = len(ch)
            row['chain_switch_wall_ms'] = sum(p.tot for p in ch) * 1000
            row['chain_switch_cpu_ms'] = sum(p.cpu for p in ch) / 1e6
            row['chain_probe_wlock_ms'] = sum(p.wl for p in ch) / 1e6
            row['chain_probe_mprot_ms'] = sum(p.mp for p in ch) / 1e6
            row['chain_gate_wait_ms'] = sum(p.gate for p in ch) / 1e6
        # ---- RenderJob runs
        rj_ints = []
        if have['SWK']:
            rj_ints = [i for i in C.rj_idx.q(a, b) if a <= i[0] <= b]
            sj_ints = [i for i in C.sj_idx.q(a, b) if a <= i[0] <= b]
            row['sj_count'] = len(sj_ints)
            if rj_ints:
                s0 = min(i[0] for i in rj_ints)
                e0 = max(i[1] for i in rj_ints)
                row['rj_run_ms'] = (e0 - s0) * 1000
                row['rj_wall_ms'] = sum(i[1] - i[0] for i in rj_ints) * 1000
                if t32 is not None:
                    st = min((i[0] for i in C.rj_idx.q(t32, b) if i[0] >= t32), default=None)
                    row['rj_queue_ms'] = (st - t32) * 1000 if st is not None else None
                # off-CPU accounting per run interval
                cpu = lock = blk = 0.0
                cpu_ok = False
                for (s, e, w, lab, job) in rj_ints:
                    tid = C.w2tid.get(w)
                    if tid is None:
                        continue
                    c = C.cpu_delta_s(tid, s, e)
                    if c is not None:
                        cpu += c
                        cpu_ok = True
                    lock += C.lockwait_s(tid, s, e)
                    gh = C.w2guest.get(w)
                    if gh is not None:
                        blk += C.blocked_s(gh, s, e)
                if cpu_ok:
                    row['rj_cpu_ms'] = cpu * 1000
                    row['rj_offcpu_resid_ms'] = max(0.0, row['rj_wall_ms'] - cpu * 1000 - lock * 1000 - blk * 1000)
                if C.w2tid:
                    row['rj_lockwait_ms'] = lock * 1000
                if C.w2guest:
                    row['rj_blocked_ms'] = blk * 1000
        # ---- fence
        if sfc:
            i0 = bisect.bisect_right(sfc_ts, a)
            i1 = bisect.bisect_right(sfc_ts, b)
            prev = sfc[i0 - 1][1] if i0 > 0 else None
            fence_tot, first_land, nfence = 0.0, None, 0
            rj_end = max((i[1] for i in rj_ints), default=None)
            for k in range(i0, i1):
                rec = sfc[k]
                if prev is not None and rec[1] > prev:
                    tc = rec[0]
                    edge = lp = None
                    for q in range(k, len(sfc)):
                        r2 = sfc[q]
                        if r2[0] > tc + 1.0:
                            break
                        lab, ex = (r2[3], r2[5]) if r2[2] == 0 else (r2[4], r2[6])
                        if lab == ex and ex != 0:
                            edge, lp = r2[0], r2[7]
                            break
                    end = edge if edge is not None else (rj_end if rj_end is not None else b)
                    if rj_end is not None and rj_end >= tc:
                        end = min(end, rj_end)
                    end = max(end, tc)
                    fence_tot += end - tc
                    nfence += 1
                    C.fences.append((tc, end, fi))
                    if edge is not None and first_land is None and lenc_by_addr and mgpu_by_serial:
                        cands = [x for x in lenc_by_addr.get(lp, ()) if x[0] <= edge]
                        if cands:
                            g = mgpu_by_serial.get(cands[-1][1])
                            if g and g[1] is not None:
                                first_land = (edge - g[1]) * 1000
                prev = rec[1]
            row['rj_fence_ms'] = fence_tot * 1000 if nfence else None
            row['label_land_ms'] = first_land
        # ---- submit and flip
        if have['HLE']:
            lo, hi = bisect.bisect_right(hle_sub_ts, a), bisect.bisect_right(hle_sub_ts, b)
            row['submit_and_flip_ms'] = sum(x[1] for x in hle_sub[lo:hi]) * 1000
        # ---- mutex
        if have['MXR']:
            ws = [w for (t, tid, w) in mxr if a < t <= b and tid in worker_tids] if worker_tids else \
                [w for (t, tid, w) in mxr if a < t <= b]
            row['mutex_block_ms'] = sum(ws) * 1000
            row['mutex_convoy_max_ms'] = max(ws, default=0.0) * 1000
        # ---- metal
        if slice_idx:
            q = slice_idx.q(a, b)
            row['metal_busy_ms'] = sum(ov(a, b, i[0], i[1]) for i in q) * 1000
            row['metal_cpu_ms'] = sum(i[2] * (ov(a, b, i[0], i[1]) / (i[1] - i[0]) if i[1] > i[0] else 1.0) for i in q) * 1000
        if widle_idx:
            q = widle_idx.q(a, b)
            row['idle_nowork_ms'] = sum(ov(a, b, i[0], i[1]) for i in q if i[2] == 0) * 1000
            row['idle_allblocked_ms'] = sum(ov(a, b, i[0], i[1]) for i in q if i[2] == 1) * 1000
        if rrw:
            row['rrwait_ms'] = sum(w for (t, w) in rrw if a < t <= b) * 1000
        if mgpu_ivs:
            row['gpu_exec_ms'] = union_len([(i[0], i[1]) for i in mgpu_ivs.q(a, b)], a, b) * 1000
        if have['CPW']:
            row['cp_wakes_retry'] = sum(wk for (t, wk) in cpw_retry if a < t <= b)
        for key, col in (('draws', 'draws'), ('uploads', 'uploads'), ('writebacks', 'writebacks')):
            row[col] = norm_in_frame(C, a, b, key)
        # ---- phase
        if fi == 0:
            phase = 'loading'
        elif bink_ts and bisect.bisect_right(bink_ts, b) - bisect.bisect_right(bink_ts, a) > 0:
            phase = 'bink'
        elif any(p.ra5 in ALT_LOOP for p in frame_parks) or (have['FSW'] and any(
                p.ra5 in ALT_LOOP for p in C.pairs_l[bisect.bisect_right(pair_ts, a):bisect.bisect_right(pair_ts, b)])):
            phase = 'alt-loop'
        else:
            phase = 'realtime'
        row['phase'] = phase
        # ---- critical segment
        best, bestv = '', 0.0
        for name, colname in SEG_COLS:
            if name == 'rj_compute':
                v = None
                if row['rj_cpu_ms'] is not None:
                    v = row['rj_cpu_ms'] + (row['rj_offcpu_resid_ms'] or 0.0)
            else:
                v = row[colname]
            if v is not None and v > bestv:
                best, bestv = name, v
        row['critical'] = best
        rows.append(row)
    for row in rows:
        for k, v in list(row.items()):
            if isinstance(v, float):
                row[k] = round(v, 4)
    C.rows = rows
    return rows


def write_frames_csv(rows, path):
    with open(path, 'w', newline='') as f:
        w = csv.writer(f)
        w.writerow(FRAME_COLS)
        for r in rows:
            w.writerow([fnum(r[c], 4) if isinstance(r[c], float) else ('' if r[c] is None else r[c]) for c in FRAME_COLS])


# --------------------------------------------------------------------------------------
# Report plumbing
# --------------------------------------------------------------------------------------
class Report:
    def __init__(self, outdir):
        self.outdir = outdir
        self.skipped, self.failed, self.ran, self.notes = [], [], [], []

    def skip(self, name, why):
        self.skipped.append((name, why))

    def note(self, msg):
        if msg not in self.notes:
            self.notes.append(msg)

    def path(self, name):
        return os.path.join(self.outdir, name)

    def write(self, name, lines):
        with open(self.path(name), 'w') as f:
            f.write('\n'.join(lines) + '\n')


def run_section(R, D, name, fn, needs=()):
    miss = [t for t in needs if not D.has(t)]
    if miss:
        R.skip(name, 'no %s records in dstrace.log' % '/'.join(miss))
        return False
    try:
        fn()
        R.ran.append(name)
        return True
    except Missing as e:
        R.skip(name, 'missing input: %s' % e)
    except KeyError as e:
        R.skip(name, 'SCHEMA lacks field/type %s' % e)
    except Exception:
        R.failed.append((name, traceback.format_exc()))
        R.skip(name, 'internal error (see skipped.txt)')
    return False


def colvals(rows, col):
    return [r[col] for r in rows if r.get(col) is not None]


def cmed(rows, col):
    return med(colvals(rows, col))


# --------------------------------------------------------------------------------------
# frames_summary + verdicts
# --------------------------------------------------------------------------------------
def proc_rates(C, t0, t1):
    D = C.D
    if not D.has('PROC'):
        return None
    P = D.col('PROC')
    recs = [r for r in D.L('PROC') if t0 - 0.5 <= r[0] <= t1 + 0.5]
    if len(recs) < 2:
        recs = D.L('PROC')
    if len(recs) < 2:
        return None
    a, b = recs[0], recs[-1]
    dt = b[0] - a[0]
    if dt <= 0:
        return None
    cpu = (b[P['user_ns']] + b[P['sys_ns']] - a[P['user_ns']] - a[P['sys_ns']]) / 1e9
    pcpu = (b[P['p_user_ns']] + b[P['p_sys_ns']] - a[P['p_user_ns']] - a[P['p_sys_ns']]) / 1e9
    return {'dt': dt, 'cores': cpu / dt, 'runnable': (b[P['runnable_ns']] - a[P['runnable_ns']]) / 1e9 / dt,
            'pshare': pcpu / cpu if cpu > 0 else None, 'load1': b[P['load1x100']] / 100.0,
            'wkups': (b[P['intr_wkups']] - a[P['intr_wkups']]) / dt, 'cpu_s': cpu}


def ab_pairs(C, rows):
    on = [r for r in rows if r['phase'] == 'realtime' and r['ab_phase'] == 'ON' and not r['ab_settle']]
    off = [r for r in rows if r['phase'] == 'realtime' and r['ab_phase'] == 'OFF' and not r['ab_settle']]
    return on, off


def per_draw(r):
    d = r.get('draws')
    return r['period_ms'] / d if d else None


def fsw_totals_for(C, rows_sel):
    pair_ts = C._pair_ts
    out = []
    for r in rows_sel:
        lo, hi = bisect.bisect_right(pair_ts, r['t_start_s']), bisect.bisect_right(pair_ts, r['t_end_s'])
        out.extend(p.tot for p in C.pairs_l[lo:hi])
    return out


def verdicts(C, rows):
    D = C.D
    out = []
    real = [r for r in rows if r['phase'] == 'realtime']
    sel = [r for r in real if r['ab_phase'] in ('OFF', 'NA') and not r['ab_settle']]
    C._pair_ts = [p.t for p in C.pairs_l]
    if not sel:
        return [('ALL', 'NO DATA', 'no realtime frames with ab_phase OFF (frames: %d, realtime: %d)' % (len(rows), len(real)))]
    t0, t1 = min(r['t_start_s'] for r in sel), max(r['t_end_s'] for r in sel)
    pr = proc_rates(C, t0, t1)

    # H7
    on, off = ab_pairs(C, rows)
    ratio_pd = fsw_ratio = None
    if on and off:
        a, b = med([x for x in map(per_draw, on) if x is not None] or [r['period_ms'] for r in on]), \
            med([x for x in map(per_draw, off) if x is not None] or [r['period_ms'] for r in off])
        ratio_pd = a / b if a and b else None
        fa, fb = med(fsw_totals_for(C, on)), med(fsw_totals_for(C, off))
        fsw_ratio = fa / fb if fa and fb else None
    lw, sw = cmed(sel, 'chain_probe_wlock_ms'), cmed(sel, 'chain_switch_wall_ms')
    ev = 'A/B period(per draw) ON/OFF=%s FSW p50 ON/OFF=%s; chain probe wlock median=%s ms/frame, switch wall median=%s ms/frame' % (
        fnum(ratio_pd, 3) or 'n/a', fnum(fsw_ratio, 3) or 'n/a', fnum(lw, 2) or 'n/a', fnum(sw, 2) or 'n/a')
    st = 'NO DATA'
    if ratio_pd is not None and fsw_ratio is not None and ratio_pd <= 0.92 and fsw_ratio <= 0.5:
        st = 'CONFIRMED'
    elif lw is not None and lw >= 10:
        st = 'CONFIRMED'
    elif lw is not None and sw and lw < 0.2 * sw and (ratio_pd is None or abs(ratio_pd - 1) < 0.03):
        st = 'REFUTED' if ratio_pd is not None else 'REFUTED (no A/B data)'
    elif lw is not None or ratio_pd is not None:
        st = 'NOT CONFIRMED'
    out.append(('H7', st, ev))

    # H2
    hop = []
    for r in sel:
        parts = [r['t_state3_ms'], r['t_state1_ms'], r['gl_resume_hop_ms'], r['rj_hop_ms'], r['sj_hop_ms']]
        if any(p is not None for p in parts):
            hop.append(sum(p for p in parts if p is not None))
    hm = med(hop)
    csat_idle0 = None
    if D.has('CSAT'):
        S = D.col('CSAT')
        rr = [r for r in D.L('CSAT') if any(x['t_start_s'] < r[0] <= x['t_end_s'] for x in sel[:5000])] if len(sel) < 5000 else D.L('CSAT')
        if rr:
            csat_idle0 = sum(1 for r in rr if r[S['eligible_idle']] == 0) / len(rr)
    st = 'NO DATA' if hm is None else ('CONFIRMED' if hm >= 15 else ('REFUTED' if hm <= 5 else 'NOT CONFIRMED'))
    if hm is not None and not (D.pairs and D.has('CSAT')):
        st += ' (partial: SRT hops only)'
    out.append(('H2', st, 'median hop sum (state3+state1+gl_resume+rj_hop+sj_hop) = %s ms/frame; chain hops with eligible_idle==0: %s' % (
        fnum(hm, 2) or 'n/a', ('%.0f%%' % (100 * csat_idle0)) if csat_idle0 is not None else 'n/a')))

    # H1
    wall = sum(r['rj_wall_ms'] for r in sel if r['rj_wall_ms'] is not None)
    resid = sum(r['rj_offcpu_resid_ms'] for r in sel if r['rj_offcpu_resid_ms'] is not None)
    share = resid / wall if wall > 0 and any(r['rj_offcpu_resid_ms'] is not None for r in sel) else None
    spin = None
    if C.cpu and t1 > t0:
        sp = [C.cpu_delta_s(C.w2tid[w], t0, t1) for w in (0, 9, 11) if w in C.w2tid]
        sp = [x for x in sp if x is not None]
        spin = sum(sp) / (t1 - t0) if sp else None
    st = 'NO DATA'
    if pr is not None or share is not None:
        st = 'NOT CONFIRMED'
        if pr and share is not None:
            if pr['runnable'] >= 2.0 and share >= 0.25:
                st = 'CONFIRMED'
            elif pr['cores'] <= 8 and pr['runnable'] <= 0.5 and share <= 0.10:
                st = 'REFUTED'
    out.append(('H1', st, 'PROC runnable=%s s/s, process CPU=%s cores; chain off-CPU residual share=%s; spinner(w0,9,11) CPU=%s cores' % (
        fnum(pr['runnable'], 2) if pr else 'n/a', fnum(pr['cores'], 2) if pr else 'n/a',
        ('%.0f%%' % (100 * share)) if share is not None else 'n/a', fnum(spin, 2) or 'n/a')))

    # H3
    fe, idn = cmed(sel, 'rj_fence_ms'), cmed(sel, 'idle_nowork_ms')
    st = 'NO DATA'
    if fe is not None and idn is not None:
        st = 'CONFIRMED' if fe >= 15 and idn <= 5 else ('REFUTED' if fe <= 2 and idn >= 15 else 'NOT CONFIRMED')
    out.append(('H3', st, 'rj_fence median=%s ms, idle_nowork median=%s ms' % (fnum(fe, 2) or 'n/a', fnum(idn, 2) or 'n/a')))

    # H6
    mb = cmed(sel, 'mutex_block_ms')
    st, ev = 'NO DATA', 'no MXR records'
    if mb is not None:
        p90 = pct(sorted(r['period_ms'] for r in sel), 90)
        tail = [r for r in sel if r['period_ms'] >= p90]
        sh = sum(1 for r in tail if (r['mutex_convoy_max_ms'] or 0) >= 5) / len(tail) if tail else 0
        st = 'CONFIRMED' if mb >= 5 or sh >= 0.30 else 'NOT CONFIRMED'
        ev = 'chain mutex block median=%s ms/frame; %.0f%% of p90 frames have a >=5 ms convoy' % (fnum(mb, 2), 100 * sh)
    out.append(('H6', st, ev))

    # H5
    st, ev = 'NO DATA', 'no CPW records'
    if D.has('CPW'):
        S = D.col('CPW')
        slow = [r for r in real if r['period_ms'] > 95]
        if slow:
            have_retry = 0
            for r in slow:
                if any(x[S['source']] == 2 for x in D.rng('CPW', r['t_start_s'], r['t_end_s'])):
                    have_retry += 1
            sh = have_retry / len(slow)
            st = 'CONFIRMED' if sh >= 0.70 else 'NOT CONFIRMED'
            ev = '%.0f%% of %d frames > 95 ms contain a CPW RetryBlocked' % (100 * sh, len(slow))
        else:
            st, ev = 'NOT CONFIRMED', 'no realtime frame exceeds 95 ms'
    out.append(('H5', st, ev))

    # MH5
    rc = colvals(real, 'rt_cycles')
    alt = sum(1 for r in rows if r['phase'] == 'alt-loop')
    if rc or alt:
        m = mean(rc) if rc else None
        st = 'CONFIRMED' if (m is not None and m > 1.2) or alt else 'NOT CONFIRMED'
        out.append(('MH5', st, 'mean rt_cycles/frame=%s; alt-loop frames=%d' % (fnum(m, 2) or 'n/a', alt)))
    else:
        out.append(('MH5', 'NO DATA', 'no SRT/FSJ data'))

    # M5
    if D.has('SFM'):
        S = D.col('SFM')
        modes = Counter(r[S['mode']] for r in D.L('SFM'))
        out.append(('M5', 'CONFIRMED' if any(m >= 2 for m in modes) else 'NOT CONFIRMED', 'SFM modes seen: %s' % dict(modes)))
    else:
        out.append(('M5', 'NO DATA', 'no SFM records'))

    # H11
    if D.has('OTHER'):
        S = D.col('OTHER')
        recs = D.L('OTHER')
        hi, prev_t = 0, None
        for r in recs:
            dt = (r[0] - prev_t) if prev_t is not None else 5.0
            prev_t = r[0]
            if dt > 0 and r[S['others_cpu_ns']] / 1e9 / dt > 0.5:
                hi += 1
        sh = hi / len(recs)
        out.append(('H11', 'CONFIRMED' if sh > 0.10 else 'NOT CONFIRMED', '%.0f%% of OTHER samples above 0.5 core (n=%d)' % (100 * sh, len(recs))))
    else:
        out.append(('H11', 'NO DATA', 'no OTHER records'))
    return out


def write_frames_summary(C, rows, path, ver):
    L = []
    ph = Counter(r['phase'] for r in rows)
    L.append('frames: %d  phases: %s' % (len(rows), dict(ph)))
    per = [r['period_ms'] for r in rows if r['phase'] == 'realtime']
    if per:
        sp = sorted(per)
        L.append('realtime period ms: n=%d median=%.2f p90=%.2f p99=%.2f (%.1f fps median)' % (
            len(sp), pct(sp, 50), pct(sp, 90), pct(sp, 99), 1000 / pct(sp, 50)))
    L.append('')
    L.append('== critical segment histogram (realtime frames) ==')
    ch = Counter(r['critical'] or '(none)' for r in rows if r['phase'] == 'realtime')
    for k, v in ch.most_common():
        L.append('  %-14s %5d  %5.1f%%' % (k, v, 100.0 * v / max(1, sum(ch.values()))))
    L.append('')
    L.append('== verdict table (realtime frames, ab_phase OFF/NA, settle frames excluded) ==')
    for h, st, ev in ver:
        L.append('%-4s %-22s %s' % (h, st, ev))
    L.append('')
    cols = [c for c in FRAME_COLS if c not in ('frame', 'phase', 'ab_phase', 'ab_window', 'ab_settle', 't_start_s', 't_end_s', 'critical')]
    groups = defaultdict(list)
    for r in rows:
        groups[(r['phase'], r['ab_phase'])].append(r)
    for key in sorted(groups):
        g = groups[key]
        L.append('== phase=%s ab_phase=%s  n=%d ==' % (key[0], key[1], len(g)))
        L.append('  %-24s %10s %10s %6s' % ('column', 'median', 'p90', 'n'))
        for c in cols:
            v = sorted(colvals(g, c))
            if not v:
                continue
            L.append('  %-24s %10s %10s %6d' % (c, fnum(pct(v, 50), 3), fnum(pct(v, 90), 3), len(v)))
        L.append('')
    pw = C.gl['present'] if C.gl else []
    if pw and C.flips:
        fr = sum(w.get('guest_frames', 0) for w in pw)
        L.append('game.log cross-check: FLIP count=%d, METAL-PRESENT guest_frames sum=%d (%s)' % (
            len(C.flips), fr, 'ok' if fr and abs(len(C.flips) - fr) <= 0.02 * fr else 'MISMATCH > 2%'))
    if C.unknown_labels:
        L.append('')
        L.append('unlabelled jobs (count of distinct label lookups): ' + ', '.join('%s x%d' % kv for kv in C.unknown_labels.most_common(10)))
        if C.D.has('JD'):
            L.append('  JD dumps present: %d records (see dstrace.log)' % len(C.D.L('JD')))
    with open(path, 'w') as f:
        f.write('\n'.join(L) + '\n')


# --------------------------------------------------------------------------------------
# workers.csv
# --------------------------------------------------------------------------------------
def wcpu_validation(C):
    """Return (valid, ratio, wcpu_us_total, tcpu_us_total)."""
    D = C.D
    if not D.has('WCPU'):
        return None
    W = D.col('WCPU')
    names = [n for n in D.schema['WCPU'] if n.startswith('d')]
    tot = 0
    per = defaultdict(float)          # worker -> us
    for r in D.L('WCPU'):
        for k, n in enumerate(names):
            v = r[W[n]]
            lo, hi = v & 0xffffffff, (v >> 32) & 0xffffffff
            per[2 * k] += lo
            per[2 * k + 1] += hi
            tot += lo + hi
    t0, t1 = D.L('WCPU')[0][0], D.L('WCPU')[-1][0]
    ref = 0.0
    for w in range(NWORKERS):
        tid = C.w2tid.get(w)
        if tid is None:
            continue
        d = C.cpu_delta_s(tid, t0, t1)
        if d is not None:
            ref += d * 1e6
    wsum = sum(per[w] for w in range(NWORKERS))
    if ref <= 0:
        return (None, None, wsum, ref)
    ratio = wsum / ref
    return (abs(ratio - 1) <= 0.10, ratio, wsum, ref)


def write_workers_csv(C, path):
    D = C.D
    sec0 = int(math.floor(min((iv[0] for iv in C.swk_all), default=0.0)))
    sec1 = int(math.ceil(C.t_end))
    jobms = defaultdict(float)           # (w, sec)
    lab_ms = defaultdict(lambda: defaultdict(float))
    covered = {}
    for w, ivs in C.swk_by_w.items():
        for (s, e, _w, lab, job) in ivs:
            for sec in range(int(s), int(e) + 1):
                o = ov(sec, sec + 1, s, e)
                if o > 0:
                    jobms[(w, sec)] += o
                    lab_ms[(w, sec)][lab] += o
    first = {}
    if D.has('SWK'):
        S = D.col('SWK')
        for r in D.L('SWK'):
            first.setdefault(r[S['w']], r[0])
    sw_ms, park_n = defaultdict(float), defaultdict(int)
    for p in C.pairs_l:
        w = C.tid2w.get(p.tid)
        if w is None:
            continue
        sec = int(p.t)
        sw_ms[(w, sec)] += p.tot * 1000
        if p.site == SITE_SUSPEND:
            park_n[(w, sec)] += 1
    hle_ms = defaultdict(float)
    if D.has('HLE'):
        S = D.col('HLE')
        for r in D.L('HLE'):
            w = C.tid2w.get(r[1])
            if w is not None:
                hle_ms[(w, int(r[S['enter_ts']]))] += r[S['dur_ns']] / 1e6
    lock_ms = defaultdict(float)
    for tid, lst in C.mla.items():
        w = C.tid2w.get(tid)
        if w is not None:
            for (t, site, ws, resid) in lst:
                lock_ms[(w, int(math.ceil(t)) - 1)] += ws * 1000
    wv = wcpu_validation(C)
    wcpu = defaultdict(float)
    if wv and wv[0]:
        W = D.col('WCPU')
        names = [n for n in D.schema['WCPU'] if n.startswith('d')]
        for r in D.L('WCPU'):
            for k, n in enumerate(names):
                v = r[W[n]]
                wcpu[(2 * k, int(r[0]))] += (v & 0xffffffff) / 1000.0
                wcpu[(2 * k + 1, int(r[0]))] += ((v >> 32) & 0xffffffff) / 1000.0
    workers = sorted(set(C.w2tid) | set(C.swk_by_w) | set(C.w2guest))
    with open(path, 'w', newline='') as f:
        cw = csv.writer(f)
        cw.writerow(['worker', 'second', 'job_ms', 'idle_ms', 'parked_switches', 'switch_ms', 'cpu_ms', 'cpu_over_wall',
                     'cpu_wcpu_ms', 'os_blocked_ms', 'ready_wait_ms', 'lockwait_ms', 'hle_ms', 'top3_delegates'])
        for w in workers:
            tid, gh = C.w2tid.get(w), C.w2guest.get(w)
            s_lo = int(first.get(w, sec0))
            for sec in range(s_lo, sec1 + 1):
                job = jobms.get((w, sec), 0.0) * 1000
                idle = max(0.0, 1000.0 - job)
                cpu = C.cpu_delta_s(tid, sec, sec + 1) if tid is not None else None
                blk = C.blocked_s(gh, sec, sec + 1) * 1000 if gh is not None else None
                rdy = sum(ov(sec, sec + 1, s, e) for s, e in C.ready_ivs.get(gh, ())) * 1000 if gh is not None else None
                top = sorted(lab_ms[(w, sec)].items(), key=lambda kv: -kv[1])[:3]
                cw.writerow([w, sec, fnum(job, 1), fnum(idle, 1), park_n.get((w, sec), 0), fnum(sw_ms.get((w, sec), 0.0), 3),
                             fnum(cpu * 1000, 1) if cpu is not None else '', fnum(cpu, 3) if cpu is not None else '',
                             fnum(wcpu.get((w, sec)), 1) if wcpu else '', fnum(blk, 2), fnum(rdy, 2),
                             fnum(lock_ms.get((w, sec), 0.0), 3), fnum(hle_ms.get((w, sec), 0.0), 3),
                             ';'.join('%s=%.0fms' % (k, v * 1000) for k, v in top)])


# --------------------------------------------------------------------------------------
# wake_latency.txt / fiber.txt / memlock.txt
# --------------------------------------------------------------------------------------
def write_wake_latency(C, path):
    D = C.D
    L = ['Wake / hop latency histograms (log2 buckets, 1us .. 1s)', '']
    if D.has('GF'):
        L += hist_text('GF Ready->RunStarted, all guest threads', [x for _, x in C.ready_lat])
        byc = defaultdict(list)
        for gh, x in C.ready_lat:
            w = C.guest2w.get(gh)
            byc['worker' if w is not None else 'other'].append(x)
        for k, v in sorted(byc.items()):
            L += hist_text('GF Ready->RunStarted, class=%s' % k, v)
        L.append('')
    else:
        L.append('GF: skipped (no GF records)\n')
    if C.parks:
        L += hist_text('CSAT -> resume FSW enter (fiber detection latency)',
                       [k.r - k.csat for k in C.parks if k.csat is not None and k.r is not None])
        L.append('')
        byc = defaultdict(list)
        for p in C.pairs_l:
            byc[(SITE_NAMES.get(p.site, 'site0x%x' % p.site), C.ab_at(p.t)[0])].append(p.tot)
        for k in sorted(byc):
            L += hist_text('FSW total_ns site=%s ab=%s' % k, byc[k])
        L.append('')
    else:
        L.append('CSAT/FSW hop histograms: skipped (no FSW/FSJ pairs)\n')
    if C.srt_ts:
        for st, nm in ((3, 't_state3 (hop A)'), (1, 't_state1 (hop B)')):
            v = []
            for i, ns in enumerate(C.srt_new):
                if ns == st and i + 1 < len(C.srt_ts):
                    v.append(C.srt_ts[i + 1] - C.srt_ts[i])
            L += hist_text(nm + ' dwell per visit', v)
        L.append('')
    else:
        L.append('SRT dwell histograms: skipped (no SRT)\n')
    if D.has('MXR'):
        S = D.col('MXR')
        L += hist_text('MXR wait', [r[S['wait_ns']] / 1e9 for r in D.L('MXR')])
        L.append('')
    else:
        L.append('MXR: skipped (no MXR)\n')
    if D.has('CPW'):
        S = D.col('CPW')
        by = defaultdict(list)
        for r in D.L('CPW'):
            by[r[S['source']]].append(r[S['oldest_blocked_ns']] / 1e9)
        for k in sorted(by):
            L += hist_text('CPW oldest_blocked, source=%d' % k, by[k])
    else:
        L.append('CPW: skipped (no CPW)')
    with open(path, 'w') as f:
        f.write('\n'.join(L) + '\n')


def write_fiber(C, path):
    D = C.D
    dur = max(1e-9, C.t_end - (C.pairs_l[0].t if C.pairs_l else 0.0))
    L = ['Fiber switch internals (FSW/FSJ), window %.1fs' % dur, '']
    groups = defaultdict(list)
    for p in C.pairs_l:
        groups[(SITE_NAMES.get(p.site, 'site0x%x' % p.site), C.ab_at(p.t)[0])].append(p)
    def pstat(vals, scale, unit):
        sv = sorted(v * scale for v in vals)
        return 'p50=%s p90=%s p99=%s %s' % (fnum(pct(sv, 50), 2), fnum(pct(sv, 90), 2), fnum(pct(sv, 99), 2), unit)
    for key in sorted(groups):
        g = groups[key]
        L.append('site=%s ab=%s  count=%d (%.1f/s)' % (key[0], key[1], len(g), len(g) / dur))
        L.append('  total   %s' % pstat([p.tot for p in g], 1e3, 'ms'))
        L.append('  gate    %s' % pstat([p.gate for p in g], 1e-3, 'us'))
        L.append('  body    %s' % pstat([p.body for p in g], 1e-3, 'us'))
        L.append('  prep    %s' % pstat([p.prep for p in g], 1e-3, 'us'))
        L.append('  probe wlock   %s' % pstat([p.wl for p in g], 1e-3, 'us'))
        L.append('  probe mprot   %s' % pstat([p.mp for p in g], 1e-3, 'us'))
        L.append('  cpu     %s' % pstat([p.cpu for p in g], 1e-3, 'us'))
        paths = Counter(p.pc & 0xff for p in g)
        L.append('  path mix (0 single-view,1 shared,2 exclusive,3 IsExecutable): %s' % dict(sorted(paths.items())))
        L.append('  mprotect_count mean=%.2f  tls_worker mix=%s' % (
            mean([(p.pc >> 8) & 0xff for p in g]), dict(Counter((p.pc >> 24) & 0xff for p in g).most_common(5))))
        L.append('')
    # WaitForCounter callers
    by = defaultdict(lambda: [0, 0.0])
    for k in C.parks:
        by[k.ra4][0] += 1
        if k.r is not None:
            by[k.ra4][1] += k.r_end - k.s
    L.append('Top WaitForCounter callers (RA4) by suspend count / summed park wall')
    for ra4, (n, w) in sorted(by.items(), key=lambda kv: -kv[1][0])[:15]:
        L.append('  0x%x %-44s count=%6d  park_wall=%9.1f ms' % (ra4, RA4_NAMES.get(ra4, ''), n, w * 1000))
    ra5 = Counter(k.ra5 for k in C.parks)
    L.append('RA5 (caller of WaitForRenderThread) mix: ' + ', '.join('0x%x%s x%d' % (a, ' (alt-loop)' if a in ALT_LOOP else (' (main)' if a == MAIN_LOOP else ''), n) for a, n in ra5.most_common(6)))
    unk = Counter(p.site for p in C.pairs_l if p.site not in SITE_NAMES)
    L.append('FSW with RA2 not in site table: %d %s' % (sum(unk.values()), ', '.join('0x%x x%d' % kv for kv in unk.most_common(5))))
    with open(path, 'w') as f:
        f.write('\n'.join(L) + '\n')


def write_memlock(C, path):
    D = C.D
    L = ['Memory-system locks (MLA per second per (site, thread); MLW individual events)', '']
    mla_t = sorted({round(r[0]) for r in D.L('MLA')})
    dur = max(1.0, float(len(mla_t)))
    L.append('MLA seconds covered: %d' % len(mla_t))
    if D.has('MLA'):
        M = D.col('MLA')
        agg = defaultdict(lambda: [0, 0, 0.0, 0.0])
        for r in D.L('MLA'):
            a = agg[r[M['site']]]
            a[0] += r[M['count']]
            a[1] += r[M['slow_count']]
            a[2] += r[M['wait_sum_ns']] / 1e9
            a[3] += r[M['hold_sum_ns']] / 1e9
        mlw_by_site = defaultdict(list)
        imp = defaultdict(float)
        if D.has('MLW'):
            W = D.col('MLW')
            for r in D.L('MLW'):
                if r[W['wait_ns']]:
                    mlw_by_site[r[W['site']]].append(r[W['wait_ns']] / 1e9)
                imp[D.S(r[W['import']]) or '(none)'] += r[W['wait_ns']] / 1e9
        L.append('%-4s %-34s %10s %7s %12s %10s %12s' % ('site', 'name', 'count/s', 'slow%', 'wait s/s', 'wait p99', 'hold s/s'))
        for s in sorted(agg):
            n, slow, ws, hs = agg[s]
            p99 = pct(sorted(mlw_by_site.get(s, [])), 99)
            L.append('%-4d %-34s %10.1f %6.1f%% %12.5f %10s %12.5f' % (
                s, MLOCK_SITES.get(s, '?'), n / dur, 100.0 * slow / n if n else 0.0, ws / dur,
                fmt_us(p99 * 1e6) if p99 else '-', hs / dur))
        L.append('')
        L.append('single-view hit rate (hit = count - slow_count):')
        for s in (9, 10):
            if s in agg and agg[s][0]:
                L.append('  site %d (%s): %.2f%% hit' % (s, MLOCK_SITES[s], 100.0 * (agg[s][0] - agg[s][1]) / agg[s][0]))
        if not any(s in agg for s in (9, 10)):
            L.append('  (no site 9/10 records)')
        L.append('')
        L.append('Top importers by MLW wait (s):')
        for k, v in sorted(imp.items(), key=lambda kv: -kv[1])[:12]:
            L.append('  %-40s %.4f' % (k, v))
        if 2 in agg:
            L.append('')
            L.append('Estimated global stall via site-2 write lock: held %.4f s/s (hold sum), awaited %.4f s/s (wait sum, summed over threads)' % (
                agg[2][3] / dur, agg[2][2] / dur))
    else:
        W = D.col('MLW')
        L.append('(no MLA; MLW only)')
        by = defaultdict(lambda: [0, 0.0])
        for r in D.L('MLW'):
            by[r[W['site']]][0] += 1
            by[r[W['site']]][1] += r[W['wait_ns']] / 1e9
        for s in sorted(by):
            L.append('  site %d %-30s events=%d wait=%.4f s' % (s, MLOCK_SITES.get(s, '?'), by[s][0], by[s][1]))
    with open(path, 'w') as f:
        f.write('\n'.join(L) + '\n')


# --------------------------------------------------------------------------------------
# hostcpu / metal / gpu_roundtrip
# --------------------------------------------------------------------------------------
def write_hostcpu(C, path):
    D = C.D
    L = ['Host CPU']
    if D.has('PROC'):
        P = D.col('PROC')
        recs = D.L('PROC')
        if len(recs) >= 2:
            pr = proc_rates(C, recs[0][0], recs[-1][0])
            L.append('PROC over %.1fs: process cores=%.2f runnable=%.3f s/s P-core share=%s load1=%.2f interrupt wakeups=%.0f/s' % (
                pr['dt'], pr['cores'], pr['runnable'], fnum(pr['pshare'], 2), pr['load1'], pr['wkups']))
        L.append('PROC records=%d, last footprint=%.0f MB, nthreads=%d' % (len(recs), recs[-1][P['footprint']] / 1048576.0, recs[-1][P['nthreads']]))
    else:
        L.append('PROC: no records')
    if D.has('OTHER'):
        S = D.col('OTHER')
        L.append('OTHER (other processes): ' + '; '.join('t=%.0f cpu=%.2fs top=%s' % (r[0], r[S['others_cpu_ns']] / 1e9, D.S(r[S['name1']]) or '-') for r in D.L('OTHER')[:12]))
    if D.has('TCPU'):
        T = D.col('TCPU')
        by = defaultdict(list)
        names = {}
        for r in D.L('TCPU'):
            by[r[T['thread_id']]].append((r[0], (r[T['user_us']] + r[T['sys_us']]) / 1e6))
            nm = D.S(r[T['name']])
            if nm:
                names[r[T['thread_id']]] = nm
        L.append('')
        L.append('Per-thread CPU / wall (TCPU, whole trace):')
        rows = []
        for tid, lst in by.items():
            dt = lst[-1][0] - lst[0][0]
            if dt > 0:
                rows.append(((lst[-1][1] - lst[0][1]) / dt, tid, names.get(tid) or C.tname(C.threadid2tid.get(tid, -1))))
        for v, tid, nm in sorted(rows, reverse=True)[:30]:
            L.append('  %-40s thread_id=%-8d %.3f' % (nm, tid, v))
        sp = [v for v, tid, nm in rows for w in (0, 9, 11) if C.w2tid.get(w) is not None and C.thr.get(C.w2tid[w], {}).get('thread_id') == tid]
        if sp:
            L.append('Spinner CPU (workers 0, 9, 11): %.3f cores' % sum(sp))
        mw = [v for v, tid, nm in rows if C.tclass(C.threadid2tid.get(tid, -1)) == 'metal-worker']
        if mw:
            L.append('Metal worker CPU/wall: %.3f' % mw[0])
    else:
        L.append('TCPU: no records')
    if D.has('TATTR'):
        A = D.col('TATTR')
        L.append('')
        L.append('TATTR (thread attributes):')
        L.append('  %-36s %5s %5s %7s %7s %6s %6s' % ('thread', 'qos?', 'cls', 'relpri', 'policy', 'prio', 'mprio'))
        for r in D.L('TATTR')[:60]:
            L.append('  %-36s %5d %5d %7d %7d %6d %6d' % (C.tname(r[1])[:36], r[A['qos_apply']], r[A['qos_class']], r[A['relpri']],
                                                          r[A['policy']], r[A['sched_prio']], r[A['managed_priority']]))
    if D.has('GC'):
        G = D.col('GC')
        recs = D.L('GC')
        L.append('')
        L.append('GC per second:')
        for a, b in zip(recs, recs[1:]):
            dt = b[0] - a[0] or 1
            L.append('  t=%.0f gen0=%.1f/s gen1=%.1f/s gen2=%.1f/s pause=%.1f ms/s alloc=%.1f MB/s lock_contention=%.0f/s pool_threads=%d' % (
                b[0], (b[G['gen0']] - a[G['gen0']]) / dt, (b[G['gen1']] - a[G['gen1']]) / dt, (b[G['gen2']] - a[G['gen2']]) / dt,
                (b[G['pause_total_ns']] - a[G['pause_total_ns']]) / 1e6 / dt, (b[G['alloc_bytes']] - a[G['alloc_bytes']]) / 1048576.0 / dt,
                (b[G['lock_contention']] - a[G['lock_contention']]) / dt, b[G['threadpool_threads']]))
    else:
        L.append('GC: no records')
    with open(path, 'w') as f:
        f.write('\n'.join(L) + '\n')


def write_metal(C, path):
    D = C.D
    L = ['Metal pipeline']
    if D.has('WIDLE'):
        S = D.col('WIDLE')
        for rs, nm in ((0, 'NoWork'), (1, 'AllBlocked')):
            v = sorted(r[S['dur_ns']] / 1e9 for r in D.L('WIDLE') if r[S['reason']] == rs)
            L.append('WIDLE %-10s n=%d total=%.1f ms p50=%s p90=%s' % (nm, len(v), sum(v) * 1000,
                                                                       fmt_us(pct(v, 50) * 1e6) if v else '-', fmt_us(pct(v, 90) * 1e6) if v else '-'))
    else:
        L.append('WIDLE: no records')
    if D.has('CPW'):
        S = D.col('CPW')
        by = defaultdict(lambda: [0, 0])
        for r in D.L('CPW'):
            by[r[S['source']]][0] += 1
            by[r[S['source']]][1] += r[S['woken']]
        L.append('CPW sources (1 GpuTick,2 RetryBlocked,3 GuestMemoryWritten,4 slice-complete wake,5 Pipeline,6 other,7 NotePendingLabel): ' +
                 ', '.join('%d: n=%d woken=%d' % (k, v[0], v[1]) for k, v in sorted(by.items())))
    else:
        L.append('CPW: no records')
    if D.has('RRW'):
        S = D.col('RRW')
        L.append('RRW n=%d total=%.2f ms' % (len(D.L('RRW')), sum(r[S['wait_ns']] for r in D.L('RRW')) / 1e6))
    else:
        L.append('RRW: no records')
    if D.has('SLICE'):
        S = D.col('SLICE')
        wall = sum(r[S['dur_ns']] for r in D.L('SLICE')) / 1e6
        cpu = sum(r[S['cpu_ns']] for r in D.L('SLICE')) / 1e6
        oc = Counter(r[S['outcome']] & 0xff for r in D.L('SLICE'))
        L.append('SLICE n=%d wall=%.1f ms cpu=%.1f ms (cpu/wall %.2f); outcomes (0 Completed,1 Progressed,2 BlockedWithoutProgress): %s; only slices that ran a submission' % (
            len(D.L('SLICE')), wall, cpu, cpu / wall if wall else 0, dict(sorted(oc.items()))))
    else:
        L.append('SLICE: no records')
    if D.has('MGPU'):
        S = D.col('MGPU')
        lc = Counter(r[S['late']] for r in D.L('MGPU'))
        L.append('MGPU n=%d sampled=%d late(1)=%d never-sampled(2, excluded from GPU timing)=%d' % (
            len(D.L('MGPU')), lc.get(0, 0), lc.get(1, 0), lc.get(2, 0)))
    else:
        L.append('MGPU: no records')
    with open(path, 'w') as f:
        f.write('\n'.join(L) + '\n')


def write_gpu_roundtrip(C, path):
    D = C.D
    rows = C.rows
    flips = C.flips
    sub = D.L('SUBMIT')
    S = D.col('SUBMIT')
    sub_ts = [r[0] for r in sub]
    qenq = {}
    if D.has('QENQ'):
        Q = D.col('QENQ')
        for r in D.L('QENQ'):
            qenq.setdefault(r[Q['submission_id']], r[0])
    sl = defaultdict(list)
    if D.has('SLICE'):
        SL = D.col('SLICE')
        for r in D.L('SLICE'):
            sl[r[SL['submission_id']]].append((r[SL['start_ts']], r[SL['start_ts']] + r[SL['dur_ns']] / 1e9))
    mcommit = {}
    if D.has('MCOMMIT'):
        M = D.col('MCOMMIT')
        for r in D.L('MCOMMIT'):
            mcommit.setdefault(r[M['submission_id']], (r[0], r[M['serial']]))
    lland = {}
    if D.has('LLAND'):
        for r in D.L('LLAND'):
            lland.setdefault(r[D.col('LLAND')['serial']], r[0])
    sfc_edges = []
    if D.has('SFC'):
        F = D.col('SFC')
        for r in D.L('SFC'):
            lab, ex = (r[F['label0']], r[F['exp0']]) if r[F['i']] == 0 else (r[F['label1']], r[F['exp1']])
            if ex and lab == ex:
                sfc_edges.append(r[0])
    mg = getattr(C, 'mgpu_by_serial', {})
    stages = ['flip', 'submit', 'qenq', 'slice_first', 'slice_last', 'mcommit', 'gpu_start', 'gpu_end', 'lland', 'label_edge', 'fence_end']
    with open(path, 'w', newline='') as f:
        cw = csv.writer(f)
        cw.writerow(['frame'] + ['d_%s_ms' % s for s in stages[1:]])
        for fi in range(1, len(rows)):
            ft = rows[fi]['t_end_s']
            ts = {'flip': ft}
            i = bisect.bisect_left(sub_ts, ft)
            sid = None
            if i < len(sub):
                ts['submit'] = sub[i][0]
                sid = sub[i][S['submission_id']]
                if sid in qenq:
                    ts['qenq'] = qenq[sid]
                if sid in sl:
                    ts['slice_first'] = min(x[0] for x in sl[sid])
                    ts['slice_last'] = max(x[1] for x in sl[sid])
                if sid in mcommit:
                    ts['mcommit'] = mcommit[sid][0]
                    ser = mcommit[sid][1]
                    if ser in mg:
                        ts['gpu_start'], ts['gpu_end'] = mg[ser]
                    if ser in lland:
                        ts['lland'] = lland[ser]
            j = bisect.bisect_left(sfc_edges, ts.get('lland', ts.get('gpu_end', ft)))
            if j < len(sfc_edges):
                ts['label_edge'] = sfc_edges[j]
            nxt = [x for x in C.fences if x[2] == fi + 1]
            if nxt:
                ts['fence_end'] = nxt[0][1]
            cw.writerow([fi] + [fnum((ts[b] - ts[a]) * 1000, 3) if a in ts and b in ts else '' for a, b in zip(stages, stages[1:])])


# --------------------------------------------------------------------------------------
# ab.txt
# --------------------------------------------------------------------------------------
def boot_ratio(on, off, n=1000, seed=12345, cap=2000):
    if not on or not off:
        return None
    rnd = random.Random(seed)
    if len(on) > cap:
        on = rnd.sample(on, cap)
    if len(off) > cap:
        off = rnd.sample(off, cap)
    mo, mf = med(on), med(off)
    if not mf:
        return None
    rs = []
    for _ in range(n):
        a = med([on[rnd.randrange(len(on))] for _ in on])
        b = med([off[rnd.randrange(len(off))] for _ in off])
        if b:
            rs.append(a / b)
    rs.sort()
    return (mo / mf, pct(rs, 2.5), pct(rs, 97.5), len(on), len(off))


def write_ab(C, rows, path):
    D = C.D
    if not C.ab:
        raise Missing('AB records (A/B was not enabled)')
    real = [r for r in rows if r['phase'] == 'realtime' and not r['ab_settle']]
    wins = defaultdict(list)
    for r in real:
        wins[(r['ab_window'], r['ab_phase'])].append(r)
    names = ('scePthreadMutexLock', 'memset', 'sceAgcCbSetShRegistersDirect')
    L = ['In-run A/B (RIP cache). Frames within the settle period (%.1fs) after a flip are excluded.' % C.settle, '']
    for key in sorted(wins, key=lambda k: (k[0], k[1])):
        g = wins[key]
        a, b = min(r['t_start_s'] for r in g), max(r['t_end_s'] for r in g)
        pd = [x for x in map(per_draw, g) if x is not None]
        fs = sorted(p.tot * 1e6 for p in C.pairs_l[bisect.bisect_right(C._pair_ts, a):bisect.bisect_right(C._pair_ts, b)])
        L.append('window %s phase=%s frames=%d t=[%.2f,%.2f]  period median=%s ms  period/draw median=%s ms  FSW p50=%s us (n=%d)' % (
            key[0], key[1], len(g), a, b, fnum(cmed(g, 'period_ms'), 2), fnum(med(pd), 4) or 'n/a', fnum(pct(fs, 50), 1) or 'n/a', len(fs)))
        if D.has('HLEA'):
            H = D.col('HLEA')
            for nm in names:
                cnt = ws = wm = ge = 0
                for r in D.rng('HLEA', a, b):
                    if D.S(r[H['import']]) == nm:
                        cnt += r[H['count']]
                        ws += r[H['wall_sum_ns']]
                        wm = max(wm, r[H['wall_max_ns']])
                        ge += r[H['n_ge_100us']]
                if cnt:
                    L.append('    %-32s calls=%d mean=%.2f us max=%.3f ms >=100us=%.2f%%' % (nm, cnt, ws / cnt / 1e3, wm / 1e6, 100.0 * ge / cnt))
        if D.has('MLA'):
            M = D.col('MLA')
            sw = defaultdict(float)
            for r in D.rng('MLA', a, b):
                if r[M['site']] in (2, 3, 6):
                    sw[r[M['site']]] += r[M['wait_sum_ns']] / 1e6
            if sw:
                L.append('    MLA wait ms: ' + ', '.join('site%d=%.2f' % kv for kv in sorted(sw.items())))
    on = [r for r in real if r['ab_phase'] == 'ON']
    off = [r for r in real if r['ab_phase'] == 'OFF']
    L.append('')
    L.append('ON/OFF ratios (median ON / median OFF, bootstrap 95%% CI, 1000 resamples), ON frames=%d OFF frames=%d' % (len(on), len(off)))
    on_p, off_p = [r['period_ms'] for r in on], [r['period_ms'] for r in off]
    on_d, off_d = [x for x in map(per_draw, on) if x is not None], [x for x in map(per_draw, off) if x is not None]
    on_f, off_f = fsw_totals_for(C, on), fsw_totals_for(C, off)
    for nm, a, b in (('period_ms', on_p, off_p), ('period_ms_per_draw', on_d, off_d), ('FSW total_ns', on_f, off_f)):
        br = boot_ratio(a, b)
        L.append('  %-20s %s' % (nm, ('ratio=%.3f CI=[%.3f, %.3f] (n_on=%d n_off=%d)' % br) if br else 'n/a (need ON and OFF samples)'))
    with open(path, 'w') as f:
        f.write('\n'.join(L) + '\n')


# --------------------------------------------------------------------------------------
# timelines
# --------------------------------------------------------------------------------------
def render_timeline(C, rows, k, path, title):
    a, b = rows[k]['t_start_s'], rows[k + 1]['t_end_s']
    step = 0.0005
    n = int((b - a) / step) + 1
    lanes = []

    def new():
        return ['.'] * n

    def paint(arr, s, e, ch, only=None):
        i0 = max(0, int((s - a) / step))
        i1 = min(n - 1, int((e - a) / step))
        for i in range(i0, i1 + 1):
            if only is None or arr[i] == only:
                arr[i] = ch
    for w in sorted(set(C.swk_by_w) | set(C.w2tid)):
        arr = new()
        for (s, e, _w, lab, job) in C.swk_by_w.get(w, ()):
            if e > a and s < b:
                paint(arr, s, e, LABEL_CHAR.get(lab, '?'))
        tid = C.w2tid.get(w)
        for pk in C.parks:
            if pk.tid == tid and pk.s_end < b and (pk.r or b) > a:
                paint(arr, pk.s_end, pk.r if pk.r else b, 'P', only='.')
        for p in C.pairs_l:
            if p.tid == tid and p.end > a and p.t < b:
                paint(arr, p.t, max(p.end, p.t + step), 'S')
        lanes.append(('W%02d' % w, arr))
    if C.D.has('SLICE') or C.D.has('WIDLE'):
        arr = new()
        if C.D.has('WIDLE'):
            S = C.D.col('WIDLE')
            for r in C.D.L('WIDLE'):
                s = r[S['start_ts']]
                if s < b and s + r[S['dur_ns']] / 1e9 > a:
                    paint(arr, s, s + r[S['dur_ns']] / 1e9, 'n' if r[S['reason']] == 0 else 'b')
        if C.D.has('SLICE'):
            S = C.D.col('SLICE')
            for r in C.D.L('SLICE'):
                s = r[S['start_ts']]
                if s < b and s + r[S['dur_ns']] / 1e9 > a:
                    paint(arr, s, s + r[S['dur_ns']] / 1e9, 's')
        lanes.append(('metal', arr))
    if getattr(C, 'mgpu_by_serial', None):
        arr = new()
        for s, e in C.mgpu_by_serial.values():
            if e > a and s < b:
                paint(arr, s, e, 'g')
        lanes.append(('gpu', arr))
    if C.srt_ts:
        arr = ['?'] * n
        for i in range(n):
            t = a + i * step
            j = bisect.bisect_right(C.srt_ts, t) - 1
            arr[i] = str(C.srt_new[j]) if j >= 0 else '?'
        lanes.append(('rt', arr))
    if C.fences:
        arr = new()
        for s, e, _ in C.fences:
            if e > a and s < b:
                paint(arr, s, e, 'F')
        lanes.append(('fence', arr))
    if C.D.has('SLK'):
        S = C.D.col('SLK')
        arr = new()
        recs = C.D.L('SLK')
        for i, r in enumerate(recs):
            e = recs[i + 1][0] if i + 1 < len(recs) else b
            if r[S['ef8']] != 0 and e > a and r[0] < b:
                paint(arr, r[0], e, 'L')
        lanes.append(('ef8', arr))
    L = [title, 'frames %d and %d, t=[%.4f, %.4f] s, 1 column = 0.5 ms, periods %.1f and %.1f ms' % (
        rows[k]['frame'], rows[k + 1]['frame'], a, b, rows[k]['period_ms'], rows[k + 1]['period_ms']),
        'legend: workers G=GameLoop T=ProcessRenderThread R=RenderJob J=SubmitJob ?=other job .=idle P=park pending S=switch; '
        'metal s=slice n=idle-nowork b=idle-blocked; gpu g=exec; rt=state digit; F=fence pending; L=[S+0xef8] held',
        'frame boundaries at columns %d and %d' % (int((rows[k]['t_end_s'] - a) / step), int((rows[k + 1]['t_end_s'] - a) / step)), '']
    W = 120
    for c0 in range(0, n, W):
        ruler = ''.join(('|' if (c0 + i) % 20 == 0 else ' ') for i in range(min(W, n - c0)))
        L.append('%-6s %s   +%.1f ms' % ('', ruler, (c0) * step * 1000))
        for nm, arr in lanes:
            L.append('%-6s %s' % (nm, ''.join(arr[c0:c0 + W])))
        L.append('')
    with open(path, 'w') as f:
        f.write('\n'.join(L) + '\n')


def write_timelines(C, rows, outdir_path):
    real = [i for i in range(len(rows) - 1) if rows[i]['phase'] == 'realtime' and rows[i + 1]['phase'] == 'realtime']
    if not real:
        raise Missing('two consecutive realtime frames')
    per = sorted(rows[i]['period_ms'] for i in real)
    for name, p in (('median', 50), ('p90', 90)):
        tgt = pct(per, p)
        k = min(real, key=lambda i: abs(rows[i]['period_ms'] - tgt))
        render_timeline(C, rows, k, outdir_path('timeline_%s.txt' % name), 'Timeline nearest %s period (%.1f ms)' % (name, tgt))


# --------------------------------------------------------------------------------------
# sanity.txt
# --------------------------------------------------------------------------------------
def build_sanity(C, rows, R):
    D = C.D
    L = []
    ok = lambda c: 'PASS' if c else 'FAIL'
    L.append('dstrace.log: %d lines, parsed in %.1fs; header: %s' % (D.nlines, getattr(D, 'parse_s', 0), ' '.join('%s=%s' % kv for kv in D.header.items())))
    if not D.header:
        L.append('WARN no "# DSTRACE" header line: timestamps are relative to the first record, freq assumed 1e9')
    src = Counter(D.schema_src.values())
    L.append('schema source: %d types from SCHEMA lines, %d from the built-in fallback lists' % (src.get('log', 0), src.get('fallback', 0)))
    fb = sorted(t for t, s in D.schema_src.items() if s == 'fallback' and D.has(t))
    if fb:
        L.append('  types parsed with fallback field lists: ' + ', '.join(fb))
    L.append('event types present (%d): %s' % (len(D.recs), ', '.join('%s=%d' % (t, len(v)) for t, v in sorted(D.recs.items()))))
    absent = sorted(t for t in FALLBACK_SCHEMA if not D.has(t))
    L.append('event types absent (%d): %s' % (len(absent), ', '.join(absent)))
    if D.unknown:
        L.append('unknown event types skipped: ' + ', '.join('%s x%d' % kv for kv in D.unknown.most_common()))
    if D.short:
        L.append('records with fewer values than the schema (zero padded): ' + ', '.join('%s x%d' % kv for kv in D.short.most_common()))
    if D.bad_lines:
        L.append('unparseable lines: %d' % D.bad_lines)
    # CLK
    if C.fits:
        for dom, f in C.fits.items():
            extra = ''
            if dom == 'tsc' and getattr(C, 'tsc_hz', 0) > 0:
                extra = '  fitted %.4g Hz vs header tsc_hz=%.4g (%s)' % (1.0 / f[0], C.tsc_hz, 'late # TSC line' if D.header.get('tsc_hz_late') else 'header')
            L.append('CLK fit %-6s: rms=%.2f us max=%.2f us slope=%.9g  %s (pass < 20 us)%s' % (dom, f[3] * 1e6, f[4] * 1e6, f[0], ok(f[4] * 1e6 < 20), extra))
    else:
        L.append('CLK fit: n/a (fewer than 2 usable CLK records)')
    # WSTAT / END
    if D.has('WSTAT'):
        W = D.col('WSTAT')
        last = D.L('WSTAT')[-1]
        wr, dr = last[W['written']], last[W['dropped']]
        frac = dr / (wr + dr) if wr + dr else 0
        L.append('WSTAT last: written=%d dropped=%d (%.4f%%) max_occupancy=%d writer_cpu=%.2fs  %s (pass <= 0.1%%)' % (
            wr, dr, 100 * frac, last[W['max_occupancy']], last[W['writer_cpu_ns']] / 1e9, ok(frac <= 0.001)))
    else:
        L.append('WSTAT: none')
    L.append('END line: %s' % (D.end if D.end else 'absent (log truncated or process killed before shutdown)'))
    if D.has('SSTAT'):
        S = D.col('SSTAT')
        p99 = max(r[S['p99_gap_ns']] for r in D.L('SSTAT'))
        L.append('SSTAT sampler: ticks/s median=%d  max p99 gap=%.3f ms  max gap=%.3f ms  avg tick cost=%.1f us  %s (pass p99 < 2 ms)' % (
            med([r[S['ticks']] for r in D.L('SSTAT')]), p99 / 1e6, max(r[S['max_gap_ns']] for r in D.L('SSTAT')) / 1e6,
            med([r[S['avg_tick_cost_ns']] for r in D.L('SSTAT')]) / 1e3, ok(p99 < 2e6)))
    else:
        L.append('SSTAT: none')
    # ROOTS
    if D.has('ROOTS'):
        S = D.col('ROOTS')
        r = D.L('ROOTS')[-1]
        for k, exp in EXPECTED_ROOTS.items():
            L.append('ROOTS %-5s = 0x%x expected 0x%x  %s' % (k, r[S[k]], exp, ok(r[S[k]] == exp)))
        L.append('ROOTS ok_bits=0x%x j=0x%x s=0x%x ctx=0x%x' % (r[S['ok_bits']], r[S['j']], r[S['s']], r[S['ctx']]))
    else:
        L.append('ROOTS: none (guest sampler roots were never resolved)')
    # frame counts
    nfl = len(C.flips)
    inc = None
    if D.has('SFC'):
        S = D.col('SFC')
        recs = D.L('SFC')
        inc = sum(1 for a, b in zip(recs, recs[1:]) if b[S['ctr']] > a[S['ctr']])
    pw = sum(w.get('guest_frames', 0) for w in C.gl['present']) if C.gl else 0
    L.append('frame counts: FLIP=%d  SFC ctr increments=%s  METAL-PRESENT guest_frames=%s' % (nfl, inc if inc is not None else 'n/a', pw if C.gl and C.gl['present'] else 'n/a'))
    for nm, v in (('SFC', inc), ('METAL-PRESENT', pw if C.gl and C.gl['present'] else None)):
        if v is not None and nfl:
            L.append('  FLIP vs %s mismatch %.1f%% (%d frames)  %s (pass <= 2%% or 1 frame)' % (
                nm, 100.0 * abs(nfl - v) / max(nfl, 1), abs(nfl - v), ok(abs(nfl - v) <= max(1, 0.02 * nfl))))
    al = C.align
    if al.get('t0') is not None:
        L.append('game.log window alignment: t0=%.3f s from %d windows, MAD=%.3f s, PERF windows placed=%d' % (al['t0'], al['n_offsets'], al['mad'], len(al['perf'])))
    else:
        L.append('game.log window alignment: unavailable (draws/uploads/writebacks columns are blank)')
    # TCPU vs PROC
    if D.has('TCPU') and D.has('PROC'):
        T = D.col('TCPU')
        P = D.col('PROC')
        pr = D.L('PROC')
        t0, t1 = pr[0][0], pr[-1][0]
        by = defaultdict(list)
        for r in D.L('TCPU'):
            by[r[T['thread_id']]].append((r[0], (r[T['user_us']] + r[T['sys_us']]) / 1e6))
        tot = 0.0
        for lst in by.values():
            inwin = [x for x in lst if t0 <= x[0] <= t1]
            if len(inwin) >= 2:
                tot += inwin[-1][1] - inwin[0][1]
        pcpu = (pr[-1][P['user_ns']] + pr[-1][P['sys_ns']] - pr[0][P['user_ns']] - pr[0][P['sys_ns']]) / 1e9
        if pcpu > 0:
            ratio = tot / pcpu
            note = ''
            if abs(ratio * 41.67 - 1) < 0.3 or abs(ratio / 41.67 - 1) < 0.3:
                note = ' (looks like a x41.67 timebase error: check hw.tbfrequency)'
            L.append('TCPU vs PROC CPU: sum TCPU=%.2f s, PROC=%.2f s, ratio=%.3f  %s (pass within 10%%)%s' % (tot, pcpu, ratio, ok(abs(ratio - 1) <= 0.10), note))
    else:
        L.append('TCPU vs PROC: n/a')
    wv = wcpu_validation(C) if D.has('WCPU') else None
    if wv:
        L.append('WCPU vs TCPU/self-CPU: ratio=%s (WCPU %.2f s vs reference %.2f s) -> %s' % (
            fnum(wv[1], 3), wv[2] / 1e6, wv[3] / 1e6, 'validated, used in workers.csv' if wv[0] else 'NOT validated, ignored'))
    else:
        L.append('WCPU: n/a')
    if C.cpupt_ok is not None:
        L.append('CPUPT monotonic per thread: %s' % ('yes (absolute clock, used)' if C.cpupt_ok else 'NO (ignored, treated as unusable)'))
    unk = sum(1 for p in C.pairs_l if p.site not in SITE_NAMES)
    L.append('FSW with RA2 not in the site table: %d of %d' % (unk, len(C.pairs_l)))
    L.append('FSW/FSJ pairing: FSW=%d FSJ=%d paired=%d' % (len(D.L('FSW')), len(D.L('FSJ')), len(D.pairs)))
    L.append('thread map: %d THR records, %d workers mapped to a thread (%s)' % (
        len(C.thr), len(C.w2tid), ','.join(str(w) for w in sorted(C.w2tid))))
    um = [l for l in (C.gl['dstrace_lines'] if C.gl else []) if re.search(r'unmatched|allow', l, re.I)]
    L.append('allow-list unmatched names (game.log): ' + ('; '.join(um) if um else 'none reported'))
    L.append('')
    L.append('Assumptions: HLE/FSW cpu_ns are per-call deltas; WCPU packs two u32 per field (low first); GF Blocked/Ready are keyed by guest handle.')
    return L


# --------------------------------------------------------------------------------------
# Orchestration
# --------------------------------------------------------------------------------------
def locate(arg):
    """Return (run_dir, dstrace_path_or_None, game_log_or_None)."""
    if os.path.isfile(arg):
        d = os.path.dirname(os.path.abspath(arg))
        gl = os.path.join(d, 'game.log')
        return d, arg, gl if os.path.exists(gl) else None
    d = os.path.abspath(arg)
    gl = os.path.join(d, 'game.log')
    gl = gl if os.path.exists(gl) else None
    ds = os.path.join(d, 'dstrace.log')
    if not os.path.exists(ds):
        ds = None
    return d, ds, gl


def run_analysis(arg, out=None, settle=1.0, log=print):
    run_dir, ds, gl_path = locate(arg)
    g = parse_gamelog(gl_path)
    if ds is None:
        alt = gamelog_dstrace_path(g)
        if alt:
            cand = alt if os.path.isabs(alt) else os.path.join(run_dir, alt)
            if os.path.exists(cand):
                ds = cand
    outdir = out or os.path.join(run_dir, 'dstrace-analysis')
    os.makedirs(outdir, exist_ok=True)
    R = Report(outdir)
    D = Data()
    C = None
    rows = None
    log('run dir: %s' % run_dir)
    log('dstrace.log: %s' % (ds or 'NOT FOUND'))
    log('game.log: %s' % (gl_path or 'NOT FOUND'))
    if gl_path:
        R.write('gamelog.txt', gamelog_text(g))
        R.ran.append('gamelog.txt')
    else:
        R.skip('gamelog.txt', 'no game.log in run dir')
    if ds is None:
        why = 'dstrace.log not found (run without SHARPEMU_DSTRACE=1, or the file was not copied)'
        R.skip('all dstrace sections (frames.csv, frames_summary.txt, workers.csv, wake_latency.txt, fiber.txt, memlock.txt, '
               'hostcpu.txt, gpu_roundtrip.csv, metal.txt, ab.txt, timelines)', why)
        R.write('sanity.txt', ['dstrace.log not found: only the game.log summary (gamelog.txt) was produced.'] + (gamelog_text(g) if gl_path else ['game.log not found either.']))
        _finish(R, log, None, D, why)
        return R, None, None
    parse_log(ds, D)
    if not D.recs:
        R.skip('all sections', 'dstrace.log contains no parseable records')
        R.write('sanity.txt', ['dstrace.log has no parseable records (lines=%d, bad=%d, unknown types=%s)' % (D.nlines, D.bad_lines, dict(D.unknown))])
        _finish(R, log, None, D, 'empty log')
        return R, None, None
    try:
        C = Context_make(D, R, g, settle)
    except Exception:
        R.skip('all sections', 'internal error while building derived tables (see skipped.txt)')
        R.write('sanity.txt', ['internal error building derived tables'])
        _finish(R, log, None, D, 'context failure')
        return R, None, None

    def do_frames():
        rows_ = build_frames(C)
        write_frames_csv(rows_, R.path('frames.csv'))
    if run_section(R, D, 'frames.csv', do_frames, ('FLIP',)):
        rows = C.rows
        ver = []

        def do_summary():
            ver.extend(verdicts(C, rows))
            write_frames_summary(C, rows, R.path('frames_summary.txt'), ver)
        run_section(R, D, 'frames_summary.txt', do_summary)
        C.verdicts = ver
    else:
        for s in ('frames_summary.txt', 'gpu_roundtrip.csv', 'ab.txt', 'timeline_median.txt', 'timeline_p90.txt'):
            R.skip(s, 'needs frames (no FLIP records)')
    run_section(R, D, 'workers.csv', lambda: write_workers_csv(C, R.path('workers.csv')), ('SWK',))
    run_section(R, D, 'wake_latency.txt', lambda: write_wake_latency(C, R.path('wake_latency.txt')))
    run_section(R, D, 'fiber.txt', lambda: write_fiber(C, R.path('fiber.txt')), ('FSW', 'FSJ'))
    run_section(R, D, 'memlock.txt', lambda: write_memlock(C, R.path('memlock.txt')), ('MLA',))
    run_section(R, D, 'hostcpu.txt', lambda: write_hostcpu(C, R.path('hostcpu.txt')))
    run_section(R, D, 'metal.txt', lambda: write_metal(C, R.path('metal.txt')))
    if rows is not None:
        run_section(R, D, 'gpu_roundtrip.csv', lambda: write_gpu_roundtrip(C, R.path('gpu_roundtrip.csv')), ('SUBMIT',))
        run_section(R, D, 'ab.txt', lambda: write_ab(C, rows, R.path('ab.txt')), ('AB',))
        run_section(R, D, 'timeline_median.txt/timeline_p90.txt', lambda: write_timelines(C, rows, R.path))
    sl = []

    def do_sanity():
        sl.extend(build_sanity(C, rows, R))
        R.write('sanity.txt', sl)
    run_section(R, D, 'sanity.txt', do_sanity)
    _finish(R, log, C, D, None)
    return R, C, rows


def Context_make(D, R, g, settle):
    C = Ctx(D, R, g, settle)
    try:
        C.build_fiber()
        C.build_srt()
        C.build_swk()
        C.build_cpu()
        C.build_locks()
        C.build_gf()
        C._pair_ts = [p.t for p in C.pairs_l]
        C.align = align_windows(C)
    except Exception:
        R.failed.append(('context build', traceback.format_exc()))
        raise
    C.rows = []
    C.verdicts = []
    return C


def _finish(R, log, C, D, why):
    lines = []
    if R.skipped:
        lines.append('SKIPPED SECTIONS:')
        lines += ['  %-40s %s' % s for s in R.skipped]
    if R.notes:
        lines.append('NOTES:')
        lines += ['  ' + n for n in R.notes]
    if D is not None and D.unknown:
        lines.append('UNKNOWN EVENT TYPES (skipped): ' + ', '.join('%s x%d' % kv for kv in D.unknown.most_common()))
    for name, tb in R.failed:
        lines.append('FAILED SECTION %s:' % name)
        lines += ['  ' + l for l in tb.rstrip().splitlines()]
    R.write('skipped.txt', lines or ['nothing skipped'])
    log('outputs in %s' % R.outdir)
    log('sections written: %s' % (', '.join(R.ran) or 'none'))
    for l in lines:
        log(l)
    if C is not None and getattr(C, 'verdicts', None):
        log('VERDICT TABLE:')
        for h, st, ev in C.verdicts:
            log('  %-4s %-22s %s' % (h, st, ev))


# --------------------------------------------------------------------------------------
# Self-test: synthetic dstrace.log + game.log
# --------------------------------------------------------------------------------------
_ST_START = 5_000_000_000_000
class Synth:
    """Writes a log in the sink's exact format (header, CONFIG, late TSC line, SCHEMA lines with :x/:s)."""

    def __init__(self):
        self.lines = []
        self.seq = 0
        self.strs = {}
        self.schema = {t: list(v) for t, v in FALLBACK_SCHEMA.items()}
        self.hexn = {t: {x.split(':')[0] for x in v.split() if x.endswith(':x')} for t, v in SINK_SCHEMA.items()}
        self.no_schema = {'SPOOL'}                    # emitted without a SCHEMA line (fallback path)
        self.counts = Counter()

    def sid(self, s):
        if s not in self.strs:
            self.strs[s] = len(self.strs) + 1
        return self.strs[s]

    def E(self, us, typ, _tid=1, **kw):
        names = self.schema[typ]
        vals = []
        for n in names:
            v = kw.get(n, 0)
            if isinstance(v, str):
                v = self.sid(v)
            if n in TS_FIELDS.get(typ, ()):
                v = _ST_START + int(v) * 1000
            vals.append(('0x%x' % v) if (n in self.hexn[typ] and v >= 0) else str(v))
        self.seq += 1
        self.counts[typ] += 1
        ts = _ST_START + int(us) * 1000
        self.lines.append((ts, self.seq, '%d %s %d %s' % (ts, typ, _tid, ' '.join(vals))))

    def text(self):
        out = ['# DSTRACE 1 freq=1000000000 pid=4242 start_ts=%d start_unix_ms=1760000000000 tsc_hz=0 tb_hz=24000000 rev=selftest' % _ST_START,
               '# CONFIG groups=all arm_s=30 flush_s=2 ring_log2=18 sampler_hz=1000 wcpu_hz=1000 hle_slow_us=1000 hle_cpu_every=64 mlw_us=50 '
               'ab_ripcache=10 ab_start_s=70 raw_reads=0']
        for t, fields in SINK_SCHEMA.items():
            if t not in self.no_schema:
                out.append('# SCHEMA %s %s' % (t, fields))
        out.append('# FUTURE some unknown comment line the parser must ignore')
        out.append('# TSC tsc_hz=3000000000')
        for s, i in self.strs.items():
            out.append('%d STR 1 %d %s' % (_ST_START, i, s.replace('\\', '\\\\').replace('\n', '\\n').replace('\r', '\\r')))
        self.sid_special = self.sid('line\r\nwith \\ backslash')
        out.append('%d STR 1 %d %s' % (_ST_START, self.sid_special, 'line\\r\\nwith \\\\ backslash'))
        out.append('this line is garbage')
        for _, _, l in sorted(self.lines):
            out.append(l)
        out.append('%d ZZZ 5 1 2 3' % (_ST_START + 10_500_000_000))
        out.append('# END written=%d dropped=0' % len(self.lines))
        return '\n'.join(out) + '\n'


ST_PARAMS = [(20, 10, 50, 10, 10), (10, 20, 50, 10, 10), (10, 10, 60, 10, 10), (20, 10, 50, 10, 10)]   # ms: s0 before kick, s3, s2, s1, s0 after
ST_FENCE_MS = [12, 14, 40, 12]
RJ_ADDR, GL_ADDR, SJ_ADDR = 0x800a793c0, 0x800bc9990, 0x800a7dda0


def gen_synth(dirpath):
    S = Synth()
    E = S.E
    J_RJ, J_GL, J_SJ = 0x7000100, 0x7000300, 0x7000400
    # threads
    workers = [(100, 0, 5000), (103, 3, 5003), (105, 5, 5005)]
    for tid, w, thid in workers:
        E(1_000_000, 'THR', tid, kind=1, port=900 + w, thread_id=thid, guest=0x7F00000 + w, name='BPE JobWorkerThread CPU%d' % w, worker_idx=w)
        E(1_000_001, 'TATTR', tid, qos_apply=1, qos_class=0x19, relpri=0, policy=1, sched_prio=47, managed_priority=2)
    E(1_000_000, 'THR', 200, kind=2, port=950, thread_id=6000, guest=0, name='MetalV2Worker', worker_idx=0xffff)
    E(1_000_001, 'TATTR', 200, qos_apply=1, qos_class=0x21, relpri=0, policy=1, sched_prio=47, managed_priority=3)
    # clocks (exactly linear: residual 0)
    for k in range(8):
        us = 1_000_000 + 2_000_000 * k
        E(us, 'CLK', 1, sw_mid=_ST_START + us * 1000, tsc=1_000_000_000_000 + 3 * us * 1000, mat_ns=us * 1000 + 999,
          ca_ns=7_000_000_000_000 + us * 1000, gap_ns=800)
    E(9_000_000, 'AB', 1, phase=0, window_idx=0)
    E(10_220_000, 'AB', 1, phase=1, window_idx=1)
    # baselines
    E(9_500_000, 'GF', 100, kind=1, guest=0x7F00000, name='BPE JobWorkerThread CPU0', reason='boot', wake='w', nid='nid', arg0=0, arg1=0, deadline=0)
    E(9_950_000, 'SRT', 1, old=1, new=0, rt=0x7100000, wait_accum=0)
    E(9_950_000, 'SFC', 1, i=0, ctr=0, label0=0, label1=0, exp0=0, exp1=0, lp0=0x800A0, lp1=0)
    E(9_950_000, 'SWK', 1, w=0, cur=J_GL, fiber=0x7200000, q10=GL_ADDR, q08=0x11, flags_aff=0x1fff, last_job_tsc=1, mailbox=0)
    E(9_950_000, 'SWK', 1, w=3, cur=0)
    E(9_950_000, 'SWK', 1, w=5, cur=0)
    E(9_950_000, 'SLK', 1, f78=0, ef8=0)
    ctr_us = {}                                   # cumulative cpu (us) of worker 3 at ms steps
    rj_windows = []
    for f in range(1, 5):
        a = 10_000_000 + 100_000 * (f - 1)
        s0b, s3, s2, s1, s0a = ST_PARAMS[f - 1]
        t = a + s0b * 1000
        E(t, 'SRT', 1, old=0, new=3, rt=0x7100000, wait_accum=1)
        t32 = t + s3 * 1000
        E(t32, 'SRT', 1, old=3, new=2, rt=0x7100000, wait_accum=2)
        E(t32 + s2 * 1000, 'SRT', 1, old=2, new=1, rt=0x7100000, wait_accum=3)
        E(t32 + (s2 + s1) * 1000, 'SRT', 1, old=1, new=0, rt=0x7100000, wait_accum=4)
        rjs, rje = t32 + 5_000, t32 + 35_000
        rj_windows.append((rjs, rje))
        E(rjs, 'SWK', 1, w=3, cur=J_RJ, fiber=0x7200003, q08=RJ_ADDR, q10=0, q58=0, q60=0, flags_aff=0x8, last_job_tsc=2, mailbox=0)
        E(rje, 'SWK', 1, w=3, cur=0)
        E(a + 80_000, 'SWK', 1, w=5, cur=J_SJ, fiber=0x7200005, q10=SJ_ADDR, flags_aff=0x20)
        E(a + 90_000, 'SWK', 1, w=5, cur=0)
        # fence: ctr increment at RJ start, label lands fe ms later
        fe = ST_FENCE_MS[f - 1]
        E(rjs, 'SFC', 1, i=0, ctr=f, label0=0x100 * (f - 1), exp0=0x100 * f, lp0=0x800A0)
        E(rjs + fe * 1000, 'SFC', 1, i=0, ctr=f, label0=0x100 * f, exp0=0x100 * f, lp0=0x800A0)
        E(rjs - 1_000, 'LENC', 1, serial=f, addr=0x800A0, value=0x100 * f, deferred=0, kind=1)
        edge = rjs + fe * 1000
        E(edge, 'MGPU', 200, serial=f, kstart_ns=1, kend_ns=2, gstart_ns=(edge - 11_000) * 1000 + 999,
          gend_ns=(edge - 1_000) * 1000 + 999, late=0)
        E(edge, 'MGPU', 200, serial=100 + f, kstart_ns=1, kend_ns=2, gstart_ns=(edge - 6_000) * 1000 + 999,
          gend_ns=(edge + 9_000) * 1000 + 999, late=1)
        E(edge, 'MGPU', 200, serial=200 + f, kstart_ns=0, kend_ns=0, gstart_ns=0, gend_ns=0, late=2)
        E(edge + 500, 'LLAND', 200, serial=f)
        E(a + 70_000, 'MCOMMIT', 200, serial=f, cause=1, pending_labels=2, submission_id=f * 10)
        # RenderJob park / resume
        s_ = rjs + 10_000
        E(s_ + 100, 'FSW', 103, enter_ts=s_, total_ns=100_000, gate_wait_ns=0, body_ns=20_000, prep_ns=30_000, probe_code=2 | (1 << 8) | (1 << 24),
          probe_wlock_wait_ns=0, probe_mprotect_ns=0, cpu_ns=50_000, **{'from': 0x7200003, 'to': 0x7200009}, ra2=SITE_SUSPEND, ra1=0x874080)
        E(s_ + 101, 'FSJ', 103, job=J_RJ, counter=0x7000200, value=-1, target=0, cond_flags=0, aff_grp=0x8, q08=RJ_ADDR,
          ra3=WAIT_FOR_COUNTER, ra4=RT_WREQ, ra5=MAIN_LOOP)
        csat = s_ + 8_000
        E(csat, 'CSAT', 1, job=J_RJ, counter=0x7000200, value=0, target=0, cond=0, suspend_ts=s_, eligible_idle=2, idle_mask=0x1ff0)
        r_ = csat + 3_000
        E(r_ + 200, 'FSW', 103, enter_ts=r_, total_ns=200_000, gate_wait_ns=40_000, body_ns=50_000, prep_ns=60_000, probe_code=2 | (1 << 8),
          probe_wlock_wait_ns=300_000, probe_mprotect_ns=100_000, cpu_ns=70_000, **{'from': 0x7200009, 'to': 0x7200003}, ra2=(1 << 63) | SITE_RESUME, ra1=0x874080)
        E(r_ + 201, 'FSJ', 103, job=J_RJ, counter=0, q08=RJ_ADDR, ra3=0, ra4=0, ra5=0)
        # GameLoop park / resume
        s_ = a + 30_000
        E(s_ + 100, 'FSW', 100, enter_ts=s_, total_ns=100_000, probe_code=0, cpu_ns=50_000, **{'from': 0x7200000, 'to': 0x7200001}, ra2=SITE_SUSPEND, ra1=0x874080)
        E(s_ + 101, 'FSJ', 100, job=J_GL, counter=0x7000500, value=-1, q10=GL_ADDR, ra3=WAIT_FOR_COUNTER, ra4=GL_WRT, ra5=MAIN_LOOP)
        E(a + 58_000, 'CSAT', 1, job=J_GL, counter=0x7000500, value=0, target=0, cond=0, suspend_ts=s_, eligible_idle=0, idle_mask=0)
        r_ = a + 60_000
        E(r_ + 500, 'FSW', 100, enter_ts=r_, total_ns=500_000, probe_code=2, probe_wlock_wait_ns=200_000, probe_mprotect_ns=50_000, cpu_ns=150_000,
          **{'from': 0x7200001, 'to': 0x7200000}, ra2=(1 << 63) | SITE_RESUME, ra1=0x874080)
        E(r_ + 501, 'FSJ', 100, job=J_GL, q10=GL_ADDR)
        # SubmitJob dependency park
        s_ = a + 82_000
        E(s_ + 100, 'FSW', 105, enter_ts=s_, total_ns=100_000, cpu_ns=10_000, **{'from': 0x7200005, 'to': 0x7200006}, ra2=SITE_SUSPEND, ra1=0x874080)
        E(s_ + 101, 'FSJ', 105, job=J_SJ, counter=0x7000600, q10=SJ_ADDR, ra3=WAIT_FOR_COUNTER, ra4=SJ_DEP, ra5=MAIN_LOOP)
        E(s_ + 2_000, 'CSAT', 1, job=J_SJ, counter=0x7000600, suspend_ts=s_, eligible_idle=1, idle_mask=0x20)
        E(s_ + 3_100, 'FSW', 105, enter_ts=s_ + 3_000, total_ns=100_000, cpu_ns=10_000, **{'from': 0x7200006, 'to': 0x7200005}, ra2=(1 << 63) | SITE_RESUME, ra1=0x874080)
        E(s_ + 3_101, 'FSJ', 105, job=J_SJ, q10=SJ_ADDR)
        # lock waits, GF blocking
        E(rjs + 20_000, 'MLW', 103, site=2, wait_ns=500_000, hold_ns=0, **{'import': 'scePthreadMutexLock'}, thread_class=1, in_rip_probe=0)
        E(rjs + 20_000, 'GF', 103, kind=0, guest=0x7F00003, name='BPE JobWorkerThread CPU3', reason='pthread_mutex_lock', wake='m', nid='nid', arg0=1, arg1=2, deadline=0)
        E(rjs + 22_000, 'GF', 104, kind=1, guest=0x7F00003, name='BPE JobWorkerThread CPU3', reason='', wake='m', nid='', arg0=0, arg1=0, deadline=0)
        E(rjs + 22_050, 'GF', 103, kind=4, guest=0x7F00003, name='BPE JobWorkerThread CPU3', reason='', wake='', nid='', arg0=0, arg1=0, deadline=0)
        if f == 2:
            E(a + 50_000, 'MXR', 103, mutex=0x7300000, guest=0x7F00003, wait_ns=6_000_000)
        # metal / gpu
        E(a + 80_000 + 5_000, 'SLICE', 200, start_ts=a + 80_000, dur_ns=5_000_000, queue=0, submission_id=f * 10, packets=10, outcome=0, wait_addr=0, cpu_ns=3_000_000)
        E(a + 91_000, 'SLICE', 200, start_ts=a + 86_000, dur_ns=5_000_000, queue=0, submission_id=f * 10 + 1, packets=10, outcome=1, wait_addr=0x1000, cpu_ns=3_000_000)
        E(a + 74_000, 'WIDLE', 200, start_ts=a + 70_000, dur_ns=4_000_000, reason=0, outstanding=0, sample_addr=0, oldest_age_us=0, cpu_ns=1000)
        E(a + 81_000, 'WIDLE', 200, start_ts=a + 75_000, dur_ns=6_000_000, reason=1, outstanding=3, sample_addr=0x1000, oldest_age_us=500, cpu_ns=1000)
        E(a + 50_000, 'RRW', 200, waited_serial=f, committed_serial=f, completed_serial=f - 1, wait_ns=1_500_000, reason=1, addr=0x2000)
        E(a + 40_000, 'CPW', 200, source=2, woken=3, oldest_blocked_ns=2_000_000)
        E(a + 41_000, 'CPW', 200, source=1, woken=1, oldest_blocked_ns=100_000)
        E(a + 90_000, 'HLE', 100, enter_ts=a + 90_000, dur_ns=2_000_000, cpu_ns=1_000_000, **{'import': 'sceAgcDriverSubmitDcb'}, rdi=1, rsi=2, rdx=3, ra1=0x874080, ra2=0x800a1000, outcome=0, rax=0)
        E(a + 92_000, 'HLE', 100, enter_ts=a + 92_000, dur_ns=500_000, cpu_ns=0, **{'import': 'sceAgcDcbSetFlip'}, ra1=0x874080)
        E(a + 93_000, 'HLE', 100, enter_ts=a + 93_000, dur_ns=1_000_000, cpu_ns=0, **{'import': 'sceAgcSuspendPoint'}, ra1=0x874080)
        # submit side of the round trip
        b = a + 100_000
        E(b + 2_000, 'SUBMIT', 100, rdi=0x1, dwords=100, gate_wait_ns=0, total_ns=50_000, submission_id=f * 10, ra2=0x800a2000)
        E(b + 3_000, 'QENQ', 100, queue=0, submission_id=f * 10, kind=1, dwords=100)
        E(a + 99_000, 'SUSP', 100, wait_ns=1000, boundaries_pending=0)
        E(b + 4_000, 'FLIPDONE', 1, request_id=f, buffer=0)
        E(b + 5_000, 'PRESENT', 200, request_id=f)
    for k in range(5):
        E(10_000_000 + 100_000 * k, 'FLIP', 100, seq=k, rdi=1, rsi=2, rdx=3, rcx=4, r8=5, ra1=0x874080, ra2=0x800a3000)
    # cpu clocks for worker thread 5003: 0.6 cpu per wall inside RenderJob windows, sampled at 1 ms
    cum, cpupt = 0, []
    for ms in range(9_900, 10_501):
        us = ms * 1000
        E(us, 'TCPU', 1, thread_id=5003, user_us=cum, sys_us=0, cpu_usage=500, run_state=1, flags=0, name='BPE JobWorkerThread CPU3', port=903)
        inside = any(s <= us < e for s, e in rj_windows)
        packed = (600 << 32) if inside else 0
        E(us, 'WCPU', 1, d2_3=packed, running_mask=0x8 if inside else 0)
        if inside:
            cum += 600
    for s, e in rj_windows:
        pass
    # CPUPT: absolute thread cpu clock, consistent with TCPU (recomputed)
    cum = 0
    for ms in range(9_900, 10_501):
        us = ms * 1000
        if any(s == us or e == us for s, e in rj_windows):
            E(us, 'CPUPT', 103, cpu_ns=cum * 1000)
        if any(s <= us < e for s, e in rj_windows):
            cum += 600
    for us in range(9_900_000, 10_501_000, 100_000):
        E(us, 'TCPU', 1, thread_id=5000, user_us=100, sys_us=0, cpu_usage=0, run_state=2, flags=0, name='BPE JobWorkerThread CPU0', port=900)
        E(us, 'TCPU', 1, thread_id=5005, user_us=50, sys_us=0, cpu_usage=0, run_state=2, flags=0, name='BPE JobWorkerThread CPU5', port=905)
    E(11_000_000, 'MLA', 1, site=2, tid=103, count=100, slow_count=8, wait_sum_ns=2_000_000, wait_max_ns=500_000, hold_sum_ns=3_000_000, hold_max_ns=900_000)
    E(11_000_000, 'MLA', 1, site=3, tid=103, count=500, slow_count=1, wait_sum_ns=10_000, wait_max_ns=5_000, hold_sum_ns=2_000, hold_max_ns=900)
    E(11_000_000, 'MLA', 1, site=6, tid=100, count=50, slow_count=0, wait_sum_ns=0, wait_max_ns=0, hold_sum_ns=1_000, hold_max_ns=100)
    E(11_000_000, 'MLA', 1, site=9, tid=100, count=1000, slow_count=100, wait_sum_ns=0, wait_max_ns=0, hold_sum_ns=0, hold_max_ns=0)
    for us, mult in ((10_150_000, 1), (10_350_000, 1)):
        for nm in ('scePthreadMutexLock', 'memset', 'sceAgcCbSetShRegistersDirect'):
            E(us, 'HLEA', 1, **{'import': nm}, count=5000, wall_sum_ns=10_000_000, wall_max_ns=800_000, n_ge_100us=20, n_ge_1ms=0, n_ge_10ms=0,
              cpu_sum_ns=5_000_000, cpu_n=78, wall_sum_of_cpu_sampled_ns=156_000)
    # host-side process records
    for k, us in enumerate((9_000_000, 10_000_000, 11_000_000)):
        E(us, 'PROC', 1, user_ns=(72_000_000 if k == 2 else 0), sys_ns=0, p_user_ns=(60_000_000 if k == 2 else 0), p_sys_ns=0,
          runnable_ns=k * 400_000_000, instr=1000 * k, cycles=2000 * k, intr_wkups=100 * k, idle_wkups=50 * k, footprint=3 << 30, load1x100=150, nthreads=40)
        E(us, 'GC', 1, gen0=10 * k, gen1=k, gen2=0, pause_total_ns=1_000_000 * k, alloc_bytes=10_000_000 * k, lock_contention=5 * k, threadpool_threads=4)
        E(us, 'SSTAT', 1, ticks=2000, max_gap_ns=1_500_000, p99_gap_ns=800_000, avg_tick_cost_ns=7_000)
        E(us, 'WSTAT', 1, written=1000 * (k + 1), dropped=0, max_occupancy=100, writer_cpu_ns=10_000_000)
    for us in (10_000_000, 15_000_000):
        E(us, 'OTHER', 1, others_cpu_ns=300_000_000, pid1=77, cpu1=200_000_000, name1='WindowServer', pid2=78, cpu2=50_000_000, name2='mds', pid3=0, cpu3=0, name3='')
    E(10_000_000, 'ROOTS', 1, j=0x8030fd5b0, s=0x7400000, g=0x7410000, s48=0x1fff, s4c=0xfffff5fe, sb90=13, rtpp=0x803181450, ctx=0x8031810c0,
      disp=0x803123450, ok_bits=0x3f)
    E(10_000_000, 'SFM', 1, mode=0, r34=0, r2c=0, hz=0x42700000)
    E(10_000_000, 'SPOOL', 1, ec8=1, ef0=2, ed0=3)
    E(10_000_000, 'SGRP', 1, g=2, sum_count=3, waiting_full=0, external_full=0)
    E(10_000_000, 'SCNT', 1, c2_0_3=1, c3_0_3=2)
    E(10_000_000, 'JD', 1, key=0x7000100, chunk=0, q0=RJ_ADDR, q1=1, q2=2)
    E(10_000_000, 'SLK', 1, f78=0, ef8=1)
    E(10_050_000, 'SLK', 1, f78=0, ef8=0)
    for typ, kw in (('BRA', dict(guest=0x7F00003, nid='nid', ra1=1, ra2=2, ra3=3)),
                    ('WAKE', dict(enter_ts=10_000_000, gate_wait_ns=100, total_ns=2000, wake='k', scanned=5, woken=1, first_woken=0x7F00003, pumped=0)),
                    ('PUMPA', dict(calls=100, cas_lost=1, cap_hit=0, claimed=50, wake_expired_ns=1000)),
                    ('USLA', dict(ra1=0x874080, count=100, req_us_sum=500, actual_ns_sum=900_000, pump_ns_sum=100_000, sleep0_count=3)),
                    ('USLP', dict(ra1=0x874080, ra2=0x800a1000, req_us=1000, actual_ns=300_000, pump_ns=1000, branch=2)),
                    ('MXQ', dict(mutex=0x7300000, waiter=0x7F00003, owner=0x7F00000, qlen=2, spin_ns=1000, outcome=1, ra1=0x874080)),
                    ('MXH', dict(mutex=0x7300000, **{'from': 0x7F00000, 'to': 0x7F00003}, to_parked=1, qlen_after=1)),
                    ('EVF', dict(handle=0x1, bits=0xff, mode=1, path=1, total_ns=1000, loop_iters=0, ra1=0x874080)),
                    ('EVS', dict(handle=0x1, bits=0xff, woken=1)),
                    ('APR', dict(ra1=1, ra2=2, n_cmds=3, total_ns=1000, pread_ns=500, waitaddr_ns=200, waitaddr_iters=1, bytes=4096)),
                    ('CPS', dict(queue=0, kind=1, addr=0x1000, ref=1, mask=0xff, func=2, cur=0)),
                    ):
        E(10_000_000, typ, 1, **kw)
    # game.log
    gl = ['[LOADER][INFO] hello',
          '[DSTRACE] output=%s' % os.path.join(dirpath, 'dstrace.log'),
          '[DSTRACE] allow-list unmatched: sceNotAnImport']
    for fr in (0, 0, 0, 5, 0):
        gl.append('[METAL-PRESENT] guest_frames=%d guest_fps=%.2f host_presents=100 host_hz=50.00 seconds=2.000 published=%d' % (fr, fr / 2.0, fr))
        if fr == 5:
            gl.append('[METAL-V2][PERF] seconds=2.00 submissions=50 guest_submissions=20 slices=50 draws=120 passes=4 gpu_busy_ms=500.0 idle_ms=1 uploads=20 writebacks=10')
    gl += ['[PERF][GUEST_SCHED] window=5.0s active_threads=3',
           "[PERF][GUEST_SCHED] name='BPE JobWorkerThread CPU3' handle=0x00007F923085B000 ready=4 claimed=3 scheduled=3 runs=3 blocked=2 resumed=2 blocked_ms=0.25 ready_ms=0.03 dispatch_ms=1.53 run_ms=3936.88 run_max_ms=2776.974 last_block=pthread_mutex_lock"]
    with open(os.path.join(dirpath, 'dstrace.log'), 'w') as f:
        f.write(S.text())
    with open(os.path.join(dirpath, 'game.log'), 'w') as f:
        f.write('\n'.join(gl) + '\n')
    return S


def _approx(name, got, want, tol=0.02):
    assert got is not None, '%s: got None, want %s' % (name, want)
    assert abs(got - want) <= tol, '%s: got %s want %s' % (name, got, want)


def selftest(keep=False):
    tmp = tempfile.mkdtemp(prefix='dstrace-selftest-')
    quiet = []
    try:
        d = os.path.join(tmp, 'full')
        os.makedirs(d)
        S = gen_synth(d)
        R, C, rows = run_analysis(d, settle=0.0, log=quiet.append)
        assert not R.failed, 'internal errors: %s' % R.failed
        assert not R.skipped, 'sections skipped on full data: %s' % R.skipped
        types = set(FALLBACK_SCHEMA) - {'JD'} | {'JD'}
        missing = [t for t in FALLBACK_SCHEMA if S.counts[t] == 0]
        assert not missing, 'generator does not cover: %s' % missing
        assert C.D.unknown.get('ZZZ') == 1 and C.D.bad_lines >= 1
        assert C.D.schema['SLK'] == ['ef8', 'f78'] and C.D.schema_src['SLK'] == 'log'
        assert C.D.schema['WSTAT'][2] == 'max_occupancy' and C.D.schema['THR'][4] == 'name'      # ':s' / ':x' suffixes stripped
        assert C.D.header['tsc_hz'] == '3000000000' and C.D.config.get('arm_s') == '30', (C.D.header, C.D.config)
        assert C.D.strs[S.sid_special] == 'line\r\nwith \\ backslash', repr(C.D.strs[S.sid_special])
        assert abs(1.0 / C.fits['tsc'][0] - 3e9) < 1e3
        assert C.D.schema_src['SPOOL'] == 'fallback'
        for fn in ('frames.csv', 'frames_summary.txt', 'workers.csv', 'wake_latency.txt', 'fiber.txt', 'memlock.txt', 'hostcpu.txt',
                   'gpu_roundtrip.csv', 'metal.txt', 'ab.txt', 'timeline_median.txt', 'timeline_p90.txt', 'sanity.txt', 'gamelog.txt', 'skipped.txt'):
            assert os.path.getsize(os.path.join(d, 'dstrace-analysis', fn)) > 0, fn
        assert len(rows) == 5 and rows[0]['phase'] == 'loading'
        want_crit = ['update', 'rj_compute', 'rj_fence', 'update']
        for f in range(1, 5):
            r = rows[f]
            s0b, s3, s2, s1, s0a = ST_PARAMS[f - 1]
            fe = ST_FENCE_MS[f - 1]
            ex = {'period_ms': 100, 't_state0_ms': s0b + s0a, 't_state3_ms': s3, 't_state2_ms': s2, 't_state1_ms': s1, 'rt_cycles': 1,
                  'gl_wait_ms': 30.5, 'gl_resume_hop_ms': 2.5, 'rj_queue_ms': 5, 'rj_run_ms': 30, 'rj_wall_ms': 30, 'rj_parks': 1,
                  'rj_park_ms': 11.2, 'rj_dep_wait_ms': 8, 'rj_hop_ms': 3.2, 'rj_fence_ms': min(fe, 30), 'label_land_ms': 1.0,
                  'rj_cpu_ms': 18, 'rj_lockwait_ms': 0.5, 'rj_blocked_ms': 2, 'rj_offcpu_resid_ms': 9.5,
                  'sj_count': 1, 'sj_dep_wait_ms': 3.1, 'sj_hop_ms': 1.1, 'submit_and_flip_ms': 3.5,
                  'chain_switches': 6, 'chain_switch_wall_ms': 1.1, 'chain_switch_cpu_ms': 0.34, 'chain_probe_wlock_ms': 0.5,
                  'chain_probe_mprot_ms': 0.15, 'chain_gate_wait_ms': 0.04, 'mutex_block_ms': 6 if f == 2 else 0,
                  'mutex_convoy_max_ms': 6 if f == 2 else 0, 'metal_busy_ms': 10, 'metal_cpu_ms': 6, 'idle_nowork_ms': 4,
                  'idle_allblocked_ms': 6, 'rrwait_ms': 1.5, 'gpu_exec_ms': 20, 'cp_wakes_retry': 3, 'draws': 6.0, 'uploads': 1.0, 'writebacks': 0.5}
            for k, v in ex.items():
                _approx('frame %d %s' % (f, k), r[k], v)
            assert r['critical'] == want_crit[f - 1], 'frame %d critical %s want %s' % (f, r['critical'], want_crit[f - 1])
            assert r['phase'] == 'realtime'
            assert r['ab_phase'] == ('OFF' if f <= 2 else 'ON'), (f, r['ab_phase'])
        # csv round trip
        with open(os.path.join(d, 'dstrace-analysis', 'frames.csv')) as fh:
            rd = list(csv.DictReader(fh))
        assert len(rd) == 5 and abs(float(rd[1]['rj_hop_ms']) - 3.2) < 0.02 and rd[3]['critical'] == 'rj_fence'
        ver = {h: (st, ev) for h, st, ev in C.verdicts}
        assert ver['H2'][0] == 'CONFIRMED', ver['H2']
        assert ver['H3'][0] == 'NOT CONFIRMED', ver['H3']
        assert ver['H6'][0] in ('CONFIRMED', 'NOT CONFIRMED') and 'MH5' in ver and 'M5' in ver and 'H11' in ver
        for fit in C.fits.values():
            assert fit[4] * 1e6 < 1.0, 'clock fit residual'
        ab = open(os.path.join(d, 'dstrace-analysis', 'ab.txt')).read()
        assert 'ratio=1.000' in ab, ab[-600:]
        san = open(os.path.join(d, 'dstrace-analysis', 'sanity.txt')).read()
        assert 'CLK fit tsc' in san and 'PASS' in san and 'validated' in san and 'sceNotAnImport' in san, san
        assert 'W03' in open(os.path.join(d, 'dstrace-analysis', 'timeline_median.txt')).read()
        # ---- degraded: minimal log with a header and a few types only
        d2 = os.path.join(tmp, 'minimal')
        os.makedirs(d2)
        m = ['# DSTRACE 1 freq=1000000000 pid=1 start_ts=1000 start_unix_ms=0 tsc_hz=1 tb_hz=1 rev=x', '# SCHEMA FLIP seq rdi:x',
             '1000000000 FLIP 5 1 0x10', '1100000000 FLIP 5 2 0x10', '1200000000 FLIP 5 3 0x10', '1250000000 NEWTYPE 5 1 2',
             '1210000000 SRT 1 0 3 0x1 5', '1230000000 SRT 1 3 0 0x1 5']
        with open(os.path.join(d2, 'dstrace.log'), 'w') as fh:
            fh.write('\n'.join(m) + '\n')
        R2, C2, rows2 = run_analysis(d2, log=quiet.append)
        assert not R2.failed and rows2 is not None and len(rows2) == 3
        assert any(n == 'fiber.txt' for n, _ in R2.skipped) and C2.D.unknown['NEWTYPE'] == 1
        assert os.path.exists(os.path.join(d2, 'dstrace-analysis', 'frames.csv'))
        # ---- game.log only
        d3 = os.path.join(tmp, 'gameonly')
        os.makedirs(d3)
        with open(os.path.join(d3, 'game.log'), 'w') as fh:
            fh.write('[METAL-PRESENT] guest_frames=3 guest_fps=1.50 host_presents=10 host_hz=5.00 seconds=2.000\n')
        R3, C3, rows3 = run_analysis(d3, log=quiet.append)
        assert C3 is None and not R3.failed and os.path.exists(os.path.join(d3, 'dstrace-analysis', 'gamelog.txt'))
        # ---- empty dir
        d4 = os.path.join(tmp, 'empty')
        os.makedirs(d4)
        R4, _, _ = run_analysis(d4, log=quiet.append)
        assert not R4.failed
        print('SELFTEST PASSED: %d synthetic records (%d types), 4 frames asserted against known values, degraded and game.log-only paths ok'
              % (len(S.lines), len(S.counts)))
        if keep:
            print('kept: %s' % tmp)
        return 0
    except AssertionError as e:
        print('SELFTEST FAILED: %s' % e)
        print('\n'.join(quiet[-30:]))
        print('kept: %s' % tmp)
        return 1
    finally:
        if not keep and sys.exc_info()[0] is None:
            import shutil
            shutil.rmtree(tmp, ignore_errors=True)


def main(argv=None):
    ap = argparse.ArgumentParser(description='Analyse a SharpEmu DSTRACE run')
    ap.add_argument('run', nargs='?', help='run dir (artifacts/runs/<ts>-ds-dstrace) or a dstrace.log path')
    ap.add_argument('--out', help='output dir (default <run dir>/dstrace-analysis)')
    ap.add_argument('--ab-settle', type=float, default=1.0, help='seconds excluded after each A/B flip (default 1.0)')
    ap.add_argument('--selftest', action='store_true')
    ap.add_argument('--keep', action='store_true', help='with --selftest keep the temp dir')
    a = ap.parse_args(argv)
    if a.selftest:
        return selftest(a.keep)
    if not a.run:
        ap.error('run dir required (or --selftest)')
    run_analysis(a.run, out=a.out, settle=a.ab_settle)
    return 0


if __name__ == '__main__':
    sys.exit(main())
