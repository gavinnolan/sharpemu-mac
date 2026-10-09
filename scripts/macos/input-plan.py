#!/usr/bin/env python3
# Copyright (C) 2026 SharpEmu Emulator Project
# SPDX-License-Identifier: GPL-2.0-or-later
"""One bounded owner for scripted keys, optionally anchored to a guest log marker."""
import argparse
import math
import os
from pathlib import Path
import signal
import time

NAMED_KEYS = {"enter", "esc", "tab", "backspace", "space", "left", "up", "right", "down"}


def parse_plan(plan):
    if plan == "-":
        return []
    steps = []
    for entry in plan.split(","):
        parts = entry.split(":")
        if len(parts) not in (2, 3):
            raise ValueError("input steps must be t:key[:hold]")
        start = float(parts[0])
        key = parts[1].lower()
        hold = float(parts[2]) if len(parts) == 3 else 0.4
        if not math.isfinite(start) or not 0 <= start <= 600:
            raise ValueError("input times must be finite and within 0..600 seconds")
        if not math.isfinite(hold) or not 0.1 <= hold <= 60:
            raise ValueError("input holds must be finite and within 0.1..60 seconds")
        if key not in NAMED_KEYS and not (len(key) == 1 and key.isascii() and key.isalnum()):
            raise ValueError("unknown scripted key")
        if steps and start < steps[-1][0]:
            raise ValueError("input steps must be ordered by time")
        steps.append((start, key, start + hold))
        if len(steps) > 256:
            raise ValueError("input plan exceeds 256 steps")
    return steps


def held_keys(steps, elapsed):
    # Overlapping holds share one owner; an earlier release cannot clear a later hold.
    return sorted({key for start, key, end in steps if start <= elapsed < end})


class LogMarker:
    def __init__(self, path, marker):
        self.path, self.marker = path, marker.encode("utf-8")
        self.offset, self.carry = 0, b""

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


def write_keys(path, keys):
    temporary = path.with_name(path.name + ".inputtmp")
    temporary.write_text(" ".join(keys) + ("\n" if keys else ""))
    temporary.replace(path)


def run(args, steps):
    keys, events = Path(args.keys), Path(args.events)
    started = time.monotonic()
    origin = None if args.start_after_log else started
    marker = LogMarker(Path(args.game_log), args.start_after_log) if args.start_after_log else None
    stopped = False

    def stop(_signum, _frame):
        nonlocal stopped
        stopped = True

    signal.signal(signal.SIGTERM, stop)
    signal.signal(signal.SIGINT, stop)
    with events.open("a", buffering=1) as log:
        log.write(f"input-owner pid={os.getpid()} anchor={args.start_after_log!r} limit={args.run_limit}\n")
        current = []
        write_keys(keys, current)
        try:
            while not stopped:
                now = time.monotonic()
                if now - started >= args.run_limit:
                    break
                if origin is None and marker.found():
                    origin = now
                    log.write(f"{now - started:.3f} anchor-observed {args.start_after_log!r}\n")
                desired = [] if origin is None else held_keys(steps, now - origin)
                if desired != current:
                    write_keys(keys, desired)
                    log.write(f"{now - started:.3f} held={' '.join(desired) or '-'} relative={now - origin:.3f}\n")
                    current = desired
                time.sleep(0.05)
        finally:
            write_keys(keys, [])
            log.write(f"{time.monotonic() - started:.3f} cleared anchor_observed={origin is not None}\n")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--plan", required=True)
    parser.add_argument("--validate", action="store_true")
    parser.add_argument("--keys")
    parser.add_argument("--events")
    parser.add_argument("--game-log")
    parser.add_argument("--start-after-log", default="")
    parser.add_argument("--run-limit", type=float, default=150)
    args = parser.parse_args()
    try:
        steps = parse_plan(args.plan)
        if len(args.start_after_log.encode("utf-8")) > 256:
            raise ValueError("input log marker exceeds 256 bytes")
        if not math.isfinite(args.run_limit) or not 0 < args.run_limit <= 600:
            raise ValueError("input run limit must be within 0..600 seconds")
    except ValueError as failure:
        parser.error(str(failure))
    if args.validate:
        return
    if not args.keys or not args.events or (args.start_after_log and not args.game_log):
        parser.error("keys/events and the anchored game-log are required")
    run(args, steps)


if __name__ == "__main__":
    main()
