#!/usr/bin/env python3
# Copyright (C) 2026 SharpEmu Emulator Project
# SPDX-License-Identifier: GPL-2.0-or-later
"""One bounded owner for scripted keys, optionally anchored to guest log markers or flip counts.

A plan is one or more phases separated by ';'. A phase is "[@anchor|]steps":
  steps   comma list of "t:key[:hold]" (key held t seconds after the phase starts),
          "t:mark:<name>" (a named marker written to --markers with its wall-clock time) or
          "t:rep:key:period[:hold]" (key pressed every period seconds from t on, until the
          next phase starts; a repeating step does not hold the next phase back)
  anchor  what starts the phase: "log=TEXT" (first occurrence of TEXT in the game log after the
          previous phase started), "flips=N" (N guest flips after the previous phase started,
          read from --frame-times), "mark=NAME" (a marker NAME written to --markers by anyone,
          e.g. run-game.sh's HUD watcher, after the previous phase started), or "after=S" (S
          seconds after the previous phase started);
          append "~T" to start anyway T seconds after the previous phase started.
A phase also waits for the previous phase's last step to finish. The first phase starts at
launch, or at --start-after-log when given (an explicit anchor on it takes precedence).
"""
import argparse
import math
import os
from pathlib import Path
import re
import signal
import time

NAMED_KEYS = {"enter", "esc", "tab", "backspace", "space", "left", "up", "right", "down"}
MAX_STEPS = 256
MAX_PHASES = 64
MARK_NAME = re.compile(r"^[A-Za-z0-9_.-]{1,48}$")


def max_seconds():
    return 2400 if os.environ.get("SHARPEMU_GAME_SESSION_RUN") == "1" else 600


class Phase:
    def __init__(self, anchor, steps, marks, reps=()):
        self.anchor, self.steps, self.marks, self.reps = anchor, steps, marks, list(reps)

    @property
    def duration(self):
        times = [end for _, _, end in self.steps] + [at for at, _ in self.marks]
        return max(times, default=0.0)


def parse_anchor(text):
    timeout = None
    if "~" in text:
        text, _, bound = text.rpartition("~")
        timeout = float(bound)
        if not math.isfinite(timeout) or not 0 <= timeout <= max_seconds():
            raise ValueError("anchor timeouts must be finite and within the run bound")
    kind, _, value = text.partition("=")
    if kind == "log":
        if not value or len(value.encode("utf-8")) > 256:
            raise ValueError("a log anchor needs 1..256 bytes of text")
        return ("log", value, timeout)
    if kind == "mark":
        if not MARK_NAME.match(value):
            raise ValueError("a mark anchor needs a marker name")
        return ("mark", value, timeout)
    if kind in ("flips", "after"):
        number = float(value)
        if not math.isfinite(number) or not 0 <= number <= (1e9 if kind == "flips" else max_seconds()):
            raise ValueError(f"{kind} anchor out of range")
        return (kind, number, timeout)
    raise ValueError("anchors are log=TEXT, flips=N, mark=NAME or after=S")


def parse_phase(text):
    anchor = None
    if text.startswith("@"):
        head, bar, text = text[1:].partition("|")
        if not bar:
            raise ValueError("an anchored phase is @anchor|steps")
        anchor = parse_anchor(head)
    steps, marks, reps = [], [], []
    last_start = 0.0
    for entry in text.split(","):
        parts = entry.split(":")
        if len(parts) >= 3 and parts[1].lower() == "rep":
            if len(parts) not in (4, 5):
                raise ValueError("a repeating step is t:rep:key:period[:hold]")
            start, key, period = float(parts[0]), parts[2].lower(), float(parts[3])
            hold = float(parts[4]) if len(parts) == 5 else 0.4
            if not (math.isfinite(start) and 0 <= start <= max_seconds() and start >= last_start):
                raise ValueError("input steps must be ordered by time and within the run bound")
            if not (math.isfinite(period) and math.isfinite(hold) and 0.1 <= hold < period <= 600):
                raise ValueError("a repeating step needs 0.1 <= hold < period <= 600")
            if key not in NAMED_KEYS and not (len(key) == 1 and key.isascii() and key.isalnum()):
                raise ValueError("unknown scripted key")
            last_start = start
            reps.append((start, key, period, hold))
            continue
        if len(parts) not in (2, 3):
            raise ValueError("input steps must be t:key[:hold]")
        start = float(parts[0])
        key = parts[1].lower() if parts[1].lower() != "mark" else "mark"
        if not math.isfinite(start) or not 0 <= start <= max_seconds():
            raise ValueError("input times must be finite and within 0..%d seconds" % max_seconds())
        if start < last_start:
            raise ValueError("input steps must be ordered by time")
        last_start = start
        if key == "mark":
            if len(parts) != 3 or not MARK_NAME.match(parts[2]):
                raise ValueError("a marker is t:mark:<name> with a name of letters, digits, _ . -")
            marks.append((start, parts[2]))
            continue
        hold = float(parts[2]) if len(parts) == 3 else 0.4
        if not math.isfinite(hold) or not 0.1 <= hold <= 60:
            raise ValueError("input holds must be finite and within 0.1..60 seconds")
        if key not in NAMED_KEYS and not (len(key) == 1 and key.isascii() and key.isalnum()):
            raise ValueError("unknown scripted key")
        steps.append((start, key, start + hold))
        if len(steps) > MAX_STEPS:
            raise ValueError("input plan exceeds 256 steps")
    if len(reps) > 16:
        raise ValueError("a phase may repeat at most 16 steps")
    return Phase(anchor, steps, marks, reps)


def parse_phases(plan):
    if plan == "-":
        return []
    texts = plan.split(";")
    if len(texts) > MAX_PHASES:
        raise ValueError("input plan exceeds 64 phases")
    phases = [parse_phase(text) for text in texts]
    if sum(len(phase.steps) for phase in phases) > MAX_STEPS * 4:
        raise ValueError("input plan exceeds 1024 steps")
    return phases


def parse_plan(plan):
    """The key steps of a single-phase plan as (start, key, end)."""
    phases = parse_phases(plan)
    if len(phases) > 1:
        raise ValueError("parse_plan takes a single-phase plan")
    return phases[0].steps if phases else []


def held_keys(steps, elapsed, reps=()):
    # Overlapping holds share one owner; an earlier release cannot clear a later hold.
    held = {key for start, key, end in steps if start <= elapsed < end}
    held |= {key for start, key, period, hold in reps if elapsed >= start and (elapsed - start) % period < hold}
    return sorted(held)


class LogMarker:
    def __init__(self, path, marker, offset=0):
        self.path, self.marker = path, marker.encode("utf-8")
        self.offset, self.carry = offset, b""

    def found(self):
        try:
            with self.path.open("rb") as stream:
                stream.seek(self.offset)
                data = stream.read(1024 * 1024)
        except FileNotFoundError:
            return False
        self.offset += len(data)
        combined = self.carry + data
        self.carry = combined[-(len(self.marker) - 1):] if len(self.marker) > 1 else b""
        return self.marker in combined


class LineCounter:
    """Complete non-comment lines of a growing file (the --frame-times flip log)."""

    def __init__(self, path):
        self.path, self.offset, self.pending, self.count = path, 0, b"", 0

    def poll(self):
        try:
            with self.path.open("rb") as stream:
                stream.seek(self.offset)
                data = stream.read(4 * 1024 * 1024)
        except FileNotFoundError:
            return self.count
        self.offset += len(data)
        lines = (self.pending + data).split(b"\n")
        self.pending = lines.pop()
        self.count += sum(1 for line in lines if line and not line.startswith(b"#"))
        return self.count


def size_of(path):
    try:
        return path.stat().st_size
    except FileNotFoundError:
        return 0


def write_keys(path, keys):
    temporary = path.with_name(path.name + ".inputtmp")
    temporary.write_text(" ".join(keys) + ("\n" if keys else ""))
    temporary.replace(path)


class Waiter:
    """Decides when a phase may start. `base` is the monotonic time the previous phase started."""

    def __init__(self, anchor, base, game_log, flips, base_flips, offset, markers=None):
        self.anchor, self.base, self.flips, self.base_flips = anchor, base, flips, base_flips
        self.markers, self.base_epoch = markers, time.time() - 0.5
        self.marker = LogMarker(game_log, anchor[1], offset) if anchor and anchor[0] == "log" else None
        self.satisfied = anchor is None

    def marker_written(self, name):
        try:
            for line in self.markers.read_text().splitlines():
                epoch, _, written = line.partition("\t")
                if written == name and float(epoch) >= self.base_epoch:
                    return True
        except (FileNotFoundError, ValueError, AttributeError):
            pass
        return False

    def check(self, now):
        """Returns None while waiting, else "anchor" or "timeout"."""
        if self.anchor is None:
            return "anchor"
        kind, value, timeout = self.anchor
        if not self.satisfied:
            if kind == "log":
                self.satisfied = self.marker.found()
            elif kind == "mark":
                self.satisfied = self.marker_written(value)
            elif kind == "flips":
                self.satisfied = self.flips.poll() - self.base_flips >= value
            else:
                self.satisfied = now - self.base >= value
        if self.satisfied:
            return "anchor"
        if timeout is not None and now - self.base >= timeout:
            return "timeout"
        return None


def run(args, phases):
    keys, events = Path(args.keys), Path(args.events)
    game_log = Path(args.game_log) if args.game_log else None
    markers = Path(args.markers) if args.markers else None
    flips = LineCounter(Path(args.frame_times)) if args.frame_times else None
    started = time.monotonic()
    if phases and phases[0].anchor is None and args.start_after_log:
        phases[0].anchor = ("log", args.start_after_log, None)
    stopped = False

    def stop(_signum, _frame):
        nonlocal stopped
        stopped = True

    signal.signal(signal.SIGTERM, stop)
    signal.signal(signal.SIGINT, stop)
    with events.open("a", buffering=1) as log:
        log.write(f"input-owner pid={os.getpid()} anchor={args.start_after_log!r} limit={args.run_limit} phases={len(phases)}\n")
        current = []
        write_keys(keys, current)
        index = -1          # the active phase; -1 until the first starts
        origin = None       # monotonic start of the active phase
        emitted = 0         # markers of the active phase already written
        first_started = False
        waiter = Waiter(phases[0].anchor, started, game_log, flips, 0, 0, markers) if phases else None
        try:
            while not stopped:
                now = time.monotonic()
                if now - started >= args.run_limit:
                    break
                nxt = index + 1
                ready = None
                if nxt < len(phases) and waiter is not None:
                    if index < 0 or now - origin >= phases[index].duration:
                        ready = waiter.check(now)
                if ready:
                    if index < 0 and phases[0].anchor is not None and phases[0].anchor[0] == "log" and ready == "anchor":
                        log.write(f"{now - started:.3f} anchor-observed {phases[0].anchor[1]!r}\n")
                    log.write(f"{now - started:.3f} phase-start index={nxt} via={ready}\n")
                    index, origin, emitted = nxt, now, 0
                    first_started = True
                    if index + 1 < len(phases):
                        waiter = Waiter(phases[index + 1].anchor, now, game_log, flips,
                                        flips.poll() if flips else 0, size_of(game_log) if game_log else 0, markers)
                    else:
                        waiter = None
                desired = []
                if index >= 0:
                    relative = now - origin
                    phase = phases[index]
                    desired = held_keys(phase.steps, relative, phase.reps)
                    while emitted < len(phase.marks) and phase.marks[emitted][0] <= relative:
                        name = phase.marks[emitted][1]
                        emitted += 1
                        if markers is not None:
                            with markers.open("a") as handle:
                                handle.write(f"{time.time():.3f}\t{name}\n")
                        log.write(f"{now - started:.3f} mark {name}\n")
                if desired != current:
                    write_keys(keys, desired)
                    log.write(f"{now - started:.3f} held={' '.join(desired) or '-'} relative={now - origin:.3f}\n")
                    current = desired
                time.sleep(0.05)
        finally:
            write_keys(keys, [])
            log.write(f"{time.monotonic() - started:.3f} cleared anchor_observed={first_started}\n")


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--plan", required=True)
    parser.add_argument("--validate", action="store_true")
    parser.add_argument("--keys")
    parser.add_argument("--events")
    parser.add_argument("--game-log")
    parser.add_argument("--start-after-log", default="")
    parser.add_argument("--markers", help="file that receives 'epoch<TAB>name' per t:mark:<name> step")
    parser.add_argument("--frame-times", help="SHARPEMU_FRAME_TIMES file, the source of flips=N anchors")
    parser.add_argument("--run-limit", type=float, default=150)
    args = parser.parse_args()
    try:
        phases = parse_phases(args.plan)
        if len(args.start_after_log.encode("utf-8")) > 256:
            raise ValueError("input log marker exceeds 256 bytes")
        if not math.isfinite(args.run_limit) or not 0 < args.run_limit <= max_seconds():
            raise ValueError("input run limit must be within 0..%d seconds" % max_seconds())
        needs_log = args.start_after_log or any(p.anchor and p.anchor[0] == "log" for p in phases)
        needs_flips = any(p.anchor and p.anchor[0] == "flips" for p in phases)
        needs_marks = any(p.anchor and p.anchor[0] == "mark" for p in phases)
    except ValueError as failure:
        parser.error(str(failure))
    if args.validate:
        return
    if not args.keys or not args.events or (needs_log and not args.game_log) or (needs_flips and not args.frame_times) or (needs_marks and not args.markers):
        parser.error("keys/events and the game-log (log anchors) or frame-times (flips anchors) are required")
    run(args, phases)


if __name__ == "__main__":
    main()
