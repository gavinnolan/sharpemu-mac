#!/usr/bin/env python3
# Copyright (C) 2026 SharpEmu Emulator Project
# SPDX-License-Identifier: GPL-2.0-or-later
"""Feeds ds-metrics.py a synthetic run directory with known frame times, markers, stalls and a crash."""
import importlib.util
import json
from pathlib import Path
import tempfile
import unittest

SCRIPT = Path(__file__).with_name("ds-metrics.py")
spec = importlib.util.spec_from_file_location("ds_metrics", SCRIPT)
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)

LAUNCH = 1_000_000.0


def make_run(folder, result="exited rc=139 after 100s", extra_log=()):
    run = Path(folder)
    (run / "run.env").write_text(f"launch_epoch={LAUNCH}\nkeyplan=-\n")
    # 60 Hz from 10 s to 20 s with one 100 ms hitch, a 35 s gap (a hang), then 30 fps until 70 s.
    times, t = [], 10.0
    while t < 20.0:
        times.append(t)
        t += 0.1 if len(times) == 300 else 1 / 60
    t = 55.0
    while t < 70.0:
        times.append(t)
        t += 1 / 30
    lines = [f"# sharpemu-frame-times v1 clock=monotonic t0_unix_us={int(LAUNCH * 1e6)}"]
    lines += [f"{i + 1} {int(v * 1e6)}" for i, v in enumerate(times)]
    (run / "frame-times.txt").write_text("\n".join(lines) + "\n")
    (run / "markers.tsv").write_text(f"{LAUNCH + 10}\ttitle\n{LAUNCH + 12}\tcontinue\n{LAUNCH + 21}\tload.stage\n{LAUNCH + 56}\tcombat\n{LAUNCH + 70}\tend\n")
    (run / "footprint.log").write_text("5 1000 900\n30 9000 400\n60 15000 300\n")
    tlog = ["[+   9.000] [METAL-PRESENT] guest_frames=1 gpu_alloc_mb=500 gpu_images=3 gpu_image_cache_mb=100",
            "[+  60.000] [METAL-PRESENT] guest_frames=1 gpu_alloc_mb=7000 gpu_images=9 gpu_image_cache_mb=900",
            "[+  40.000] [LOADER][INFO] PPSA01342: starts in its display mode 2 for a 1920x1080 display (11 sites).",
            "[+  40.100] [METAL-V2][INFO] render pass 1920x1080 samples=1 layers=1"]
    tlog += list(extra_log)
    (run / "game.tlog").write_text("\n".join(tlog) + "\n")
    (run / "result.txt").write_text(result + "\npeak_footprint_mb=15000\n")
    (run / "shot-mark-title.png").write_bytes(b"")
    (run / "shot-0015.png").write_bytes(b"")
    return run


class MetricsTests(unittest.TestCase):
    def test_percentiles_interpolate(self):
        self.assertEqual(2.5, module.percentile([1, 2, 3, 4], 0.5))
        self.assertEqual(1, module.percentile([1], 0.99))
        self.assertIsNone(module.percentile([], 0.5))

    def test_segments_frames_peaks_hangs_and_crash(self):
        with tempfile.TemporaryDirectory() as folder:
            run = make_run(folder, extra_log=["[+  65.000] [LOADER][INFO] savedata.commit resource=1 mode=0x1"])
            metrics = module.summarise(run, r"savedata\.commit")  # the log-regex fallback
            by_name = {s["name"]: s for s in metrics["segments"]}
            self.assertEqual(["boot", "title", "continue", "load.stage", "combat"], list(by_name))
            self.assertAlmostEqual(10.0, metrics["launch_to_first_frame_s"], places=2)
            self.assertEqual(10.0, metrics["launch_to_title_s"])
            self.assertEqual(65.0, metrics["launch_to_gameplay_s"])
            title = by_name["title"]  # 10..12 s of 60 Hz
            self.assertAlmostEqual(60, title["fps_avg"], delta=1)
            self.assertEqual(0, title["hitches_over_50ms"])
            self.assertAlmostEqual(60, title["fps_p1"], delta=1)
            self.assertEqual(1, by_name["continue"]["hitches_over_50ms"])
            combat = by_name["combat"]  # 56..70 s at 30 fps
            self.assertAlmostEqual(30, combat["fps_avg"], delta=1)
            self.assertAlmostEqual(30, combat["fps_p1"], delta=1)
            self.assertEqual(15000, metrics["peaks"]["footprint_mb"])
            self.assertEqual(7000, metrics["peaks"]["gpu_alloc_mb"])
            self.assertEqual(7000, combat["peaks"]["gpu_alloc_mb"])
            self.assertTrue(metrics["crash"]["crashed"])
            self.assertIn("signal 11", metrics["crash"]["reasons"][0])
            # The 20 s -> 55 s gap starts in the `continue`/`load.stage` boundary region: it begins at ~20 s,
            # inside `continue` (12..21 s), so it is reported; a gap beginning in load.* would not be.
            self.assertEqual(1, len(metrics["hangs"]["events"]))
            self.assertEqual("1920x1080", list(metrics["resolution"]["render_passes"])[0])
            self.assertIn("1920x1080 display", metrics["resolution"]["display_patch"])
            self.assertIn({"file": "shot-mark-title.png", "at": "marker title"}, metrics["screenshots"])

    def test_gap_inside_a_loading_segment_is_not_a_hang_and_clean_exit_is_not_a_crash(self):
        with tempfile.TemporaryDirectory() as folder:
            run = make_run(folder, result="alive at 100s")
            (run / "markers.tsv").write_text(f"{LAUNCH + 10}\ttitle\n{LAUNCH + 19}\tload.stage\n{LAUNCH + 56}\tcombat\n")
            metrics = module.summarise(run, "")
            self.assertEqual([], metrics["hangs"]["events"])
            self.assertFalse(metrics["crash"]["crashed"])
            self.assertIsNone(metrics["launch_to_gameplay_s"])
            self.assertIn("no in-world HUD", metrics["launch_to_gameplay_note"])
            with (run / "markers.tsv").open("a") as handle:
                handle.write(f"{LAUNCH + 60}\thud\n")
            self.assertEqual(60.0, module.summarise(run, "")["launch_to_gameplay_s"])

    def test_fatal_log_line_and_memory_limit_are_crashes(self):
        with tempfile.TemporaryDirectory() as folder:
            run = make_run(folder, result="memory limit footprint 19000MB > 18000MB at 50s",
                           extra_log=["[+  50.000] [GPU][FATAL] something broke"])
            reasons = module.summarise(run, "x")["crash"]["reasons"]
            self.assertEqual(2, len(reasons))

    def test_writes_json_and_markdown(self):
        with tempfile.TemporaryDirectory() as folder:
            run = make_run(folder)
            summary = module.summarise(run, "x")
            (run / "metrics.json").write_text(json.dumps(summary))
            text = module.markdown(summary)
            self.assertIn("launch -> title screen", text)
            self.assertIn("| combat |", text)


if __name__ == "__main__":
    unittest.main()
