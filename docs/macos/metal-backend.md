<!--
Copyright (C) 2026 SharpEmu Emulator Project
SPDX-License-Identifier: GPL-2.0-or-later
-->

# Metal backend

This is an overview for contributors. It describes how the guest's GPU work
becomes Metal work, and where to look in the source. The backend is the
`SharpEmu.Gpu.Metal` package in `src/graphics/SharpEmu.Gpu.Metal/V2`. The
shader translator is `src/SharpEmu.ShaderCompiler.Metal`. A macOS build links
these two and nothing from Vulkan; see [Building](building.md#output-policy).

Behaviour switches are in [Configuration](configuration.md).

## Overview

The shared engine in `src/SharpEmu.Libs/Gpu` decodes the guest's command
buffers and decides what to draw. It talks to a backend through three
contracts:

| Contract | Defined in | Role |
| --- | --- | --- |
| `ICommandStreamHost` | `src/SharpEmu.Libs/Gpu/GpuCommands/ICommandStreamHost.cs` | What the packet interpreter needs: memory fills and copies, end-of-pipe labels, flips. |
| `IRenderHost` | `src/SharpEmu.Libs/Gpu/Rendering/IRenderHost.cs` | What `RenderExecutor` needs: render targets, pipelines, draws, dispatches, barriers. |
| `IShaderPipelineHost` | `src/SharpEmu.Libs/Gpu/Pipelines/IShaderPipelineHost.cs` | What the shader pipeline cache needs: compile and bind programs. |

The command translation layer builds its executor only when one object
implements all three, so `MetalRenderHost` is one class spread over many partial
files (`MetalRenderHost*.cs`, `MetalCommandStream.cs`). The backend object that
the application registers at start-up is
[`MetalV2Backend`](../../src/graphics/SharpEmu.Gpu.Metal/V2/MetalV2Backend.cs)
(`IGuestGpuBackend`). `src/SharpEmu.CLI/GpuComposition.cs` registers the Metal
package at start-up, and the package registers that backend.

```text
guest threads          GPU worker thread                         completion thread
-------------          -----------------                         -----------------
AGC/GNM exports   ->   CommandStreamQueue + packet interpreter   MTLSharedEvent wait
 (src/SharpEmu.Libs)   RenderExecutor -> MetalRenderHost         runs end-of-pipe work,
                         MetalImageCache, MetalGuestMemory         interrupts and flip
                         MTLCommandBuffer encoding                 completions in order
                                |                                         ^
                                v                                         |
                          commit (serial N) -------- signals event N -----+
                                |
                                v
                   window thread: CAMetalLayer present of the flipped texture
```

## Threads

| Thread | Source | Work |
| --- | --- | --- |
| Guest threads | `src/SharpEmu.Libs/Agc` | Submit command buffers. They never encode Metal work. |
| `SharpEmu Metal v2 GPU worker` | `MetalV2Presenter.cs` (`RunWorker`) | Runs the command stream and records everything into the current `MTLCommandBuffer`. Wakes on new work, parks when idle. |
| `SharpEmu Metal completion` | `MetalGpuWorker.cs` (`MetalCompletionQueue`) | Waits for command-buffer completion and runs the work that depends on it. |
| Window thread | `MetalV2Presenter.cs` (`PresentFrame`) | Owns the window and `CAMetalLayer`, and presents. On the CLI path this is the process main thread. |
| `SharpEmu Metal v2 flip pacer` | `MetalV2Presenter.cs` (`RunFlipPacer`) | Promotes finished frames at the window's drawable cadence. The guest's flip event and buffer release follow presentation. |
| Compile threads | `MetalPipelineCompiler.cs`, `MetalLibraryCompilePool.cs`, `MetalShaderPrewarm.cs` | Build pipeline states and libraries away from the worker. |

Other threads reach the worker through `MetalGpuWorkerRelay`, which orders work
before the next command-stream slice without waiting for the GPU. Each worker
pass runs inside an autorelease pool (`MetalAutoreleasePool`), because the
thread has none of its own.

## Command processing

`CommandStreamQueue` (`src/SharpEmu.Libs/Gpu/GpuCommands`) holds the guest's
queues and the PM4 interpreter. On each worker pass `RunWorkerStep` runs the
relay's commands, then up to 64 command-stream slices. Draws and dispatches go
through `RenderExecutor` into `MetalRenderHost`, which records them straight into
the current command buffer.

A command buffer is committed when the stream asks, when an end-of-pipe
completion needs it, on a flip, after 2048 recorded commands, before a host wait
for the GPU, and for a few other reasons (`MetalFlushCause` in
`MetalRenderHost.cs`). The periodic `[METAL-V2][PERF]` line breaks flushes down
by cause. Several guest submissions can share one command buffer
(`SHARPEMU_METAL_V2_FLUSH_COALESCE`).

Metal binds resources per encoder, while the shared executor records them before
a render pass opens. The host therefore keeps the pending draw state in fixed
fields and applies it at draw time. Render and compute encoders stay open across
compatible work, because on a tile-based GPU every render-pass end stores and
reloads the attachments. Operations that only observe an image must not end the
pass, and a guest cache flush is recorded as a scoped memory barrier when a pass
is open.

Relevant files: `MetalRenderHost.cs`, `MetalRenderHost.Rendering.cs`,
`MetalRenderHost.Descriptors.cs`, `MetalRenderHost.Pipelines.cs`,
`MetalCommandStream.cs`, `MetalPassLoads.cs`, `MetalPassStores.cs`.

## Guest memory

On Apple silicon the GPU and CPU share memory, and the backend makes guest memory
the GPU's memory. `MetalGuestMemory` wraps the guest's direct-memory backing as
no-copy `MTLBuffer` windows (`newBufferWithBytesNoCopy`). Vertex, index,
constant, storage and indirect bindings are then `(window, offset)` pairs and
nothing is copied. GPU stores land in guest memory directly.

Naming a no-copy buffer in a command buffer makes the driver wire all of its
pages for that command buffer, so the windows are kept small:

- The backing is cut into non-overlapping windows of about one granule (32 MiB by
  default). A range inside one window binds it. A range across a cut moves the
  cut (`MetalWindowPartition`, `MetalWindowSet`).
- A range that is backed but not contiguous in the backing is read through a
  guest view, a no-copy buffer over the guest's own mapping
  (`MetalGuestMemory.GuestViews.cs`).
- Anything else is staged through a per-submission upload ring
  (`MetalStagingPool`, `MetalStagedStorage`, `MetalStagedRangeCache`).
- Windows are `hazardTrackingModeUntracked`. A submission names only the windows
  its own resolves touched.
- Shader programs that follow raw 64-bit device addresses search a sorted range
  table (`MetalDeviceAddressTable`, `sharpemu_resolve_device_address` in
  `Templates/resource_prelude.msl`). `MetalRenderHost.DeviceAddressReach.cs`
  learns which windows such programs reach and names them in later submissions.

The guest backing itself is chunked purgeable memory by default
(`src/platforms/macos/SharpEmu.Host.MacOS/Memory/MacHostViews.Chunked.cs`).
Single-copy textures (`MetalSingleCopy.cs`) use it: when a clean texture's Metal
copy can give its guest bytes back exactly through a retile, the covered chunks
are emptied, and restored the moment anything else wants them.

Host reads of guest memory (label polls, indirect arguments, DMA sources) must
not wait for the GPU unnecessarily. `MetalGpuWriteRanges` records the guest byte
range of each recorded GPU store with the serial that finishes it, and a read
waits only if it overlaps a range that has not completed. `MetalPendingWriteOverlay`
and `MetalRenderHost.LabelOverlay.cs` answer reads of pending command-processor
writes and end-of-pipe labels without a wait. `MetalGuestWriteGuard` is a debug
check that GPU writes stay inside their declared ranges.

## Images and textures

`MetalImageCache` stores images by guest address range:

- Lookup goes through a page-owner table. The key is the `ImageRequest` and
  `ImageDescription` built by shared code in `src/SharpEmu.Libs/Gpu/Images`.
- CPU writes are caught by the shared `PageGuard` write watch
  (`src/SharpEmu.HLE/GpuMemory/PageGuard.cs`) and mark an image CPU-dirty. A
  dirty image is refreshed on the GPU.
- Render targets, storage images and depth images the GPU writes are marked
  GPU-modified. They are written back to guest memory lazily, when a buffer
  binding, an image of another layout or a CPU read overlaps them.
- A request with a compatible layout gets an `MTLTexture` view, reinterpreting
  the format when bytes per texel match.

Tiled guest surfaces never pass through the CPU. `MetalImageTransfer` moves
every image between guest memory and Metal textures with the compute kernels of
`MetalTiler` (MSL source in `MetalTilerShaders.cs`, layout math in
`MetalTextureLayout.cs`), into a linear staging buffer and then a blit per
subresource. That path also covers depth, stencil and block-compressed formats.

Memory limits:

- `MetalImageBudget` sizes the cache from the memory the host has free and the
  kernel's memory-pressure level, and evicts images that are idle
  (`MetalImageCache.Budget.cs`). Eviction writes the texture back first.
- Large array textures are tile-backed only where the guest put data
  (`MetalImageCache.Sparse.cs`). A sampled-only texture is allocated from the
  descriptor's `BASE_LEVEL` down, because titles that stream textures release the
  finer levels and the GPU never samples them (`MetalImageCache.BaseLevel.cs`).
- `MetalHostMemory.cs` reads the host's memory state for the budget.

Other cache behaviours (DCC metadata, depth twins, kernel fills, alias handling)
are in the other `MetalImageCache.*.cs` files.

## Synchronization

Each committed command buffer gets a serial. The serial is signalled on one
`MTLSharedEvent` when the command buffer finishes. The completion thread waits on
the event and, in serial order, runs end-of-pipe label writes, interrupts and
flip completions. A global Objective-C completion block
(`MetalCompletionSignal.cs`) wakes it, so it does not poll.

The worker waits for the GPU only when it has to: a host read that overlaps an
unfinished GPU write, and a few other host paths. Each wait is counted by reason
(`MetalWaitReason`), and the periodic counter line reports them. A wait that
takes more than 20 ms logs `[METAL-V2][SLOW-WAIT]`.

A flip hands the cached image's `MTLTexture` to the window thread together with
the serial it needs. The window thread presents once that serial has completed.
The frame never travels through the CPU. HDR output uses an extended-range
drawable (`MetalV2HdrOutput.cs`, `MetalV2Presenter.Hdr.cs`).

## Shader translation

`SharpEmu.ShaderCompiler` decodes guest shader instructions into an IR
(`Gen5ShaderTranslator.cs`, `Gen5ShaderIr.cs`). `SharpEmu.ShaderCompiler.Metal`
turns a compile request into Metal Shading Language source
(`Gen5MslTranslator*.cs`), which the host compiles with `MTLDevice` at run time.

- Resources reach a shader through one argument buffer per stage. Its layout
  mirrors the binding layout (`Gen5MslArgumentLayout.cs`). The host fills it
  directly.
- `Templates/*.msl` hold the fixed helper code ("preludes") that a translated
  module includes on demand: format loads, buffer offsets, coherent loads, pair
  atomics, 64-bit float, ray-box intersection and mip statistics.
- Control flow is emitted structured (`if` and `while`) when the program's
  control-flow graph allows it, with a program-counter dispatcher as the
  fallback (`Gen5MslTranslator.Structured.cs`). Guest registers are declared
  only as far as the program uses them.
- Primitive-shader (NGG) programs become Metal mesh functions
  (`Gen5MslTranslator.Mesh.cs`) and run as mesh render pipelines
  (`MetalRenderHost.MeshIndirect.cs`, `MetalMeshletCache.cs`).
- A step cap on translated loops stops a mistranslated loop from hanging the GPU.
- `Gen5MslCodegenOptions.cs` lists the code generation switches. Most of them are
  optimizations that an environment variable turns off for bisecting.
- `Gen5MslDump.cs` writes translated MSL and the guest instruction listing for
  a shader hash. See [Configuration](configuration.md#diagnostics).

`MetalV2Backend.TryCompileProgram` calls the translator.
`MetalRenderHost.Pipelines.cs` builds the pipeline states, keyed by
`MetalPipelineStateKey`, and the shared shader pipeline cache keeps the compiled
programs.

## Shader and pipeline caches

Compiling MSL and pipeline states is the slowest part of a first run, so the
backend layers several caches. All of them are per title, under
`user/pipeline_cache/<title ID>/metal-v2/` in the data root, except the macOS
system cache.

| Cache | Source | Content |
| --- | --- | --- |
| Binary archive | `MetalBinaryArchive.cs` | The title's own `MTLBinaryArchive` files: one file per compiled library, which stands in for the MSL front end (and holds the pipeline binary for a compute program), plus segment files of render pipelines. Files carry a trailer and checksum, are bounded in size (8 GiB by default) and are discarded when the device, macOS version or format changes. |
| Prewarm lists | `MetalShaderPrewarm.cs`, `MetalLibraryPrewarm.cs` | Programs a session compiled. The next start compiles them again before the guest needs them, and holds the guest at its first frame until the first frame's programs are ready. A progress bar is drawn over the splash meanwhile. |
| Static precompile | `MetalStaticPrecompile.cs`, `MetalShaderPrecompile.cs` | Programs the game has not used yet, found in the shader inventory and in the game's files, compiled in the background behind the game. `--precompile-only` runs it to completion. |
| Device-address reach, data-load slots | `MetalRenderHost.DeviceAddressReach.cs`, `MetalDeviceDataLoadsStore.cs` | Small per-title profiles learned from a previous run. |
| macOS shader cache | the system | The driver's own per-app cache, which makes a repeated compile of the same source cheap. |

`MetalCompileScheduler` orders the driver's compiler between the game's own
compiles and the background work, so the background work yields to the game.
`MetalPipelineCompiler` builds pipeline states off the worker thread; a draw
whose pipeline is not ready is held or left out rather than blocking the worker.
`MetalRenderHost.ShaderCache.cs` releases idle modules and pipelines past a
limit.

## Tests

The Metal tests, including the device-gated ones, are described in
[Testing](testing.md).
