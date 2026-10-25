<!--
Copyright (C) 2026 SharpEmu Emulator Project
SPDX-License-Identifier: GPL-2.0-or-later
-->

# Guest memory for the two-process split

The two-process split runs the guest and HLE in process A (x86-64 under Rosetta) and the Metal
backend in process B (native arm64). This page describes how B sees guest memory, how split mode
puts the guest memory the GPU needs into the shared backing, and which memory stays private to A.
Nothing here is active unless `SHARPEMU_GUEST_BACKING_SHAREABLE=1` or `SHARPEMU_GPU_PROCESS=1` is
set.

## What A shares

With either switch set, the guest backing (16 GiB of direct memory plus 448 MiB of flexible
memory) is created shareable (`HostBackingObject.Export`, `MacHostViews.Sharing.cs`):

- The shm backing (`SHARPEMU_GUEST_BACKING=shm`) needs nothing extra. Its descriptor already lives
  as long as the backing. The object stays unlinked, so a crash cannot leave a 16 GiB shm object
  behind until the next boot.
- The chunked backing (the default) keeps the port of each 2 MiB purgeable named entry instead of
  dropping it after mapping: 8416 ports for the full backing. Chunk releases still work. An emptied
  object reads zero through every mapping in every process.

`SharedBackingViews` also keeps a `GuestViewTable` in a shared region (10.5 MiB, of which only the
pages in use are touched). The table has two parts:

- **Entries.** Every guest range a backing view maps, with its backing offset. Readers use a
  seqlock. A shareable backing's table keeps each view's mapping protection. Following protection
  changes in the entries is optional (`trackProtection`), because each change moves the entries
  above it. That measured 3.5 µs per change at 10,000 entries on the write-fault path.
- **View log.** A ring of 65,536 records: every view map, unmap and protection change, in order,
  with a sequence number. The table header records which log record the entries are current to.

`MacOSHost.ServeGuestMemory` serves the backing and the table to other processes under a Mach
bootstrap name. `MacOSHost.ConnectGuestMemory` receives them. Chunk ports travel as port rights,
and the shm descriptor and the table descriptor travel as fileports. This works between an x86-64
process under Rosetta (4 KiB pages) and a native arm64 process (16 KiB pages). The probe for that
mapped a purgeable entry and an shm object both ways.

## What B does

1. Map the backing once as a mirror at an address the host picks (`TryMapMirror`).
2. Map the table read-only (`TryMapSharedRegion`).
3. Attach a `GuestReplica`. It reserves guest `[0, 0x100_0000_0000)` + K and the guest allocator
   arena `[0x6000_0000_0000, +512 MiB)` + K, with K = `0x1000_0000_0000` (16 TiB: clear of the
   macOS 27 guard; the arena + K still ends inside the 47-bit arm64 VA).
   It then maps every view at guest address + K, remapped from the mirror.
4. Call `GuestReplica.Sync(upTo)` at the split's sync points. Sync replays the log: a map becomes a
   remap at VA + K, and an unmap puts the reservation back. Protection records are skipped, because
   B keeps every view read-write. A replica that has fallen a whole ring behind starts again from
   the entries.

Each "host pointer = guest address" in the GPU backend then becomes "host pointer = guest address +
K", and a Metal no-copy window over a guest range is as contiguous as it is in A.
`GuestMirrorTranslator` is the lookup alternative: guest address to mirror pointer through the
entries, with a 64-slot cache. A cache hit costs 2–5 ns.

Measured on an M4, arm64 test processes, full 16.4 GiB backing:

| | shm | chunked |
|---|---|---|
| A: create the shareable backing | 0.1 ms | 12–20 ms (the same as unshared; ports add ~4 ms at release) |
| B: receive over Mach | 2.5 ms | 5 ms (8416 port rights) |
| B: map the mirror | 0.7 ms | 8 ms (8416 `mach_vm_map`) |
| B: attach the replica windows (1 TiB + the arena) | 3–4 ms | 3–4 ms |
| B: replay a remap (4 log records) | 0.1 ms | 0.1 ms |

The tradeoff between the two backings is the same as in one process. The chunked backing returns
memory the guest frees to the OS. The shm backing cannot do this while it is mapped, but each
Metal window over it spans one object. Both share equally well.

## Guest memory that is private to A

Only views of the backing are replicated. At the guest address + K of anything else B has an
inaccessible reservation, and a GPU access there faults. So in split mode A maps the guest memory
the GPU needs from the backing too (`SplitBackingLayout`, `PhysicalVirtualMemory.SplitLayout.cs`).
The backing grows past direct and flexible memory by 1.5 GiB, so the guest's direct-memory budget
does not change:

| Backing part | Offset | Size | Mapped as |
|---|---|---|---|
| Direct memory, flexible memory | 0 | 16.4 GiB | Guest views, as before |
| Guest allocator arena | 16.4 GiB | 512 MiB | One view at `0x6000_0000_0000`, made with the arena |
| Module image slice | 16.9 GiB | 1 GiB | One view per run of data pages of each module, handed out in load order |

Only a backing the host made shareable (`SHARPEMU_GPU_PROCESS=1` or
`SHARPEMU_GUEST_BACKING_SHAREABLE=1` on macOS) uses the layout. Without it, nothing changes.

- **Module images.** `SelfLoader` allocates each eboot and PRX with
  `PhysicalVirtualMemory.TryAllocateModuleImage`. Every 16 KiB guest page that no executable
  segment touches is a view of the image slice: `.rodata` (shader binaries, constant tables),
  `.data`, `.bss`, and the trailing no-access segment. Code, the pages it shares with data, and
  the partial pages at either end of the image stay private and executable, because a shared view
  of the backing is never executable on macOS. Modules start on a 16 KiB boundary in split mode, so
  segment edges fall on page edges. `Map` then fills and protects each segment over that mix of
  regions. The owner of the backing protects the views. Relocations and patches into read-only
  data write through the backing alias, as they do for any backed page. The guest writes `.data` and
  `.bss` through the same view that B maps, so the two sides use the same memory, not a copy. The
  slice is not reused while modules stay loaded. `Clear` starts it again from the beginning, and
  pages handed out again are zeroed. A module the slice cannot hold stays private, with a warning.
  Black Myth: Wukong needs 297 MiB of the slice (285 MiB for its eboot, 11.5 MiB for 32
  plug-ins). Ghost of Tsushima needs 43 MiB.
- **Guest allocator arena.** One view of its slice, at the same guest address as before. It holds
  the VideoOut buffer labels, which the GPU writes. `GuestReplica.DefaultWindows` adds the arena
  to B's replica. The arena stays private, with a warning, if its address cannot be mapped from the
  backing.
- **HLE data.** `TryAllocateHleData` takes its memory from the backed arena
  (`IGuestMemoryAllocator.AllocatesFromBacking`). That covers AGC register-default blobs, AvPlayer
  frames and the VideoOut label fallback, with no mapping per allocation. The memory is zeroed as
  before. Like every other arena allocation, it is not recorded in the guest's VA map.

Still private to A:

| Memory | Allocated by | Why it stays private |
|---|---|---|
| Executable module pages, and the data bytes on pages code touches | `TryAllocateModuleImage` (private pieces) | A view of the backing cannot be executable. PS5 segments are 16 KiB aligned, so in practice only code is left. |
| Main and callback stacks, TLS, return-to-host stubs, import stubs, trampolines | `CpuDispatcher`, `DirectExecutionBackend`, `SelfLoader`, `GuestRedZonePatcher` | CPU-only. Stubs and trampolines are code. |
| HLE libc heap (`malloc` / `aligned_alloc` for the guest) | `Marshal.AllocHGlobal`, outside the guest VA | Host heap memory. A texture placed there by a title would be invisible to B. |
| Large lazily committed data reservations | `PhysicalVirtualMemory.AllocateAt` (reserve, then commit on demand) | Only if a title puts GPU data there. |

Guest `mmap` (anonymous or file), direct, flexible, pool and JIT shared memory were backed views
already.

### Finding what is left

- **In A:** `SplitPrivateAccess.CheckGpuRead` runs in split mode where the GPU path reads guest
  memory: each submitted command stream, each new shader's code and header
  (`ShaderHeaderRegistry`), and each range the Metal backend stages into its ring because no
  window covers it. The first private or unmapped range per reader and 16 MiB region logs
  `[SPLIT][WARN] The GPU path reads guest memory the GPU process cannot see ...` with its first
  private page. After 32 such ranges, the rest are only counted (`SplitPrivateAccess.PrivateReads`).
  Outside split mode a check costs one load.
- **In B:** `GuestMirrorTranslator.IsFullyBacked(address, size, out firstUnbacked)` answers the same
  question from the shared view table. A range it rejects goes to A, instead of faulting at its guest
  address + K.

### Cost

Measured on an M4 with arm64 test processes, comparing the backing with and without the split
layout (16.4 GiB, or 17.9 GiB with the layout). The test image has Black Myth: Wukong's eboot
shape (566 MiB image, 285 MiB of it backed):

| | shm | chunked |
|---|---|---|
| A: create the backing | 0.1 → 2 ms (within run-order noise) | 15–17 ms both; 8416 → 9184 kept ports |
| A: map the arena | 2 ms (view) vs 2–5 ms (private) | 0.4 ms vs 1.6 ms |
| A: allocate the image (2 private pieces + 1 view) | 1.9 ms vs 3.0 ms | 1.1 ms vs 2.3 ms |
| A: fill and protect `.rodata`/`.data`/`.bss` | 28 ms vs 32 ms (copying 236 MiB dominates) | 27 ms vs 24 ms |
| A: memory footprint after the fill | 250 MiB vs 253 MiB | 140 MiB vs 258 MiB (purgeable entries are accounted differently; the same pages are touched) |
| B: receive over Mach | 3 ms both | 5.7 ms vs 6.2 ms (9184 vs 8416 ports) |
| B: map the mirror | 0.8 ms both | 9.6–10.1 ms vs 8.5–9.1 ms |

Memory only grows as the guest touches pages, as it does for the private memory this replaces. The
cost of the layout is 1.5 GiB of address space and, with the chunked backing, 768 more kept ports.
Startup time does not change measurably.
