<!-- Copyright (C) 2026 SharpEmu Emulator Project -->
<!-- SPDX-License-Identifier: GPL-2.0-or-later -->

# Current Mac optimization evidence — 2026-10-08

The [all-nine frozen baseline](all-games-baseline-20261008.md) remains the current
collection-wide result. No title has passed the complete correct-rendering,
controllable-gameplay, 60+ FPS and fast-loading gate. The
[optimization plan](../all-games-optimization-plan.md) names dependencies and
acceptance criteria. All changes are local; no remote publication or merge.

## Integrated host optimizations and diagnostics

Clean `b7c0df4e` dedicated native-only signed bundle:
`artifacts/macos/game-opt-20261008/SharpEmu.app`. SDK10.0.103/runtime10.0.3,
osx-x64/Rosetta on Apple M4, 24GB, macOS27.2. Closure, architectures and ad-hoc
signature pass. No notarization. Default `artifacts/macos/SharpEmu.app` remains
at `8c19cd7`; do not infer its binary from the current checkout.

- Selector cache integrated in `b48cac16`: warmed paired native registration
  versus actual production cache,10 alternating million-call samples and4096
  concurrent native-identity checks. x64 median38.850833→9.920250ns/call (74.47%
  lower); ARM64 median20.728062→4.650541ns/call (77.56% lower). Zero measured
  allocations in each sample. Actual Tier1 assembly shows cache hits avoid
  UTF-8/PInvoke while preserving acquire loads and concurrent publication.
  This is per-call microbenchmark evidence, not game FPS. Final accepted timings:
  `selector-warm-{x64,arm64}.json`; exact copied DLL/source manifest in
  `artifacts/validation/all-games-20261008/selector-benchmark-c97b685/`.
- Main-presenter phase tracing integrated in `92f52bed`: bounded4096-row ring,
  unique guest commit boundary, disjoint elapsed-wall phases. Host redraws do
  not count as guest frames. Disabled path does not access timing storage/clocks.
- Balanced HLE timing in `89713824`/`b7c0df4e`: per-thread LIFO, inclusive/exclusive
  wall time, exception-safe disposal, bounded128-depth omission accounting.
  Exact integrated tests:10/10 per CLR with profiling off,9/9 per CLR on, zero
  skips. These timings include waits; they are not CPU self-time or CPU load.
- Native settings controls/persistence in `61dad03`/`b20aa4f`/`f92e3dc`: private
  atomic save, expected write/delete failures surface without destroying old
  config. Integrated Application76/76 +Launcher38/38 per CLR. Actual typed AppKit
  control/disk-error interaction remains unqualified.
- Dormant Mac owned-operation prototype in `2608b09e`/`5493dced`/`b32a56c7`:
  integrated managed20/20 per CLR. Candidate actual native9/9 per CLR uses
  4KiB/16KiB pages and observed mprotect failures, partial-prefix restoration,
  held-pin retirement denial and exact surviving munmap ranges. Not activated
  in production, not an authoritative mapping graph or Poppy fix.

## Dreaming Sarah: actual new frame-pacing capture

`artifacts/runs/20261008-041630-sarah-metal-profile-b7c0df4e` runs the dedicated
signed clean b7 bundle for150s. Input scripts and screenshots show a coherent
outdoor scene, rightward scrolling/world movement and return left. Peak1183MiB;
TERM stop succeeds with no KILL. This run does not show an airborne jump;
previous8c19/e627 airborne/landing evidence is separate. Guest pthread_join
error persists, so a clean guest-teardown claim is unsupported.

Compilers were paused for this capture. An unrelated REmu process was briefly
observed after completion; overlap with the game is unestablished. Do not label
the OS globally idle or use this single run as a controlled A/B improvement.

The trace retained4096 of9189 unique guest commits (5093 overwritten):

| Metric | Result |
| --- | ---: |
| Mean retained commit interval |17.252397ms |
| Effective retained commit frequency |57.962959Hz |
| p50 /p95 /p99 commit interval |16.720 /29.115 /38.693ms |
| Maximum retained interval |65.880ms |
| Median DrainGuestWork wall time |5.2355ms |
| Median MaterializePresentation wall time |0.014ms |
| Median AcquireDrawable wall time |10.812ms |
| Median EncodeAndCommit wall time |0.171ms |

These phase medians are not additive attribution of a median frame. Worst row7510
contains48.238ms drawable acquisition and15.947ms drain; another56.119ms interval
contains55.763ms drain. The title screenshot during scrolling reads41.7FPS.
Some two-second windows exceed60FPS, but this is not sustained60-FPS/good-pacing
acceptance. GPU execution, scanout and CPU self-time were not measured.

`current-b7-runtime/manifest.json` under the all-games evidence directory hashes52
retained payload/build/run/trace/screenshot/summary/archive files.

## Poppy: actual current fault ordering

`artifacts/runs/20261008-041407-poppy-native-fault-b7c0df4e` exits138 after27s,
peak1717MiB. It uses native scalar trace schema2, no performance/HLE profiling.
The immutable binary parses identically to the reader JSON:71records,0drops.

Invocation3 observes owner11/epoch1 Armed and its own full2211840-byte RW
mprotect returns0/errno0. It publishes Disarmed and reports recovered.
Invocation4 for another address in that extent arrives about240µs after the
restore-return observation, observes Disarmed, performs no native restore and
ends declined. Rearm to epoch2 occurs about18ms after invocation4 entry.

This is current Disarmed/declined evidence, distinct from an earlier trace's
Invalidating/recovered route. The aligned VMOVNTDQ operands do not justify an
instruction/alignment patch. Delayed pre-restoration trap delivery is consistent
with the trace, but kernel trap timing, all permission owners, aliases and
backing lifetime remain unproved. No unconditional retry or accepted-on-busy
behavior has been introduced.

Exact classification,11 b7 source hashes, original binary/JSON/log/build/payload
hashes and the source-linked recovery protocol are under
`artifacts/validation/integrated-20261007/poppy-fault-classification-tools/`.
Offline classifier8/8 and schema reader6/6 pass. Schema3 per-range decision diagnostics are integrated in63a198ac without
changing recovery ordering. Candidate13/13 per CLR plus12/12 offline reader
controls pass. An actual schema3 run on the frozen AE bundle exits138 after29s,
peak1164MiB. Its4096-slot prefix is saturated with28720 dropped records; a missing
fatal decision in that prefix must not be treated as proof that no decision ran.
Classification and diagnostic capture refinement are pending; recovery is unchanged.

## Texture compatibility qualification

Native singleton array source `13272308` is integrated as
`5e0e0476`/`52b57265`/`412e0562`. Managed73/73 per CLR; exact four numerical
native cases4/4 ARM64 and4/4 Rosetta, with zero skipped/early-return cases.
Production raw descriptor→owned complete64KiB physical block→four-byte seed
produces native texture2d_array, arrayLength1, RGBA8Unorm. Valid MIMG origin store
and translated float read agree; invalid slice1/sliceFFFFFFFF/x1 writes preserve
all four asymmetric seed bytes and original guest padding. Exact sources/DLLs/TRXs
and retained markers: `package-agent/singleton-array-native-1327230/` under the
integrated evidence directory. Raw non-content fields remain strictly gated.
Whole title rendering, progression, FPS and loading are not established by this
fixture. Array feedback copies remain explicitly unsupported.

Selected storage-mip and exception-safe publication changes are integrated through
`ae157337`. The [selected-mip report](selected-storage-mip-20261008.md) records
actual native store/load, rollback and retry qualification. Root combined frozen
qualification has22/22 managed controls per CLR and8/8 required-device numerical
cases per CLR, zero skips. Exact copied source/DLL/results are retained under
`graphics-combined-ae157337/`. The texture-source graph stays hard-disabled.

## Frozen AE compatibility re-runs

The dedicated `artifacts/macos/graphics-compat-ae157337/SharpEmu.app` is signed,
native-only, clean exact `ae157337`, SDK10.0.103/runtime10.0.3. Source and bundle
were frozen throughout five runs. Concurrent compilers, CPU controls and foreign
REmu activity make these blocker diagnostics, not FPS/loading comparisons.

| Title | Actual result | First remaining blocker |
| --- | --- | --- |
| Silent Hill | Exit134,24s,1960MiB | Singleton array raw WORD5=`00700000`, DST_SEL=`F2E` rejected before native dispatch |
| Octopath II | Exit134,33s,3142MiB | Same exact non-content field and BGRA selector family |
| Oregon Trail | Exit134,29s,2128MiB | Same exact family |
| Demon’s Souls | Alive90s,2832MiB, failed GPU queue, zero guest frames | Selected-mip admission rejects changed session/epoch/token observation before queue insertion |
| Poppy | Exit138,29s,1164MiB | Native fault persists; schema3 prefix saturated |

`ae157337-game-replays/manifest.json` hashes the original run provenance,
logs, screenshots, outcomes and immutable Poppy binary/decoded trace.

Compute-readiness source `b71c9129` integrates as `4d3dabfd`: nil command buffer
or encoder rejects before dispatch/content acknowledgement; temporary texture
retains receive one cleanup attempt per owned slot, preserving the first failure.
Root Release build125 existing warnings/zero errors and65/65 fake-handle controls
per CLR pass. No native failure injection, successful binding, pooled-failure
ownership or whole-game fix is established by those controls.

The previous singleton array fixture qualified WORD5 zero and identity selectors;
it did not qualify these actual game descriptors. A successor must replay each
exact descriptor and use asymmetric physical/store/sampled-channel oracles.

DS passes the former snapshot-placement gate but stops at
`ValidateSelectedStorageMipOwner(requireObserved:false)` through queue admission.
The aggregate diagnostic does not identify whether session, global mapping epoch
or tracker identity changed. No gate has been relaxed. Host redraws (~120) are
not guest frames. TERM removes the process without KILL, but the stop log contains
an abort trap, so this is not clean process/guest shutdown.

Retained runs: `artifacts/runs/20261008-045425-sh-singleton-array-ae157337`,
`20261008-045634-octo-singleton-array-ae157337`,
`20261008-045759-oregon-singleton-array-ae157337`,
`20261008-045952-ds-selected-mip-ae157337`, and
`20261008-050440-poppy-native-range-decisions-ae157337`.

## Isolated runtime patch controls

An official checksum-verified SDK10.0.112/runtime10.0.12 is installed only in
`artifacts/toolchains/`; global SDK/config and production bundles are unchanged.
The same archived x64 diagnostic DLL passes13/13 on runtime10.0.3 and10.0.12,
with host traces proving the parent and testhost CoreCLR selections. These are
metadata controls, not actual signal/protection or game qualification.

Four same-DLL warmed selector processes in baseline/candidate/candidate/baseline
order report zero allocations, identical native selector identities and actual
X64/SSE2=true/AVX2=false. Runtime3 cached medians9.767/9.907ns and runtime12
9.564/10.293ns overlap; no stable patch performance improvement is established.
The selector cache benefit reproduces within both runtimes. These measurements
are not game/loading/FPS evidence, and do not justify replacing runtime3 yet.
Exact payload/runtime hashes, host traces, commands and results:
`artifacts/validation/integrated-20261007/dotnet10-patch-controls-20261008/`.

## Latest signed DF replay batch

`artifacts/macos/compat-df173172/SharpEmu.app` has clean exact source
`df173172e8aeda6d7a31720bad794274f6864628`, SDK10.0.103/runtime10.0.3,
native-only closure/architecture checks and valid ad-hoc signing. Source and
bundle stayed frozen through the six serialized runs. Foreign REmu was observed;
these establish blockers, not measured FPS/load improvements. Agent compilers
were paused during the game batch.

Before this bundle, combined root `e4117548` passed81/81 Libs controls and38/38
non-native Metal controls per CLR, plus12/12 required-device numerical cases per
CLR (selected mip/publication, BGRA singleton array and sRGB singleton volume).
The frozen75-file archive is `compat-e4117548-frozen/manifest.json`. The later
DF diagnostic-only additions pass43/43 non-native Metal controls per CLR and
15/15 native-range metadata controls per CLR; offline decoder12/12 passes. The
first decoder invocation used a nonexistent filename and exited2 before testing;
its log is preserved, and the corrected command passes12/12.

| Title | Actual DF result | Next blocker |
| --- | --- | --- |
| Silent Hill | Exit134,26s,1921MiB | RGBA descriptor gate advances; R32Uint mode27 singleton array now rejected |
| Octopath II | Exit134,32s,3190MiB | Same next R32Uint class |
| Oregon Trail | Exit134,30s,2045MiB | Same next R32Uint class |
| Superliminal | Alive90s,3665MiB, TERM0s without KILL | Singleton sRGB volume advances; sampled R8Unorm4x4x16 tiled volume rejected, loading screen persists |
| Demon’s Souls | Alive90s,4628MiB, TERM0s without KILL | Admission veto identifies global mapping epoch324087→324134, before any guest frame |
| Poppy | Exit138,22s,1631MiB | Fatal native write fault persists; refined schema3 capture retains85 records/zero drops |

The three integer descriptors have raw words
`<address> 01400000 00000000 D1B00204 00000000 00700000 00000000 00000000`,
with addresses8010700000/8030B20000/8011530000. They require R32Uint and Uint
shader typing, not RGBA/Float reinterpretation. The exact qualifier is now
integrated through `4f9e94ec`. Root Libs controls pass93/93 per CLR. The isolated
frozen numerical proof passes4/4 required-device cases per CLR: native R32Uint,
explicit singleton array bounds, asymmetric seed/store and logical X001 reads.
See [R32Uint qualification](r32uint-singleton-array-20261008.md). These are device
fixtures; the signed DF game binary predates this fix, so title progression is
still pending a new signed bundle.

Superliminal's next exact raw descriptor is
`701529A0 C0100000 0000C000 A0500800 0000000F 00700000 00000000 00000000`:
R8Unorm, mode5,4x4x16, pitch4, one mip, sampled Float3D, selectors000X.
An independent CPU-only AMD AddrLib oracle agrees with Kairo equation118 for
all256 voxel coordinates across six modeled configurations. Physical footprint
4096B, logical256B, maximum addressed byte1215; stacked XY decoding differs at
248/256 coordinates. This is layout evidence; no production R8 volume or native
filter/game qualification exists yet.

DS's scalar veto does not establish a mutation of its resource at7092C80000.
Its memory-domain reference and tracker token are not a per-range mapping lease.
Tracker arming calls direct Mprotect, without publishing the HostMemory epoch;
the new values therefore do not establish self-invalidating tracker arming.
The global gate remains intact pending an ownership-safe solution. Final window
is black/zero guest frames.

Poppy's filtered trace records four actual target-RIP invocations. The first
returns Armed from CAS, restores the full2211840-byte owner RW (op13 result0),
then disarms and reports recovered. Three following entries return Disarmed from
CAS and are declined, with no own permission syscall. They arrive approximately
90/190/476 microseconds after the restore-return observation. Rearm to epoch2
occurs about14.55ms later, before their delayed final declines. This confirms the
historical Disarmed branch with exact CAS evidence, not a busy-Invalidating race.
Kernel trap time, all permission owners and backing lifetime remain unproved;
no unconditional retry or instruction workaround is justified by these records.

## References and next gates

The read-only [REmu/Kairo audit](metal-reference-audit-20261008.md) records exact
reference SHAs and the tile-mode-24 topology disagreement. Do not blindly copy
GPU equations or zero-copy paths without this emulator's current backing authority.

The next bundle must have its own frozen provenance and output directory. Repeat
SH/Octopath/Oregon after typed R32Uint qualification, and Superliminal after
full XYZ R8 qualification. DS now requires a solution to overly broad mapping
vetoes that preserves actual per-range backing authority.
Owned-frame idle experimenta31c112c (candidate2576429d) has6/6 policy controls
per CLR,19 retained source/DLL/result hashes and independent source review.
Persistent restore/minimize/hidden invalidation, externally enabled HUD veto and
nonzero native creation guards prevent false committed-frame skips. A1ms idle
runs after borrow/pool/gate release; default stays off. Actual surface restore,
HUD and same-build on/off gameplay pacing are still required before promotion. Runtime patch upgrades,
ReadyToRun, display-link integration, pipeline archives and deep SIMD/GC changes
remain measured experiments rather than claimed gains.

## Allocation instrumentation and Game Mode metadata

Opt-in `SHARPEMU_PROFILE_ALLOCATIONS=1` is integrated in `f48e7b4e`; it measures
managed allocation on the actual presenter drain thread and correlates with the
existing wall trace without changing its schema. Fresh integrated Release
processes execute54/54 controls per CLR with zero skips (enabled8, disabled1,
legacy43, mixed2); decoder9/9 passes. The first root command selected an older
Debug DLL and executed zero tests despite exit0. Its logs are retained separately
and excluded; the corrected commands validate actual TRX execution counts.
No game allocation result or allocation rewrite is established yet.

The Mac bundle now declares `public.app-category.games`, `LSSupportsGameMode=true`
and the older `GCSupportsGameMode=true` compatibility spelling. Apple documents
[Game Mode eligibility](https://developer.apple.com/documentation/bundleresources/information-property-list/lssupportsgamemode)
and [native full-screen activation](https://support.apple.com/en-euro/105118).
The template passes `plutil -lint`. This metadata enables OS eligibility; it
does not force Game Mode, override a user's setting, or demonstrate an FPS gain.
Actual activation and same-scene comparison remain unqualified.

## Signed876 compatibility and Sarah ABBA follow-up

The new `artifacts/macos/compat-87616e29/SharpEmu.app` includes exact R32Uint
and R8 volume fixes plus optional drain allocation capture and Game Mode
eligibility metadata. It remains runtime10.0.3, clean source87616e29, native-only
closure/architecture/signature verified. Root integrated required-device tests
pass20/20 per CLR; targeted genuine mip/R8/sRGB managed controls52/52 per CLR.
The broader84 device-disabled runner includes early-return source paths and is
excluded from genuine managed/native execution counts. Archive96 copied
source/DLL/result files: `r8-integrated-87616e29-frozen/manifest.json`.

Fresh SH24s/1944MiB, Octopath33s/3171MiB and Oregon31s/2388MiB now pass the old
Uint gate and reject RGBA16Float mode27 singleton arrays. Superliminal41s/3231MiB
passes R8 preparation and shows its loading hallway, then exits134 with MSL
name collisions and a storage-view mismatch. All four exit134; no gameplay/FPS
acceptance. Exact logs/screenshots/build/provenance are under
`compat-87616e29-game-replays/{REPORT.md,manifest.json}`.

Sarah off/on/on/off uses this same signed binary and identical timed inputs in
four serialized150-second runs, compilers/test/device workers paused. Effective
retained guest-commit rates: off63.337/65.857Hz, on73.345/69.406Hz. Each ring
retains4096 commits, not the whole run. Median drawable wait is10.36/10.56ms
off versus0.032/0.033ms on. p99 gaps off36.155/33.811ms versus on35.003/34.828ms
do not consistently improve. All rows have zero discarded scopes/failed drains;
mean Drain allocation is41.8–48.2KB per retained commit. Each run reaches the
coherent outdoor world and stops on TERM without KILL; guest pthread_join
error persists. Final camera/sprite positions differ despite timed-input equality.
This is commit cadence, not GPU/scanout or matched fixed-scene/guest-clock
qualification. Default idle stays off pending surface lifecycle and timing checks.
Detailed metrics/sampled host observations/112 hashed evidence files:
`sarah-abba-87616e29/{REPORT.md,comparison.json,manifest.json}`.

Proactive R32Float exact arrays integrate through438cfd28 with111/111 managed
and12/12 source/codegen controls per CLR plus4/4 actual native Float cases each.
No actual game R32Float descriptor was captured here. R32Sint remains blocked by
shared legacy numeric normalization; no global policy or fallback was introduced.


## Signed d756 typed formats, sampler ownership and shader names

The next clean signed bundle includes exact RGBA16Float full8-byte Half array seeds,
R32Float, sampler descriptor/duplicate-state ownership, Metal-qualified sample
modifiers and distinct sampled_compare argument fields. Compare binding IDs,
offsets, native types and numeric behavior are unchanged. Strict storage view
rejection now records both metadata tuples; it does not allow reinterpretation.
Combined root Release qualification:116 Libs,71 genuine presenter and119 shader
managed controls each CLR;28 actual native numerical cases and5 actual native
fragment-parser cases each, zero skips. Frozen93-file source/DLL/TRX archive:
`typed-shader-integrated-a790ff0d/manifest.json`. Build profiles keep test outputs
separate from app publication. Prior first manifest script syntax typo is retained
and excluded; corrected exact TRX counts passed.

Signed d756a2b3 reruns advance all previous targeted blockers. SH25s/2004MiB
and Octopath34s/3247MiB now reject R8Uint1x1 mode24 array descriptors, while
Oregon29s/2013MiB rejects RGBA8Uint1x1 mode27. Superliminal42s/3440MiB no
longer logs its MSL collision errors but stops at address706B7D0000: existing
512x512 versus requested128x64, both native RGBA16Float115/type2/depth1/layer1.
The title/loading hallway renders; no menu/gameplay/FPS acceptance. Every run
exits134 before its150-second cap. Evidence, actual raw records, screenshots,
inputs and payload provenance: `compat-d756a2b3-game-replays/{REPORT.md,manifest.json}`.
Next work must qualify the two unsigned format families and diagnose alias/view
lifetime without discarding live GPU-written contents. Ordinary backing change
remains outside this bundle pending corrected race and native provider proof.

## SmallUint and image provenance integration

Clean a8c65ae1 signed app passed123 texture/readiness and74 genuine presenter controls plus36 actual numerical Metal cases perCLR. [Actual four-title replay](smalluint-provenance-game-replay-20261008.md) advances the three smallUint failures to shared1024x1024 BGRA arrays; allthree report one early guestframe, then zero. Superliminal identifies an earlier GPU-written512x512 color target at the incoming128x64 storage address. No gameplay, loading or sustainedFPS gain follows from this evidence. CPU backing/performance candidates are excluded.

A separate signed d756 Sarah EventPipe capture attributes allocation ticks to vertex copies and duplicate texture arrays; exact trace/reader/failure records are retained under artifacts/validation/all-games-20261008/sarah-eventpipe-d756a2b3.101475 ticks belong to the owned app, EventsLost0.92270 trigger Byte[] allocations;50013 stacks include CopyVertexBuffers,20948 the initial texture copy and20945 Span.ToArray. These are samples, not exact typed bytes or CPU self-time. An isolated linear ownership candidate reduces the actual warmed256KiB snapshot allocation528376→266208 bytes onbothCLR architectures (3genuine managed cases each); actual native upload and game performance qualification remain pending.
