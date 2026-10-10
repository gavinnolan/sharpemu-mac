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
buffer held, which is usually enough to pick the shader to read.

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
  at 163 ms — about 200 MB/s for a 33 MB surface, two orders of magnitude off what the
  blit should cost. The stencil plane goes in through
  `MTLBlitOptionStencilFromDepthStencil`, one texel at a time as far as the GPU is
  concerned. Everything else in that title measures in single-digit milliseconds.
- **Demon's Souls has runaway compute shaders.** `0xFB8FF966CA63E288` ran one command
  buffer for 2449 ms, and `0xE2609CAFCC903222` spent 619 ms on 4096 threads. Both loop on
  a counter compared against a dword loaded from a constant buffer (`s14 = 0;
  s15 = SBufferLoadDword; while (s14 < s15)`), which the translator emits correctly, so
  either the loaded bound or the buffer behind it is wrong and the loop runs to the
  100,000-step dispatcher cap. That cap is silent: nothing on the host says a shader hit
  it, and making it observable is the next step.

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
