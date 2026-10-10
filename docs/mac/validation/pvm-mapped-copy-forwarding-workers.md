# Actual PVM/Tracked forwarding workers

Status: qualified at clean `754a7637a725e0c1d4080f69320cdc3ef3b555d0`, based on root `115316f0`, then imported locally as test-only `ef8d4f20`. The separately frozen ordinary writer slice is not part of this candidate.

## Actual qualification on 2026-10-08

The reference build passed with 8 warnings and zero errors in 12.56 s. Each of
the four scenarios ran separately through an exact xUnit display-name filter on
each actual CLR: **4/4 ARM64 and 4/4 x64 passed**, zero skips, failures, timeouts
or forced signals. Each fresh worker returned from the real constructor/self-test,
performed the owned mapping/copy, returned from production disposal and exited 0.
The parent owned-group deadline was 60 s per scenario; no unrelated process was
signalled. Both CLRs reported .NET 10.0.3 and measured 16 KiB kernel pages.
ARM64 provider pages were 16 KiB; Rosetta x64 provider pages were 4 KiB.

Source and DLL hashes matched before/after. The archive contains five exact
source paths, nine frozen production/test DLLs, runtime configs, eight TRXs,
commands, logs and the bounded driver at
`artifacts/validation/all-games-20261008/pvm-mapped-copy-754a7637/`.
The build terminal result was recovered from the root tool output because the
subsequent native driver replaced its generic results file; the complete build
log remains, and the missing build child PID is not reconstructed.

Actual constructor return qualifies execution of its existing self-test. Its
shared-half branch is platform-conditional and was not separately certified.
Disposal return does not certify guest fault recovery, kernel permissions or
native writer completeness. No game or GPU work ran in this qualification.

## Worker implementation and retained limits

`SharpEmu.Core.Memory.Tests` is a dedicated executable test project referencing production Core/HLE/Mac. It neither references nor imports `SharpEmu.Libs.Tests`, its module initializers, the Host.Memory.Tests composition, a guest execution backend or graphics package. Core's production Libs dependency remains. No production friend assembly, API, observation policy or capability changes are needed.

Each of four opt-in tests launches the same neutral DLL under the current test process's actual .NET host in a fresh process. The worker asserts `SHARPEMU_TEST_EXPECT_ARCH`, exact `SHARPEMU_TEST_EXPECT_FRAMEWORK` and `SHARPEMU_TEST_EXPECT_KERNEL_PAGE`, and records actual geometry. On the qualification machine the intended kernel measurement is 16384; provider page size is expected to differ between Arm64 and Rosetta X64 and is checked against the current CLR's Environment.SystemPageSize. Geometry is read from the real provider's startup measurement and validated again against the returned original copy observation, never reconstructed or fabricated.

Composition explicitly registers plain Mac memory authority, creates the real Mac view provider, constructs `PhysicalVirtualMemory` with 128KiB backing and no aperture pre-reservation, and constructs `TrackedCpuMemory`. The native PVM constructor's ordinary backing self-test and low-address reservation search run unchanged. Stages are flushed before and after this constructor. A directed failure, fail-fast or deadline is preserved; there is no fake PVM, owner injection, foreign MAP_FIXED, alternate search strategy or skipped self-test. If a constructor fails before returning an owner, its unknown cleanup is retained until fresh process exit rather than reported as successful disposal.

After construction the worker obtains an owned 32KiB range through actual `TryHoldRangeAtOrAbove`, checks `TryHoldRange`, and maps it through `TryMapBacked` with physical offset 16KiB. No address is read or written before successful mapping. Distinct per-kernel-page seed bytes prevent an incorrect physical page offset from accidentally matching a repeating seed.

The four scenarios are:

- Full mapped footprint through Tracked, exact original physical offset/identity/incarnation/kernel geometry, second-page small payload, later participating alias mutation leaves detached bytes unchanged, and actual direct-PVM copy sees the new bytes. Observation currentness after bytes change is explicitly mapping-only.
- Unrelated canonical allocation, read-only/read-write protections and release preserve the original observation; no released pointer is dereferenced.
- Actual unmap and same-address remap to a different backing offset reject the old observation and return a new incarnation. Successful disposal rejects both currentness and forwarding without touching the released address.
- A real ordinary wrapper that omits the optional interface makes Tracked return Unavailable, leaves destination sentinels untouched and never calls the wrapper's ordinary read as a fallback.

Each child has a 40s total deadline including composition, constructor, search, actions and disposal. Owned-child TERM gets 10s; owned-tree KILL is used only after that deadline. Output/read tasks are bounded and captured even on failure. Parent qualification must additionally hold the shared machine lock, forbid simultaneous emulator/native workers, use an outer 60s owned process-group wrapper and archive pre/post source and DLL hashes. The worker itself creates no subprocesses. Native cleanup failures preserve the primary test failure as primary; uncertain unrelated release is consumed before calling Free and is not retried. Failed PVM disposal is not retried by this fixture.

Future qualification requires the shared native slot. Frozen-DLL selection is `FullyQualifiedName~ActualPvmAndTrackedForwarding&DisplayName~<scenario>`; opt-in variable is `SHARPEMU_TEST_REQUIRE_PVM_MAPPED_COPY_NATIVE=1`. Without opt-in or off macOS the Theory is declared skipped, never a native pass. Expected framework must be set to the measured qualification CLR, e.g. `.NET 10.0.3`, and architecture separately to `Arm64` or `X64`.

These controls establish only actual wrapper forwarding, owned alias copy and mapping/lifetime veto semantics if they execute successfully. They do not certify atomic content, raw/native writer participation, page permission ownership, signal/guest recovery, GPU publication or any game behavior. Existing backing constructor search may fail on this OS; that is a concrete qualification outcome rather than justification to bypass it.
