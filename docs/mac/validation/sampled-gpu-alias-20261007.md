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

Clean b038acd precommit lifetime proof now passes four required-device cases on
ARM64 and four on Rosetta x64 with identical neutral assembly hashes (Apple M4;
262ms /344ms, zero skips). The actual no-writeback compute consumer remains
uncommitted while preparation and queued ownership are released: references0,
native pin1, immutable old texture survives pruning. Commit cases read every
byte of all16red texels; discard cases remain unsubmitted with all64sentinel
bytes intact. Completion/discard releases the exact old native texture/pins,
and explicit shutdown verifies source byte budget0, no source-native batches
and no matching guest-image/extent registry owner. Evidence lives in
`immutable-pin-b038acd-arm` /`immutable-pin-b038acd-x64` with source, architecture,
assembly hashes, commands, log, TRX and parsed actual-execution markers.
This closes the precompletion owner-release test gap. Work owner release is
manual in the fixture; production Drain writer/error/shutdown integration and
CPU backing/copy leases remain unqualified, and the application graph stays off.

### W13 actual drain lifecycle proof (source 72750f1)

The private disabled graph now conditionally releases the exact queued work
owner in `CompleteGuestWork`. Successful native pins record their exact work's
batch identity; a scalar release receipt distinguishes that work's uncommitted
use from a peer's pin on the same version. It is diagnostic only, with no handle,
retention or readiness authority. Ordered managed actions flush before callbacks;
the receipt records the earlier release phase rather than making a timing claim.

Fatal selection/execution cancels source work owners, discards only uncommitted
batches, retires failed versions, preserves committed native pins and GPU-address
tombstones, and drops canceled queue nodes/accounting without acknowledgements.
`Run` exit uses the actual close helper; native shutdown rejects callers holding
the gate and waits committed buffers outside it. Graphfalse behavior is unchanged.
Pooled CPU payload return/serialization beyond source ownership remains a separate
cancellation audit; dropping queue nodes is not proof of pool ownership semantics.

Six device fixtures compile: sampled/readonly-storage actual Drain consumers with
production zero-CPU-read snapshot capture; missing-producer dependency fatal
discard; ordered-action fatal after real commit; pending-reader close with a
committed producer; and an actual same-version unencoded peer receipt negative.
Source setup uses actual admission/take plus controlled GPU render-pass clear,
materialization/immutable copy and logical completion. It does not run the dummy
admitted pixel shader or qualify translated producer execution. A private drain
core takes a bounded 5 s fixture budget while the runtime wrapper retains 12 ms,
avoiding wall-clock-dependent discard ordering. CPU source/backing ownership,
full producer/output wiring and graph activation remain open.

Actual graph-enabled drain rejects work owning producer outputs before native
materialization/encoding until producer hooks are separately qualified. The
controlled setup explicitly bypasses that consumer-only drain boundary to use
the previously proved GPU clear/copy helpers. It cannot advertise a successful
unwired runtime producer. Before enabling any broader path, color writers need
exact materialization before attachment resolution and copy after encoder end
before writeback flush; storage draw writers need shared exact destinations,
depth/other image mutations need explicit version publication or rejection, and
skipped/failed shader encoders must never certify output readiness.

The matching CPU-only source/admission/scheduler/frontend/drain selection passed
111/111 on ARM64 and 111/111 on Rosetta x64, including five new runtime lifecycle
cases. These managed results do not execute or qualify the six device fixtures.

Clean `72750f17e20bffda1a13bbf3d1751948d63e6f01` passed all six actual
required-device cases on Apple M4: ARM64 6/6 (275 ms), Rosetta x64 6/6
(848 ms), no skips. Both runs used identical neutral assembly hashes and the
shared machine lock. Source/commands, log, TRX, runner and parsed actual-execution
markers are retained under `artifacts/validation/metal-agent/immutable-drain-72750f1-{arm,x64}`.
The assertions read every output byte, prove zero CPU guest reads, and inspect
exact queued-owner receipts plus native pin/registry/budget cleanup. Runtime
producer execution, full ISA coverage and production activation remain excluded.

### Final-flush and canceled payload followup

Graph-enabled drain now latches final-flush failures and cancels source/queue
ownership. Native batches already marked committed keep their pins; only
uncommitted batches are discarded. A private default-null before-commit failure
hook permits a real encoded consumer to fail before commit without inventing
hardware completion. Clean `2192f71d757e6d014084ee931ffc81c6755c077f`
passed that actual required-device fixture on ARM64 1/1 (156 ms) and x64 1/1
(305 ms), Apple M4, no skips. Both runs retain matching DLL hashes, exact
commands/log/TRX/runner and all64A5/zeroCPUread markers under
`artifacts/validation/metal-agent/immutable-flush-2192f71-{arm,x64}`.

Cancellation inventories only still-queued unexecuted compute/draw payloads,
deduplicates pooled global/vertex arrays across the canceled queues, and returns
shared index leases through their idempotent owner. Queue ownership is removed
before returns, so a repeated close cannot return an array that has been rerented.
Completed CWT work history is never used to return CPU data. Managed tests use the
actual pool lease count/rerent paths, including cross-work duplicates, foreign
nonpooled data, index late copies and a completed work's newer array lease.
In-flight partial pool returns, presentation-carried payload ownership and broad
CPU producer/copy serialization remain separate qualification work.

A followup fatal-only cleanup guard preserves the original GPU exception, retains
any secondary cleanup exception separately, and clears the borrowed batch plus
wakes gate waiters in `finally`. Two CPU-only actual Drain cases inject a failure
after a real canceled pool return; both execution and final-flush paths preserve
the original cause, return remaining arrays, latch the cleanup diagnostic, and
wake a real condition-variable waiter. This followup does not invent GPU
completion or change the graphfalse path. The matching source/queue/frontend
selection passes 116 managed cases on each architecture. The device executions
above qualify their exact earlier source revisions, not this later exception
bookkeeping revision. Source graph activation remains off.

### Exact 2D color producer prerequisite (managed qualification only)

The private defaultfalse graph now allows only validated offscreen single-target
2D color triangle work, with matching nonempty target/pipeline write masks, no
depth attachment/state or shader-storage/global-data-share writer. A successful
pipeline is required before a fresh reserved output is allocated and installed;
attachment lookup then validates the reserved object's actual native type,
depth, dimensions and pixel format. A real render encoder must open and end;
only then does `MarkContentChanged` precede the immutable blit, before any buffer
writeback can flush the batch. Presentation/transient and other unqualified
writer paths fail explicitly rather than certifying a skipped source.

Fresh output initialization is the existing host first-use full-target render
pass clear. Apple's [load action documentation](https://developer.apple.com/documentation/metal/mtlloadaction/clear)
defines this as initialization of every attachment pixel; the default
[clear color](https://developer.apple.com/documentation/metal/mtlrenderpasscolorattachmentdescriptor/clearcolor)
is opaque black. This is host behavior, not a decoded guest clear intent or
proof that prior guest bytes were undefined. Pending CPU seeds and initialized
live contents without a retained immutable GPU predecessor are rejected. Later
partial draws must initialize from that predecessor, regardless of vertex count,
viewport/scissor or channel masks. Game activation still requires a separate
initialization/backing/coherence policy.

The matching CPU-only source/queue/frontend/color selection passes 135/135 on
ARM64 and 135/135 on Rosetta x64, no skips. Nineteen added production-path cases
cover unsupported actual Drain writers, pending CPU data, initialized live
contents, real vertex-slot pipeline failure, native metadata mismatch/zero
handle rejection and presentation exclusion before native mutation. No device
execution is claimed by these managed controls. Actual queued vertex/pixel
producer fixtures and old/new all-byte native consumer oracles follow separately.

The first color increment now requires an explicit vertex shader and nonindexed triangles. Indexed resources and the existing fixed fullscreen vertex substitution remain outside this proof. Two production Drain negative cases cover those boundaries. `MetalImmutableGpuVersionTests.Color.cs` contains four required-device fixtures for the real queued color draw path: captured-old/new all-pixel shader consumers with full and scissored/red-masked first draws, actual invalid native pixel compilation, and immutable-copy ordering before the real buffer-writeback flush. Native shaders are independent ABI/pipeline oracles, not guest ISA translation fixtures. Their hardware execution is pending review and an exclusive device grant. The existing shader-function retain cleanup concern and in-flight/presentation pooled CPU payload ownership remain separate limitations.

Frozen `152c570` executed all four required-device ARM cases on Apple M4: actual compile-failure and pre-writeback-copy cases passed; both old/new tests verified the old 64-byte oracle and actual VS/PS, then failed a premature test assertion about `NativeCompleted`. The ordered checkpoint had really waited for GPU completion, but Drain only recycles completion metadata at entry. The fixture now explicitly invokes the real recycle path after that wait, before asserting completion metadata and executing the new-version oracle. Failed TRX/log/source hashes are retained under `artifacts/validation/metal-agent/immutable-color-152c570-arm`; no production completion semantics were relaxed.

Frozen successor `ebc986dba71bdd8366399219f4b61dd12a78272d` passed **4/4 actual required-device color cases on ARM64 and 4/4 on Rosetta x64**, Apple M4, zero skipped cases (123ms/386ms). All 64 old and new bytes were checked through the real queued compute consumer after the real render draw/copy producer. The scissored red-only first draw verified host full-target clear outside the scissor and in masked channels; the second green-only partial draw preserved the prior red/blue/alpha. The actual invalid pixel-source case rejected without allocating/publishing an output. The writeback-trigger case verified copy encoding before actual native commit and all-red immutable bytes at real completion before CPU buffer writeback; it does not claim shader buffer stores. Both arches used identical managed assembly hashes, exclusive shared lock/watchdog, and exited with owned process groups dead/lock absent. Exact runner, command/source/DLL/native-import-source hashes, TRX/log hashes and per-case output are in `artifacts/validation/metal-agent/immutable-color-ebc986d-{arm,x64}/verified-results.json`; the failed `152c570` run remains separate. Exact managed controls passed 137/137 on both arches with device requirement disabled (213ms/557ms), build zero errors, at `color-managed-ebc986d`. Production graph remains hardfalse. This qualifies the bounded native writer/binding/lifecycle routes, not guest ISA translation, guest clear intent, CPU/GPU backing coherence, indexed/depth/storage/presentation writers or game progression.

The next graph-only CPU payload increment replaces cancellation's raw queued-array inventory with private accepted-transfer receipts. Queue and presentation admission validate/deduplicate Pooled Global/Vertex/index data before publication; accepted work holds its original owner/receipt after release. Shared arrays keep a holder count and canonical index lease, so the last holder alone returns the array. Index return is deferred from draw-call encoding in the graph path. Conflicting, returned and mismatched index leases reject before transfer; the additive neutral `GuestIndexBufferLease.Owns(byte[])` proves only descriptor/lease array identity, not exclusive lifetime. Admission rollback removes registration without returning caller-owned bytes. Presentation replacement acquires its new holder before dropping the old; per-frame payload-return calls keep that presentation's CPU snapshot until replacement/close. Fatal/close inventories only accepted live owners, including taken work, consumes each receipt before actual pool return, attempts remaining returns after exceptions, and keeps the existing original-cause/secondary-cleanup/wakeup behavior. Completed work never rediscovers return authority from an array that may have been rerented. Nonpooled/unaccepted data and aliases outside the ownership transfer remain caller-owned. Graphfalse behavior remains unchanged.

Nine new no-device production-path controls exercise real shared pool operations (pool gate held around deterministic return/rerent sequences): taken color rejection with shared Global/Vertex/index, taken cleanup after actual return/rerent with original GPU cause and secondary failure, shared-work canonical lease last-holder return, conflicting/already-returned/mismatched index rejection, completed work late-release/duplicate admission after real rerent, repeated/shared-replacement presentation, and registration rollback after actual disposed-source retain failure. ARM64 selected 146/146 passed, zero skips; native draw/image execution and runtime graph activation are outside these CPU controls.

Independent review found the first receipt commit (`bcab6cc`, 146/146 ARM64 and x64 managed controls) had not routed the main BGRA/splash publications through the owner-aware publisher. The followup covers every `_latestPresentation` publication, including initial splash, HideSplash and public BGRA Submit, with two additional leased-draw replacement controls. The splash control invokes the actual common publisher because EnsureStarted preserves an existing presentation, then exercises public HideSplash. A translated presentation CPU read-pin/renderer borrowing protocol remains a pre-activation requirement: current graph-enabled ExecutePresentationDraw rejects before pipeline, binding or pooled CPU payload reads; other selected production presentation variants carry no transferred Pooled draw payload. The managed per-frame return helper retention tests do not qualify concurrent native presentation rendering. Graph remains disabled in production.

The complete payload followup at clean `fecc97f7ef480527867d9194c55ddaa0fef83f50` passed **148/148 selected no-device controls on ARM64 and 148/148 on Rosetta x64**, zero skipped cases (219ms/632ms). Eleven added cases cover pool receipt lifetime and the BGRA/splash publication gap; existing original-cause and actual gate-waiter wake controls also remain green. Final test teardown closes accepted owners before raw pool returns while the owned pool gate remains held, including assertion failure paths. Source/assembly and log/TRX hashes, case identities/outcomes, build commands' worker constraints and zero-error build logs are archived in `artifacts/validation/metal-agent/payload-lifetime-fecc97f/source-results.json`. No device/game ran for this increment. Native renderer borrowing, graph activation and guest ISA/runtime progression remain outside these results.
