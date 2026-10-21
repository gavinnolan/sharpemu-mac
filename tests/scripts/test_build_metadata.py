# Copyright (C) 2026 SharpEmu Emulator Project
# SPDX-License-Identifier: GPL-2.0-or-later

import json
import pathlib
import subprocess
import tempfile
import unittest


SCRIPT = pathlib.Path(__file__).resolve().parents[2] / "scripts/macos/build-metadata.py"


class BuildMetadataTests(unittest.TestCase):
    def test_publish_source_change_is_rejected_and_dirty_build_is_identified(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = pathlib.Path(tmp)
            subprocess.run(["git", "init", "-q", str(root)], check=True)
            (root / "src").mkdir()
            source = root / "src/Example.cs"
            source.write_text("class Example {}\n")
            for args in (["add", "src"], ["-c", "user.name=Fixture", "-c", "user.email=fixture@example.invalid", "commit", "-qm", "fixture"]):
                subprocess.run(["git", "-C", str(root), *args], check=True)
            snapshot = root / "snapshot.json"
            output = root / "build-info.json"
            base = ["python3", str(SCRIPT), "--repo", str(root), "--snapshot", str(snapshot)]
            subprocess.run([*base, "snapshot"], check=True)
            source.write_text("class Changed {}\n")
            result = subprocess.run([*base, "embed", "--output", str(output), "--sdk", "fixture"], capture_output=True, text=True)
            self.assertNotEqual(result.returncode, 0)
            self.assertIn("Source changed during publish", result.stderr)
            self.assertFalse(output.exists())
            (root / "src/Untracked.cs").write_text("class Untracked {}\n")
            subprocess.run([*base, "snapshot"], check=True)
            subprocess.run([*base, "embed", "--output", str(output), "--sdk", "fixture"], check=True, stderr=subprocess.DEVNULL)
            data = json.loads(output.read_text())
            self.assertTrue(data["source"]["dirtySource"])
            self.assertIn("src/Untracked.cs", data["source"]["untrackedSourceSha256"])
            self.assertEqual(data["graphicsBackend"], "Metal")


if __name__ == "__main__":
    unittest.main()
