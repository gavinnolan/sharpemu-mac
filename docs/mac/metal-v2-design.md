<!-- Copyright (C) 2026 SharpEmu Emulator Project -->
<!-- SPDX-License-Identifier: GPL-2.0-or-later -->

# Metal v2 render host

Status: accepted 2026-10-10. Replaces the snapshot/admission Metal path as the default.

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
| `MetalCommandStream.cs` | `ICommandStreamHost` (fills, copies, EOP, flips) + GPU worker thread + completion thread |
| `MetalV2Backend.cs` | `IGuestGpuBackend` (minimal like `VulkanGuestGpuBackend`); selected by default |

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
- Persistent residency sets. Argument buffers are suballocated from a ring.

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
