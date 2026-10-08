# Integrated prefix completion and measured submission waits — 2026-10-08

Local main now contains the qualified startup ordering at `5b54e07ffa02752b67bb07c0a50e0cf28f11259a` and the submission profiler correction at `7bc134b16f322381304485a1cce99792beaca8fa`. The earlier isolated 71b replay is retained. Integration preserves main's original worker exception stack and attempt-all cancellation cleanup; it is not a wholesale replacement with the older candidate.

## Current main qualification

At 5b, 186 focused library checks, 128 managed Metal checks, eight managed shader checks, 68 actual native Metal cases and six actual native compiler cases pass on each ARM64 and Rosetta/x64 host runtime, with zero failures or skips. Current DLL closures and exact case identities are recorded. ARM host testing does not enable ARM guest execution.

The 7bc change touches only the AGC submission profiler and its successful-call timestamps. Its 186 focused library cases pass again on each host runtime, zero failures or skips. Native production source is unchanged from 5b; the 74 native cases per runtime were not rerun at 7bc. Both sources build and publish signed native-only Mac applications with 239 files. Signed bundle hashes are unchanged after the actual games.

Quake on signed 5b passes the former frame-start context-label timeout and logs engine initialization, Starting Game and demo1/the Necropolis loading. It exits 134 after 64 seconds, peak 1709 MiB, at the same 8192-square R32Float tile-24 depth source: 256 MiB required against the 128 MiB snapshot bound. The final warning screenshot does not establish a rendered demo or controllable gameplay. No depth budget increase or invented texture contents were used.

Sarah on signed 5b stays alive for 150 seconds, peak 754 MiB. Reviewed screenshots show rightward movement and left-facing return. Late guest cadence windows are 41.83–50.29 Hz. Signed 7bc with the same input plan also stays alive for 150 seconds, peak 900 MiB, with reviewed movement in both directions and late windows 53.78–61.97 Hz. These runs are not a matched quiet-host comparison; their difference does not establish an optimization or regression. Neither establishes sustained 60+, scanout cadence, whole-game fidelity or loading duration.

## Profiler correction and next performance target

Before 7bc, successful DCB submission total time ended at enqueue, omitting the subsequent preparation wait. The corrected total ends after that wait and reports `preparation_wait_ms` separately, subtracting it once from other time. Failure/cancellation still does not create a successful submission row. No scheduling or completion policy changes with this correction.

In Sarah's final four five-second reporting windows, average call latency is 15.927, 14.952, 14.788 and 14.729 ms. Preparation waits account for over 99% of each window's measured caller submission time. This is time waiting for the existing parsing/render/native-completion path, not measured GPU execution. It overlaps other threads' presenter phases and must not be added to those totals. Native command-buffer GPU timestamps remain unmeasured.

The measured wait makes completion scheduling a concrete performance target. The persistent Metal shader-library cache candidate addresses a separate measured cold-library compilation cost; it is still default-off and unqualified for games. Source compilation, pipeline compilation and GPU execution are separate costs.

## Isolated game-blocker evidence

The signed EFE original native-owner candidate still exits Octopath after 33 seconds at GPU-produced/pending-write ownership, peak 3216 MiB, with only one early guest frame and no gameplay. The later 069 dirty diagnostic identifies a changed protected-owner token (generation 0 to 1) at the four-MiB RGBA resource; it cannot authorize acknowledgement of the original token. Canonical publication routing must preserve the original observation through actual successful GPU completion and final mapping/dirty checks.

Quake's isolated fd59 diagnostic explicitly retains the exact taken work. Its signed replay exits after 62 seconds at the same depth bound, peak 2027 MiB. Pending, live and taken normalized depth-overlap checks report no current matching writer; taken sequence is zero and its inspection is complete. The bounded historical writer trace has evicted entries, so CPU authority remains false. Absence of a current depth target does not prove absence of retired, aliased, storage or buffer GPU writes. Persistent original physical writer attribution and an actual all-alias content read window remain required before selecting a finite CPU depth-copy path.

No additional title meets all acceptance gates. All-nine current-main replay, correct rendering, sustained gameplay 60+ and fast-loading verification remain outstanding. No remote push or upstream merge occurred.

## Retained evidence

- `artifacts/validation/main-completed-prefix-prepared`: exact 5b source, build/test/native receipts, case identities, source and binary hashes, publish context and game commands.
- `artifacts/validation/main-preparation-wait-profile`: exact 7bc source, build and managed receipts, publish context and signed bundle manifest.
- `artifacts/runs/20261008-182229-quake-main-prefix-5b54e07f`: actual current-main Quake failure and screenshots.
- `artifacts/runs/20261008-182406-sarah-main-prefix-5b54e07f`: first integrated Sarah input replay and runtime summary.
- `artifacts/runs/20261008-185208-sarah-main-wait-profile-7bc134b1`: corrected wait logs, reviewed screenshots and hashed runtime summary.
- `artifacts/validation/all-games-20261008/selected-native-efe7235c-game`, `selected-native-dirty-06965a5c`, `depth-source-0aa8a1bd` and `depth-owner-fd59c7df`: isolated candidate evidence, preserved source and original manifests.
