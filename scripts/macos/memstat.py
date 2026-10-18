#!/usr/bin/env python3
# Copyright (C) 2026 SharpEmu Emulator Project
# SPDX-License-Identifier: GPL-2.0-or-later
"""Glue around the memstat tool (memstat.c) and the system pressure log.

  memstat.py sample  --pid P --every S --out memstat.log      run memstat on P every S seconds until P exits
  memstat.py sample  ... --zero-at 60,120                     also scan guest_shm for zero pages at the first sample at or after those seconds
  memstat.py zero    --pid P --out file --bin memstat         one memstat --zero-scan sample, written to file
  memstat.py sysmem  --pid P --every S --out system-mem.log   vm_stat / swap / pressure deltas every S seconds
  memstat.py summary RUN_DIR                                  print what ds-metrics.py will report

memstat needs task_for_pid, so `sample` runs it through passwordless `sudo -n` (`sudo -n true` must
succeed; with no sudo and no get-task-allow on the target the log stays empty and the run goes on).
Every sample is one JSON line, written by memstat.c: unique (de-duplicated per VM object) resident /
dirty / swapped bytes in total and per category, plus the kernel's phys_footprint for comparison.
A --zero-scan sample adds "zero_scan": how many resident guest_shm pages (per 16 KiB host page) are all
zero, and a histogram of the zero fraction per 32 MiB granule (see memstat.c).

system-mem.log is one line per interval, "key=value" fields:
  t=<epoch> dt=<s> pageins= pageouts= swapins= swapouts= compressions= decompressions=  (deltas over dt)
  swap_used_mb= swap_total_mb= pressure=<kern.memorystatus_vm_pressure_level: 1 normal, 2 warn, 4 critical>
  free_mb= compressor_mb= (pages occupied by the compressor)
"""
import argparse
import json
import os
from pathlib import Path
import re
import statistics
import subprocess
import sys
import time

MB = 1048576.0
SYSMEM_DELTAS = ("pageins", "pageouts", "swapins", "swapouts", "compressions", "decompressions")
VM_STAT_KEYS = {
    "Pageins": "pageins", "Pageouts": "pageouts", "Swapins": "swapins", "Swapouts": "swapouts",
    "Compressions": "compressions", "Decompressions": "decompressions",
}


def read_memstat(path):
    """Rows of memstat.log as dicts, oldest first; torn or empty lines are skipped."""
    rows = []
    path = Path(path)
    if path.exists():
        for line in path.read_text(errors="replace").splitlines():
            line = line.strip()
            if not line.startswith("{"):
                continue
            try:
                row = json.loads(line)
            except ValueError:
                continue
            if "unique" in row and "task" in row:
                rows.append(row)
    return rows


def read_sysmem(path):
    rows = []
    path = Path(path)
    if path.exists():
        for line in path.read_text(errors="replace").splitlines():
            fields = dict(re.findall(r"(\w+)=(-?[0-9.]+)", line))
            if "t" in fields:
                rows.append({k: float(v) for k, v in fields.items()})
    return rows


def unique_mb(row):
    """Unique resident plus compressed/swapped: the memory the process really holds, in MB."""
    return (row["unique"]["resident"] + row["unique"]["swapped"]) / MB


def shm_fields(row):
    shm = row.get("categories", {}).get("guest_shm", {})
    return shm.get("resident", 0) / MB, shm.get("swapped", 0) / MB


def summarise_window(memstat, sysmem, start, end):
    """Memory numbers for samples with start <= t < end (seconds since launch, already rebased)."""
    rows = [r for r in memstat if start <= r["_s"] < end]
    sys_rows = [r for r in sysmem if start <= r["_s"] < end]
    result = {
        "samples": len(rows),
        "unique_mb_peak": None, "unique_resident_mb_peak": None, "phys_footprint_mb_peak": None,
        "unique_mb_median": None, "unique_mb_last": None,
        "shm_resident_mb_last": None, "shm_swapped_mb_last": None,
        "swap_used_mb_delta": None, "swapouts": None, "compressions": None, "pageouts": None,
    }
    if rows:
        totals = [unique_mb(r) for r in rows]
        result["unique_mb_peak"] = round(max(totals), 1)
        result["unique_mb_median"] = round(statistics.median(totals), 1)
        result["unique_mb_last"] = round(totals[-1], 1)
        result["unique_resident_mb_peak"] = round(max(r["unique"]["resident"] for r in rows) / MB, 1)
        result["phys_footprint_mb_peak"] = round(max(r["task"]["phys_footprint"] for r in rows) / MB, 1)
        shm_res, shm_swp = shm_fields(rows[-1])
        result["shm_resident_mb_last"] = round(shm_res, 1)
        result["shm_swapped_mb_last"] = round(shm_swp, 1)
    if sys_rows:
        for key in ("swapouts", "compressions", "pageouts"):
            result[key] = int(sum(r.get(key, 0) for r in sys_rows))
        if "swap_used_mb" in sys_rows[0]:
            result["swap_used_mb_delta"] = round(sys_rows[-1]["swap_used_mb"] - sys_rows[0]["swap_used_mb"], 1)
    return result


def steady_state(memstat, start, end):
    """Median unique MB of the last third of [start, end); None without samples there."""
    if end <= start:
        return None
    from_s = end - (end - start) / 3.0
    values = [unique_mb(r) for r in memstat if from_s <= r["_s"] < end]
    return round(statistics.median(values), 1) if values else None


def rebase(rows, launch_epoch, key="t"):
    for row in rows:
        row["_s"] = row[key] - launch_epoch
    return rows


# ---------------------------------------------------------------- samplers

def alive(pid):
    try:
        os.kill(pid, 0)
        return True
    except ProcessLookupError:
        return False
    except PermissionError:
        return True


def run_sample(pid, every, out, binary, zero_at=()):
    out = Path(out)
    zero_at = sorted(zero_at)
    started = time.time()
    while alive(pid):
        began = time.time()
        zero = bool(zero_at) and began - started >= zero_at[0]
        if zero:
            zero_at.pop(0)
        proc = subprocess.run(["sudo", "-n", binary, str(pid)] + (["--zero-scan"] if zero else []),
                              capture_output=True, text=True, timeout=300 if zero else 60)
        if proc.returncode == 0 and proc.stdout.strip().startswith("{"):
            with out.open("a") as handle:
                handle.write(proc.stdout.strip() + "\n")
        elif proc.returncode != 0 and not out.exists():
            out.with_suffix(".err").write_text(proc.stderr)
        time.sleep(max(0.0, every - (time.time() - began)))
    return 0


def run_zero(pid, out, binary):
    """One memstat --zero-scan sample, written to out (a single JSON line)."""
    proc = subprocess.run(["sudo", "-n", binary, str(pid), "--zero-scan"], capture_output=True, text=True, timeout=300)
    if proc.returncode != 0 or not proc.stdout.strip().startswith("{"):
        sys.stderr.write(proc.stderr)
        return proc.returncode or 1
    Path(out).write_text(proc.stdout.strip() + "\n")
    return 0


def parse_vm_stat(text):
    values = {}
    for line in text.splitlines():
        key, _, rest = line.partition(":")
        match = re.match(r"\s*(\d+)", rest)
        if match:
            values[key.strip('" ')] = int(match.group(1))
    return values


def sysctl(name):
    return subprocess.run(["sysctl", "-n", name], capture_output=True, text=True).stdout.strip()


def sysmem_sample(previous, page_size):
    counters = parse_vm_stat(subprocess.run(["vm_stat"], capture_output=True, text=True).stdout)
    now = time.time()
    line = {"t": f"{now:.3f}"}
    if previous is not None:
        line["dt"] = f"{now - previous[0]:.1f}"
        for vm_key, name in VM_STAT_KEYS.items():
            line[name] = max(0, counters.get(vm_key, 0) - previous[1].get(vm_key, 0))
    swap = re.findall(r"(total|used) = ([0-9.]+)M", sysctl("vm.swapusage"))
    for key, value in swap:
        line[f"swap_{key}_mb"] = value
    line["pressure"] = sysctl("kern.memorystatus_vm_pressure_level") or "0"
    line["free_mb"] = f"{counters.get('Pages free', 0) * page_size / MB:.0f}"
    line["compressor_mb"] = f"{counters.get('Pages occupied by compressor', 0) * page_size / MB:.0f}"
    return now, counters, " ".join(f"{k}={v}" for k, v in line.items())


def run_sysmem(pid, every, out):
    page_size = int(sysctl("hw.pagesize") or 16384)
    previous = None
    with Path(out).open("a") as handle:
        while pid <= 0 or alive(pid):
            began = time.time()
            now, counters, text = sysmem_sample(previous, page_size)
            if previous is not None:  # the first sample only sets the baseline for the deltas
                handle.write(text + "\n")
                handle.flush()
            previous = (now, counters)
            time.sleep(max(0.0, every - (time.time() - began)))
    return 0


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = parser.add_subparsers(dest="command", required=True)
    for name in ("sample", "sysmem", "zero"):
        p = sub.add_parser(name)
        p.add_argument("--pid", type=int, required=True)
        if name != "zero":
            p.add_argument("--every", type=float, default=15.0 if name == "sample" else 5.0)
        p.add_argument("--out", required=True)
        if name != "sysmem":
            p.add_argument("--bin", required=True)
        if name == "sample":
            p.add_argument("--zero-at", default="", help="comma list of seconds since start")
    p = sub.add_parser("summary")
    p.add_argument("run")
    args = parser.parse_args()
    if args.command == "sample":
        zero_at = [float(v) for v in args.zero_at.split(",") if v.strip()]
        return run_sample(args.pid, args.every, args.out, args.bin, zero_at)
    if args.command == "zero":
        return run_zero(args.pid, args.out, args.bin)
    if args.command == "sysmem":
        return run_sysmem(args.pid, args.every, args.out)
    run = Path(args.run)
    env = dict(line.partition("=")[::2] for line in (run / "run.env").read_text().splitlines())
    launch = float(env.get("launch_epoch", 0) or 0)
    memstat = rebase(read_memstat(run / "memstat.log"), launch)
    sysmem = rebase(read_sysmem(run / "system-mem.log"), launch)
    end = max([r["_s"] for r in memstat + sysmem] + [0.0]) + 1
    print(json.dumps(summarise_window(memstat, sysmem, 0.0, end), indent=2))
    return 0


if __name__ == "__main__":
    sys.exit(main())
