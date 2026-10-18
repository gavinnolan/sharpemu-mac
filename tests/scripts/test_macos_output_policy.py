# Copyright (C) 2026 SharpEmu Emulator Project
# SPDX-License-Identifier: GPL-2.0-or-later

"""Black-box dependency policy checks; native architecture is tested by publish."""

import pathlib
import subprocess
import shutil
import html
import tempfile
import unittest


SCRIPT = pathlib.Path(__file__).resolve().parents[2] / "build/platforms/macos/check-macos-output.sh"


class MacOutputPolicyTests(unittest.TestCase):
    def check(self, directory, *args):
        return subprocess.run(
            ["bash", str(SCRIPT), str(directory), "--skip-arch", "--forbid-sdl", *args],
            capture_output=True,
            text=True,
            check=False,
        )

    def test_retired_sdl_exceptions_are_rejected(self):
        for filename in ("SDL3-CS.dll", "libSDL3.dylib"):
            with self.subTest(filename=filename), tempfile.TemporaryDirectory() as tmp:
                root = pathlib.Path(tmp)
                (root / filename).touch()
                result = self.check(root)
                self.assertNotEqual(result.returncode, 0)
                self.assertIn(filename, result.stderr)

    def test_forbidden_library_alias_is_rejected(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = pathlib.Path(tmp)
            (root / "payload").touch()
            (root / "libMoltenVK.dylib").symlink_to("payload")
            self.assertNotEqual(self.check(root).returncode, 0)

    def test_explicit_strip_preserves_other_files_in_path_with_spaces(self):
        with tempfile.TemporaryDirectory(prefix="sharpemu policy ") as tmp:
            root = pathlib.Path(tmp)
            forbidden = root / "native libs" / "libMoltenVK.dylib"
            forbidden.parent.mkdir()
            forbidden.touch()
            retained = root / "native libs" / "keep-me.dylib"
            retained.touch()
            result = self.check(root, "--strip")
            self.assertEqual(result.returncode, 0, result.stderr)
            self.assertFalse(forbidden.exists())
            self.assertTrue(retained.exists())

    def test_single_file_resolved_closure_is_checked_before_bundling(self):
        dotnet = shutil.which("dotnet")
        if dotnet is None:
            self.skipTest("MSBuild is unavailable; directory-policy cases still run")
        targets = SCRIPT.with_name("macos.targets")
        with tempfile.TemporaryDirectory() as tmp:
            root = pathlib.Path(tmp)
            project = root / "closure.proj"
            for filename, accepted in (("Silk.NET.Vulkan.dll", False), ("SharpEmu.Gpu.Vulkan.dll", False),
                                       ("SharpEmu.Adapters.Sdl.dll", False), ("SharpEmu.GUI.dll", True),
                                       ("Avalonia.Base.dll", True), ("libSkiaSharp.dylib", True)):
                with self.subTest(filename=filename):
                    project.write_text('<Project><ItemGroup><ResolvedFileToPublish Include="' + filename +
                                       '" /></ItemGroup><Import Project="' + html.escape(str(targets), quote=True) + '" /></Project>')
                    result = subprocess.run([dotnet, "msbuild", str(project), "-nologo", "-m:1", "-nr:false",
                                             "-t:SharpEmuMacValidatePublishClosure"], capture_output=True, text=True)
                    self.assertEqual(result.returncode == 0, accepted, result.stdout + result.stderr)
                    if not accepted:
                        self.assertIn(filename, result.stdout + result.stderr)

    def test_neutral_and_avalonia_payload_is_allowed(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = pathlib.Path(tmp)
            for filename in ("SharpEmu.Gpu.Metal.dll", "Avalonia.Vulkan.dll", "libSkiaSharp.dylib"):
                (root / filename).touch()
            result = self.check(root)
            self.assertEqual(result.returncode, 0, result.stderr)


if __name__ == "__main__":
    unittest.main()
