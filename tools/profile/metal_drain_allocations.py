#!/usr/bin/env python3
# Copyright (C) 2026 SharpEmu Emulator Project
# SPDX-License-Identifier: GPL-2.0-or-later
"""Read bounded presenter drain allocation rows; missing allocation data is unknown."""
import argparse
import json
import re
from pathlib import Path

COUNTERS = ("allocated_bytes", "drain_calls", "completed_work", "completed_draws",
            "completed_compute", "failed_drains", "discarded_scopes")
PREFIX = re.compile(r"\[PERF\]\[(METAL_DRAIN_ALLOC_TRACE|METAL_DRAIN_ALLOC_FRAME|METAL_DRAIN_ALLOC_TAIL|METAL_PRESENT_FRAME)\]\s+(.*)")


def fields(payload):
    result = {}
    for word in payload.split():
        if "=" in word:
            key, value = word.split("=", 1)
            if key in result:
                raise ValueError(f"duplicate field {key}")
            result[key] = value
    return result


def integer(values, key):
    if key not in values or not re.fullmatch(r"[0-9]+", values[key]):
        raise ValueError(f"missing or invalid nonnegative {key}")
    return int(values[key])


def counters(values):
    result = {key: integer(values, key) for key in COUNTERS}
    if result["completed_draws"] + result["completed_compute"] > result["completed_work"]:
        raise ValueError("draw/compute completions exceed acknowledged work")
    result["allocation_coverage"] = "partial_discarded_scope" if result["discarded_scopes"] else "current_thread_scope"
    result["bytes_per_acknowledged_work"] = (
        result["allocated_bytes"] / result["completed_work"]
        if result["completed_work"] and not result["discarded_scopes"] else None)
    return result


def parse_log(text):
    header = None
    rows = {}
    tail = None
    wall = {}
    for line in text.splitlines():
        match = PREFIX.search(line)
        if not match:
            continue
        kind, payload = match.groups()
        values = fields(payload)
        if kind == "METAL_PRESENT_FRAME":
            # Existing timing fields remain unmodified, including future extras.
            sequence = integer(values, "sequence")
            if sequence in wall:
                raise ValueError("duplicate wall sequence; split concatenated sessions")
            wall[sequence] = values
        elif kind == "METAL_DRAIN_ALLOC_TRACE":
            if header is not None:
                raise ValueError("multiple allocation sessions; split logs first")
            if values.get("schema") != "1":
                raise ValueError("unsupported allocation schema")
            header = {"schema": 1, "retained": integer(values, "retained"),
                      "overwritten": integer(values, "overwritten"), "scope": values.get("scope")}
            if header["scope"] != "current_presenter_thread_allocated_bytes" or header["retained"] > 4096:
                raise ValueError("unsupported scope or unbounded retention")
        elif kind == "METAL_DRAIN_ALLOC_FRAME":
            sequence = integer(values, "sequence")
            if sequence in rows:
                raise ValueError("duplicate allocation sequence")
            rows[sequence] = {"sequence": sequence, "guest_sequence": integer(values, "guest_sequence"),
                              **counters(values)}
        else:
            if tail is not None:
                raise ValueError("duplicate allocation tail")
            if values.get("scope") != "since_last_guest_commit":
                raise ValueError("invalid tail scope")
            tail = counters(values)
    if header is None:
        if rows or tail is not None:
            raise ValueError("allocation rows without header")
        return {"allocation_available": False, "reason": "uncollected", "frames": [],
                "tail": None, "legacy_wall_frames": len(wall)}
    if len(rows) != header["retained"] or tail is None:
        raise ValueError("truncated allocation report; missing rows or tail")
    expected = list(range(header["overwritten"], header["overwritten"] + header["retained"]))
    if sorted(rows) != expected:
        raise ValueError("noncontiguous retained allocation ordinals")
    frames = []
    for sequence in expected:
        frame = rows[sequence]
        frame["presenter_wall_fields"] = wall.get(sequence)  # absent timings stay unknown
        frames.append(frame)
    return {"allocation_available": True, "header": header, "frames": frames, "tail": tail,
            "legacy_wall_frames": len(wall),
            "limits": "Current presenter-thread allocated bytes only; excludes other threads, GC pauses, CPU self-time and GPU time. Counts acknowledged work including no-ops; bytes per work is aggregate, not individual attribution."}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("log", type=Path)
    parser.add_argument("--output", type=Path)
    args = parser.parse_args()
    result = parse_log(args.log.read_text(errors="replace"))
    output = json.dumps(result, indent=2) + "\n"
    if args.output:
        args.output.write_text(output)
    else:
        print(output, end="")


if __name__ == "__main__":
    main()
