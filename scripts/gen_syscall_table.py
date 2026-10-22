#!/usr/bin/env python3

# Copyright (C) 2026 SharpEmu Emulator Project
# SPDX-License-Identifier: GPL-2.0-or-later

"""Generates the Prospero syscall table from psdevwiki's Syscalls page.

Input: the captured psdevwiki "PS5 Syscalls" markdown table (ID | PS4 | PS5).
Output: src/SharpEmu.Libs/Kernel/Syscalls/OrbisSyscallTable.g.cs, one entry per
slot 0x0..max: the native PS5 name, the slot kind, and the libkernel exports
(name, NID, error flavour) a raw syscall of that number is routed to. The
exports are only candidates: the dispatcher resolves them by NID through the
module manager at run time, so a wrapper added to the HLE later lights the
slot up without regenerating this file.

Usage:
  scripts/gen_syscall_table.py [--source ps5-Syscalls.md] [--output path] [--check]
"""

from __future__ import annotations

import argparse
import re
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
from aerolib_catalog import compute_nid, read_names  # noqa: E402

REPO = Path(__file__).resolve().parents[1]
DEFAULT_SOURCE = Path.home() / "Documents" / "ps5emu" / "docs" / "psdevwiki" / "ps5-Syscalls.md"
DEFAULT_OUTPUT = REPO / "src" / "SharpEmu.Libs" / "Kernel" / "Syscalls" / "OrbisSyscallTable.g.cs"
NAMES_FILE = REPO / "scripts" / "ps5_names.txt"

ROW = re.compile(r"^\|\s*(0x[0-9a-fA-F]+)(?:-(0x[0-9a-fA-F]+))?\s*\|([^|]*)\|([^|]*)\|\s*$")

# Slots the page marks as unimplemented: FreeBSD's nosys (SIGSYS + ENOSYS).
NOSYS = re.compile(r"^(sys_obsolete\d*|sys_number\d*|sys_obs_\w+|sys_lkmnosys|sys_nosys)$")

# The indirect forms: the number is in rdi and the arguments shift by one.
INDIRECT = {"sys_syscall", "sys___syscall"}

# Real syscalls whose libkernel/libc export of the same name has a different
# ABI than the raw syscall, so routing the raw call to it would be wrong:
# pipe returns both fds in rax:rdx (libc takes int[2]); the fork family returns
# (pid, is-child) in rax:rdx; the context calls act on the caller's register
# state, which a forwarded HLE call does not have.
ABI_MISMATCH = {
    "sys_pipe", "sys_fork", "sys_vfork", "sys_rfork",
    "sys_sigreturn", "sys_getcontext", "sys_setcontext", "sys_swapcontext",
}

# Exports that are not spelled like the syscall but have its exact ABI.
ALIASES = {
    "sys___sysctl": ["sysctl"],
}

# Exports that are spelled like the syscall but must not receive it:
# libc exit() runs atexit handlers, the exit syscall does not.
EXCLUDED_EXPORTS = {
    "sys_exit": {"exit"},
}


def expand(first: int, last: int, name: str) -> list[str]:
    count = last - first + 1
    if count == 1:
        return [name]
    numbered = re.fullmatch(r"(sys_(?:obsolete|number))(\d+)-(\d+)", name)
    if numbered:
        lo, hi = int(numbered.group(2)), int(numbered.group(3))
        if hi - lo + 1 != count:
            raise SystemExit(f"range 0x{first:x}-0x{last:x}: '{name}' does not cover {count} slots")
        return [f"{numbered.group(1)}{n}" for n in range(lo, hi + 1)]
    if "/" in name:
        parts = name.split("/")
        head = parts[0]
        prefix = head[: head.rfind("_") + 1]
        names = [head] + [prefix + part for part in parts[1:]]
        if len(names) != count:
            raise SystemExit(f"range 0x{first:x}-0x{last:x}: '{name}' does not cover {count} slots")
        return names
    return [name] * count


def clean(name: str) -> str:
    name = re.sub(r"\(.*?\)", "", name).strip()
    if name in ("", "-"):
        return ""
    if not name.startswith("sys_"):
        name = "sys_" + name
    return name


def parse(source: Path) -> dict[int, str]:
    slots: dict[int, str] = {}
    for line in source.read_text(encoding="utf-8").splitlines():
        match = ROW.match(line.strip())
        if not match:
            continue
        first = int(match.group(1), 16)
        last = int(match.group(2), 16) if match.group(2) else first
        ps4, ps5 = match.group(3).strip(), match.group(4).strip()
        name = ps4 if ps5 == "same" else ps5
        if name in ("", "-"):
            name = ps4
        for offset, expanded in enumerate(expand(first, last, re.sub(r"\(.*?\)", "", name).strip())):
            number = first + offset
            expanded = clean(expanded)
            if number in slots and slots[number] != expanded:
                raise SystemExit(f"0x{number:x}: conflicting rows '{slots[number]}' and '{expanded}'")
            slots[number] = expanded
    return slots


def classify(name: str, catalog: set[str]) -> tuple[str, list[str]]:
    if not name or NOSYS.match(name):
        return "NoSys", []
    if name in INDIRECT:
        return "Indirect", []
    if name.startswith("sys_compat"):
        return "Compat", []
    if name in ABI_MISMATCH:
        return "AbiMismatch", []
    base = name[len("sys_"):]
    candidates = [f"__sys_{base}", f"_{base}", base] + ALIASES.get(name, [])
    excluded = EXCLUDED_EXPORTS.get(name, set())
    exports: list[str] = []
    for candidate in candidates:
        if candidate in catalog and candidate not in excluded and candidate not in exports:
            exports.append(candidate)
    return ("Forwarded" if exports else "Unmapped"), exports


def render(slots: dict[int, str], catalog: set[str], source_name: str) -> str:
    maximum = max(slots)
    lines = [
        # REUSE-IgnoreStart
        "// Copyright (C) 2026 SharpEmu Emulator Project",
        "// SPDX-License-Identifier: GPL-2.0-or-later",
        # REUSE-IgnoreEnd
        "",
        "// <auto-generated>",
        f"// Generated by scripts/gen_syscall_table.py from {source_name}. Do not edit by hand.",
        "// </auto-generated>",
        "",
        "namespace SharpEmu.Libs.Kernel.Syscalls;",
        "",
        "internal static partial class OrbisSyscallTable",
        "{",
        f"    public const int MaxNumber = 0x{maximum:X};",
        "",
        "    private static readonly OrbisSyscallEntry[] s_entries =",
        "    [",
    ]
    for number in range(maximum + 1):
        name = slots.get(number, "")
        kind, exports = classify(name, catalog)
        display = name or f"sys_unlisted{number}"
        targets = ", ".join(
            f'new("{export}", "{compute_nid(export)}", KernelAbiFlavor.Posix)' for export in exports)
        lines.append(
            f'        new(0x{number:03X}, "{display}", OrbisSyscallKind.{kind}, [{targets}]),')
    lines += ["    ];", "}", ""]
    return "\n".join(lines)


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--source", type=Path, default=DEFAULT_SOURCE)
    parser.add_argument("--output", type=Path, default=DEFAULT_OUTPUT)
    parser.add_argument("--check", action="store_true", help="fail if the output is out of date")
    args = parser.parse_args()

    slots = parse(args.source)
    missing = [n for n in range(max(slots) + 1) if n not in slots]
    if missing:
        print(f"warning: slots absent from the page (generated as nosys): {[hex(n) for n in missing]}",
              file=sys.stderr)
    catalog = set(read_names(NAMES_FILE))
    text = render(slots, catalog, args.source.name)
    if args.check:
        current = args.output.read_text(encoding="utf-8") if args.output.exists() else ""
        if current != text:
            print(f"{args.output} is out of date; rerun scripts/gen_syscall_table.py", file=sys.stderr)
            return 1
        return 0
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(text, encoding="utf-8")
    kinds: dict[str, int] = {}
    for number in range(max(slots) + 1):
        kind, _ = classify(slots.get(number, ""), catalog)
        kinds[kind] = kinds.get(kind, 0) + 1
    print(f"wrote {args.output} ({max(slots) + 1} slots: {kinds})")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
