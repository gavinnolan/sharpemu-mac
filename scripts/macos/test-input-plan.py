#!/usr/bin/env python3
# Copyright (C) 2026 SharpEmu Emulator Project
# SPDX-License-Identifier: GPL-2.0-or-later
"""Checks timing boundaries and actual owned-worker shutdown without launching a game."""
import importlib.util
from pathlib import Path
import subprocess
import sys
import tempfile
import time
import unittest

SCRIPT = Path(__file__).with_name("input-plan.py")
spec = importlib.util.spec_from_file_location("input_plan", SCRIPT)
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)


class InputPlanTests(unittest.TestCase):
    def test_overlapping_holds_do_not_release_the_newer_key_or_chord(self):
        steps = module.parse_plan("0:w:1,0.5:w:1,0.7:d:1")
        self.assertEqual(["d", "w"], module.held_keys(steps, 1.1))
        self.assertEqual(["d"], module.held_keys(steps, 1.6))
        self.assertEqual([], module.held_keys(steps, 1.7))

    def test_invalid_or_unbounded_plan_declines_before_launch(self):
        for plan in ["nan:w", "-1:w", "601:w", "0:w:inf", "0:w:0", "0:w:61", "1:w,0:d", "0:unknown", "0:w:1:2"]:
            with self.subTest(plan=plan), self.assertRaises(ValueError):
                module.parse_plan(plan)
        with self.assertRaises(ValueError):
            module.parse_plan(",".join("0:w" for _ in range(257)))

    def test_marker_survives_append_boundaries(self):
        with tempfile.TemporaryDirectory() as folder:
            path = Path(folder) / "game.log"
            marker = module.LogMarker(path, "Quake Initialized")
            self.assertFalse(marker.found())
            path.write_bytes(b"noise\nQuake Ini")
            self.assertFalse(marker.found())
            with path.open("ab") as stream:
                stream.write(b"tialized\n")
            self.assertTrue(marker.found())

    def worker(self, folder, marker, limit=3):
        keys, events, log = [Path(folder) / name for name in ("keys.state", "keys.log", "game.log")]
        process = subprocess.Popen([sys.executable, str(SCRIPT), "--plan", "0:w:2", "--keys", str(keys),
            "--events", str(events), "--game-log", str(log), "--start-after-log", marker, "--run-limit", str(limit)],
            stdout=subprocess.PIPE, stderr=subprocess.PIPE)
        return process, keys, events, log

    def until(self, predicate, seconds=2):
        deadline = time.monotonic() + seconds
        while time.monotonic() < deadline:
            if predicate():
                return
            time.sleep(0.02)
        self.fail("owned input worker did not reach expected state")

    def test_actual_worker_waits_for_marker_then_term_clears_held_key(self):
        with tempfile.TemporaryDirectory() as folder:
            process, keys, events, log = self.worker(folder, "Quake Initialized")
            try:
                self.until(keys.exists)
                time.sleep(0.15)
                self.assertEqual("", keys.read_text())
                log.write_text("Quake Initialized\n")
                self.until(lambda: keys.read_text().strip() == "w")
                process.terminate()
                _, errors = process.communicate(timeout=2)
                self.assertEqual(0, process.returncode, errors.decode())
                self.assertEqual("", keys.read_text())
                self.assertIn("anchor-observed", events.read_text())
            finally:
                if process.poll() is None:
                    process.terminate()
                process.communicate(timeout=2)

    def test_missing_marker_never_presses_and_exits_at_bound(self):
        with tempfile.TemporaryDirectory() as folder:
            process, keys, events, _ = self.worker(folder, "missing", limit=0.25)
            _, errors = process.communicate(timeout=2)
            self.assertEqual(0, process.returncode, errors.decode())
            self.assertEqual("", keys.read_text())
            self.assertNotIn("held=w", events.read_text())
            self.assertIn("anchor_observed=False", events.read_text())

    def test_launch_relative_plan_remains_supported(self):
        with tempfile.TemporaryDirectory() as folder:
            process, keys, events, _ = self.worker(folder, "", limit=0.25)
            _, errors = process.communicate(timeout=2)
            self.assertEqual(0, process.returncode, errors.decode())
            self.assertIn("held=w", events.read_text())
            self.assertEqual("", keys.read_text())

    def test_markers_and_phases_parse(self):
        phases = module.parse_phases("0:a:1,2:mark:intro;@log=Starting main loop~30|0:mark:menu,1:enter;@flips=5|0:mark:end")
        self.assertEqual(3, len(phases))
        self.assertEqual([(2.0, "intro")], phases[0].marks)
        self.assertEqual(("log", "Starting main loop", 30.0), phases[1].anchor)
        self.assertEqual(("flips", 5.0, None), phases[2].anchor)
        self.assertEqual(1.4, phases[1].duration)

    def test_repeating_steps_press_every_period_and_do_not_extend_the_phase(self):
        phase = module.parse_phases("0:rep:enter:6:2,3:mark:m")[0]
        self.assertEqual(3.0, phase.duration)
        self.assertEqual(["enter"], module.held_keys(phase.steps, 1.0, phase.reps))
        self.assertEqual([], module.held_keys(phase.steps, 3.0, phase.reps))
        self.assertEqual(["enter"], module.held_keys(phase.steps, 6.5, phase.reps))
        for bad in ["0:rep:enter", "0:rep:enter:1:2", "0:rep:nope:5", "0:rep:enter:5:0.05"]:
            with self.subTest(plan=bad), self.assertRaises(ValueError):
                module.parse_phases(bad)

    def test_bad_markers_and_anchors_decline(self):
        for plan in ["0:mark", "0:mark:bad name", "0:mark:", "@bogus=1|0:w", "@log=x", "@after=-1|0:w", "0:w;@flips=nan|0:w"]:
            with self.subTest(plan=plan), self.assertRaises(ValueError):
                module.parse_phases(plan)
        with self.assertRaises(ValueError):
            module.parse_plan("0:w;0:d")

    def test_second_phase_waits_for_its_anchor_and_after_the_first_phases_steps(self):
        with tempfile.TemporaryDirectory() as folder:
            keys, events, log, marks = [Path(folder) / n for n in ("keys.state", "keys.log", "game.log", "markers.tsv")]
            log.write_text("")
            process = subprocess.Popen([sys.executable, str(SCRIPT), "--plan", "0:mark:one,0:a:0.4;@log=go now~20|0:mark:two,0:w:0.5",
                "--keys", str(keys), "--events", str(events), "--game-log", str(log), "--markers", str(marks), "--run-limit", "6"],
                stdout=subprocess.PIPE, stderr=subprocess.PIPE)
            try:
                self.until(lambda: marks.exists() and "one" in marks.read_text())
                time.sleep(0.6)
                self.assertNotIn("two", marks.read_text())
                log.write_text("go now\n")
                self.until(lambda: "two" in marks.read_text())
                self.until(lambda: keys.read_text().strip() == "w")
                self.assertIn("via=anchor", events.read_text())
            finally:
                process.terminate()
                process.communicate(timeout=3)

    def test_mark_anchor_waits_for_a_marker_written_by_someone_else(self):
        with tempfile.TemporaryDirectory() as folder:
            keys, events, marks = [Path(folder) / n for n in ("keys.state", "keys.log", "markers.tsv")]
            marks.write_text(f"{time.time() - 100:.3f}\thud\n")  # an old one must not count
            process = subprocess.Popen([sys.executable, str(SCRIPT), "--plan", "0:mark:one;@mark=hud~30|0:mark:two",
                "--keys", str(keys), "--events", str(events), "--markers", str(marks), "--run-limit", "6"],
                stdout=subprocess.PIPE, stderr=subprocess.PIPE)
            try:
                self.until(lambda: "one" in marks.read_text())
                time.sleep(0.4)
                self.assertNotIn("two", marks.read_text())
                with marks.open("a") as handle:
                    handle.write(f"{time.time():.3f}\thud\n")
                self.until(lambda: "two" in marks.read_text())
                self.assertIn("via=anchor", events.read_text())
            finally:
                process.terminate()
                process.communicate(timeout=3)

    def test_anchor_timeout_starts_the_phase_anyway(self):
        with tempfile.TemporaryDirectory() as folder:
            keys, events, log, marks = [Path(folder) / n for n in ("keys.state", "keys.log", "game.log", "markers.tsv")]
            process = subprocess.Popen([sys.executable, str(SCRIPT), "--plan", "0:mark:one;@log=never~0.5|0:mark:two",
                "--keys", str(keys), "--events", str(events), "--game-log", str(log), "--markers", str(marks), "--run-limit", "4"],
                stdout=subprocess.PIPE, stderr=subprocess.PIPE)
            try:
                self.until(lambda: marks.exists() and "two" in marks.read_text(), seconds=3)
                self.assertIn("via=timeout", events.read_text())
            finally:
                process.terminate()
                process.communicate(timeout=3)

    def test_flip_count_anchor_counts_flips_after_the_previous_phase_started(self):
        with tempfile.TemporaryDirectory() as folder:
            keys, events, log, marks, flips = [Path(folder) / n for n in ("keys.state", "keys.log", "game.log", "markers.tsv", "flips.txt")]
            flips.write_text("# header\n1 100\n2 200\n")
            process = subprocess.Popen([sys.executable, str(SCRIPT), "--plan", "0:mark:one;@flips=3~30|0:mark:two",
                "--keys", str(keys), "--events", str(events), "--frame-times", str(flips), "--markers", str(marks), "--run-limit", "6"],
                stdout=subprocess.PIPE, stderr=subprocess.PIPE)
            try:
                self.until(lambda: marks.exists() and "one" in marks.read_text())
                time.sleep(0.4)
                self.assertNotIn("two", marks.read_text())
                with flips.open("a") as handle:
                    handle.write("3 300\n4 400\n5 500\n")
                self.until(lambda: "two" in marks.read_text())
            finally:
                process.terminate()
                process.communicate(timeout=3)


if __name__ == "__main__":
    unittest.main()
