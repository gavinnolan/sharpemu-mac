<!--
Copyright (C) 2026 SharpEmu Emulator Project
SPDX-License-Identifier: GPL-2.0-or-later
-->

# Guest memory for the two-process split

The two-process split runs the guest and HLE in process A (x86-64 under Rosetta) and the Metal
backend in process B (native arm64). This page describes how B sees guest memory, and which guest
memory it does not see yet. Nothing here is active unless `SHARPEMU_GUEST_BACKING_SHAREABLE=1` or
`SHARPEMU_GPU_PROCESS=1` is set.

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
3. Attach a `GuestReplica`. It reserves guest `[0, 0x100_0000_0000)` + K, with
   K = `0x1000_0000_0000` (16 TiB: clear of the macOS 27 guard and inside the 47-bit arm64 VA).
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
| B: attach the 1 TiB replica window | 3 ms | 3 ms |
| B: replay a remap (4 log records) | 0.1 ms | 0.1 ms |

The tradeoff between the two backings is the same as in one process. The chunked backing returns
memory the guest frees to the OS. The shm backing cannot do this while it is mapped, but each
Metal window over it spans one object. Both share equally well.

## Guest memory B does not see

Only views of the backing are replicated. Everything below is private anonymous memory in A. At
its guest address + K in B there is an inaccessible reservation, and a GPU access there faults.

| Memory | Allocated by | Does B need it? |
|---|---|---|
| Module images (eboot and PRX): code, `.rodata`, `.data`, `.bss` | `SelfLoader` → `PhysicalVirtualMemory.TryAllocateAtExact` / `AllocateAt` / `TryBackFixedRange` (one RWX allocation per image) | Yes. Shader binaries and constant tables are in `.rodata`. GPU labels and fences can be globals in `.data`/`.bss`. |
| Guest allocator arena, 512 MiB at `0x6000_0000_0000` | `PhysicalVirtualMemory.TryAllocateGuestMemory` (`IGuestMemoryAllocator`) | Yes, for the VideoOut buffer labels, which the GPU writes. Ngs2, Font, JSON, AudioOut2 and pthread objects are CPU-only. |
| HLE data | `KernelMemoryCompatExports.TryAllocateHleData` → `KernelVirtualRangeAllocator` | Partly: AGC register-default blobs, AvPlayer video frame buffers (if the title samples them), and the VideoOut labels fallback. Libc stdio, Ajm, AudioOut2 and Ngs2 are CPU-only. |
| Main and callback stacks, TLS, return-to-host stubs | `CpuDispatcher` (`MapGuestRegion`) and `DirectExecutionBackend` | Not normally. |
| Large lazily committed data reservations | `PhysicalVirtualMemory.AllocateAt` (reserve, then commit on demand) | Only if a title puts GPU data there. |

Guest `mmap` (anonymous or file), direct, flexible, pool and JIT shared memory are all backed views
already.

### Proposal

- **Module images.** Code cannot come from the backing. On macOS a `MAP_SHARED` or remapped view
  of the backing is never executable (its maximum protection is read-write), so executable segments
  stay private. Map the non-executable segments from a new image slice of the backing instead, as
  backed views at the segment addresses. The backing grows past direct and flexible memory in split
  mode, so the guest's direct-memory budget does not change. This needs `SelfLoader` to map each
  segment separately instead of one RWX allocation followed by per-segment protection changes, so
  it is not done yet. A cheaper first step covers shader code: after relocation, copy each read-only
  segment into the image slice and log a B-only view of the copy. Read-only segments do not change
  after relocation, so the copy stays coherent. Writable segments then need the full change, or a
  write request to A.
- **Guest allocator arena and HLE data.** In split mode, back the 512 MiB arena with a slice of the
  backing (one view mapped when the arena is created), and route `TryAllocateHleData` into it. This
  covers the VideoOut labels, AGC blobs and video frames with one mapping and no per-allocation work.
- **Stacks, TLS and stubs.** Leave them private.
- **In B.** Check `GuestViewTable` (or `GuestMirrorTranslator.TryGetEntry`) before using a guest
  range that does not come from a backed allocation. A range that is not there goes to A, instead of
  faulting at its guest address + K.
