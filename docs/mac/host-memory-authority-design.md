# Canonical anonymous host-memory routing

Design base `b6fe8f7`. This increment unifies the provider reached by legacy
`HostMemory` and the process-wide `IHostPlatform.Memory`. It does not wire the
unused mapping registry, owner pins, tracker recovery, or pre-kernel drains.
Neither a common provider reference nor its shadow Query proves backing lifetime.

## Bounded migration

A neutral `HostMemoryAuthority` publishes one raw-capable `IHostMemory` provider
at safe startup. Construction/publication is serialized, complete before exposure,
and immutable for the process. The selected OS package exposes plain memory
construction separately from x64 guest execution. CLI and test composition register
it before the lazily constructed guest platform. Fault callers only read the
published reference; they never initialize or lock this startup gate.

An additive raw interface preserves the legacy allocation flags, release size/type,
raw protection words and complete 48-byte 64-bit `HostMemory.BasicInfo` query
result. Windows forwards exact raw calls to VirtualAlloc/Free/Protect/Query.
Raw modifiers, reserved/committed state, allocation protection, allocation base,
query Type and return-size are never reconstructed from neutral enum values.
The facade keeps its public constants and structure layout while removing its
independent native declarations and POSIX allocation table.

macOS/Linux raw and neutral entrypoints reach the same package allocation table.
The existing provider implements exact fixed Darwin placement using a non-clobber
Mach allocation when mmap hint placement fails. Legacy sparse protection-run
Query logic must be carried into each provider so large apertures are not scanned
one 4KiB page at a time. Free publication follows the provider's existing
success-only metadata update instead of legacy removal before failed munmap.
This is an explicit discrepancy resolution, not a new fault acceptance policy.

The existing POSIX logical bookkeeping uses 4KiB units even in a native ARM64
process. This migration preserves that compatibility convention and tests native
memory only at full Environment.SystemPageSize boundaries. It does not claim the
shadow extent covers all actual 16KiB kernel pages. Native-page extent, reserved
versus committed logical state, backing/alias registration, and raw modifier
representation must be reconciled before owner-domain registry integration.
Windows raw state and modifiers continue to round-trip through the OS unchanged.

## Unintegrated mutation paths

View/placeholder maps, MAP_FIXED replacement, backing alias windows/disposal,
SharedBackingViews retirement, direct tracker/native mprotect, lazy-commit signal
recursion and per-page rollback still bypass owner-domain exclusion. Query view
metadata remains an overlay and has no native permission receipt. No callback
pin or Invalidating/Disarmed retry behavior may use this routing migration as an
authorization. Actual injected custom/test IHostMemory instances remain separate
unless deliberately composed as the process authority.

## Controls and qualification

Real native full-page allocations exercise legacy-allocate→neutral-query/protect/
free and neutral-allocate→legacy-query/protect/free, kernel permissions, interior
commit, protection runs, failed exact overlap without clobber and successful
release across both entrypoints. Plain memory must work on native ARM64 without
constructing the x64 execution backend. Windows-only controls preserve raw guard
modifiers and reserve/commit state; Linux/Windows runtime qualification cannot be
claimed from macOS builds. Ordinary composition controls prove immutability and
identity, not signal or backing lifetime.

The dedicated Host.Memory.Tests project is independent of Core, GPU and launcher
packages. Existing Libs module initialization and CLI composition register memory
first; no other test project directly calls HostMemory. A mismatched directly
registered global host platform is rejected before publication. A mismatched
lazy factory is rejected when resolved, preserving lazy guest-architecture
validation. This does not prohibit an explicitly injected custom provider in an
isolated component; it cannot silently become the global platform authority.

Commit boundary compatibility: legacy POSIX commit previously clamped overrun
requests to an allocation prefix and reported success. Canonical providers retain
their existing full-request rejection. The real native fixture verifies both raw
and neutral overrun calls decline without changing kernel permissions. Query
continues to report POSIX reserve-only mappings as committed/demand-paged;
Windows raw reserve/commit state is forwarded unchanged. Unknown POSIX raw
protection values keep the existing provider conversion convention. No metadata
is removed when provider release fails; failed/interior-release controls exercise
rejection, not forced native syscall failure injection.

ModuleManager warms HLE and scanned export assemblies plus one-hop guest-reachable
references; it does not guarantee an arbitrary loaded Host assembly. The authority
therefore exercises the new raw dispatch on the ordinary startup stack before
publication: checked non-mutating guard probes followed by one exclusively owned
full native page allocation, raw query, RO/RW protections, instruction-cache flush
and checked release. Public facade calls and startup use identical NoInlining
private helpers so this warms the actual migrated interface-dispatch cells.
Cleanup failure prevents publication. These probes bind new thin/positive paths;
they do not exercise every partial-protection/nonzero query branch or qualify all
signal/GC behavior. Existing tracker/native callback warm-up and integrated
protected-store/native-worker checks remain separate requirements. In particular,
POSIX instruction-cache flush remains the existing x64 no-op; future native ARM
execution still needs its own cache-invalidation implementation.

## Focused evidence

The final selection passed 16/16 on x64/Rosetta (native page 4096) and 16/16
on native ARM64 (native page 16384), zero declared skips. It includes seven
isolated startup workers and actual cross-entry native page/protection controls.
Both final builds and Windows/Linux provider crossbuilds have zero warnings/errors.
Exact source, commands and results are recorded with this commit's validation
artifact. Per-test TRX StdOut records runtime architecture/native page/provider.
Windows/Linux host packages are cross-built on macOS; their kernel behavior is
unexecuted here. The Windows-only raw guard/reserve fixture is compiled only on
Windows, outside both macOS counts. Actual protected-store recovery, Core/game
integration and GPU/device qualification were not run by this increment. Failed
initial test-entrypoint and Windows marshaller builds are retained as preparation
failures, not native-test failures.

A declined warm-up cleanup receipt records its address in the allocation-free
sticky LastStartupCleanupFailureAddress diagnostic. If a probe and cleanup both
fail, the original exception is preserved and the secondary receipt remains
observable. This address does not prove a live leaked mapping: the adversarial
worker actually releases its owned page but deliberately declines its receipt.
All failure paths leave the authority unpublished; an honest startup retry in
the isolated worker remains distinct from native fault/recovery retry policy.

The sticky diagnostic also distinguishes a declined receipt (kind 1) from thrown
cleanup (kind 2). If ordinary cleanup throws after an existing probe error, the
primary exception is preserved. Fatal cleanup exceptions still propagate rather
than being swallowed. Address and kind are separately sampled observations, not
an atomic native receipt. Isolated controls cover declined and thrown cleanup,
with and without a simultaneous primary failure.
