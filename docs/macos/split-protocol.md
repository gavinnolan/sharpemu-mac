# Two-process split: wiring contract (Phase 2)

Status: contract for the implementation, 2026-10-24. Design: `SPLIT-PLAN.md` (repo-external draft), Phase 0
rates: commit `fc963f02`, shared memory: `docs/macos/split-shared-memory.md`, transport: `src/SharpEmu.GpuRemote`.

Flag: `SHARPEMU_GPU_PROCESS=1`. Off by default; nothing changes without it.

## Roles

| Process A (x86-64 under Rosetta, `SharpEmu.CLI`) | Process B (native arm64, `SharpEmu.GpuHost`) |
|---|---|
| Guest CPU, HLE exports, kernel event queues, VideoOut state machine | GPU worker: command interpreter (`SharpEmu.Libs` GPU engine) + Metal v2 render host + image cache |
| Owns guest memory, page protections (`mprotect`), the write-fault signal handler | Guest memory through the shared backing (`GuestReplica` at guest address + K, `GuestViewTable`) |
| `GuestGpuMemory` + `PageGuard` used by the fault handler | Its own `GuestGpuMemory` over a remote address space; its `PageGuard` asks A to apply protections |
| Registers a proxy `IGuestGpuBackend` (`GuestGpu.Register`) | Registers the real `MetalV2Backend` and calls it from the message loop |
| Input from B's events into the HLE pad/keyboard state | The window, `CAMetalLayer`, present loop, input capture (AppKit main thread) |

## Measured rates that shape it (Phase 0)

GTA street / Wukong intro: write faults 7-87/s and 1-3/s (synchronous is fine); managed writes ~1.2M/s and
150-290K/s (must be filtered locally in A); shader-header lookups ~100K/s and 9-19K/s (must be local in B);
page watches 100-2600/s (batch); submissions 30 and 200-350/s; SubmitDone/flips ~15/s; mapping changes <= 1/s.

## Messages

All records are little-endian blittable structs on the transport rings; sync calls use `SyncCallTable`
(A -> B, signal-safe on the A side). Add new kinds at the end of the enums; bump `GpuRemoteProtocol.Version`.

### A -> B, asynchronous (ring)

| Message | Fields | Semantics |
|---|---|---|
| `SubmitCommandStream` | queue, address, dwordCount, submissionId, viewLogSequence | B first `GuestReplica.Sync(viewLogSequence)`, then `MetalV2Backend.SubmitCommandStream`. |
| `FlipRequest` | videoOutHandle, displayBufferIndex, address, width, height, pitch, flipRequestId | `TrySubmitGuestImage`; the result (accepted/refused) goes back as `FlipAccepted`. A treats it as accepted when it sends (as today the call only enqueues) unless B refuses. |
| `CpuWrite` | address, size, flags (landed, precise) | Only for ranges whose pages are set in the GPU-interest summary (below). B calls its stores' `MarkCpuWrite`/`MarkPreciseCpuWrite`/`MarkCpuWriteLanded` in ring order, before it processes any later submission. |
| `ShaderHeader` | codeAddress, headerAddress | Mirrors `AgcExports._shaderHeadersByCode` into B (registration is rare; lookups stay local in B). |
| `DisplayBuffers` / `VideoOutState` | handle, buffer table, title id, ... | Whatever `VideoOutBackendServices` lookups the Metal host makes (`TryGetDisplayBufferInfo`, title id, HDR) are mirrored. |
| `Control` | EnsureStarted(w,h), HideSplashScreen, RequestClose, Submit(splash frame bytes), CountShaderCompilation | One-way calls of `IGuestGpuBackend`. |
| `ProtectAck` | sequence | A applied every `ProtectRequest` up to `sequence`. |

### A -> B, synchronous (`SyncCallTable`)

| Opcode | Request | Response | Semantics |
|---|---|---|---|
| `SubmitDone` | - | IdleOutcome | B returns when its command stream reaches the frame boundary (`CommandStream.Done()`). |
| `RunAfterPending` | - | done | B answers when every accepted command stream ran; A then runs the `Action` itself. |
| `Fault` | kind, address | handled | From A's signal handler: B runs its stores' fault path (`MarkCpuWrite` / `DownloadToCpu`). Must be answered without B waiting on A. |
| `MappingChangeBegin` / `MappingChangeEnd` | viewLogSequence | ack | `GuestGpuMemory.RunMappingChange` in A: Begin parks B's GPU worker at a safe point (the relay's `RunOnGpuQueue` semantics); A performs the change (the view log records it); End makes B sync its replica and resume. |
| `Query` | which, args | value | `IsGpuGuestImageAvailable`, `SupportsPrimitiveShaderMeshPipelines`, perf counters, `HasFlipSlot`/flip state if A needs them. |

### B -> A, asynchronous (ring)

| Message | Fields | Semantics |
|---|---|---|
| `ProtectRequest` | sequence, address, size, protection | B's `PageGuard` (watch arm/drop) asks A to `TryProtect`. B must not treat a newly watched page as clean (upload, then rely on a write fault) until `ProtectAck >= sequence`; B waits for the ack before it reads the bytes it just armed (a short spin + doorbell wait; the A dispatcher thread applies requests immediately). |
| `Interrupt` | eventId, contextId | `EndOfPipeEvents.TriggerInterrupt` in A. |
| `FlipComplete` / `FlipPresented` / `FlipDiscarded` | flipRequestId | `VideoOutBackendServices.CompleteFlipOnGpu` / `MarkFlipPresented` / `DiscardFlip` in A. |
| `FlipAccepted` | flipRequestId, accepted | See `FlipRequest`. |
| `Input` | device, payload | Keyboard / controller state from B's window into A's HLE input. |
| `GpuInterest` | (none; shared memory) | B maintains a page bitmap in shared memory: bit set while any image/buffer store holds a GPU copy or a watch on the page. A's `CpuWrite` filter reads it. |

## Memory

- B attaches the guest backing (`MacOSHost.ConnectGuestMemory`), a `GuestReplica` at guest address + K and the
  `GuestViewTable`. B's remote address space implements `ICpuMemory`, `IGuestBackedSpace` and
  `IGuestAddressSpace` over it: `TryResolveBackingAlias` from the view table, `BackingAliasRange` = B's mirror of
  the backing, `TryResolveBackedHostView` = the replica (address + K), `TryProtect` = `ProtectRequest`,
  allocation members unsupported (the GPU side never allocates guest memory).
- Memory that is not in the backing (module images, the guest allocator arena with the VideoOut labels, HLE data)
  is moved into the backing by the private-memory work; until then a B read there fails loudly with the address.

## Teardown and failure

- B exits when A exits (already). A's proxy fails every pending call and stops the guest with a clear error when
  B dies (`GpuProcessHost.HasExited`).
- Every wait has a timeout and checks the peer state (transport rule).
