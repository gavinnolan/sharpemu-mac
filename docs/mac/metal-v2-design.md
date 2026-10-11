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

  The sample that then looked worst, a 48x24 `R16Unorm` writeback of one 64 KiB tile at
  328 ms, is the same artifact again: on the device that transfer costs 0.20 ms and its
  upload 0.22 ms (`ASmallTiledSurfaceTransfersInMicroseconds`).

- **Demon's Souls renders once a large image is moved in pieces.** The title drew
  nothing for its whole run — `published=0` on every `[METAL-PRESENT]` line — and the
  only other symptom was one warning about a guest range smaller than its image. That
  image is the shadow atlas: `D32Sfloat`, 1024x1024, 80 layers, `0x14000000` bytes.

  Guest memory could not map it, and the reason matters: it is not one backed span at
  all. `DescribeResolve` reports a contiguous prefix of about `0x3FFC00` — under a
  single 4 MiB layer — so no window arrangement would ever have covered it whole, and
  the ring fallback truncates at 64 MiB. The transfer was dropped, the atlas never got
  its contents, and the title never reached a flip.

  Moved in runs of eight layers, every piece resolves and the whole atlas lands:

      [METAL-V2][INFO] An image is transferred in pieces because it does not map or
      stage whole: address=0x0000007144700000 size=0x14000000 layers=80 batch=8
      (not one backed span (contiguous prefix about 0x3FFC00 of 0x14000000)).

  Demon's Souls then runs its render loop and presents: 465 frames in one six-minute
  run at 3-5 fps, `flip_attempts` and `flip_published` equal on every interval, 1228
  draws and 127 passes per two seconds across render targets from 1280x720 up to a
  2048x2048 shadow map. The frames are still black, which is now a content question and
  not a presentation one. Dreaming Sarah is unaffected and holds 60.00 fps.

- **Demon's Souls' black frames are not a presentation fault, and they do not stay
  black.** The title presents, and its first minutes of frames are black. Four things were candidates: the flip resolving to a different
  image from the one the passes wrote, an HDR or tiling mismatch, the display surface
  being refilled from guest memory on the way to the screen, or the frame genuinely
  being black. `SHARPEMU_METAL_V2_DUMP_TARGETS` and the flip-resolution line rule out
  the first three.

      The first published flip resolved to image={Index 77, Generation 1}
      texture=0x7FA6D51AA0B0 address=0x0000007057F00000 3840x2160 tiling=0 hdr=False
      description=A2B10G10R10UnormPack32 view=A2B10G10R10UnormPack32
      gpuWritten=True renderTarget=True.

  The flip lands on the image the passes wrote, through a view of its own format; the
  surface is not HDR and not tiled; and `flip_refreshed` is zero on every interval, with
  the surface dumped either side of its refresh reading identically. Nothing between the
  composite and the screen touches the pixels.

  What the dumps show instead is that **every colour target in the frame is exactly
  zero** — 98 dumps across one run, all `max_rgb=0`, none with a single lit pixel, while
  the passes that wrote them recorded 1 to 48 draws each and every colour attachment
  loads rather than clears. The chain is self-consistently black: fullscreen post passes
  sample inputs that are zero and write zero.

  Two things narrow where to look next. Every pass dumped in the loading phase shares one
  vertex shader, `0x29F08899182DFBF7`, which reads as an NGG primitive shader
  (`s_sendmsg` for export space, an `EXP_PRIM` export the translator emits nothing for)
  but is a correct procedural fullscreen quad: it exports POS0 as
  `(2*i0-1, 1-2*i1, 1, 1)`. So the geometry of these passes is fine and they do cover
  their targets. And no scene-geometry pass appears at all in that phase, which is what a
  black loading screen looks like — a 330 s run did eventually dump a 4K target at
  `mean_rgb=67` with recognisable silhouettes and a light source, so the engine does
  start drawing content.

  And the frames stop being black on their own. In an eight-minute run the window is
  still black at 4:51 and by 7:46 it is drawing the scene: architecture, stairs, terrain
  silhouettes, a light source — recognisably Boletarian Palace, at 2 fps. So the title
  renders; what it renders is wrong.

  The corruption has a shape worth naming: a flat mid-grey everywhere, overlaid with
  saturated green, magenta and purple speckles in **square blocks** that follow the
  geometry's edges. Uniform blocks rather than per-pixel noise is what a block-compressed
  or metadata-compressed surface read without its metadata looks like, which makes DCC on
  the colour targets the first thing to check — `ImageDescription.ClassifyDisplayCompression`
  and `MetadataKind.Dcc` are where that decision is made.

  The dumper missed this entirely: it spent its budget on the first few occurrences of
  each texture, and those are all in the loading phase. It now samples one frame's
  targets every `SHARPEMU_METAL_V2_DUMP_EVERY` flips (30 by default), so the next run
  dumps the frames that have something in them.

- **Demon's Souls' corruption is not a DCC fast clear.** The frames it draws are a
  blue-grey field with saturated speckles, which reads as a compressed surface shown
  without its metadata, so the DCC path was the first suspect. It is not the cause.

  Metal v2 already carries the Vulkan metadata machinery nearly line for line —
  `RegisterDccMetadata`, `TryAbsorbDccFill`, `MaterializeDccClear` with its real-pixel
  `ClearImageRange`, `TryConsumeDccAttachmentClear`. One gap was real and is now closed:
  Vulkan has a second pass, `RecordSampledColorMetadataClears`, for a surface that was
  fast-cleared and is *sampled* before it binds as a colour target again, which
  `MaterializeDccClear` cannot serve because a texture request carries no metadata range
  and the register clear colour is known only to a target. v2 had no equivalent.
  `AcquireTextureView` now materializes it, reported as `sampled_dcc_clears`.

  It fires zero times in Demon's Souls, and the frame is unchanged: `mean_rgb` 135.06
  before, 134.32 after, pixel for pixel the same corruption. The fix closes a parity hole
  that will matter to some other title; it is not this one, and it is unverified by any
  title that exercises it.

  What the pixels actually say, from a dumped 4K flip: the dominant colour is a *cluster*
  around (130,136,152) rather than one flat value — a smooth low-contrast field, not a
  fill — and the speckles saturate (75,255,25). Horizontal runs of both are dominated by
  **two pixels**, and the frame has a sharp discontinuity across its **vertical
  midpoint**. Two-pixel granularity and a half-height seam are a tiling or element-stride
  mismatch, not a block-compression one: DCC keys cover 8x8 texels at 32 bpp and would
  not alternate every two. That is where to look next, with
  `SHARPEMU_METAL_V2_DUMP_TARGETS` and the 1920x540 target the frame also carries.

- **What the Demon's Souls corruption is not.** The two-pixel granularity and the
  apparent seam across the frame's middle both dissolved under measurement, and three
  candidate mechanisms are ruled out. Recorded so nobody walks them again.

  *No MSAA anywhere.* All eleven distinct render-pass shapes the title uses report
  `samples=1`, so two horizontally interleaved samples cannot be the source of a
  two-pixel pattern.

  *No seam.* Scanning the dumped 4K flip row by row, the dominant colour's share varies
  smoothly with image content (0.48 at the top, 0.04 around y=1140, 0.46 at the bottom).
  The "seam" was an artefact of looking at a downscaled view.

  *The two-pixel runs are an upscale, not a stride.* They appear in both the dominant
  field and the speckles, which is what a half-resolution source point-sampled to 4K
  looks like; a wrong element size would not alternate in both.

  *The froxel volumes are not one surface described two ways.* The input dumps show
  `R16G16B16A16Sfloat Color3D Standard64KB 214x120x72 L1A1 bytes=0x1180000` and
  `R16G16B16A16Sfloat Color2D RenderTarget 214x120 L1A72 bytes=0x1200000`, which looked
  like one volumetric grid seen as a 3D image by its reader and a 72-layer array by its
  writer — a round trip through guest memory under two tile modes would corrupt it
  exactly this way. They are at different addresses (`0x708CE80000`/`0x708E000000`
  against `0x7066EF0000`), so they are different surfaces and no such conflict exists.

- **Where the Demon's Souls frame actually comes from.** Dumping every pass of a sampled
  frame in order, with the textures each one binds, gives a frame that is not what it
  looked like. Every scene-sized target — 1280x720 and 2560x1440, the G-buffer and
  lighting shapes — is `max_rgb=0`, pure black, though its passes record 1 to 48 draws.
  All the content arrives in about twenty **single-quad draws into a 4K target**
  (`vs=0x91C625E416E34368 ps=0x17A41E9C3C41BE38`), which is a composite of sampled
  surfaces, not rendered geometry. The recognisable architecture is therefore the
  loading screen's artwork, not Boletarian Palace being rendered: the title never gets
  past loading in the time we can run it, and the first thing to fix for a correct title
  screen is the boot, not the renderer.

  The one concrete anomaly among those inputs is a **flat bright green
  `R8G8B8A8Unorm RenderTarget 1280x720`** surface, uniform to the pixel apart from a thin
  strip at the bottom. Green is also the dominant speckle colour in the composited frame.
  It is not a DCC fixed clear — `TryDecodeDccColor` can only ever produce 0.0 or 1.0 per
  channel, never the ~(0.5, 1.0, 0.5) this surface holds — so where it comes from is the
  open question and the next thing to chase.

- **Demon's Souls is loading, not stuck.** `SHARPEMU_LOG_LOAD_PROGRESS=1` prints, every
  two seconds, the bytes the guest's reads and AMPR command buffers delivered, the
  running total, how many intervals in a row delivered nothing, and what every guest
  thread is doing — naming each blocked one with the reason it blocked, the key it waits
  on, its last HLE call and how many calls it made that interval.

  Over 150 seconds the title read **1.67 GB** through AMPR, in bursts of 20-35 MB per
  interval separated by quiet gaps of up to thirteen intervals (26 s). Through every gap
  15 to 18 of its 26 threads kept taking HLE calls, so the guest is computing, not
  waiting on us. `ampr_unknown` is zero throughout: every file id resolves. The threads
  parked through the longest gap are the four Bink movie threads, `Core.Res.Decompressor`
  and `Core.Res.FileLoader`, all on their own `pthread_cond_wait`, with
  `Core.Res.TaskManager` still cycling — the resource pipeline is request-starved between
  bursts, not blocked on a primitive the emulator never delivers.

  AMPR cannot hang in any case: submission is synchronous
  (`sceKernelAprSubmitCommandBufferAndGetResult` replays the queued commands on the
  calling thread) and there is no Kraken or Oodle stage in it — a read-file record is a
  raw host read into guest memory. So the boot is slow, and what makes it slow is guest
  CPU work between streaming bursts, which is the same cost the worker-side accounting
  found.

  The flat bright green `R8G8B8A8Unorm` 1280x720 surface left over from the previous
  investigation is never a render-pass target: it binds only as a storage image and a
  sampled texture, so a compute shader writes it. A compute shader whose own inputs are
  zero producing a constant is consistent with the rest of the frame rather than an
  independent fault, so it is downstream of the blackness, not its source.

- **Demon's Souls' long boot is host-side command-stream work, not the GPU.** Over one
  454-second run the GPU worker spent 14.3 s executing GPU work (3%), 49.8 s waiting on
  the GPU (11%), 21.9 s idle (5%) and **368 s — 81% — in its own CPU work**: interpreting
  packets, resolving descriptors, keeping the image cache. The guest sent 371,022
  submissions for 93,632 draws, four submissions per draw, and the queue ran 952,889
  slices against them: **2.57 slices per submission**, because a submission that suspends
  is re-queued and re-run from its suspend point. Better than half a million slice runs in
  that one run were retries. Neither GPU throughput nor GPU stalls are the boot's cost.

- **Where a flip stops is reported.** `published=0` alone cannot tell a title that never
  flips from one whose flips are released or discarded on the way to the presenter, and
  all three printed nothing. Every flip reaching `CaptureFlip` now counts as an attempt
  and lands in one outcome, on the `[METAL-PRESENT]` line as `flip_attempts`,
  `flip_no_buffer`, `flip_discarded` and `flip_published`. For Demon's Souls before the
  fix all four were zero, which ruled out the presenter entirely and pointed at the
  title not asking — which it was not, because it was still waiting on the atlas.

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

The HLE memory subsystem was then ruled out as well, in its own right:

- A value watch filtered to the slot (`SHARPEMU_WATCH_QWORD=0x4000000040000000`,
  `_LOW=0x720`, `_LOW_MASK=0xFFFF`) saw **no managed guest write at all** land that value
  in a `+0x720` slot, over a whole run that ended in the usual fault. That covers every
  HLE import, `TryCopy`, `TryWrite` and the GPU command-memory path.
- Arming any watch disables the native `memcpy` stub, so those runs are also runs with
  `SHARPEMU_HLE_FAST_MEMCPY=0` - and `SHARPEMU_HLE_FAST_PATH=0`, which disables all three
  stubs, reproduces the fault identically too.
- Guest-to-guest copies over fragmented backing are correct, including when the source's
  pieces and the destination's split the payload at different points, for plain,
  overlapping and edge-straddling copies alike (`BackingCopyFragmentationTests`).
- No mapping was recorded over guest bytes, or over backing bytes, that another live
  mapping still held: zero reports from either check across a full run.
- The allocator's mutex really excludes: real threads through the HLE exports, counting a
  guest counter, with no lost acquisitions (`PthreadMutexExclusionTests`).

And the dump of the neighbourhood settles what the write was. The 0x100 bytes around the
faulting entry are **entirely zero** except the eight bytes that faulted, with the count
beside them still zero. Not an overrun, not a mispaired copy piece, not stale memory, and
not an index that left its table: one targeted eight-byte store of `float2(2.0, 2.0)` into
a slot the allocator had never used.

What was left wrote those bytes from the GPU, and the slot trap caught it doing so.

#### The slot trap, and what it found

`SHARPEMU_TRAP_SLOT=0x720 SHARPEMU_TRAP_BAND_HI=0x90 SHARPEMU_TRAP_VALUE=0x4000000040000000`
names a structure by its offset inside an aligned block rather than by an address the run
chooses, adopts every mapping of that shape in that band, and reports the moment one of
those slots takes the value being hunted. Two halves, armable apart:

- the CPU half write-protects the slot's page through `PageGuard` and reports the guest
  RIP, instruction and registers of the store that faults. It caught the allocator's own
  push — `mov [rcx],r15` storing a pointer — and never the corruption. Protecting a
  hundred and fifty pool pages costs a signal per write and stalled the guest's clock at
  27 s, so `SHARPEMU_TRAP_PAGE_GUARD=0` leaves it off;
- the GPU half reads the slots at every submission commit. It costs a few bytes per commit
  and does not perturb the run, and it is the half that answered:

      [METAL-V2][SLOT-TRAP] A watched slot took the hunted value: slot=0x0000009003260720
        was=0x0000000000000000 now=0x4000000040000000 at_commit_of_serial=4142
        slot_resolves_to=buffer=0x7F8EA0A1D170+0x16370720 cpu_faults=0

  `cpu_faults=0`: no CPU write faulted on that page. The GPU wrote it, and no submission
  in flight had declared a destination anywhere near it.

#### The cause: end-of-pipe labels published before the GPU runs

V2 publishes an end-of-pipe label when the command processor parses it, not when the GPU
finishes the work it ends. The guest polls the label, believes the GPU is done with a
surface, frees it, and the allocator hands those bytes out again — while the writeback or
transfer that was still queued lands in them. The float payload is the old owner's; the
slot is the new one's.

Measured both ways on the same build, same key plan:

| | result | slot trap | guest |
| --- | --- | --- | --- |
| default | `rc=139 after 37-63 s` | poisoned, every run | MOB logo, then dead |
| `SHARPEMU_METAL_V2_DEFER_EOP=1` | **alive at 110 s** | never fired | **main menu**, clock advancing |

Deferring the labels to GPU completion is correct and slow (1-6 fps while it waits); the
efficient form is GPU-timed label writes.

### The Oregon Trail

The title renders essentially correctly — pixel-art scene, starry background, card
panels, all in the right places and colours. One thing is visibly missing: the **text**
inside the UI panels, which come out as flat dark green with faint lighter cells where
glyphs belong. The panel chrome and the little arrow glyph draw, so it is the text that
is absent rather than the panel.

Frame rate depends sharply on the phase. In the light phase it holds 48-58 fps; on the
card-selection screen it drops to **31-32** against a 60 fps target. In that heavy phase
the GPU is busy 54% of wall and the GPU worker is **idle 80%** with zero host waits, so
the cost is GPU-side, not the worker's.

What the GPU is spending it on is render-pass restarts. Per two-second interval the heavy
phase records `passes=1325` against `draws=4800` — under four draws a pass, about twenty
passes a frame — and the pass-end breakdown says where they come from:

    end_clear=0 end_target=0 end_transfer=1205 end_compute=0

Almost every pass is torn down to record a transfer, not because the guest changed render
target. Every such teardown costs the attachment a store and a reload, and at 1920x1080
`B10G11R11UfloatPack32` that is several megabytes each way, twenty times a frame.

`transfer_causes` says which transfers, and it is not the uploads:

    transfer_causes=[Upload=5 ColorClear=76 GuestFill=67]

So roughly 95% of the teardowns are standalone colour clears and guest buffer fills. Both
have a route out that uploads do not: a clear recorded as the next pass's load action
costs nothing, since the attachment descriptor already distinguishes `IsClear`, and a
guest fill that does not touch the open render target could be deferred to the next
natural encoder boundary or done on the host. An upload bound mid-pass genuinely has to
end the encoder, and there are only a handful of those.

What those teardowns actually are is not what the first reading said. `end_transfer` is
incremented inside `EndEncoders`, which `BeginTransfer` and `FlushCommandBuffer` both
call, so it counts every pass closed on the way to a transfer *or* a flush rather than
transfers alone. Counting the transfers by cause, and counting how a guest clear arrives,
names the real source:

    transfer_causes=[Upload=22 ColorClear=1490 GuestFill=1490]
    clears=[no_pass=0 empty_pass=0 drawn_pass=0]

`ClearColorTargets` is never called — the guest issues no clear at all. The 1490 colour
clears and 1490 guest fills are one pair each, and that pairing is `MaterializeDccClear`:
a real clear of the image followed by the fill that writes the already-materialized
sentinel into the guest's DCC metadata. Twenty-four materializations a frame, two
render-pass teardowns apiece.

They repeat because the sentinel is written by the GPU while the check that reads it back
is a host read of the same bytes. Between the fill being recorded and the GPU running it,
every lookup of that surface sees the old uniform clear code and materializes again.

The cache now remembers which metadata slice and clear code it has already applied and
skips the repeat, keyed on the code as well as the address so a different clear colour is
a new clear. Guest memory is written exactly when it was before. The re-arm lives in
`TryAbsorbDccFill` and **only** there: an earlier version also re-armed from
`InvalidateMemoryFromGpu`, which `MaterializeDccClear`'s own sentinel fill reaches, so
every materialization immediately forgot the claim it had just made and the set
suppressed nothing — the A/B caught it, with the skip counter at zero in both arms.

Measured back to back on a quiet machine, same input plan, 150 s each, with
`SHARPEMU_METAL_V2_DCC_MATERIALIZED_SET=0` as the control:

| | on | off |
|---|---|---|
| colour clears | **466** | 6544 |
| repeats skipped | **3074** | 0 |
| pass teardowns | **2627** | 9127 |
| peak frame rate | 58.8 | 57.3 |

The card screen renders identically in both arms — panels, artwork, glyphs all present —
so the panel loss seen while chasing an earlier variant of this was machine load, not the
change. Frame rate is not the discriminator here: both arms reach about 58 fps once the
machine is quiet, and it is the counters that separate them.

Separately, the panel text is **not** globally broken: the same run renders a full
sentence of crisp white text on the loading screen. Whatever is missing from the card
panels is specific to them.

Still open from the earlier triage: `A GPU-written image has no writable guest window:
address=0x801C6F0000 size=0x400000`, now with its reason — *not one backed span
(contiguous prefix about 0x240000 of 0x400000)*. The piecewise writeback added for Demon's
Souls only splits an image whose layers are separable, so a single-layer 4 MiB surface
still falls back to the whole-range resolve and is skipped.

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
