# W13 ownership iteration — 2026-10-07

Latest bounded integration: clean source `dd19db69f386d2d5d7b7fa0cc0891241416d38f9`.
The source version graph remains isolated and inactive; its ownership and native
completion work is not covered by the bounded integration results below.

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

## Selected mip, native null reads and exact Poppy shape

Source `dd19db6` integrates the base-zero selected-mip distinction and the strict
readonly null policy. Required-device native checks pass 36/36 for materializer,
layout and null coverage. This includes faithful owned R16 base-mip bytes from a
two-mip allocation after the original source is mutated. Two additional native
kernel checks distinguish sampled `.sample` from storage `.read`; both read four
zeros from the production normalized null textures. The affected integrated
managed selection passes 91/91. The signed app embeds clean `dd19db6` provenance.
See `native-mip-null-integration-x64`, `native-null-modes-x64`,
`mip-null-managed-integration-x64` and `app-dd19db6-build` in the W13 directory.

Its frozen Poppy followup exits with code134 after20s, peak RSS1412MiB, at the
same compute sequence8 / graphics submission12. The precise descriptor is type10,
1x1, depth1, one layer, non-array, base/selected mip0 and one view/resource mip;
format10/4, pitch1, tile27. Actual shader metadata is volume storage, unsigned
integer, write access. This is a native 3D binding requirement even though depth
is one; the 2D materializer correctly rejects it. The earlier Poppy rejection is
therefore not explained by a larger backing mip chain.

The trace again contains30 startup/protection records, zero drops and no game
signal or streaming-store entries. No screenshot or positive presentation sample
was captured, and no emulator process remained after collection. CPU final game
disposition, menu and gameplay remain unqualified. Evidence is under
`artifacts/validation/integrated-20261007/game-poppy-disposition-followup/`.

The same frozen `dd19db6` app runs Dreaming Sarah for 150 seconds at peak
RSS1137MiB. Screenshots verify coherent opening/title rendering, right movement
into a new area, left movement back toward the opening platform and a jump.
The 72 presentation periods average 59.84 reported guest frames/s; concurrent
CPU-only development means this is compatibility evidence, not a controlled
performance comparison. TERM stops the process in two seconds without KILL,
but the log records forced guest unwind and a not-implemented dispatch result;
clean guest shutdown remains unqualified. No emulator process remains. Hashes,
visual evidence and scope are in `game-sarah-dd19db6/REPORT.md` and `index.json`
under the integrated evidence directory.

## Tracker content observation

Source `2c5ab39f0f16d6d534b5d327070bee2507c1230b` adds a neutral safe-context
content token containing separate protected/watch owner identities, generations
and byte extents. This closes an observation ambiguity: untrack/retrack can
reset the old scalar write generation to zero. The token exposes that owner
replacement without changing legacy read snapshots, signal handling or retry.
It is not a mapping lease; full-footprint observation and before/after equality
remain the caller's responsibility. Its use by the source graph stays isolated.

The integrated Rosetta content-token/tracker selection passes 24/24 without
skips (`content-token-integration-x64` in W13 evidence). Standalone token checks
pass 7/7 on ARM64 and Rosetta. Exact clean-source Windows/Linux CLI cross-builds
pass with zero errors and 9/3 warnings, recorded under
`cross-platform-final/2c5ab39/`. These are cross-builds, not native non-Mac runtime
verification. The game-qualified archived application still embeds `dd19db6`.

## Integrated upstream and native volume checkpoint

Local merge `a46904f` preserves upstream `b02c06181a296f4dc7d9ee2a44780a6ecd959592`
ancestry. The reviewed update includes AudioOut2 PCM snapshot and cleanup fixes,
service exports and ABI corrections. Its intended managed/architecture selection
passes 182/182 on ARM64 and Rosetta; Windows/Linux CLI cross-builds pass with
zero errors and 9/3 warnings. Service stubs remain scoped in the upstream review;
these checks do not qualify a title or native Windows/Linux execution. Exact
commands, hashes and review are in `package-agent/upstream-b02c061-caa2f92/`
under the integrated evidence directory. No remote push or merge was performed.

The clean combined source `a00db6662d2543569bd3daab5744360c2d3afc60` contains
bounded native storage-volume materialization. Depth-one volume bindings use a
real Metal 3D texture. Linear volumes support complete padded slices; tiled
volumes support only the independently bounded, aligned mode27 single-mip
1x1x1 origin. Larger tiled volumes remain unsupported. Two-dimensional image
operations reject volume handles explicitly. See `storage-volume-materialization.md`
for descriptor admission and the address-oracle limitations.

The combined managed selection passes 280/280 without skips. Release actual-device
materialization checks pass 39/39 without skips, including translated guest MIMG
loads/stores across depth-one and depth-three linear textures and the bounded
mode27 singleton. They validate materialization and native argument-buffer
execution, not complete command scheduling. Logs/TRX are
`volume-upstream-integrated-x64` and `volume-native-integrated-x64` in W13
evidence; the latter build and test logs are retained separately.

The signed app embeds clean a00 source, upstream b02, Metal, AppKit, CoreAudio,
GameController and osx-x64. Build log is `app-a00db66-build.log`; the archived
zip SHA256 is `58bd474add1c70d08258b44f958f78bde4edbac1ba0a29687bcfe2f6f33b69a8`.
Its manifest is `sharpemu-a00db66-archive-manifest.json` in integrated evidence.

Poppy exits with SIGABRT/code134 after21s, peak1390MiB. It rejects a different
larger tiled volume during Compute binding preparation: shader
`DB87BCC8ED30AD37`, address `0x7AC109C000`, depth16, tile5. Raw resource words
are `7AC109C0 C0B00000 0003C003 A0500004 0000000F 00700000 00000000 00000000`.
The resource is 16x16x16 R16Uint: `(0xC0B00000 >> 20) & 0x1FF` is format11,
not format176. An initial investigation misread the nibble position as BC4;
the exact raw words remain authoritative. This capture does not print the
shader's sampled/storage access metadata or an exact admitted work sequence.
This run does not prove the previous exact singleton binding was reached.
There are no screenshots or positive presentation periods. Schema2 records26
entries with zero drops, but no actual game fault or prior VMOVNTDQ callback;
CPU recovery remains unqualified. The indexed report is
`game-poppy-volume-a00db66/REPORT.md`. No process or machine lock remains.

The larger volume requires a true 3D address oracle including z and allocation
extents before widening support. The isolated GPU source graph remains disabled
pending native writer/copy/completion/retirement and failure/close proofs. Tracker
observations alone do not serialize guest copies or retain backing mappings.

## Observation metadata qualification

Root `6248d0f` adds `TryGetObservedContentToken(address, byteCount, out token)`.
It requires full-footprint armed protection metadata or an explicitly registered
managed-writer footprint; missing, ordinary watch-only and insufficient owners
fail closed. It preserves legacy tokens, snapshots and fault paths. Managed
notification occurs before the copy and native fault handling before the resumed
store, so a successful observation does not certify a stable copy or backing
lease. The GPU source graph remains isolated.

The integrated Rosetta token/tracker selection passes 35/35 with zero skips
(`observed-token-integrated-x64` log/TRX). The standalone metadata fixture and
existing token selection passes 18/18 on ARM64 and Rosetta under
`package-agent/observed-token-2d25589/`. Fabricated protected-owner state tests
are metadata checks, not native arming or kernel permission verification.

Exact clean `6248d0f508f92596d6b6284421ebab3dadfc9807` Windows/Linux CLI
cross-builds pass with zero errors and 9/3 warnings; commands and output hashes
are under `cross-platform-final/6248d0f/`. No non-Mac native execution is claimed.

## Poppy typed-volume metadata followup

Clean diagnostic app `34fc6d752350b8bcf0210f50b97e47f26a48e84e` adds only
shader shape/access fields to snapshot failure evidence relative to the reviewed
tracker checkpoint. Its Release Metal build and signed app assembly pass.
The hash-filtered shader capture confirms Volume/Storage=true, NumericClass=Uint,
access=Write and an actual guest ImageStore at pc0xB4. Both the raw format11
R16Uint descriptor and compiled unsigned 3D store agree. The earlier BC4
investigation was a nibble extraction mistake, not a cache defect established
by this capture.

The run exits SIGABRT/code134 after22s, peak1447MiB, before any screenshot or
guest presentation period. Eight shader evidence files total127785 bytes;
schema2 again contains26 published records with zero drops and no actual game
fault or prior VMOVNTDQ callback. Indexed evidence is
`game-poppy-bc4-metadata-34fc6d7/`; its retained folder name reflects the initial
hypothesis. The app archive SHA256 is
`fb3ae8ce85574c344be972df96934d2f133c7b01803164e1f45957c55c8b4bfb`.
No shader semantics, tiling support or CPU retry policy changed in this build.

A separate snapshot admission correction rejects unsupported compressed storage
formats before any guest memory read, including linear 2D/3D and tiled volume
shapes. The affected snapshot/storage selection passes45/45 without skips
(`compressed-storage-snapshot-x64` log/TRX), including four no-read regressions.
This guard does not affect Poppy's R16Uint resource and does not qualify tile5.

The exact descriptor/store replay is integrated at `0c58480`. It runs through
the real shader materializer and permutation cache with a fake compiler/host:
format11 Uint, a deliberately changed format176 Float, then the original Uint
descriptor. The original permutation is reused and each stage retains detached,
matching descriptor words. Two checks pass on ARM64 and Rosetta in the isolated
replay; integrated Rosetta also passes2/2 without skips. No specialization/cache
defect was reproduced. Root's combined test command stalled during build before
test execution; its log is retained. A separate build passes with zero errors,
then the no-build test succeeds. See `poppy-descriptor-replay-integrated-*` logs
and TRX, and `poppy-volume-descriptor-replay-20261008.md` for limits.

## Isolated native GPU ownership proof

Private source `bfe518db58140eba47f50f76fc435152c97b1b3b` passes six actual-device
ownership fixtures on ARM64 and Rosetta, with required-device1 and zero skips.
They verify all16 RGBA texels of an old red immutable GPU copy after a later
green writer, in one command buffer and in ordered separate buffers; reject a
different buffer consuming an uncommitted producer; invalidate all earlier
outputs when an uncommitted batch is discarded; wait for a committed buffer
before close releases native and live-registry ownership; and reject an evicted
GPU source before any CPU memory read. The writer is an actual render-pass clear.
The byte oracle uses CPU texture readback after native completion, not a shader
consumer. Logs/TRX and source/assembly hashes are under
`artifacts/validation/metal-agent/immutable-gpu-bfe518d-{arm,x64}/`.

Reviewed cleanup refinement `6dbb13d01eaf4f7fec4d8974de904f18840baedd` constructs
native-error context before pin bookkeeping and preflights registry capacity
before extra retains, with rollback on registry failure. Its managed selection
passes104/104; allocation failure was not injected into an actual device test.
The source graph stays disabled and isolated pending integrated compatibility,
actual shader consumption and production execution/cleanup qualification. Native
clear/copy fixtures do not qualify translated draw/compute scheduling or games.

## Integrated dormant graph and bounded tile-5 volume

Local merge `15c0e55` integrates the reviewed dormant ownership/preparation graph.
Its private switch remains hard default-false, without a production setter or
producer/Drain activation. The mapping/content observation guard also affects
legacy CPU snapshot preparation; it is not wholly dormant behavior.

Local merge `4b5e0fae30d343716be15976d98b8b7c2223f343` integrates candidate
`f6668a19a6d2c09456e34175bc1646b12cea9b06`; source/tests match exactly. Combined
managed checks pass200/200 on ARM64 and Rosetta. The host-neutral layout is
bounded to a single-mip16x16x16 16-bit mode5 volume, independently matched against
all4096 AMD AddrLib offsets. Metal admission additionally pins the observed
R16Uint/X000 raw descriptor and rejects metadata/flags/unknown shapes. Sampled
tiled3D snapshots and deferred XY uploads of native3D textures reject before
reading/uploading; this does not add general sampled-volume support.

Integrated Release native materialization passes40/40 under Rosetta with
required-device1 and zero skips. Every4096 texels matches the oracle, a translated
store changes only its selected texel, and a translated sampled load through an
explicit native X000 texture view returns `[54321,0,0,0]`. The fixture does not
qualify production sampled-volume materialization or compute submission.
Logs/TRX are `w13-20261007/tile5-integrated-*`. The clean signed osx-x64 app uses
Metal/AppKit/GameController/CoreAudio and upstreamb02c061; archive SHA256 is
`b9f482bb8e7a1caeaf9aaa0ab52c396fe74bdc840d0ed91ae56fac798c1f5dc1`.

Actual games expose a runtime regression in this build. Poppy stops at21s with
SIGABRT, peak1402MiB, at the supported mode27 singleton0x9007000000: three
snapshot attempts fail the combined freshness guard. It produces no screenshot
or guest frame; no tile5 materialization is established. Sarah remains alive150s,
peak844MiB, but presents only eight initial guest frames and then stalls on the
Ratalaika logo. Pixel shader0x109472F03FF5477A rejects three snapshot attempts
at0x70056B0000. Movement/jump and gameplay are not reached. TERM ends the process
immediately while repeating the stored worker exception. Compared with the
dd19db6 scene/movement/jump proof, this is a regression requiring diagnosis,
not game acceptance. See `game-poppy-tile5-4b5e0fa/` and `game-sarah-4b5e0fa/`.

## Actual compute consumer fixture

Isolated `839ec71` fails four actual-device cases because its handwritten MSL
uses invalid C-array declarations with argument-buffer id attributes. Full TRX
retains the compilation error and untouchedA5 output sentinel. Production shader
translation/ABI was not changed on this evidence.

Corrected fixture `fe7c72766794262fc9440fa7e8f314ae48af837c` passes4/4 on both
ARM64 and Rosetta, zero skips, required-device1. It asserts nonzero native shader
library/pipeline and completed real command buffer, then checks all64 guest
writeback bytes for old/red and new/green captured GPU versions through actual
ExecuteComputeDispatch/EncodeStageResources. Both sampled and readonly-storage
cases run with preparation alive and disposed. Evidence is under
`metal-agent/immutable-consumer-fe7c727-{arm,x64}/`.

The kernel is an independent MSL ABI oracle, not translated guest ISA. Queued
ownership is released after synchronous writeback completion in these fixtures;
release before completion and actual Drain integration remain unqualified.
The production source graph remains disabled.

## Snapshot admission correction and fresh games

Diagnostic-only `618b57f` identifies a mapping-only veto in both games: Sarah's
global epoch85610→85611 and Poppy's554266→554287, with unchanged qualified tokens,
generation0 and Armed owners. The fields are separately sampled, not an atomic
lease or attribution of the mapping mutation address. A real-host control
reproduces unrelated HostMemory.Protect vetoing three copies while kernel
permissions remain READONLY and content tokens/generations are unchanged.

Reviewed correction `661dc8d` integrates as `ba83361`: owned-copy admission with
the graph disabled retains ReadSnapshot and qualified content identity checks;
full mapping/content observation still certifies provenance and graph-enabled
admission. A mapping-raced owned copy receives Stable=false graph provenance.
Actual notified writes and generation0 owner replacement reject in both modes.
No new retry or backing lease is claimed. Combined210/210 pass on ARM64 and
Rosetta; integrated required-device14 ownership and40 materialization checks
pass under Rosetta without skips (`w13-20261007/owned-copy-integrated-*`).
Signed clean app archive SHA256 is
`fcdfdeb4a13a27afe4e516dff94404f77edfe55191bef72ededf0c383369aa31`.

Poppy's new24s run exits SIGBUS/code138, peak1549MiB. Schema2 publishes77 records,
zero drops. Real signal10/code2/error6 entries3-6 retain nonzero raw==normalized
registers and VMOVNTDQ RIP0x80135FB0B. Invocation6 accepts Invalidating before
peer restore operation11 returns; operation11 then succeeds, while later rearm
epoch2 precedes earlier invocations3/4 final declines. These observations do not
authorize recovery or prove a pinned mapping lifetime. One telemetry guest frame
is recorded, no screenshot/menu/scene/gameplay; tile5 materialization is not
established by this run. See `game-poppy-snapshot-fix-ba83361/`.

Sarah remains alive150s, peak1215MiB, restores coherent scene and right/left
movement into another area and back. Z input is delivered at140s, but captures
miss an airborne phase; jump is unqualified for this run. Its72 intervals total
9952 guest frames over144.377s (weighted reported cadence68.93/s, range34.28–100.59).
Concurrent compilation/testing prevents a controlled performance comparison.
TERM stops in2s without KILL but logs a repeating import loop/forced unwind and
NOT_IMPLEMENTED; clean guest exit remains unqualified. See
`game-sarah-snapshot-fix-ba83361/`. Source/app remained frozen through both runs.

The native precommit pin fixtures at `b038acd` also pass4/4 on each architecture:
prep/work references drop to0 before commit while native pin1 survives pruning,
actual shader output remains old red after completion, and uncommitted discard
leaves the A5 sentinel while releasing all native/registry ownership. This is
controlled execution, not actual runtime Drain or producer-pipeline activation.

## Guest fonts integrated and fresh d231b6d app

Reviewed font source9f69c2e and combined candidate1d882ef integrate as d231b6d.
CoreText/CoreGraphics owns guest fonts on macOS; Fonts.Skia remains a separate
Windows/Linux adapter. Shared Libs no longer references Skia. GUI/Avalonia still
adds Skia to the Mac launcher closure; native launcher extraction is pending.

Combined candidate220/220 passes on each CLR architecture, with integrated
source/tests matching that candidate. Root font build has0warnings/errors;
13/13 Rosetta checks pass including8 actual CoreText CPU cases and the packed
guest font ABI lifecycle. Native CPU raster proof is not GPU/device proof.
The signed app verifies and is archived with SHA256
`1728869b8c1c098b02c1ec21580c15786178f118e774708c73da632f71b43ae3`.

Fresh Sarah150s remains alive, peak1169MiB. Inspected105s scene,120s new area
after right movement and139s return after left movement are coherent. Z input
at141s is logged; reviewed139/141/142/143/144s captures remain grounded, so
airborne jump verification is still open. Reported7748guest frames over142.450s
give54.39/s weighted cadence; concurrent build/tests prevent a controlled
performance comparison. TERM stops in2s without KILL but repeating-import
forced unwind/NOT_IMPLEMENTED persists. This title does not establish use of
the guest CoreText exports. Source/app were frozen through the run.
Evidence: `game-sarah-coretext-d231b6d/`, archive manifest
`sharpemu-d231b6d-manifest.json`, and `w13-20261007/font-integrated-*`.

## Test-only restoration bookkeeping

Reviewed b0647d7 integrates as131f674. Eleven x64/Rosetta cases pass: seven
actual native page operations and four scalar CAS/ABA/exhaustion/allocation
controls. Failed harness attempts are preserved. The helper is test-only; it
does not reserve backing, certify fault-frame ownership or authorize retry.
Production mapping/fault/recovery behavior is unchanged. Native cases return
early outside macOS and do not establish portable runtime qualification.
See [prototype scope](../page-restoration-bookkeeping-prototype.md) and
`cpu-page-restoration-prototype-b0647d7/` for exact source/results/hashes.

## Shutdown redirect, shared Application package and actual Metal Drain

Reviewed shutdown b20ebac integrates asf994192. The import guard was unreachable
because ActiveForcedGuestExit includes the global request it was asked to
negate. Global volatile request and current-slice state are now checked
separately. Original-guard differential controls fail the positive case; final
6/6 x64 helper simulations pass, as does a separate actual synthetic native
guest normal-return worker. The helper controls use simulated ICpuMemory
return slots and do not qualify native guest teardown by themselves.

Signed isolated b20ebac Sarah150s stays alive, peak1126MiB, and stops within
the first harness poll without KILL. Guest return1 is consistent with the
shutdown redirect; join error and forced-unwind/NOT_IMPLEMENTED classification
remain. Scene/right/left response stay coherent. Z at134s produces a change
from grounded134s to platform-top135/136s; upward response is verified, but
no airborne frame is captured. Reported8948frames over142.341s (62.86/s) is
not a controlled performance comparison. See `host-shutdown-slice/`.

Neutral Application source654af7e integrates as76d840d: existing settings JSON
filenames/schema, catalogue/cache/watch, localization and update/link services
move out of the GUI. Root Application73/73 passes on both CLR architectures.
The native AppKit frontend is separate and not active in main yet; Mac still
carries Avalonia/Skia through the current launcher.

Metal lifecycle chain6ac4bef/72750f1/2192f71/0fdd049 merges ascf2b3d6. Actual
Drain/Complete owner release, fatal cancellation, gate-free close wait and
final-flush exception latching are conditional on the hard-disabled graph.
Pool cleanup preserves the original GPU cause, retains secondary failure and
always wakes admission waiters. Controlled native cases qualify consumer
execution and precommit failure; real driver-error injection and actual
producer pipeline activation remain separate. Unwired runtime producers are
explicitly rejected. Pending queue payload returns are tested; in-flight
partial-return and presentation ownership still need their own audit.

Combined cf2b3d6 selection247/247 passes on Rosetta, including six shutdown and
eleven test-only restoration cases. ARM64 selection230/230 passes; it excludes
those17 x64-qualified cases. Integrated required-device ownership cases21/21
pass under Rosetta without skips, including the seven new actual Drain/final
precommit controls. Earlier ARM/x64 native candidate proofs remain exact727
and219 revisions. Evidence is under `w13-20261007/lifecycle-integrated-*` and
`application-integrated-*`. The source graph remains disabled.

## Mapping registry scaffold and refreshed integrated bundle

Owner ef49e43 and registry c166e99 merge as b6fe8f7. The immutable mapping
domain uses DomainEpoch independently of tracker arm/page epochs. Safe-side
transactions preallocate replacement records, close affected domains, drain
outside the registry gate, bind exact reported kernel scopes and quarantine
failed or partial outcomes. These contracts have no production provider or
fault callback callers and grant no permission to retry a protected store.
Canonical backing identity, every native mutation path and actual per-page
permission receipts remain prerequisites for activation. Poppy's Invalidating
recovery race is still unresolved.

The integrated owner/registry/architecture selection passes 101/101 on both
ARM64 and Rosetta without declared skips: 30 ordinary-thread bookkeeping
controls and 71 architecture rules. No kernel mapping operation, signal
callback, device or game ran in that selection. The Release build has 110
warnings and zero errors. Exact logs/TRX are `mapping-registry-integrated-*`.

The default signed Mac app now contains clean b6fe8f7, built at
2026-10-08T00:12:00Z, upstream b02c061, osx-x64 with native Metal host stack.
The publish closure excludes Vulkan, MoltenVK and SDL. The launcher still
includes Avalonia/Skia pending separate AppKit window qualification. This is
an ad hoc signed local bundle; notarization did not run. No game or device
qualification is claimed at b6fe8f7. Its archive and hashed evidence manifest
are under `integrated-b6fe8f7/`; the manifest preserves the earlier cf2b3d6
source provenance of lifecycle/Application/cross-platform build results.

## Isolated native AppKit window qualification

Clean candidate3b54f79 remains outside main. Independent ABI/session/modal
review and14/14 ARM64 plus14/14 Rosetta controls pass. A signed frozen candidate
runs against an ephemeral synthetic catalogue/data root under the shared lock.
The bounded visible session exits0 after278.59s without timing out. Native
catalogue/search, per-game/global settings saves, folder-picker cancellation,
settings-window modal close and owned-child Stop & Quit confirmation are
observed. Console captures stdout/stderr and the synthetic TERM handler's
final output. The displayed exit -2 is the launcher's stop-request tag; the
synthetic handler exits0. This is not a guest CPU exit result.

Root visually reviews catalogue and console screenshots. Signed app hashes
remain unchanged; the shared lock is released and all three owned synthetic
PIDs are dead. No game or GPU fixture runs in this qualification. Evidence is
`package-agent/native-launcher-3b54f79/qualification-manifest.json` and the
separate `appkit-independent-review-3b54f79/` source review.

The Quit menu is disabled by Cocoa while settings are modal; that path remains
unqualified. Minimum resize bounds, full frontend parity, bounded notification
backlog and parent-exits-first inherited-pipe descendants also remain open.
The candidate has no forbidden UI assets, but main retains the earlier
Avalonia launcher until lifecycle and integration acceptance are complete.
