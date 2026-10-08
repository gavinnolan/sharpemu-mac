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

## References and next gates

The read-only [REmu/Kairo audit](metal-reference-audit-20261008.md) records exact
reference SHAs and the tile-mode-24 topology disagreement. Do not blindly copy
GPU equations or zero-copy paths without this emulator's current backing authority.

The next bundle must have its own frozen provenance and output directory. Repeat
SH/Octopath/Oregon after exact-descriptor qualification; add scalar DS admission
reasons before considering any ownership change.
Owned-frame idle experimenta31c112c (candidate2576429d) has6/6 policy controls
per CLR,19 retained source/DLL/result hashes and independent source review.
Persistent restore/minimize/hidden invalidation, externally enabled HUD veto and
nonzero native creation guards prevent false committed-frame skips. A1ms idle
runs after borrow/pool/gate release; default stays off. Actual surface restore,
HUD and same-build on/off gameplay pacing are still required before promotion. Runtime patch upgrades,
ReadyToRun, display-link integration, pipeline archives and deep SIMD/GC changes
remain measured experiments rather than claimed gains.
