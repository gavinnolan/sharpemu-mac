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
