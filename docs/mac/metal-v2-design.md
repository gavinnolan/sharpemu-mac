<!-- Copyright (C) 2026 SharpEmu Emulator Project -->
<!-- SPDX-License-Identifier: GPL-2.0-or-later -->

# Metal v2 render host

Status: accepted 2026-10-10; built and default on macOS the same day. Replaces the
snapshot/admission Metal path, which stays behind `SHARPEMU_METAL_LEGACY=1`.

## Why

The first Metal backend copies every texture and buffer into a per-draw record on the
guest submission thread, then replays it on the AppKit thread. Every unusual descriptor
needs its own "admission", and any refusal becomes `GpuFatal` (exit 134). After
1,300 commits, 1 of 9 titles is playable. Detiling is on the CPU, there are 128 MiB
snapshot caps, and GPU-written images are never written back.

The Vulkan backend implements the same shared `IRenderHost`/`RenderExecutor` contract
with an address-keyed image cache, page-guard dirty tracking and GPU tiler shaders.
Metal v2 follows that architecture and adds one Apple-specific property: **unified
memory**. The guest's 16 GiB direct-memory backing is one contiguous host alias.
Measured on the M4 under Rosetta, four 4 GiB `newBufferWithBytesNoCopy` windows over
a lazily backed Mach memory entry cost about 0.5 s of GPU mapping once and then
about 0.2 ms per command buffer. Footprint stays lazy (46 MB after GPU use), and GPU
writes are visible to the CPU.

## Core model

**Guest memory is the GPU's memory.**

`MetalGuestMemory` wraps the backing alias (`IGuestBackedSpace.TryResolveBackingAlias`,
`HostBackingObject.AliasBase`) as four no-copy `MTLBuffer` windows
(`maxBufferLength` is 13.6 GB). It answers `Resolve(gpuVa, size) → (buffer,
offset, gpuAddress)`. Ranges that are not backed, or that straddle windows, fall back
to a per-command-buffer upload ring.

1. **Buffers have no cache.** Vertex, index, constant, storage and indirect bindings
   are `(window, offset)`. Argument-buffer pointers are `window.gpuAddress + offset`.
   GPU stores land directly in guest memory, so there is no readback, no writeback and
   no dirty tracking for buffers. The windows live in an `MTLResidencySet` attached to
   the queue, so there are no per-encoder `useResource` calls for them.
2. **Images live in an address-keyed cache** (`MetalImageCache`), with the semantics of
   the Vulkan `GuestImageCache`:
   - The key is the `ImageRequest`/`ImageDescription` from shared Libs code.
   - Lookup goes through a page-owner table.
   - CPU writes are detected with the shared HLE `PageGuard` write watch
     (`blockReads: false`). A write marks the image CPU-dirty.
   - A CPU-dirty image is refreshed **on the GPU**: a tiler compute kernel reads the
     tiled bytes straight from the guest window into a linear staging buffer, then a
     blit copies them into the private texture. The blit path is universal: it also
     covers depth, stencil and block-compressed formats.
   - There is no CPU detile, no size cap and no "qualified" descriptor list.
3. **GPU-written images** (render targets, storage images, depth) are marked
   GPU-modified. They are written back to guest memory **on the GPU**: texture → linear
   staging blit, then a retile kernel into the guest window. This happens lazily,
   when one of these overlaps the image:
   - a buffer binding;
   - a different-layout image request;
   - a CPU read request (`TryReadGuest`, flip of a non-cached surface, `RunAfterPendingCommandStreams`).

   An image request with a compatible layout gets an `MTLTexture` view (format
   reinterpretation through `newTextureViewWithPixelFormat` when the bytes per texel
   match).
4. **Recording happens on the GPU worker thread**, as in Vulkan's `GpuWorkerRelay`.
   `CommandStreamQueue` and `RenderExecutor` call `IRenderHost`, which encodes directly
   into the current `MTLCommandBuffer`. The AppKit thread only presents flip textures.
5. **Completion** uses an `MTLSharedEvent` with a monotonically increasing value per
   command buffer. One completion thread waits on it
   (`waitUntilSignaledValue:timeoutMS:`) and runs end-of-pipe label writes and
   interrupts in order. No Objective-C blocks are needed.

## Package layout

Everything is in `src/graphics/SharpEmu.Gpu.Metal/V2/`, namespace `SharpEmu.Gpu.Metal.V2`:

| File | Owns |
| --- | --- |
| `MetalGuestMemory.cs` | no-copy windows, `Resolve`, upload ring fallback, residency set |
| `MetalTiler.cs` + `MetalTilerShaders.cs` | MSL detile/retile kernels for every `TileBlockKind` and element size 1–16; depth widen/narrow |
| `MetalTextureLayout.cs` | port of Vulkan `TextureTransferLayout` (per subresource linear/tiled offsets) |
| `MetalImageCache*.cs` | address cache, page-owner table, dirty/GPU-modified state, overlap/alias, views, eviction |
| `MetalRenderHost*.cs` | `IRenderHost` + `IShaderPipelineHost`: render passes, pipelines, argument buffers, draws, dispatches, barriers, clears |
| `MetalCommandStream.cs` | `ICommandStreamHost` (fills, copies, EOP, flips) + the GPU worker loop |
| `MetalGpuWorker.cs` | the worker relay and the shared-event completion thread |
| `MetalV2Presenter.cs` | the AppKit window, the CAMetalLayer, the GPU worker thread and the flip seam |
| `MetalV2Backend.cs` | `IGuestGpuBackend` (minimal like `VulkanGuestGpuBackend`); selected by default |

The three `IRenderHost`, `IShaderPipelineHost` and `ICommandStreamHost` contracts are
one object (`MetalRenderHost`, spanning those files), because the shared command
translation builds its `RenderExecutor` only when one host implements all three —
exactly as the Vulkan `Presenter` does.

Reuse:
- From the existing Metal package: `MetalNative`, `MetalGuestFormats`, the MSL
  compiler (`SharpEmu.ShaderCompiler.Metal`, argument-buffer layout
  `Gen5MslArgumentLayout`), and the window/drawable presentation in `MetalVideoPresenter`.
- From Libs: shared `RenderExecutor`, `ImageRequestBuilders`, `TileGeometry`,
  `PageGuard`, `ShaderPipelineCache` and pipeline keys.

## Performance rules

- Zero CPU copies of guest data on hot paths. Any `byte[]` per draw is a bug.
- One command buffer per guest submission, split only at flips, waits and host reads.
  Encoders are reused across consecutive compatible passes.
- Pipelines compile asynchronously and are persisted in an `MTLBinaryArchive` keyed by
  shader hash, translator revision and device. Misses compile on a worker. Draws
  using an uncompiled pipeline wait for it; they are never silently skipped.
- Cache selectors and `objc_msgSend` function pointers. Use `delegate* unmanaged`.
  No `Marshal` reflection per call.
- Argument buffers are suballocated from a ring.

### Image transfers

Every tiled surface moves on the GPU, and the only rate worth quoting is guest bytes per
second. `DepthStencilTransferThroughputIsBandwidthBound` and
`ColorTransferThroughputIsBandwidthBound` print the whole transfer and every stage of it;
on an M4, with a 4K RGBA8 render target as the control:

| 4K surface, 40.3 MiB of planes | before | after |
| --- | --- | --- |
| D32S8 upload | 2.31 ms, 18.3 GB/s | 1.85 ms, 22.8 GB/s |
| D32S8 writeback | 2.26 ms, 18.7 GB/s | 1.31 ms, 32.4 GB/s |
| stencil plane detile | 1.02 ms, 17.4 GB/s | 0.49 ms, 36.1 GB/s |
| stencil plane retile | 0.98 ms, 18.1 GB/s | 0.43 ms, 41.1 GB/s |
| RGBA8 upload / writeback | 1.58 / 1.60 ms | 1.60 / 1.62 ms |

A device another process is also using makes the old numbers much worse than the new ones
(the atomics degrade where the dword stores do not), so compare only runs whose control
agrees. Three things shape the rate:

- **A sub-dword element is copied a dword at a time.** One thread covers the element
  group whose destination bytes are one aligned dword, so a one- or two-byte surface no
  longer pays two masked atomics per element. `MetalTilerShaders.WideGroup` derives the
  group from the shared block tables; the one-byte depth kind interleaves x and y in the
  two low address bits, so its retile dword is a 2x2 quad. The variant is bound only when
  the destination side is dword-aligned and the transfer is not a block tail, and a group
  the row cuts short falls back to the atomics, so row padding is never written.
- **A tiled depth plane is written back in one pass.** The kernel binds the texture as
  `depth2d`/`depth2d_array` and tiles the guest plane straight out of it, narrowing to 16
  bits on the way when the guest wanted 16 bits; no staging buffer and no retile.
- **The remaining passes are a staging round trip, and two Metal rules keep them there.**
  Compute cannot write a depth texture, so the way in stays a detile plus a
  buffer-to-texture blit. Stencil leaves a combined texture only through
  `MTLBlitOptionStencilFromDepthStencil`, because reading it in a shader needs an
  `X32_Stencil8` view and the `PixelFormatView` usage that view asks for is what turns off
  the attachment's lossless compression while it renders. A fullscreen render pass
  exporting `[[depth(any)]]` would fuse the upload, but fragment depth is clamped to the
  depth range, so a guest plane holding anything outside [0, 1] would not survive the
  round trip.

## What the first Dreaming Sarah runs measured

The host was built as described and ran the title at five frames a second with the
GPU idle (`GPUEndTime - GPUStartTime` under a millisecond per submission). Five
things were wrong, each found by measuring rather than by reading, and together
they are the difference between 5 and about 108 fps — above the legacy path's 62:

- **No `MTLResidencySet` over the guest windows.** A set holding the 16 GiB backing
  added roughly seventy milliseconds of *scheduling* latency to every submission.
  Naming the five windows with `useResource` per encoder costs five calls instead.
  The design's "persistent residency sets" line is therefore wrong for a set this
  large; `SHARPEMU_METAL_V2_RESIDENCY_SET=1` restores it for comparison.
- **The windows must stay `hazardTrackingModeUntracked`.** With tracking, Metal
  fences every submission that touches a 4 GiB window, which is every submission.
- **Nothing that merely observes an image may end the open encoder.** Reading the
  submission serial through the transfer scope ended the render pass once per draw,
  which on a tile-based GPU stores and reloads the whole framebuffer.
- **A guest cache flush records a scoped `memoryBarrierWithScope`**, not an encoder
  end, when a pass is open.
- **Pass continuation is decided on what Metal bakes into the pass**, not on
  `RenderingState` equality: that struct also carries the clear values of
  attachments which are not being cleared.

Two more that are about the host, not the GPU:

- `waitUntilSignaledValue:timeoutMS:` does not wake this process on a GPU signal; it
  spent its full timeout every time while `signaledValue` already showed the value.
  The completion thread spins briefly and then polls.
- The presenter promotes a completed frame when the next one is published. With one
  slot and a completion wait, a guest that flips faster than the display never has a
  ready frame and the display starves.

A flip is presented by sampling the display surface into a plain BGRA8 drawable, so
an sRGB surface is read through a linear view; sampling it as sRGB converts to
linear and shows the frame far too dark.

## What the host waits measured

Quake's menu spent 1440 ms of every two seconds in 288 host waits while the GPU was
busy for 670, and Void Terrarium's intro spent all of every two seconds in eight to
twenty-four waits of 150 to 250 ms each with the GPU busy for under a millisecond.
Three things were wrong, and none of them was the GPU.

- **A host read waited on one global flag.** `TryReadGuest` waited whenever any
  shader store had been recorded since the last wait, so every label poll, indirect
  argument read and DMA source read serialized the command processor against the GPU,
  and each wait also committed the open command buffer. `MetalGpuWriteRanges` records
  the guest byte range of every recorded store with the serial that finishes it, as
  the Vulkan `GuestBufferCache` does with `_gpuModifiedRanges`; a read waits only when
  it overlaps a range whose serial has not completed, and an already-committed serial
  is waited for without touching what is being recorded.
- **Every encoder made every guest window resident.** `useResource` over the windows
  per encoder asks the kernel to make the whole backing writable-resident for a draw
  that touches one window. That, and not the `MTLResidencySet` as such, is what the
  Dreaming Sarah runs above measured as scheduling latency: on Void it was sixty to
  two hundred and fifty milliseconds between `commit` and the driver starting on the
  submission, with the GPU then running it in ten microseconds. Only the windows a
  submission resolved a range through are named now, plus the window set the
  device-address table covers when a program binds it.
- **A packet-sized fill ran on the GPU.** The command processor writes a dword with
  DMA_DATA and polls it with WAIT_REG_MEM; filling it on the GPU made the poll wait
  for the whole submission. A fill or copy of at most a packet's worth of fully mapped
  bytes runs on the host, which is also where the interpreter already writes its own
  labels. A fill whose range is exactly one cached image clears that image's texture
  instead of writing the bytes and making the image upload again.

Two measurements that did not pay off, kept behind environment variables so they can
be re-run: `-[MTLCommandBuffer waitUntilCompleted]` instead of the shared-event wait
(`SHARPEMU_METAL_V2_WAIT_ON_COMMAND_BUFFER=1`) halved the frame rate, because the
driver still took a hundred milliseconds and more to schedule the submission; and the
old global flag (`SHARPEMU_METAL_V2_CONSERVATIVE_READS=1`) for checking a correctness
question against the range tracking.

A command-buffer completion handler does pay off. It is a global Objective-C block
literal whose `invoke` is an `[UnmanagedCallersOnly]` function, so there is no
per-buffer callback object, and the host parks instead of polling the shared event
every millisecond. `SHARPEMU_METAL_V2_POLL_COMPLETIONS=1` restores the poll.

`[METAL-V2][WAITS]` reports host waits per reason and GPU-write records per origin,
`[METAL-V2][CHURN]` attributes every image upload and writeback to its cause, and
`[METAL-V2][SLOW-WAIT]` names the read, the recorded range it collided with, and the
`commit → kernel → GPU` breakdown of where the submission's latency went.

## Measuring one draw or dispatch

The periodic `[METAL-V2][PERF]` line reports `gpu_busy_ms` per command buffer, which is
where the GPU time is but not *whose* it is. `SHARPEMU_METAL_V2_PROFILE_SHADERS=1` adds
the attribution: every draw and dispatch gets its own command buffer, which is committed
and waited on, and `GPUEndTime - GPUStartTime` is charged to the bound pipeline's shader
hashes. Totals per `(kind, shaders)` key print every two seconds and once at exit as
`[METAL-V2][SHADER-PROF]`, worst first; `SHARPEMU_METAL_V2_PROFILE_TOP` sets how many.

Per-draw timestamps are not an option on this hardware: Apple silicon reports
`supportsCounterSampling:` only for `MTLCounterSamplingPointAtStageBoundary`, which times
a whole encoder, not a draw inside it. The command-buffer split is therefore the measurement,
and it is the reason the mode is opt-in: it serializes the GPU with the host and splits
render passes, so its absolute frame rate means nothing. Only the per-key ratios do.

A hash from such a line is read back with `SHARPEMU_SHADER_MSL_DUMP_DIR` (see
`Gen5MslDump`), which also annotates the emitted dispatcher with guest PCs. Outside the
profiling mode, `[METAL-V2][SLOW-WAIT]` names the last draw or dispatch the slow command
buffer held, which is usually enough to pick the shader to read — but only the *last*
one, so outside this mode a `SLOW-WAIT` figure belongs to the whole buffer, not to the
shader it names.

Every transfer the host encodes — an image upload or writeback, a guest fill or copy, a
texture-to-texture copy, a clear pass — closes its own command buffer in this mode and is
charged to itself. It has to: anything that does not leaves its GPU time in the buffer
the next draw is measured in, which is exactly how Demon's Souls came to look like it had
runaway compute shaders (below).

### What the first measured runs found

- **The guest windows must fit what the device can hold resident.** Every encoder names
  them with `useResource`, so their mapped total is a per-command-buffer residency
  request. Half-overlapping 4 GiB windows map a 16.4 GiB backing as 30.4 GiB; on a 24 GB
  M4 the driver refused *every* submission with `Insufficient Memory`
  (`kIOGPUCommandBufferCallbackErrorOutOfMemory`) — 57 of 57 per interval in Silent Hill,
  which is why it rendered nothing at all, and the same error ended Demon's Souls. The
  layout is now chosen against `recommendedMaxWorkingSetSize`.
- **Image transfers, not shaders, dominate Silent Hill.** With the windows fixed, the
  worst single items are a 3840x2160 `D32SfloatS8Uint` upload at 156 ms and its writeback
  at 163 ms, with everything else in that title in single-digit milliseconds.

  A transfer's sample is not a transfer, though: `ProfileEncoded` charges the label the
  whole command buffer's `GPUEndTime - GPUStartTime`, and those two lines say
  `transfers=4` and `transfers=5`, so each one is a first-ever command buffer holding
  five surfaces and the first touch of every staging slab they allocated. Timed on the
  device on their own (`DepthStencilTransferThroughputIsBandwidthBound`), the same 4K
  D32S8 upload and writeback cost about 2 ms each, not 160, and the stage breakdown shows
  no serialized blit: `MTLBlitOptionDepthFromDepthStencil` moves the depth plane at over
  80 GB/s in both directions and the stencil option at 25-40 GB/s. Read a `transfer`
  sample as an upper bound on a group of surfaces, and use the device benchmark for the
  rate.
- **Demon's Souls does not have runaway compute shaders; the profiler was charging
  dispatches for transfers.** The first reading of these runs was that
  `0xFB8FF966CA63E288` (664 ms), `0x3E9247D2A2F42DBB` (1211 ms) and
  `0xE2609CAFCC903222` (619 ms) spun to the 100,000-step dispatcher cap. Three
  measurements say otherwise.

  Only uploads and writebacks closed their own profiled command buffer; a guest fill, a
  guest copy, a texture copy and a clear pass each left their work in the buffer the next
  draw was measured in, which is what `transfers=N` on a dispatch sample counted. With
  every transfer closing its own buffer, the same dispatches measure 8.1 ms, 18.0 ms and
  below the top twelve, and the title's exit total falls from 2980.7 ms to 517.5 ms.

  The step cap is no longer silent either (see below) and never fires in this title. And
  `SHARPEMU_METAL_V2_DUMP_BUFFERS` shows the loop's descriptor is right: buffer 0 of
  `0xE2609CAFCC903222` resolves to `stride=16 records=65 size=0x410 window=True` with
  head `00000021 41000000 ...` — a trip count of 33 over 64 sixteen-byte seed points. The
  loop is a brute-force nearest-seed pass over a 64x64 image, and a hand-written MSL
  equivalent of it runs in 0.030 ms on an M4 even with the translator's full
  `s[128] v[256]` register-file declaration, so neither the trip count nor the register
  file is the cost.

  What is left is the same finding as Silent Hill's: image transfers. The worst single
  item in Demon's Souls is now a 48x24 `R16Unorm` writeback of 64 KiB at 328 ms.

- **The dispatcher step cap reports itself.** The last field of every stage argument
  buffer points at one word of a host-readable buffer, private to the program's hash,
  which the dispatcher increments on the way out. The v2 host reads the words back after
  each flush and on shutdown:

      [METAL-V2][STEP-CAP] hash=0x109472F03FF5477A stage=fragment invocations=8294400 total=8294400

  which is the hash to pass to `SHARPEMU_SHADER_MSL_DUMP_HASH`. A missing line is now
  evidence: a slow shader with no `STEP-CAP` is loop-bound, not capped.

### The guest write guard

Guest memory being the GPU's memory has one failure mode the snapshot backend did not
have: a write that overruns its range does not fault and does not land in a staging
buffer. It rewrites whatever the guest's own allocator put next door, and the title dies
much later inside its own code on a clobbered pointer — in Poppy Playtime, a `mov
rax,[rcx]` with `rcx = 0x4000000040000000`, two floats where a free-list pointer belongs.

`SHARPEMU_METAL_V2_GUARD_WRITES=1` arms `MetalGuestWriteGuard`. For every write to guest
memory the host declares — a storage-buffer or written binding, a fill, a copy, an image
writeback, and the fills and copies the host performs itself — it snapshots the 4 KiB
either side of the range and compares them again once the GPU has signalled that
submission. A byte that changed, and that no other declared range covers, is reported with
the producer that ran off its end:

    [METAL-V2][GUARD] A GPU-side write changed guest bytes outside the range it declared:
      producer=writeback [writeback format=... extent=1x1 ...] declared=0x...+0x10000
      serial=812 side=after violation=0x... distance=0x20 was=0x... now=0x4000000040000000.

The same switch arms `GuestWriteJournal` (in `SharpEmu.HLE`, because the native fault
dumper in `SharpEmu.Core` is what asks it), a ring of the declared ranges. On a fault the
dumper prints, for every faulting register that is a guest address, which producer had
declared those bytes:

    [LOADER][INFO]   gpu-write-journal rdx=0x... producer=storage [...] range=0x...+0x...

An address no record covers was not written by a declared host-side GPU write, which
points at a shader store through a descriptor, or away from the GPU altogether. A clean
guard with a reproducible fault is therefore evidence too, not an absence of it.

#### What it found in Poppy Playtime, and what it ruled out

The title dies after about a minute, always the same way: a free-list pop in the guest's
own binned allocator follows `0x4000000040000000` out of bin 14 of a 64 KiB-aligned
direct-memory table, at offset `+0x720`. Four runs, two different guest threads, three
different table addresses, the same offset and the same value.

- The guard's journal covered every declared GPU-side write of the run (3013 records,
  below the ring's 8192, so nothing was lost to wrapping) and **none of them covered the
  faulting address** — not an image writeback, a fill, a copy, a storage binding or a host
  fill. The margin check found no producer writing past its range either; its handful of
  reports are the guest's own CPU writing structures next to a declared range while the
  submission is in flight.
- `device_address_writes=0`: this title never stores through the device-address page
  table, so the hole closed above was not the cause here either.
- The fault dump of the table (`fault-register-rax`) shows it otherwise pristine — valid
  `(pointer, count)` pairs throughout, zeros where unused — and bin 14's **count is still
  zero** while its head holds the poison. Exactly eight bytes are wrong, in a slot the
  allocator had never used, so this is a stray eight-byte store and not stale memory, a
  lost write or an overrun.
- `SHARPEMU_WATCH_QWORD=0x4000000040000000 SHARPEMU_WATCH_QWORD_HI=0x90` shows the value
  itself is ordinary guest float data: every hit is `PhysicalVirtualMemory.TryCopy` under
  `DispatchImportCore`, a guest-to-guest `memcpy` import moving kilobyte blocks of
  `2.0f` pairs through direct memory. None of the 54 distinct destinations seen before the
  report cap was the faulting slot.

So the GPU writes nothing that lands there, and the payload is bulk-copied float data.
The next step is a bulk-copy destination watch (`SHARPEMU_WATCH_BULK_TORN` and friends
already exist for this), not more GPU instrumentation.

## Process-level macOS tuning (separate workstream)

- `Info.plist`: `LSSupportsGameMode`, `GCSupportsGameMode`, and
  `LSApplicationCategoryType = public.app-category.games`.
- `NSProcessInfo beginActivityWithOptions:` with UserInitiated | LatencyCritical
  (disables App Nap).
- QoS `USER_INTERACTIVE` on the GPU worker, the presenter and guest main threads.
- `CAMetalLayer`: `maximumDrawableCount = 3`, `displaySyncEnabled` per vsync setting,
  `allowsNextDrawableTimeout`.
- .NET runtime config experiments, measured individually: `TieredPGO`, ReadyToRun,
  `DOTNET_EnableWriteXorExecute=0`, `GCgen0size`. Use Vector128 only: Rosetta reports
  SSE4.2 and no AVX.

## Acceptance

Per title, one run of a signed build: log, screenshots, `[METAL-PRESENT] guest_fps`,
peak RSS. Order: Sarah (regression) → Void → Quake → Superliminal → Oregon → Silent
Hill → Octopath → Demon's Souls → Poppy. A title is done when it reaches controllable
gameplay that renders correctly at its native cadence (60 where the game targets 60).
