# W13 ownership iteration — 2026-10-07

Starting application source: `382bc5eda671c338b781bce400fdf39b62f4c020`.
Documentation head: `43525de`. The prior source-linked two-title report is
`artifacts/validation/integrated-20261007/game-final-382bc5e/REPORT.md`.

## Starting evidence

Demon's Souls fails with compute consumer 231 while advertised image producer
219 remains pending on another logical queue. No compatible initialized native
alias exists. These are failure-time observations, not captured source provenance.
Poppy still faults on a streaming write. Raw signal fields and native-entry
timing are missing; printed lifetime order cannot identify the recovery race.

## Implementation boundaries

1. Preserve copied raw descriptors, actual compiled shader image shape and
   instruction-derived read/write/atomic intent. Resource class is not intent.
   Unknown metadata must remain distinguishable from a readonly storage binding.
2. Metal owns source reservations and native version retention privately.
   Shared contracts may carry neutral immutable metadata, never backend objects
   or native graphics handles. CPU source means retained faithful bytes; GPU
   source means a captured producer/content version with explicit readiness.
3. Admit work before publishing its producer. Capture reads before advertising
   that work's writes. Preserve the selected version across later writers,
   replacement, eviction and retirement; producer completion alone is insufficient.
4. Preserve FIFO within each logical queue. Skip blocked heads with a bounded
   ready-queue scan; allow required producers and unrelated work to progress.
   Reject impossible/failed dependencies instead of silently completing or stalling.
5. Preparation owns its reservation until disposal; accepted work owns a separate
   lease through encoding/native completion. Cancellation, enqueue rejection,
   failure and shutdown must release exactly once. Account retained payloads.
6. CPU fault telemetry is opt-in and bounded. Preallocate storage before handlers,
   record scalar raw/normalized fields and owner/protection observations, publish
   atomically and format only in a safe context. Observations never authorize retry.

## Required verification

Production-path tests must cover readonly versus writable storage, descriptor
copy ownership, typed views, advertised-only addresses, pending producers across
queues, later writers between capture and execution, cancellation and admission
pressure. Real Metal tests must read back old and new GPU-written versions and
verify resource lifetime through completion. Native CPU workers must retain real
protected-page/SSE4a recovery and verify telemetry publication without signal-path
allocation or I/O. Cross-build both non-Mac CLI targets after shared contracts change.

Freeze and hash each native application before serialized game runs. Inspect
Sarah control response, DS progression/rendering, Superliminal visuals/stall,
and Poppy raw-entry records independently of counters and exception removal.
Record RSS, process stop and guest exit separately from frame cadence. Unsupported
views and incomplete ownership remain explicit failures, not stale CPU, raw tiled,
zero-image or alternate-graphics fallbacks.

## CPU lifetime work remains gated

Signal-entered lazy mapping/protection, mutation lock ordering, overlapping page
owners, rearm progress and partial native restoration failures still require a
complete owner-lifetime contract. A post-syscall epoch check cannot undo a native
permission change against reused backing. The withheld retry prototype remains
evidence only; see [the reservation design](../write-fault-reservation-design.md)
and [the prior review](../write-fault-ownership.md).

## First reviewed integration

Root source `c78e8adb71a2babc29aad7297a4e0d05be2a0b4b` integrates the
descriptor/shape/access metadata and opt-in fault telemetry. Source reservations
are not active in this build. The required memory-fault recorder uses explicit
invocation and permission-operation IDs, never new thread-local state. Its
fixed-size file mapping is initialized before handlers and retained until
process exit; scalar slots publish once and exhaustion counts drops.

The integrated x64 selection passes 112/112 without skips, including a real
protected-store callback, native return/SSE4a workers, raw-field/protection
pairing, zero allocation after warmup, record bounds/concurrency, metadata and
architecture checks. The separately retained worker observed Darwin SIGBUS
10/code 2, equal raw/normalized addresses and x64 error code 6. A protected store
is therefore described as a memory fault, rather than assuming SIGSEGV on Darwin.
Evidence is under `artifacts/validation/w13-20261007/`; the actual game trace is
recorded separately after rebuilding and freezing this source.

## Frozen Poppy trace qualification

Two serialized runs of the clean `c78e8ad` native application stop with SIGBUS
after 28 and 21 seconds, at peak RSS 1613 and 1359 MiB. Their independent trace
sessions publish 110 and 115 records with zero drops. In both runs the captured
streaming-store faults have nonzero raw addresses equal to normalized addresses,
Darwin signal 10/code 2 and x64 write error code 6. Address normalization therefore
does not explain these captured faults.

The observations include an armed peer restoring access, callbacks observing
invalidating state, and later callbacks observing disarmed state before rearm.
The latter return false from the early tracker. Successful protection returns
and entry timestamps do not establish kernel trap order or atomic ownership;
the current trace also excludes the later Core recovery decision. These results
justify recording final disposition next, not introducing an unqualified retry.

Run one's 25-second screenshot shows a black framebuffer with the application
HUD; run two exits before its first scheduled screenshot. Neither establishes
menu or gameplay. Both runs retain source/payload hashes, raw trace files,
decoded records and correlation details under
`artifacts/validation/integrated-20261007/game-poppy-native-c78e8ad/`.
The frozen application archive is retained as
`artifacts/validation/integrated-20261007/sharpemu-c78e8ad-osx-x64-app.zip`.

## Reviewed writer admission

Root source `820d4ba` includes a bounded queue correction: compute and render
targets advertise writer sequences only after queue admission succeeds. Compute
storage writer detection and content-change markers use normalized instruction
access; readonly storage is not advertised or marked GPU-written. Unknown access,
undefined flags and incomplete atomic intent fail explicitly before admission.
CPU seeding initializes a texture without falsely marking it GPU-written.

The integrated Rosetta selection passes 25/25 with no skips, covering real
full-queue backpressure, failed admission, readonly/writable/atomic intent,
duplicate content markers, retained CPU snapshots and fatal queue behavior.
Evidence: `writer-admission-integration-x64.log` / `.trx` in the W13 directory.
These tests use production admission and helper paths without a Metal device;
they do not qualify native source ordering or repair the DS alias failure.

Private source leases and preparation cleanup remain isolated with the source
versioning work. Their synthetic lifetime tests are useful contract checks, but
are not native writer/copy/completion proof. The separate storage layout review
also leaves address-current image reuse explicitly unqualified until captured
source, CPU generation and mapping ownership are enforced.

## Integrated bounded checkpoint

Clean source `87d7fbb` integrates storage texture-format/row validation and the
schema-2 final POSIX bridge disposition recorder. Its selected x64 suite passes
150/150 with no skips; the existing AVX2 capability-gated fixture is not evidence
of AVX2 guest execution. The exact clean-source native Metal texture
materialization suite passes 33/33 with `SHARPEMU_TEST_REQUIRE_DEVICE=1`, including
both storage row readbacks. The offline parser passes 6/6 and retains schema-1
compatibility. Windows/Linux CLI Release cross-builds both pass with zero errors
(9/3 warnings); neither is native Windows/Linux runtime qualification.

The signed native app builds successfully with embedded clean source `87d7fbb`.
Its frozen Poppy run stops after 21 seconds with a Metal storage-view rejection
at compute sequence 8, `dcb.graphics`, submission 12, before the earlier streaming
store fault. The schema-2 file publishes 30 startup/protection records, zero
drops and no game signal or VMOVNTDQ entry. No screenshot, positive guest-frame
period, menu, gameplay or final game-fault recovery is established. This is an
earlier failure and must not be presented as CPU recovery improvement.

That result prompted a selected-view audit: the backing resource's
`ResourceMipLevels` determines its layout and is not the native selected view's
mip count. A faithful static base-zero single-level view should not be rejected
solely because its allocation contains a larger mip chain. Nonzero selected mip,
arrays and volumes still require separate support and native proof. The exact
Poppy descriptor still needs the more precise rejection diagnostic.

Evidence: `bounded-87d7fbb-x64`, `storage-native-87d7fbb-x64`,
`app-87d7fbb-build` logs in the W13 directory; `cross-platform-final/87d7fbb/`
and `game-poppy-disposition-87d7fbb/` under the integrated evidence directory.
Both `c78e8ad` and `87d7fbb` applications retain archives and manifests for replay.
