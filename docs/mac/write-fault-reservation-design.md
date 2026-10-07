# W13 reservation design: review draft

Base 43525de; worktree agent-cpu-owner-lifetime. No retry or lifecycle fence is enabled by this draft.

## Required invariants

Protection ownership and receipts cover actual kernel pages, not merely guest 4 KiB units: Rosetta/x64 currently has 4 KiB pages, native ARM64 macOS has 16 KiB pages. Architecture/page-size qualification is explicit; a guest-page epoch cannot authorize changing part of a larger native page.

A normalized write invocation may reserve only an immutable mapping owner that contains the whole page and a particular tracker page/arm epoch. Successful acquisition precedes any native tracker mprotect. The owner remains live until that operation releases its reservation. A safe-side mutation revokes new reservations before touching native memory, drains existing reservations outside locks needed by any fault recovery, and retains the old mapping/backing until the drain completes. Reuse creates a different owner identity even at the same address.

A restoration receipt names owner identity, arm/page epoch, page address, normalized invocation and kernel-success result. Dirty/generation publication precedes opening the page for writing. A receipt is never inferred from shadow RW permissions, global MappingGeneration, Arming/Invalidating state, or another subsystem's write retry. A token can be consumed once and only by the same normalized write invocation (address, access, fault-entry identity); read/fetch/unclassified frames cannot consume it. A failed restore retains its outstanding watcher/owner state; successfully restored earlier pages remain recorded separately.

Signal code performs bounded CAS attempts and preallocated scalar writes only. It neither allocates, logs, locks, waits, nor spins. Contention and exhausted capacity decline conservatively; they do not return handled. Safe-side code may wait after revocation, with an explicit failure/timeout disposition that preserves backing rather than continuing a mutation.

## Mutation inventory

| Surface | Operations requiring pre-kernel ownership transition | Current issue |
| --- | --- | --- |
| HLE HostMemory | Alloc pure commit, mmap allocation, cleanup of relocated mmap, Free, Protect | Separate legacy provider remains used by Core CPU wrappers/default allocator; Free removes metadata before munmap; protection publication follows syscall |
| Host.MacOS/Host.Linux memory providers | allocation/reservation/commit/free/protect/raw protect, exact Mach fallback and cleanup | Provider gates and post-kernel MappingGeneration are not reservations |
| Host.Windows memory provider | VirtualAlloc commit/reserve, VirtualFree, VirtualProtect | No common mapping lifetime contract |
| HostViews on all three OSes | reservation, split/join/free placeholder, MapView, unmap/RestoreHole, private commit/release, ChangeAccess, FreeOwnedRange | MAP_FIXED/placeholder replacement can reuse an address while tracker still holds an old range |
| HostBackingObject.Dispose and OS release callback | alias unmap and fd/section release | Backing.Gate protects managed map calls, not a signal-side restoration pin |
| GuestSpaceOwner | shared/private map, unmap, transient access, owned-range disposal | Several compound operations contain native mutation plus metadata under their own gates |
| PhysicalVirtualMemory | allocation/free, backed-map transition, TryProtect, lazy commit, temporary write protection and restoration | Takes locks before provider calls; lazy commit and instruction patching can be reached through fault recovery |
| GuestImageWriteTracker | Track replacement, Arm/Rearm, Disarm/Untrack, native RemovePageWatchers | Direct mprotect bypasses providers; Arming/Invalidating accepted without writable proof; dirty publication follows restoration; failure can orphan readonly pages |
| PageGuard/GuestGpuMemory | address-space TryProtect and restore | Independent watcher ownership and retry receipts must participate in the same protection owner, not just mapping generation |

Read-only native probes do not mutate mapping lifetime. Native runtime stack/JIT allocations not owned by the emulator are not eligible tracker owners and must never be manufactured from Query results. Native cleanup after an unsuccessful exact placement must close the actual returned address, not the requested one.

## Minimal defensible contract

Use neutral HLE.Host ownership records (scalar fields plus safe-side immutable page/range arrays), with per-owner closed bit and reader count in one atomic word. A signal acquire reads a published owner, attempts one count increment CAS only if open, and validates its exact identity/epoch. Failure releases immediately and declines. Safe-side close sets the closed bit before native mutation; old readers are allowed to complete their bound operation. A separate mutation serialization gate is never acquired by a signal. Owner objects/backing references remain retained by the published/retired registry until drained.

An initial global close/drain fence can prove exclusion in isolated tests but is not enough to ship: it still needs authoritative expected owners, classification of recursive signal-entered mutations and a defined progress policy during arming. Per-owner/page domains allow unrelated mappings to proceed. Acquiring multiple domains uses a safe-side stable ordering; signal-side acquisition is bounded with complete rollback, never an unbounded retry loop.

Host mutations revoke the affected owners before their syscall and publish replacement owners only after success. Metadata failure does not reopen a stale owner. Tracker arm/rearm must acquire the same owner/domain; page watcher updates, kernel permission changes and receipt publication need one coordinated page transition. Overlapping image and GPU owners share that page transition. A successfully restored page cannot be rearmed, retired or remapped while a captured invocation is consuming its receipt.

## Unclosed proof obligations

1. Audit fault-entered lazy commit, temporary instruction protection, GPU recovery and every direct provider caller. A signal holding a reservation cannot enter safe-side close/drain on that same domain. Either provide a proven non-recursive operation under its live owner or defer mutation to an ordinary managed recovery context; generic synchronous waiting is forbidden.
2. Show lock order from the outer owner operation: revoke/drain before GuestSpaceOwner, PhysicalVirtualMemory, HostViewRegions, provider, backing and tracker gates required by recovery. Nested host calls need a proven already-held mutation token, not hidden reentrant waiting.
3. Define what a write during Arming/Invalidating does without waiting or claiming writable before kernel success. The current unconditional handled result cannot become a receipt. Declining is safe but may terminate legitimate concurrent writers; no progress claim until tested.
4. Define retained partial-failure page ownership and rollback for overlapping watches. Decrementing refcounts first, then declaring Disarmed on syscall failure is insufficient.
5. First lookup after restoration has no captured protected owner and remains unresolved; accepting arbitrary disarmed dirty addresses would hide real readonly/unmapped faults.

## Bounded first step

Add opt-in native-entry telemetry independently of retries. Preallocate scalar records before installing handlers; record raw si_addr/si_code, normalized address/access/error, owner address/state/arm epoch and monotonic event sequence. Record native protection begin/result and retire/rearm transitions on the same event sequence. Reserve each slot with bounded atomic operations, publish only after all fields are written, count drops without overwriting an in-flight record. Safe contexts flush records; signal context never formats or performs I/O. Owner observations are diagnostic snapshots and cannot authorize recovery.

Test publication/concurrent slots, capacity exhaustion, raw/fallback layout, missing context, zero allocations after warm-up and no cold telemetry methods in the real native recovery fixture. Then deterministic ownership fixtures must pause native restoration while safe-side close/remap/rearm/retire attempts occur, inspect actual kernel protections, and prove that the old syscall cannot affect replacement backing. Include partial first/middle/final failures, overlap, forged/wrong-frame/copied claims and nested fault mutation. A pure primitive test cannot certify full provider integration.

## Diagnostic implementation and local qualification

The implemented first step remains diagnostic only. Set `SHARPEMU_NATIVE_FAULT_TRACE_PATH` to a new absolute file path before startup; the directory must exist. The file is created exclusively with user read/write permissions, fully written/flushed and pre-touched before handler installation. It contains a schema/version/session header, native page size/process architecture, 4096 fixed records (about 608 KiB), and a dropped-record counter. Production keeps the mapping alive until process exit. Setup is serialized on a gate used only by ordinary startup, with complete rollback for expected setup failure; OOM is not swallowed.

The signal-facing path adds no TLS: a scalar invocation ID flows explicitly through the early tracker and native protection calls. A separate operation ID pairs every mprotect begin/return, including safe-side operations with invocation0. Selected snapshot owner observations and initiating-range observations are distinguished. Raw errno is captured immediately after mprotect, before clock/record calls. Entry/result mean the early tracker path only, not the later Core recovery result. No owner lifetime fence, watcher transaction, writable proof for Arming/Invalidating or retry policy changed.

After the process has stopped, parse with:

```sh
python3 scripts/macos/read-native-fault-trace.py /absolute/native-trace.bin --output /absolute/native-trace.json
```

The parser validates schema, exact record/file bounds, session identity and native page/process metadata. It skips unpublished slots and reports exhaustion. Sequence orders diagnostic reservation/observation, not original kernel trap timing; timestamps precede owner-field observations, which are not an atomic reservation. MAP_SHARED published records can survive fatal process exit, but no fatal-tail durability guarantee is made.

Rosetta x64 local qualification: 36 focused tracker/frame/diagnostic tests passed, followed by 6 final telemetry tests after startup serialization was added. Coverage includes concurrent unique slot publication/exhaustion, negative/overflowing counters, native field retention, actual kernel RW restoration, zero warmed-thread managed allocations in entry/result methods, and an isolated real protected store through the installed production POSIX callback returning to the host with matched syscall records. A separately retained actual-worker trace exercises the parser. Logs and binary/JSON evidence are under worktree `artifacts/validation/native-fault-trace/`. No GPU/game was launched in this worktree. Linux/Windows native runtime and native ARM64 signal tracing remain unqualified.
