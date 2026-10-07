# CPU native platform extraction — 2026-10-07

The shared direct-execution engine now calls one registered per-OS integration.
Its native sources contain no P/Invoke declaration and no operating-system check.
The CPU integrations use their assembly names as namespaces:
`SharpEmu.Cpu.DirectExecution.{MacOS,Linux,Windows}.X64`.

## Ownership

- MacOS.X64 owns Darwin pthread TLS/helper stubs, Mach exception-port routing,
  Mach memory probes, signal numbers and `sigaction`/`ucontext`/`mcontext` layouts.
- Linux.X64 owns Linux pthread TLS/helper stubs, process memory probes and Linux
  signal/context layouts.
- Windows.X64 owns TLS and kernel exports, native thread/worker declarations,
  vectored exception registration/thunks, native context primitives and TEB
  stack-bound emitters.
- Core retains guest instruction recovery, demand paging, guest/GPU recovery
  policy and decoding of the normalized x64 fault context. Windows supplies
  this layout directly; POSIX bridges translate to it.
- The POSIX bridge is linked source compiled separately in each OS package;
  there is no shared POSIX runtime assembly and no runtime OS branch in it.

Production CPU packages no longer have `InternalsVisibleTo` access to Core.
`DirectExecutionFaultFrame` specifies the binary interop layout;
`DirectExecutionFaultHandlers` publishes recovery and stack-query callbacks.
Test-only friend access remains for focused ABI fixtures.

## Transition and lifetime review

The native POSIX trampoline's saved registers, host-RSP TLS lookup, stack gap,
stack alignment and restoration are preserved. POSIX calls prepublished
**managed** function pointers after its original signal entry; it does not add
another native-to-managed transition around shared recovery. The existing
fabricated-frame warm-up also exercises the new session-dispatch wrapper before
Rosetta can encounter it in a signal. GC settings and startup policy are unchanged.

Windows retains `Marshal.GetFunctionPointerForDelegate` reverse-P/Invoke entry,
with delegates rooted before registration, rather than replacing the original
marshalling transition with `UnmanagedCallersOnly`. The emitted Windows filter
and stack restoration sequence are preserved. Reinstalling an active Windows
hook with different baked TLS/abort addresses fails instead of reusing a stale
thunk; normal disposal removes its registrations and thunk allocations.

No callable, delegate, native thunk or session object is created inside the new
fault-dispatch adapters. POSIX hooks deliberately remain installed for process
lifetime and chain to prior handlers after the active backend is cleared. If
workers cannot quiesce, the existing leaked-session behavior retains their native
state. **The older full diagnostic/GPU recovery paths include optional logging
and managed work; this extraction is not proof of async-signal safety for those
paths.**

## Verification

Commands use one MSBuild worker and disable node reuse. Device suites and games
were not run in this worktree; integrated before/after game checks are owned by
the parent task to avoid concurrent device work.

- macOS CLI Release build succeeded before and after fault extraction.
- Linux.X64 and Windows.X64 CPU package Release compilation succeeded on macOS.
  This is cross-package compilation, not execution on those operating systems.
- x64/Rosetta CPU + architecture selection: **319 passed, 0 failed**, with no
  runner-reported skips. Raw result: `/tmp/sharpemu-cpu-tests/cpu-x64.trx`.
- ARM64 CPU + architecture selection: **305 passed, 0 failed, 11 explicitly
  skipped**, total 316. Raw result: `/tmp/sharpemu-cpu-tests/cpu-arm64.trx`.
  ARM64 does not execute the x64 CPU backend.
- All 10 `Sse4aPosixSignalRecoveryTests` cases executed on Rosetta: five Darwin
  vector-state size bounds, EXTRQ/INSERTQ round trips, register/pointer preservation
  and rejecting instruction recovery without bridged XMM state. These use
  fabricated native signal frames and actual guest instruction bytes; they do
  not install a real OS signal handler or prove game compatibility.
- Six CPU architecture cases check source/package boundaries and fault-frame
  binary sizes/offsets against the shared recovery ABI.

Several inherited Windows fixtures early-return on macOS and therefore appear
as runner passes even though their native assertions did not execute:
`WindowsExceptionTrampolineTests`, `WindowsFaultFilterTests`,
`WindowsDebuggerBreakTests`, `WindowsCrashCaptureTests`, the native cases in
`WindowsThreadContextCaptureTests`, and Windows stack-bound fixtures. The
platform-neutral context-decoder case does execute. Native Windows and Linux
runtime acceptance remains outstanding; no claim of validation on those hosts
is made. Likewise, ARM64 pass counts include other inherited architecture-gated
returns, not native ARM64 guest execution.

## Remaining work

Run the exact integrated application head against the same game and startup
conditions as its baseline. Compare boot/progression, fault recovery and shutdown
separately. Preserve hashes of the loaded application assemblies. Windows/Linux
native exception/signal fixtures must subsequently run on their real x64 hosts.
The public seam still resides in Core; extracting a smaller CPU contracts
assembly can be a later dependency-graph change after shared engine ownership is
settled. Shared Win64-shaped guest/fault context vocabulary is intentional ABI
compatibility, not a Windows host implementation leak.

## Follow-up: concrete probe warm-up and real POSIX faults

The HLE warm-up's seed set is the assemblies that contributed exports. Its
`WithGuestReachableDependencies` visits their direct matching references; it does
not walk outward from the CLI's registered CPU integration. Thus the new concrete
`TryReadHostMemory` and `TryReadExecutableHostMemory` interface implementations
are not guaranteed to be in that sweep. The old fabricated-frame path warmed the
Mach/Linux probe directly, which also bypassed those new interface entry methods.
This is a source/ownership coverage gap; no game deadlock has been attributed to
it.

The POSIX integration now executes just those two interface entries and the
capability getter on a normal host stack before installing fault interception.
The helper disables inlining/optimization so the dispatch cannot disappear into
an inlined substitute. It reads one stack byte; it does not add a global type
sweep, change GC defaults or add work inside a fault callback. Captured x64 JIT
assembly confirms three actual interface calls:
`/tmp/sharpemu-cpu-posix-preflight-disasm.log`.

Two new fixtures use the existing isolated native-return harness, with a
30-second child timeout and a dedicated completion report written only after all
native assertions pass. Both execute on x64/Rosetta with no graphics device:

- A guest store into a tracker-armed protected page returns to its host caller,
  publishes changed bytes and dirty state, and disarms protection. No managed
  write helper performs the tested store. The mapped-view region query caches
  allocation protection metadata, so it is not used as a kernel protection
  measurement in this fixture.
- A minimal SSE2/SSE4a guest sequence triggers real SIGILL for unsupported EXTRQ,
  recovers through the normalized frame/native probe path, preserves its full
  return value and increments the instruction recovery counter exactly once.
  It does not depend on AVX2. An AMD host with native SSE4a returns before this
  unsupported-instruction fixture; that does not apply to the verified Rosetta
  run.

The two isolated fixtures passed. A separate direct bounded SIGILL worker also
exited 0 and wrote its completion evidence to
`/tmp/sharpemu-cpu-real-sigill-evidence.txt`. These results exercise real OS fault
entry and guest-stack return, beyond the earlier fabricated-frame fixtures. They
still do not prove GPU fault coherence, game compatibility, Windows/Linux native
fault handling or signal safety under arbitrary GC/runtime interruptions.

Final follow-up x64/Rosetta CPU + architecture selection: **321 passed, 0 failed,
0 runner-reported skipped**. Results: `/tmp/sharpemu-cpu-tests/cpu-x64-followup.trx`.
The previously listed Windows early-return limitations still apply.
