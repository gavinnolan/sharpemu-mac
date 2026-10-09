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


if __name__ == "__main__":
    unittest.main()
