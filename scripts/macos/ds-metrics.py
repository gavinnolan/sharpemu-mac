#!/usr/bin/env python3
# Copyright (C) 2026 SharpEmu Emulator Project
# SPDX-License-Identifier: GPL-2.0-or-later
"""Summarises one run-game.sh run directory into metrics.json and metrics.md.

Inputs (all optional except run.env): run.env (launch_epoch), frame-times.txt (one line per guest
flip, SHARPEMU_FRAME_TIMES), markers.tsv (epoch<TAB>name per plan marker), footprint.log
("seconds footprint_mb rss_mb"), game.tlog (game.log with "[+seconds]" prefixes), result.txt and
the screenshots.

Definitions (all times are seconds since launch_epoch, the wall clock when run-game.sh started
the game):
  launch_to_first_frame_s   first guest flip.
  launch_to_title_s         the `title` marker. The ds.sh plans place it a fixed delay after the
                            last boot-logo movie starts (the third 'Bink IO' guest thread), which is
                            when the title menu is drawn; the delay is a constant calibrated from
                            screenshots, so this is the time of the logo sequence plus that constant.
  launch_to_gameplay_s      the `hud` marker: the first 2 s screenshot probe (run-game.sh,
                            SHARPEMU_HUD_WATCH=1) that shows the in-world health/magic/stamina bars
                            (hud-detect.py). They appear with the fade-in from the stage-loading
                            screen, a second or two before the character answers the pad, and are
                            never on a loading screen, movie or menu. Without a `hud` marker the
                            first game-log line matching --gameplay-regex is used when given.
                            Null when neither happened: the run never reached gameplay.
  segments                  a marker starts a segment that lasts until the next marker; `end` closes
                            the last one; time before the first marker is `boot`.
  fps_avg                   flips in the segment divided by its duration, so stalls count against it.
  frame_time_ms             intervals between consecutive flips inside the segment: avg, p50, p95,
                            p99, max. fps_p1 = 1000 / p99, the "1% low". hitches_over_50ms counts
                            intervals above 50 ms.
  peaks                     physical footprint and RSS (footprint.log, 1 s samples) and Metal
                            allocation (gpu_alloc_mb on the [METAL-PRESENT] lines, 2 s samples).
  crash                     non-zero exit, death by signal, a [FATAL]/[CRASH] log line, or the
                            harness killing the run (memory limit). A run still alive at its time
                            limit is not a crash.
  hangs                     no guest flip for >= 30 s while the process was alive, from the first
                            flip on, not counting gaps that begin inside a segment whose name starts
                            with `load.` (known loading screens) or that start before the first flip.
"""
import argparse
import bisect
import json
from pathlib import Path
import re
import sys

HANG_SECONDS = 30.0
FATAL = re.compile(r"\]\[FATAL\]|\[CRASH\]|Unhandled exception|Fatal error")
PRESENT = re.compile(r"\[METAL-PRESENT\].*?gpu_alloc_mb=(\d+) gpu_images=(\d+) gpu_image_cache_mb=(\d+)")
TLOG = re.compile(r"^\[\+\s*([0-9.]+)\] (.*)$")


def percentile(sorted_values, fraction):
    if not sorted_values:
        return None
    position = fraction * (len(sorted_values) - 1)
    low = int(position)
    high = min(low + 1, len(sorted_values) - 1)
    return sorted_values[low] + (sorted_values[high] - sorted_values[low]) * (position - low)


def read_env(path):
    values = {}
    for line in path.read_text().splitlines():
        key, _, value = line.partition("=")
        if _:
            values[key] = value
    return values


def read_flips(path, launch_epoch):
    """Flip times in seconds since launch."""
    if not path.exists():
        return []
    origin = None
    times = []
    for line in path.read_text(errors="replace").splitlines():
        if line.startswith("#"):
            match = re.search(r"t0_unix_us=(\d+)", line)
            if match:
                origin = int(match.group(1)) / 1e6 - launch_epoch
            continue
        parts = line.split()
        if len(parts) == 2 and origin is not None:
            try:
                times.append(origin + int(parts[1]) / 1e6)
            except ValueError:
                pass  # a half-written last line
    times.sort()
    return times


def read_markers(path, launch_epoch):
    if not path.exists():
        return []
    marks = []
    for line in path.read_text().splitlines():
        epoch, _, name = line.partition("\t")
        if name:
            marks.append((float(epoch) - launch_epoch, name))
    return sorted(marks)


def read_footprint(path):
    rows = []
    if path.exists():
        for line in path.read_text().splitlines():
            parts = line.split()
            if len(parts) == 3 and all(p.isdigit() for p in parts):
                rows.append((float(parts[0]), int(parts[1]), int(parts[2])))
    return rows


def read_tlog(path):
    rows = []
    if path.exists():
        for line in path.read_text(errors="replace").splitlines():
            match = TLOG.match(line)
            if match:
                rows.append((float(match.group(1)), match.group(2)))
    return rows


def build_segments(markers, run_end):
    if not markers:
        return [("all", 0.0, run_end)]
    segments = []
    if markers[0][0] > 0:
        segments.append(("boot", 0.0, markers[0][0]))
    for index, (start, name) in enumerate(markers):
        end = markers[index + 1][0] if index + 1 < len(markers) else run_end
        if name != "end":
            segments.append((name, start, max(end, start)))
    return segments


def frame_stats(flips, start, end):
    low = bisect.bisect_left(flips, start)
    high = bisect.bisect_left(flips, end)
    inside = flips[low:high]
    duration = max(end - start, 1e-9)
    result = {"flips": len(inside), "duration_s": round(end - start, 3),
              "fps_avg": round(len(inside) / duration, 2) if end > start else None}
    intervals = sorted((b - a) * 1000.0 for a, b in zip(inside, inside[1:]))
    if intervals:
        p99 = percentile(intervals, 0.99)
        result["frame_time_ms"] = {
            "avg": round(sum(intervals) / len(intervals), 2), "p50": round(percentile(intervals, 0.5), 2),
            "p95": round(percentile(intervals, 0.95), 2), "p99": round(p99, 2), "max": round(intervals[-1], 2)}
        result["fps_p1"] = round(1000.0 / p99, 2) if p99 > 0 else None
        result["hitches_over_50ms"] = sum(1 for v in intervals if v > 50.0)
    else:
        result["frame_time_ms"] = None
        result["fps_p1"] = None
        result["hitches_over_50ms"] = None
    return result


def peaks(footprint, present, start, end):
    fp = [(f, r) for t, f, r in footprint if start <= t < end]
    gpu = [g for t, g in present if start <= t < end]
    return {"footprint_mb": max((f for f, _ in fp), default=None), "rss_mb": max((r for _, r in fp), default=None),
            "gpu_alloc_mb": max(gpu, default=None)}


def find_hangs(flips, segments, run_end, alive_until):
    if not flips:
        return []
    loading = [(s, e) for name, s, e in segments if name.startswith("load.")]
    boundaries = flips + [alive_until]
    hangs = []
    for a, b in zip(boundaries, boundaries[1:]):
        if b - a >= HANG_SECONDS and not any(s <= a < e for s, e in loading):
            hangs.append({"from_s": round(a, 2), "to_s": round(b, 2), "seconds": round(b - a, 1)})
    return hangs


def detect_crash(result_lines, tlog):
    reasons = []
    status = result_lines[0] if result_lines else ""
    match = re.match(r"exited rc=(\d+) after (\d+)s", status)
    if match and int(match.group(1)) != 0:
        code = int(match.group(1))
        reasons.append(f"exit code {code}" + (f" (signal {code - 128})" if code > 128 else ""))
    if status.startswith("memory limit"):
        reasons.append(status)
    if status.startswith("input scheduler failed"):
        reasons.append(status)
    if any(line.startswith("stop=SIGKILL") for line in result_lines):
        reasons.append("did not stop on SIGTERM within 10 s")
    fatal = [text for _, text in tlog if FATAL.search(text)]
    if fatal:
        reasons.append(f"{len(fatal)} fatal log line(s), first: {fatal[0][:160]}")
    return reasons


def resolution_evidence(tlog):
    info = {"display_patch": None, "video_out": None, "first_flip": None, "render_passes": {}}
    for _, text in tlog:
        if info["display_patch"] is None and "display mode" in text and "starts in its" in text:
            info["display_patch"] = text
        elif info["video_out"] is None and text.startswith("[VIDEO-OUT][INFO] display buffer"):
            info["video_out"] = text
        elif info["first_flip"] is None and "first published flip" in text:
            match = re.search(r" (\d+x\d+) guestFormat", text)
            info["first_flip"] = match.group(1) if match else text
        match = re.search(r"\[METAL-V2\]\[INFO\] render pass (\d+x\d+) ", text)
        if match:
            info["render_passes"][match.group(1)] = info["render_passes"].get(match.group(1), 0) + 1
    info["render_passes"] = dict(sorted(info["render_passes"].items(), key=lambda kv: -kv[1])[:8])
    return info


def screenshots(run, segments):
    shots = sorted(p.name for p in run.glob("shot-*.png"))
    by_segment = {name: [] for name, _, _ in segments}
    fixed = []
    for shot in shots:
        match = re.fullmatch(r"shot-(\d+)\.png", shot)
        if shot.startswith("shot-mark-"):
            fixed.append({"file": shot, "at": "marker " + shot[len("shot-mark-"):-4]})
        elif match:
            at = float(match.group(1))
            for name, start, end in segments:
                if start <= at < end:
                    by_segment[name].append(shot)
                    break
            fixed.append({"file": shot, "at": f"{at:.0f} s"})
        elif shot == "shot-final.png":
            fixed.append({"file": shot, "at": "end of run"})
    return fixed, by_segment


def summarise(run, gameplay_regex):
    env = read_env(run / "run.env")
    launch = float(env.get("launch_epoch", 0) or 0)
    result_lines = (run / "result.txt").read_text().splitlines() if (run / "result.txt").exists() else []
    tlog = read_tlog(run / "game.tlog")
    flips = read_flips(run / "frame-times.txt", launch)
    markers = read_markers(run / "markers.tsv", launch)
    footprint = read_footprint(run / "footprint.log")
    present = []
    for t, text in tlog:
        match = PRESENT.search(text)
        if match:
            present.append((t, int(match.group(1))))
    last_log = tlog[-1][0] if tlog else 0.0
    run_end = max([last_log, flips[-1] if flips else 0.0, footprint[-1][0] if footprint else 0.0])
    alive_until = run_end
    segments = build_segments(markers, run_end)
    mark_time = {}
    for t, name in markers:
        mark_time.setdefault(name, t)

    def first_after(pattern, after):
        regex = re.compile(pattern)
        for t, text in tlog:
            if t >= after and regex.search(text):
                return t
        return None

    gameplay = mark_time.get("hud")
    if gameplay is None and gameplay_regex:
        gameplay = first_after(gameplay_regex, mark_time.get("continue", 0.0))
    gameplay_note = None
    if gameplay is None:
        gameplay_note = "no in-world HUD was seen on screen (no `hud` marker): the run did not reach gameplay"
    shots, shots_by_segment = screenshots(run, segments)
    segment_rows = []
    for name, start, end in segments:
        row = {"name": name, "start_s": round(start, 2), "end_s": round(end, 2)}
        row.update(frame_stats(flips, start, end))
        row["peaks"] = peaks(footprint, present, start, end)
        row["screenshots"] = ([f"shot-mark-{name}.png"] if (run / f"shot-mark-{name}.png").exists() else []) + shots_by_segment.get(name, [])
        segment_rows.append(row)
    first_flip = flips[0] if flips else None
    overall = frame_stats(flips, first_flip, run_end) if flips else None
    crash = detect_crash(result_lines, tlog)
    return {
        "run": run.name,
        "result": result_lines,
        "launch_to_first_frame_s": round(first_flip, 2) if first_flip is not None else None,
        "launch_to_title_s": round(mark_time["title"], 2) if "title" in mark_time else None,
        "launch_to_gameplay_s": round(gameplay, 2) if gameplay is not None else None,
        "launch_to_gameplay_note": gameplay_note,
        "overall_after_first_frame": overall,
        "segments": segment_rows,
        "peaks": peaks(footprint, present, 0.0, run_end + 1),
        "crash": {"crashed": bool(crash), "reasons": crash},
        "hangs": {"threshold_s": HANG_SECONDS, "events": find_hangs(flips, segments, run_end, alive_until)},
        "resolution": resolution_evidence(tlog),
        "screenshots": shots,
        "markers": [{"at_s": round(t, 2), "name": n} for t, n in markers],
    }


def fmt(value, unit=""):
    return "n/a" if value is None else f"{value}{unit}"


def markdown(m):
    lines = [f"# {m['run']}", "", f"Result: {' / '.join(m['result']) or 'n/a'}", ""]
    lines += ["| metric | value |", "|---|---|",
              f"| launch -> first frame | {fmt(m['launch_to_first_frame_s'], ' s')} |",
              f"| launch -> title screen | {fmt(m['launch_to_title_s'], ' s')} |",
              f"| launch -> controllable gameplay | {fmt(m['launch_to_gameplay_s'], ' s')} |"]
    if m["launch_to_gameplay_note"]:
        lines.append(f"| gameplay note | {m['launch_to_gameplay_note']} |")
    o = m["overall_after_first_frame"]
    if o:
        ft = o["frame_time_ms"] or {}
        lines += [f"| FPS avg (first frame -> end) | {fmt(o['fps_avg'])} |", f"| FPS p1 (1% low) | {fmt(o['fps_p1'])} |",
                  f"| frame time p99 / max | {fmt(ft.get('p99'), ' ms')} / {fmt(ft.get('max'), ' ms')} |",
                  f"| hitches > 50 ms | {fmt(o['hitches_over_50ms'])} |"]
    p = m["peaks"]
    lines += [f"| peak physical footprint | {fmt(p['footprint_mb'], ' MB')} |", f"| peak RSS | {fmt(p['rss_mb'], ' MB')} |",
              f"| peak GPU (Metal allocated) | {fmt(p['gpu_alloc_mb'], ' MB')} |",
              f"| crash | {'YES: ' + '; '.join(m['crash']['reasons']) if m['crash']['crashed'] else 'no'} |",
              f"| hangs (>= {m['hangs']['threshold_s']:.0f} s without a flip) | "
              + (", ".join(f"{h['from_s']}-{h['to_s']} s ({h['seconds']} s)" for h in m['hangs']['events']) or "none") + " |", ""]
    r = m["resolution"]
    lines += ["## Resolution", f"- display patch: {r['display_patch'] or 'n/a'}", f"- video out: {r['video_out'] or 'n/a'}",
              f"- first published flip: {r['first_flip'] or 'n/a'}", f"- render passes (top): {r['render_passes']}", ""]
    lines += ["## Segments", "", "| segment | start | end | flips | FPS avg | FPS p1 | p99 ms | hitches >50 ms | footprint MB | GPU MB | screenshots |",
              "|---|---|---|---|---|---|---|---|---|---|---|"]
    for s in m["segments"]:
        ft = s["frame_time_ms"] or {}
        lines.append(
            f"| {s['name']} | {s['start_s']} | {s['end_s']} | {s['flips']} | {fmt(s['fps_avg'])} | {fmt(s['fps_p1'])} | "
            f"{fmt(ft.get('p99'))} | {fmt(s['hitches_over_50ms'])} | {fmt(s['peaks']['footprint_mb'])} | "
            f"{fmt(s['peaks']['gpu_alloc_mb'])} | {len(s['screenshots'])} |")
    lines += ["", "## Screenshots", ""]
    for shot in m["screenshots"]:
        lines.append(f"- {shot['file']} ({shot['at']})")
    return "\n".join(lines) + "\n"


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("run", help="artifacts/runs/<run> directory")
    parser.add_argument("--gameplay-regex", default="", help="fallback signal: a game-log regex, used when there is no hud marker")
    args = parser.parse_args()
    run = Path(args.run)
    if not (run / "run.env").exists():
        print(f"{run} has no run.env", file=sys.stderr)
        return 2
    metrics = summarise(run, args.gameplay_regex)
    (run / "metrics.json").write_text(json.dumps(metrics, indent=2) + "\n")
    (run / "metrics.md").write_text(markdown(metrics))
    return 0


if __name__ == "__main__":
    sys.exit(main())
