# Sampled GPU alias ownership gap — 2026-10-07

## Observed failure

Demon's Souls at root source `b557990d20fd9144dc0292b2546e89ef905976b8`
stopped near 45 seconds on `ComputeGuestDispatch` sequence 119, logical queue
`acb.compute[48]`, submission 7, texture address `0x7092C80000`.
Evidence: `artifacts/runs/20261007-205520-ds-followup-b557990/game.log`,
lines 630 onward. The runtime recorded zero guest frames. This is unresolved.

The producer is a separate shortcut from the evictable native upload cache fixed
in `b557990`. `MetalTextureSnapshots.TryCreateGuestDrawTexture` returns an empty
sampled payload when `IsGuestImageUploadKnown` reports true. In the normalized
Metal path this is the only nonzero sampled producer with neither linear nor
tiled bytes. Deferred tiled snapshots retain their tiled bytes, while unreadable
real descriptors explicitly throw. Compute routes nonzero storage bindings to
`EnsureStorageImage`, so this failure's `CreateDrawTexture` stack identifies the
sampled path.

`IsGuestImageUploadKnown` currently checks address/format advertisement. With
write tracking enabled, advertisement alone returns true. Display registration,
render-target submission and storage-binding submission can advertise an address
before an initialized native image exists. The consumer requires a matching
extent and initialization; it correctly rejects recovering tiled or mip bytes
through a late raw linear guest-memory read.

The trace lacks captured descriptor/producer metadata. A pending producer,
missing initialization, incompatible extent, or an uninitialized higher-scoring
variant remain alternatives for this particular address. New failure-only
diagnostics print current advertisement, active/selected images, latest producer
sequence/completion and its queue/submission if still pending. They explicitly
say captured alias provenance is unavailable: these observations are not an
ownership reservation and cannot reconstruct preparation-time state.

## Source boundaries

- Normalized Metal prepares new snapshots for each draw/dispatch; no persistent
  prepared-image cache was found in this path. Prepared stages retain the empty
  result through collection and queueing.
- The logical queue scheduler preserves FIFO per queue, but round-robin dequeue
  does not add a sampled-image dependency on its advertised producer.
- Storage resource class does not establish read/write intent. Every storage
  binding is currently advertised and marked changed, including read-only use.
- Storage reuse matches address alone and native creation is 2D with one mip.
  Alias selection scores extent above initialization and format is a preference,
  rather than a proven compatible view. These require separately qualified fixes.

## W13 acceptance specification

Implement the next seam as an explicit source choice: an owned faithful CPU
snapshot, or an ordered GPU image alias. An alias must capture stable backing
identity and mapping epoch, a supported descriptor/view identity, producer
sequence and logical queue, and the content/ownership version required by that
use. Address advertisement is insufficient. The implementation may remain local
to Metal initially; promote contracts to shared packages only with equivalent
semantics and evidence for other backends.

Pending initialization must be owned by a real producer with retained faithful
seed data, or by a prior GPU writer. A read must not depend on its own prospective
write. The scheduler must honor valid required producers without introducing
arbitrary ordering between unrelated queues or deadlocking under admission
pressure. Capture dependencies at preparation/submission; the latest writer at
consumption is not automatically the correct source. Guest synchronization and
CPU dirty epochs must participate in source selection. GPU-authored content must
not be overwritten by an older CPU snapshot merely to avoid an unresolved alias.

Keep the chosen backing/content alive through preparation, pending execution,
encoding and command-buffer completion. Define remap, retirement, overlapping
views and CPU-write behavior; reject unsupported or invalidated reservations.
Account retained CPU payloads and native resources without counting an empty
alias descriptor as proof that initialization consumes no memory.

Required managed regressions exercise production capture and queue consumption:

1. Advertised-only display address does not qualify as an initialized alias.
2. Same-queue pending producer owns faithful initialization; a later sampled
   read consumes that producer's version without late guest reads.
3. Cross-queue dependency delays a consumer until the captured producer is
   ready; unrelated work progresses. A later writer cannot replace the captured
   requirement or create a self-dependency.
4. Sampled and storage bindings in either descriptor order preserve the intended
   pre-dispatch read version. Missing initialization rejects explicitly.
5. Same-address extent/format/type/mip/layer changes, uninitialized variants,
   remap, retirement, dirty generation changes and failed producers cannot
   authorize an incompatible or stale source.
6. Retained tiled/mip seed bytes survive cache flush and queue backpressure;
   failed consumption is not acknowledged and partial GPU work is not committed.

Separately qualify storage read/write intent and typed same-address identity
before broadening storage alias support. Real Metal tests must verify GPU-write
then sample/readback across the supported views and queues, mutation while work
is pending, and retirement through GPU completion. Windows/Linux must preserve
their ownership behavior and platform dependency closure.

Runtime acceptance requires frozen-source DS and Superliminal reruns with
descriptor/provenance evidence, guest frame cadence, visual correctness,
progression, shutdown, memory and copy cost recorded independently. A green
managed test or removal of this exception is insufficient. Do not restore raw
tiled-as-linear reads, fabricate zero/null resources, blindly reorder queues, or
disable live aliases and substitute CPU bytes without source-ordering proof.

## Scope of this diagnostic change

Only failure formatting changes. Alias selection, payload skipping, storage
creation, queue scheduling and recovery policy remain unchanged. No new shared
API, friend assembly, native query or texture allocation is introduced. Tests
call the actual failing texture materialization entry point with no device and
synthetic managed advertisement/producer/image state, including a producer
already removed from the pending queue during real ordered drain. Existing fatal
and allocation exceptions propagate unchanged. Tests verify preserved failure
and context, not GPU correctness or a DS resolution.

Forty-eight focused managed cases passed on ARM64 and x64/Rosetta with zero
skips, based on isolated source `92deb56` plus this diagnostic change. Filters:
`MetalGuestAliasDiagnosticsTests`, `MetalGuestFailureTests`,
`MetalQueuedTextureSnapshotTests`, `MetalTextureShapeTests`, and
`ExtractedPackageContractTests`. ARM64 uses `dotnet test -c Release -m:2
-nodeReuse:false -p:UseSharedCompilation=false`; Rosetta uses
`arch -x86_64 /Users/xnetcat/.dotnet-x64/dotnet test -c Release --no-build
--no-restore -m:2 -nodeReuse:false` against the same built managed assemblies.
TRX/log evidence is copied to
`artifacts/validation/integrated-20261007/package-agent/sampled-gpu-alias/`.
No device suite or game was run for this patch. The root app remained frozen at
`b557990`; later integration and runtime diagnostics require separate evidence.

### Isolated CPU provenance refinement

The dormant private source registry now keeps the mapping epoch and CPU write
counter observed for owned snapshot bytes through record copies, rather than
stamping a later counter during synthetic publication. Producer initialization
rechecks both before and after widening read observation. A partial snapshot
batch owns each returned source immediately and releases its captures on failure.
Managed fixtures cover copied generation0 followed by CPU generation1 and reject
stale previous-producer initialization without changing the latest registry.
The related ARM64 no-device selection passes76/76.

This does not activate capture, queue ownership, native version copies or producer
publication. Address counters can restart after tracker untrack/retrack; a neutral
tracking identity token is still required before active selection. Mapping epochs
are conservative vetoes, not mapping lifetime reservations. Native version budget,
transactional admission, fatal/close cleanup and actual command-completion pins
remain required; no game or device acceptance follows from these fixtures.

### Tracker identity observation before copying

The isolated graph branch captures the neutral content token only after
`BeginReadSnapshot` establishes observation and before the actual CPU copy.
Both storage and sampled producers compare that original identity/generation/
extent tuple after copying; copied metadata and per-work reuse preserve it.
Repeated storage instability now fails after three attempts rather than returning
an empty seed. Native source selection/publication compares the original token
again and rejects unobserved CPU initialization. Owner retirement can no longer
be hidden by resetting an address counter to0 in these paths.

The ARM64 managed105-case selection passes, including actual producer callbacks
that write, untrack and recreate a watch during every sampled/storage copy.
Scheduler stage fixtures remain synthetic and do not certify native copies.
No global CPU-pixel reuse remains in this producer; the separate native texture
cache still needs token qualification or disabling under the eventual active
graph. Detached immutable native versions need independent lifetime ownership,
not indiscriminate mapping-epoch checks at consumption. Source activation and
native old/new-version readbacks remain outstanding.

### Dormant transactional admission boundary

The isolated branch now plans every output and initialization/WAW dependency
before queue mutation. Admission publishes queue sequence, schedule, pending
count/bytes, explicit input/work owners and latest producer versions together;
rollback restores all of them. Native budgeting reserves both the fresh writer
image and immutable copy, including latest versions without read references.
An incomplete resource descriptor, conflicting same-address initialization,
released preparation or budget rejection cannot manufacture a producer.
Color views come from validated real color-target registers; a texture binding
requires all eight owned descriptor words and matching decoded view fields.

The ARM64 no-device89-case selection includes twelve actual production admission
cases alongside lease, scheduler, metadata and frontend ownership cases. The
private activation boundary stays disabled. Native allocation/copy, actual
command-completion retention, pruning, cancellation and shutdown hooks remain
outstanding; these tests do not establish the DS219 source or fix its format/view
compatibility. Content identity plus extents also does not prove successful page
protection or serialize against an in-flight managed write. That observation
contract is a separate activation blocker, never a mapping lease.

### Observation qualification refinement

Before-copy and subsequent selection/publication token checks now use the
additive observed-content API. Ordinary watch-only owners and failed/disarmed
native protection can no longer qualify a CPU initializer merely because an
identity and extent exist. Full raw descriptors require identity component
selection FAC; raw selection0 is not certified as the legacy identity convention.
The related ARM64 managed102-case selection passes, including a real private
seed-publication rejection from an ordinary watch owner and a raw-zero selection
rejection. These are metadata/selection tests, not kernel or game tests.

Successfully armed observation is still not a mapping/backing lease. Managed
writer notification precedes the actual write and is not a copy exclusion
protocol. Native graph materialization will therefore begin with GPU-only
immutable initialization/fresh clear output; CPU seed admission remains a dormant
transaction fixture until a backing/copy lifetime contract is qualified.

### GPU-only native lifecycle qualification boundary

The isolated branch now has private native materialization and eager-copy helpers.
Each supported producer receives a fresh exact2D image; only fresh clear outputs
or retained immutable GPU initializers are accepted by native materialization.
CPU-only seed materialization explicitly rejects the missing backing/copy lease.
The copy's producing batch identity is retained. Same-buffer consumption is
ordered after the copy; another buffer needs the actual native commit operation
first. Read-use completion cannot mark a producing copy completed. Explicit
version pins survive until actual Metal command-buffer completion/error.

The native helpers implement whole-uncommitted-batch invalidation, exact registry
retain removal, committed-close waiting outside the presenter gate and pruning.
Soft-budget retirement keeps bounded GPU-address tombstones; source preparation
cannot recover retired GPU content from old guest bytes or mutable registry
aliases. The actual snapshot path captures pending source metadata before any
guest-byte read and rolls back unreturned multi-image captures.

The ARM64 managed104-case selection passes. Six hardware fixtures compile but
are not executed yet: real GPU clear→immutable old/new copies in one/two buffers,
uncommitted cross-buffer rejection, whole-batch discard, committed close and
native eviction→production preparation with zero guest reads. Their render-pass
clear is the actual GPU writer; they do not yet qualify translated draw/compute,
a GPU shader consumer, full Drain hooks, shutdown hooks or game behavior.
Graph activation remains disabled and no whole-graph root integration is implied.

### Exact hardware results and exception-safety followup

Clean bfe518db58140eba47f50f76fc435152c97b1b3b passed all six hardware
fixtures on ARM64 and Rosetta x64, require-device1 under the exclusive machine
game lock:6/6 each, no skipped cases or bypassed device gates. Old/red and
new/green or blue readbacks compare every byte of all16RGBA8texels. The clear
pattern is uniform; no coordinate permutation or additional format is qualified.
Commands, exact source status, architecture drivers, neutral assembly hashes,
TRX outputs and actual execution markers are retained under
`artifacts/validation/metal-agent/immutable-gpu-bfe518d-{arm,x64}`.

The next isolated refinement builds error context before changing pin ownership,
and preflights live-registry capacity before acquiring its extra native retain.
Registry publication restores both dictionaries and releases that retain if a
publication fails. The related managed104-case selection remains green. No
artificial OOM or native command-buffer error was injected; ordering/ownership
for those failures is reviewed source evidence, not hardware-fault qualification.
Hardware proof belongs to clean bfe518d, not this later exception-path revision.

### Actual compute argument-buffer consumer followup

The next isolated branch routes captured immutable textures through the actual
`CreateDrawTexture` materializer before CPU-stability/cache/alias paths. Under the
still-disabled graph boundary, read-only storage inputs use that path too;
writable storage destinations remain separately unqualified. Batched command
submission publishes native source commit state only after the real commit call.

Four hardware consumer fixtures compile (sampled and read-only storage, with
preparation alive/disposed before execution). They capture old/red, admit a
consumer, execute a later new/green producer, then run actual
`ExecuteComputeDispatch`→`EncodeStageResources`→GPU shader→guest buffer writeback.
Every byte of all16texels is compared to independent expected colors, starting
with sentinel destination bytes. The MSL kernel independently exercises the
production argument-buffer ABI; it is not a Gen5 ISA translation fixture.
The ARM64 no-device106-case regression selection passes, including pending-copy
and unassociated-GPU-address rejection before native materialization access.
The four new GPU consumer cases have not run yet. Drain writer/lifecycle hooks,
CPU backing/copy ownership, all mutation paths and graph activation remain open.

The consumer fixture teardown now aborts only uncommitted borrowed source batches,
clears open-batch fields and isolates/restores the global upload arena. Fixture
upload pages wait for their tagged committed buffers before release; only the
fixture's pipeline/library cache entries are removed. Fixture construction
requires no open batch. These are test-harness cleanup guards, not runtime Drain
cancellation qualification. Queued consumer ownership is released after the
production writeback completion wait, so these cases do not prove owner release
before native completion.

Clean839ec71 ARM device execution attempted all four consumer fixtures and all
four failed with untouched sentinel output. Full TRX run output identifies
fixture MSL compilation rejection: trailing `[[id]]` on C-array declarations
applied the attribute to a type. The production translator uses `array<T,N>`
fields and did not cause this syntax error. The corrected fixture uses that
valid MSL declaration form and asserts a nonzero compiled library/pipeline and
a completed real flushed command buffer before the pixel oracle. Failed source,
assembly hashes, log and TRX remain in `immutable-consumer-839ec71-arm`; the
consumer shader did not execute in that failed run. Corrected device execution
is pending a new exclusive grant. No production ABI change is implied.

Clean fe7c727 consumer proof now passes four actual required-device cases on
ARM64 and four on Rosetta x64 using identical neutral DLL hashes (Apple M4;
232ms /363ms, zero skips). Both complete through the real compiled pipeline and
completed native command buffer; every byte of all16old/red and new/green texels
passes through production argument-buffer binding and guest writeback. Evidence:
`immutable-consumer-fe7c727-arm` and `immutable-consumer-fe7c727-x64`, each with
exact clean source, architecture, assembly hashes, commands, log, TRX and parsed
execution markers. This closes the native shader-consumer gap from bfe518d.
Queued ownership is still released after writeback wait in those cases. Gen5 ISA
translation, runtime Drain writer/error/shutdown hooks, CPU seed/backing leases
and graph activation remain unqualified. The preexisting new-function ownership
in `GetShaderFunction`/`TryGetComputePipeline` needs separate cleanup review.

The next isolated fixture disables guest writeback synchronization, uses a fresh
isolated upload arena and retains the actual output MTLBuffer. It releases
preparation and queued ownership while the real consumer remains encoded in an
uncommitted batch. Source references reach0 while its native pin keeps the
captured old image. Positive cases will commit/wait/read all64old bytes and then
assert exact native retirement; negative cases discard the uncommitted batch and
require unchanged sentinel output plus released pins/handles. Both sampled and
read-only storage cases end with explicit shutdown owner/registry assertions.
These four new lifetime cases are not device-qualified yet.
