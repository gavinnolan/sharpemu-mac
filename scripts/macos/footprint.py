#!/usr/bin/env python3
# Copyright (C) 2026 SharpEmu Emulator Project
# SPDX-License-Identifier: GPL-2.0-or-later
#
# Prints "<footprint_mb> <rss_mb>" for a pid: the physical footprint Activity Monitor
# shows (proc_pid_rusage ri_phys_footprint) and the resident size. ps -o rss misses
# compressed memory and, for a Rosetta process, most of what it really holds.
# Prints "0 0" when the pid is gone.
import ctypes
import sys


class RusageInfoV0(ctypes.Structure):
    _fields_ = [("uuid", ctypes.c_uint8 * 16)] + [
        (name, ctypes.c_uint64)
        for name in ("user_time", "system_time", "pkg_idle_wkups", "interrupt_wkups",
                     "pageins", "wired_size", "resident_size", "phys_footprint",
                     "proc_start_abstime", "proc_exit_abstime")
    ]


def main() -> int:
    info = RusageInfoV0()
    libc = ctypes.CDLL("/usr/lib/libSystem.dylib")
    if libc.proc_pid_rusage(int(sys.argv[1]), 0, ctypes.byref(info)) != 0:
        print("0 0")
        return 0
    print(info.phys_footprint // 1048576, info.resident_size // 1048576)
    return 0


if __name__ == "__main__":
    sys.exit(main())
