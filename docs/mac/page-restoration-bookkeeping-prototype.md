# Per-page restoration bookkeeping prototype

Updated 2026-10-08. This is test-only work under
`tests/SharpEmu.Libs.Tests/Memory/Prototype/`. Production tracker, host mapping,
fault callback and retry behavior are unchanged. It is a bounded implementation
step toward [write-fault reservation](write-fault-reservation-design.md), not
permission to activate recovery against retired or rearmed pages.

## Implemented invariant

Each range-to-page membership stays counted while its final native writable
restoration is pending. A successful syscall releases that membership; failed
and unattempted pages retain ownership. With overlapping watchers, removing an
earlier membership does not open the page; only the last watcher requests native
restoration. A partially successful range therefore cannot be treated as fully
disarmed or retired.

Membership generation and state share one atomic CAS word. Completion claims
the exact pending generation once. This rejects copied results, stale failed
results at retry publication, and a preempted old completion attempting to claim
a newer operation. Exhausted generations decline without wrapping. Page and
membership claims use bounded CAS attempts rather than waiting or spinning.

The helper does not reserve backing, block retirement, validate a current fault
frame, issue syscalls or certify permissions. Its caller-supplied success scalar
is not an owner-bound kernel permission receipt. Test scheduling callbacks and
invariant exceptions are deliberate fixture seams; they must not be copied
into a signal handler. The warmed scalar helper has an ordinary-thread
zero-allocation check, not full native callback qualification.

## Verification

On macOS x64/Rosetta, the final eleven-case selection passed with no declared
skips. Seven cases performed actual native memory operations using the current
4 KiB kernel page size:

- Missing first, middle and last pages: actual `munmap` and per-page `mprotect`
  restore surviving pages while failed memberships remain counted.
- Read-only shared file backing in first, middle and last positions: actual
  writable restoration fails for the file page; other pages become writable.
- Overlapping memberships: kernel protection remains read-only after removing
  the first watcher, and becomes writable after successful last-watcher restore.

The other four cases cover allocation-free warmed bookkeeping, two deterministic
stale-ticket interleavings and generation exhaustion. Kernel protection is
queried using the existing Mach query fixture. These native cases explicitly
return early off macOS; a passing runner on another OS would not qualify them.
No ARM64, Linux, Windows, game, Metal or production callback run was performed
for this prototype.

The fixture reserves a unique exact high address using no-clobber fixed Mach
allocation. It warms helper/query paths before creating a hole and frees only
surviving owned pages, never the released gap. Initial allocator-based runs
failed or timed out with a Rosetta code-fragment error. Gap reuse by runtime/JIT
was a plausible harness hazard, not a traced cause. A first fixed-address
attempt failed safely with `KERN_NO_SPACE`; the corrected high address and final
fixture passed. Failed logs remain alongside the final evidence and are not
acceptance results.

## Required before production activation

Move the shared ownership seam into the neutral host contract only after all
mapping mutations are audited. A safe-side close must revoke new signal pins
before its kernel operation, then drain existing pins outside every gate needed
by recovery. Retain backing through pinned restoration and publish replacement
ownership only after successful mapping work. Cover fixed maps, views, unmaps,
backing releases, protect/commit, tracker rearm/retirement and direct native
protection operations, using actual kernel page granularity.

A signal claim must bind one normalized address/access invocation to the live
owner, page and arm epoch without allocation, logging, locks, native queries or
waiting. Only successful per-page restoration under that pin can produce an
authorization receipt. Signal-entered lazy commit or recovery must not close
and drain its own pin. The existing Invalidating/Arming acceptance and delayed
first lookup after disarm remain unresolved until this lifetime contract exists.
