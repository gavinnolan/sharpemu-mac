#!/usr/bin/env python3
# Copyright (C) 2026 SharpEmu Emulator Project
# SPDX-License-Identifier: GPL-2.0-or-later
"""Tests for memstat.py (log parsers, window summaries) and a self-test of the memstat tool.

The self-test builds memstat, starts a child that maps one 64 MiB shared-memory object three times
and touches 40 MiB of it through two of the mappings, and expects memstat to count the object once
(40 MiB unique, 120 MiB when every mapping is summed). It needs passwordless `sudo -n` for
task_for_pid and is skipped without it.
"""
import importlib.util
import json
from pathlib import Path
import subprocess
import sys
import tempfile
import time
import unittest

HERE = Path(__file__).resolve().parent
spec = importlib.util.spec_from_file_location("memstat_glue", HERE / "memstat.py")
glue = importlib.util.module_from_spec(spec)
spec.loader.exec_module(glue)
MB = 1048576

CHILD = r'''
import ctypes, os, sys, time
libc = ctypes.CDLL("/usr/lib/libSystem.dylib", use_errno=True)
libc.mmap.restype = ctypes.c_void_p
libc.mmap.argtypes = [ctypes.c_void_p, ctypes.c_size_t, ctypes.c_int, ctypes.c_int, ctypes.c_int, ctypes.c_longlong]
libc.shm_open.argtypes = [ctypes.c_char_p, ctypes.c_int, ctypes.c_int]
MB = 1 << 20
size = 64 * MB
name = ("/ms-%d" % os.getpid()).encode()
fd = libc.shm_open(name, os.O_RDWR | os.O_CREAT | os.O_EXCL, 0o600)
libc.shm_unlink(name)
os.ftruncate(fd, size)
views = [libc.mmap(None, size, 3, 1, fd, 0) for _ in range(3)]
ctypes.memset(views[0], 1, 32 * MB)            # first 32 MiB through view 0
ctypes.memset(views[1] + 24 * MB, 2, 16 * MB)  # 24..40 MiB through view 1: 8 MiB overlap
print("ready", flush=True)
time.sleep(60)
'''


def sample_row(t, unique_resident, swapped, footprint, shm_resident=0, shm_swapped=0):
    return {"t": t, "unique": {"resident": unique_resident * MB, "dirty": 0, "swapped": swapped * MB},
            "task": {"phys_footprint": footprint * MB},
            "categories": {"guest_shm": {"resident": shm_resident * MB, "swapped": shm_swapped * MB}}}


class ParserTests(unittest.TestCase):
    def test_read_memstat_skips_torn_and_foreign_lines(self):
        with tempfile.TemporaryDirectory() as folder:
            path = Path(folder) / "memstat.log"
            path.write_text(json.dumps(sample_row(10, 100, 5, 120)) + "\nnot json\n{\"t\": 1}\n{\"t\": 11, \"unique\": {\"res")
            rows = glue.read_memstat(path)
            self.assertEqual(1, len(rows))
            self.assertEqual([], glue.read_memstat(Path(folder) / "missing.log"))

    def test_read_sysmem_and_windows(self):
        with tempfile.TemporaryDirectory() as folder:
            path = Path(folder) / "system-mem.log"
            path.write_text(
                "t=100.000 dt=5.0 pageins=1 pageouts=0 swapins=0 swapouts=10 compressions=100 decompressions=3 "
                "swap_total_mb=4096.00 swap_used_mb=1000.00 pressure=1 free_mb=500 compressor_mb=900\n"
                "t=105.000 dt=5.0 pageins=2 pageouts=1 swapins=0 swapouts=30 compressions=300 decompressions=3 "
                "swap_total_mb=4096.00 swap_used_mb=1250.50 pressure=2 free_mb=400 compressor_mb=950\n"
                "t=110.000 dt=5.0 pageins=0 pageouts=0 swapins=0 swapouts=7 compressions=11 decompressions=0 "
                "swap_total_mb=4096.00 swap_used_mb=1260.00 pressure=2 free_mb=400 compressor_mb=950\n")
            sysmem = glue.rebase(glue.read_sysmem(path), 100.0)
            self.assertEqual(3, len(sysmem))
            memstat = glue.rebase([sample_row(100 + s, 1000 + s * 10, 0, 1500 + s * 10) for s in (0, 5, 10, 15, 20, 25)], 100.0)
            whole = glue.summarise_window(memstat, sysmem, 0.0, 30.0)
            self.assertEqual(6, whole["samples"])
            self.assertEqual(1250.0, whole["unique_mb_peak"])
            self.assertEqual(1750.0, whole["phys_footprint_mb_peak"])
            self.assertEqual(47, whole["swapouts"])
            self.assertEqual(411, whole["compressions"])
            self.assertEqual(260.0, whole["swap_used_mb_delta"])
            window = glue.summarise_window(memstat, sysmem, 4.0, 11.0)  # samples at 5 and 10 s
            self.assertEqual(2, window["samples"])
            self.assertEqual(37, window["swapouts"])
            self.assertEqual(9.5, window["swap_used_mb_delta"])

    def test_steady_state_is_median_of_the_last_third(self):
        rows = glue.rebase([sample_row(s, v, 0, v) for s, v in [(0, 100), (10, 200), (20, 5000), (30, 5200), (40, 5100)]], 0.0)
        # window 0..45: last third is 30..45 -> 5200 and 5100
        self.assertEqual(5150.0, glue.steady_state(rows, 0.0, 45.0))
        self.assertIsNone(glue.steady_state(rows, 100.0, 200.0))

    def test_unique_counts_compressed_pages_and_shm_fields_split_them(self):
        row = sample_row(1, 300, 200, 900, shm_resident=250, shm_swapped=180)
        self.assertEqual(500.0, glue.unique_mb(row))
        self.assertEqual((250.0, 180.0), glue.shm_fields(row))

    def test_vm_stat_parser(self):
        text = 'Mach Virtual Memory Statistics: (page size of 16384 bytes)\nPages free:      104574.\n"Translation faults":  684.\nSwapouts:   1063362608.\n'
        values = glue.parse_vm_stat(text)
        self.assertEqual(104574, values["Pages free"])
        self.assertEqual(1063362608, values["Swapouts"])
        self.assertEqual(684, values["Translation faults"])


def sudo_available():
    return subprocess.run(["sudo", "-n", "true"], capture_output=True).returncode == 0


@unittest.skipUnless(sudo_available(), "needs passwordless sudo -n for task_for_pid")
class ToolSelfTest(unittest.TestCase):
    def test_shm_object_mapped_three_times_is_counted_once(self):
        binary = subprocess.run([str(HERE / "build-memstat.sh")], capture_output=True, text=True, check=True).stdout.strip()
        child = subprocess.Popen([sys.executable, "-c", CHILD], stdout=subprocess.PIPE, text=True)
        try:
            self.assertEqual("ready", child.stdout.readline().strip())
            started = time.time()
            proc = subprocess.run(["sudo", "-n", binary, str(child.pid), "--naive"], capture_output=True, text=True, check=True)
            elapsed = time.time() - started
            row = json.loads(proc.stdout)
            # The interpreter's own few KiB of shared memory (runtime, commpage) may sit next to the object.
            ours = [o for o in row["top_objects"] if o["views"] == 3]
            self.assertEqual(1, len(ours))
            self.assertEqual("guest_shm", ours[0]["category"])
            self.assertEqual(40 * MB, ours[0]["resident"])  # counted once, not 3 x 40 MiB
            self.assertEqual(40 * MB, ours[0]["dirty"])
            self.assertEqual(0, ours[0]["swapped"])
            shm = row["categories"]["guest_shm"]
            self.assertLess(shm["resident"] - 40 * MB, 256 * 1024)
            self.assertGreaterEqual(shm["naive_resident"], 120 * MB)  # summing the three mappings
            self.assertGreater(row["task"]["phys_footprint"], 40 * MB)
            self.assertLess(elapsed, 2.0)
        finally:
            child.kill()
            child.wait()
            child.stdout.close()


if __name__ == "__main__":
    unittest.main()
