<!--
Copyright (C) 2026 SharpEmu Emulator Project
SPDX-License-Identifier: GPL-2.0-or-later
-->

# Guest write watch

`GuestWriteWatch` is an optional diagnostic tool. It helps you find managed
code and HLE code that damage guest memory. The tool starts only if you set one
or more `SHARPEMU_WATCH_*` environment variables.

The tool monitors writes through the SharpEmu managed virtual-memory APIs. It
does not monitor stores that native guest code makes directly. Use a platform
debugger or a hardware watchpoint to monitor these stores.

## Watch modes

- `SHARPEMU_WATCH_WRITE=0x<address>` logs a write that overlaps the eight-byte
  block at the specified guest address.
- `SHARPEMU_WATCH_POOL_HEADER=1` monitors the pointer at offset `0x40`. It
  monitors the first 64 direct mappings that have a size of 64 KiB and
  protection value `0xF2`.
- `SHARPEMU_WATCH_VALUE_PATTERN=1` logs an eight-byte write if its lower 32 bits
  are `1`. The upper 32 bits must look like a small guest-pointer prefix.
- `SHARPEMU_WATCH_VALUE1=1` logs short writes of value `1` in the high guest
  memory range. The tool logs a maximum of 128 entries for each process.
- `SHARPEMU_WATCH_BULK_TORN=1` scans aligned 64-bit words in bulk writes. It
  finds damaged pointer patterns and byte-shifted pointer patterns. The tool
  logs a maximum of 64 entries for each process.
- `SHARPEMU_WATCH_BULK_DEST_HI=0x<high-dword>` scans only writes that have the
  specified upper 32 bits in the destination address.

For each match, the tool logs the destination address, the data pattern, and the
managed call stack. The log uses the `watch_write` or `watch_bulk_torn` warning
tag.

Use these variables together to scan bulk writes in the
`0x00000080xxxxxxxx` region.

macOS and Linux:

```sh
SHARPEMU_WATCH_BULK_TORN=1 \
SHARPEMU_WATCH_BULK_DEST_HI=0x80 \
SharpEmu /path/to/eboot.bin
```

Windows PowerShell:

```powershell
$env:SHARPEMU_WATCH_BULK_TORN = "1"
$env:SHARPEMU_WATCH_BULK_DEST_HI = "0x80"
& .\SharpEmu.exe C:\path\to\game\eboot.bin
```

To reduce unnecessary log entries, use an exact `SHARPEMU_WATCH_WRITE`
address from a crash dump.

## Watching a value instead of an address

A corrupted structure rarely lives at the same address twice: its base moves with the
title's allocation order, so `SHARPEMU_WATCH_WRITE` can only be armed *after* a crash
dump, for a run that will not reproduce it there. What usually does stay the same is the
value that lands in it and the offset of the slot inside its aligned block.

```sh
SHARPEMU_WATCH_QWORD=0x4000000040000000 \
SHARPEMU_WATCH_QWORD_HI=0x90 \
SHARPEMU_WATCH_QWORD_LOW=0x720 \
SHARPEMU_WATCH_QWORD_LOW_MASK=0xFFFF \
SHARPEMU_WATCH_QWORD_REPORTS=512 \
SharpEmu /path/to/eboot.bin
```

- `SHARPEMU_WATCH_QWORD` — the eight-byte value to look for, in any aligned slot a write
  lands on. Alone it is usually too noisy to be useful: most values worth hunting are also
  ordinary data somewhere.
- `SHARPEMU_WATCH_QWORD_HI` — keep only destinations whose top 32 bits match, so one
  address band (direct memory, say) can be watched on its own.
- `SHARPEMU_WATCH_QWORD_LOW` with `SHARPEMU_WATCH_QWORD_LOW_MASK` (64 KiB by default) —
  keep only the destinations whose masked low bits match: "the slot at +0x720 of any
  64 KiB block", which is armable in advance when the address is not. The filter is
  applied inside the scan, so a write that lands the value in several slots still reports
  the one that was asked for.
- `SHARPEMU_WATCH_QWORD_REPORTS` — how many hits to print before going quiet.

A silent filtered watch is evidence, not an absence of it: the hooks cover every managed
guest write — the HLE imports, `TryCopy`, `TryWrite` and the GPU command-memory path — so
nothing landing the value in the watched slot means the writer was not one of them.
Arming any watch also disables the native `memcpy` stub, so a run with a watch armed is
also a run with `SHARPEMU_HLE_FAST_MEMCPY=0`.

## Mappings that share memory

Two independent reports need no configuration and are always on:

- `mapping overlaps a live one` — a backing view was recorded over guest bytes another
  live view still holds.
- `mapping aliases live backing bytes` — a view at a different guest address was recorded
  over backing bytes a live view still holds. A title may map direct memory twice on
  purpose, so this reports rather than refuses; it is also what an allocator handing the
  same physical offset out twice looks like.

Either one means the title has been given two allocations over one piece of memory, and a
write through one silently rewrites what the other holds.
