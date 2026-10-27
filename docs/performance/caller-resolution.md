<!-- Copyright (C) 2026 SharpEmu Emulator Project
SPDX-License-Identifier: GPL-2.0-or-later -->

# Caller resolution for short draw queues

Frequent query and barrier drains can leave the render thread waiting for a
resolver thread to wake for only one or a few draws. The drawing thread can now
claim the untouched FIFO head when at most eight jobs remain. It refuses a
worker-owned job, an active resolution, a paused queue, an unmet dependency gate,
a stopped queue, or any other head. Longer queues retain background overlap.
Only one resolution runs at a time. The speculative read context, input checks,
ownership checks, exception policy and query ordering are retained.

Metal enables this policy by default; set
`SHARPEMU_METAL_V2_CALLER_RESOLVE=0` to retain worker-only resolution. Other backends opt
in through `IConcurrentResolveHost.AllowsCallerResolution`; the interface default
is false. The `caller_jobs` and `caller_ms` counters account for work moved onto
the drawing thread. `resolve_ms` still measures background resolution, and
`wait_ms` excludes successful caller resolution. A shorter wait alone is not a
performance result.

## Component measurements

Apple M4, .NET 10.0.3, native ARM64 and Rosetta x64, Release, normal tiering.
The real prefetch/executor queue uses synthetic unavailable programs, without a
GPU. The caller and worker both use the native UserInteractive QoS class.
Seven alternating paired rounds follow a five-second initial warmup and
two-second warmups for later cases, with approximately 8,192 jobs per round.
The table reports paired-run medians, not differences between historical runs.
Whole-process CPU includes runtime activity; caller CPU alone would hide work
moved from the resolver. Allocation stays about 272 B/job in both modes.

| Architecture | Added spins | Burst | Wall off ns/job | Wall on ns/job | Process CPU off ns/job | Process CPU on ns/job | Caller claims |
|---|---:|---:|---:|---:|---:|---:|---:|
| arm64 | 0 | 1 | 2874.1 | 542.1 | 4786.9 | 961.5 | 91.4% |
| arm64 | 0 | 5 | 732.1 | 508.7 | 1327.7 | 881.0 | 85.5% |
| arm64 | 0 | 32 | 329.3 | 356.2 | 654.5 | 691.2 | 0.0% |
| arm64 | 0 | 256 | 333.5 | 331.4 | 682.6 | 679.2 | 0.0% |
| arm64 | 512 | 1 | 26908.0 | 23027.0 | 41943.8 | 25838.3 | 100.0% |
| arm64 | 512 | 5 | 23084.3 | 23080.8 | 39583.4 | 25860.9 | 100.0% |
| arm64 | 512 | 32 | 21741.7 | 21812.6 | 38534.7 | 38630.0 | 0.0% |
| arm64 | 512 | 256 | 21546.5 | 21407.6 | 38444.2 | 38110.8 | 0.0% |
| x64 | 0 | 1 | 3160.1 | 972.2 | 5509.8 | 1710.6 | 88.4% |
| x64 | 0 | 5 | 917.2 | 889.0 | 1677.5 | 1605.3 | 65.8% |
| x64 | 0 | 32 | 541.4 | 526.3 | 1070.1 | 1039.4 | 0.0% |
| x64 | 0 | 256 | 427.8 | 403.3 | 874.6 | 824.1 | 0.0% |
| x64 | 512 | 1 | 45216.6 | 41632.8 | 71946.9 | 45248.0 | 100.0% |
| x64 | 512 | 5 | 41720.9 | 41654.9 | 71201.7 | 45246.4 | 99.9% |
| x64 | 512 | 32 | 40928.5 | 41014.9 | 71259.0 | 71510.5 | 0.0% |
| x64 | 512 | 256 | 40590.0 | 40811.4 | 71200.9 | 71170.3 | 0.0% |

The single-job cases improve clearly. The empty-program ARM64 burst of 32 has
an 8.2% wall-time and 5.6% process-CPU cost (about 27 ns and 37 ns/job); this
cost is retained in the acceptance record. Larger/spinning batches are mostly
flat. An earlier uncapped implementation had mixed large-batch behavior and
was restricted to eight outstanding jobs. Host load changed substantially
between development versions, so their absolute timings are not code comparisons.

To reproduce the component benchmark, set
`SHARPEMU_CALLER_RESOLVE_BENCH_OUTPUT` to an absolute JSON output path and run
`ParallelProgramPrefetchCallerTests` in `SharpEmu.Libs.Tests` with the serial
runsettings. The benchmark is dormant without that environment variable.

## Correctness coverage

Tests cover the FIFO completion prefix, the worker claim while a gate opens,
pause and stop refusal, the eight/nine-job boundary, indexed inputs and ownership
changes, exception policy restoration, 2,048-job competing claims, and a pause
while the caller resolves. Device tests check exact pixels and an earlier query
snapshot after later draws, plus speculative refusal of GPU-owned query bytes
without waiting on the GPU. Enabled, disabled and command-thread modes are
checked separately.

The capped implementation passed the full x64 Libs suite (9,100 passed,
0 failed, five existing skips) and required-device ARM64 Metal suite (1,639
passed, 0 failed, seven existing skips). The final early eligibility guard then
passed 31 focused tests on each architecture, plus 21 enabled, 21 disabled and
58 command-thread Metal checks. The final default-policy validation is recorded below.

## Runtime comparison

The v24 immutable payload was built from `500a1c1a09eccef58470f63ecf48aa7ed877b8ae`
plus tracked patch SHA-256
`fa4bd3a6df60af685643ba4fe4de7a42ad3bae9f32bf3fb6f30ba1524be50e4d`.
Its 471 payload hashes are checked before each launch. Both arms use a fresh
clone of the same seed, 1920x1080, scale 1, VSync, unchanged quality, the ten
previously promoted defaults, parallel resolution, and a serial command stream.
Detailed render/pass/memory profiling and prewarming are disabled. Host load
and paging still limit causal attribution. GeForce NOW settings are unchanged.

Enabled arm: `20261027-234802-silent hill short message.zar-room-caller-resolve-v24-on-1`.
The initially selected 250–285-second window still covered the intro and is not
room evidence. At elapsed 258 seconds, before gameplay, the window was moved to
375–410 seconds. The shelf/lamp room was visually verified before and during
that window. Its 18 presentation reports measured **16.49 FPS median,
11.98–18.98 range**. This remains below 30 FPS.

Seventeen PERF reports ending in that window cover 34.35 seconds: 1,810,848
resolutions, 341,756 caller jobs (18.9%), 984.7 ms caller resolution,
8,752.1 ms background resolution, 1,215.4 ms wait, 139 speculative abandons,
zero ownership/input refusals, 220.5 ms synchronous fallback and zero host-GPU
wait. These wall counters can overlap and must not be added as exclusive CPU
costs. GPU busy time was 21,100.8 ms. The later 417–427-second thread sample
recorded 8.051 CPU-seconds on the GPU worker, 3.053 on the resolver, 6.949 on
RHI and 9.823 on the guest audio event thread.

Seven system-memory samples in the FPS window recorded 5,961 swap-in and
9,900 swap-out pages, 836,450 compressions and 778,447 decompressions; pressure
was 2, free memory 77–120 MiB, swap use 4,893–5,024 MiB. Launch load was 3.64,
far lower than v23's 36.09, so the v23-to-v24 FPS change is not an isolated code
win. Main-process peak footprint/RSS was 9,747/4,537 MiB, not combined process
memory. The stop marker was written at elapsed 427.160 seconds. CPU-trap status
appeared at 429.449 seconds during requested shutdown; launcher exit was zero.

Disabled arm: `20261027-235523-silent hill short message.zar-room-caller-resolve-v24-off-2`.
The 375–410-second window was selected at elapsed 30 seconds. It replayed the
same intro-skip and phone-close timing, and the stationary room was verified
before and during measurement. Its 17 reports measured **16.48 FPS median,
14.00–17.93 range**. The enabled/disabled medians are effectively identical.

Seventeen PERF reports cover 34.54 seconds: 1,835,475 resolutions, no caller
jobs, 9,954.4 ms background resolution, 4,233.4 ms wait, 299 speculative abandons,
zero ownership/input refusals, 5.2 ms synchronous fallback and zero host-GPU
wait. GPU busy time was 21,786.2 ms. Idle time rose from 1,934 ms disabled to
5,754 ms enabled while resolver wait fell; **this is not a game speedup**.
The later 417–427-second thread sample recorded 8.913 CPU-seconds on the GPU
worker, 3.270 on the resolver, 6.810 on RHI and 9.857 on the audio event thread.
A separate three-second native stack capture at 411 seconds was outside the
FPS window; managed JIT frames were not symbolized sufficiently to rank source
methods, so no source-level hotspot claim is made from it.

Seven disabled-window memory samples recorded 9,665 swap-in and 6,924 swap-out
pages, 305,595 compressions and 382,481 decompressions; pressure was 1, free
memory 82–117 MiB, swap use 6,078–6,190 MiB. Launch load was 8.76. Main-process
peak footprint/RSS was 9,956/4,127 MiB. Its stop marker was elapsed 427.157 seconds;
CPU-trap status appeared at 428.632 seconds during requested shutdown, with
launcher exit zero. Neither run logged that trap before its stop marker.

## Default decision

Enable the bounded path based on its short-batch wall/process-CPU component
wins and ordering/device coverage, consistent with the maintainer's acceptance
of component improvements without an isolated Silent Hill gain. The ARM64
burst-of-32 cost remains a limitation; this is not a universal speedup claim.
Both runtime arms rendered the intended room and had effectively identical
median FPS under different host paging conditions. All ten earlier accepted
switches remain enabled, bringing the documented promoted defaults to eleven.
No quality reduction or unrelated application setting is part of this change.
The 30-FPS goal is still unmet.


## Reproduction and evidence locations

Run validations sequentially under the shared machine-wide game/test lock.
The final suite commands use Release, `--no-restore`,
`--settings docs/performance/shader-serial.runsettings`,
`--blame-hang-timeout 120s`, `-p:UseSharedCompilation=false -m:2 -nr:false`,
and TRX output. Run `tests/SharpEmu.Libs.Tests/SharpEmu.Libs.Tests.csproj` with
the x64 SDK and no `SHARPEMU_*` overrides. Then run
`tests/SharpEmu.Gpu.Metal.Tests/SharpEmu.Gpu.Metal.Tests.csproj` with the ARM64
SDK and only `SHARPEMU_TEST_REQUIRE_DEVICE=1`. The explicit fallback control
adds `SHARPEMU_METAL_V2_CALLER_RESOLVE=0` and filters
`CallerResolutionSceneTests`, `NativeOcclusionSceneTests` and
`GpuWordNoWaitSceneTests` (OR each with `FullyQualifiedName~`).

For the optional component benchmark, use each architecture's SDK in turn:

```sh
SHARPEMU_CALLER_RESOLVE_BENCH_OUTPUT="$PWD/artifacts/caller-resolve-bench.json" \
  dotnet test tests/SharpEmu.Libs.Tests/SharpEmu.Libs.Tests.csproj \
  -c Release --settings docs/performance/shader-serial.runsettings \
  --filter 'FullyQualifiedName~ParallelProgramPrefetchCallerTests' \
  --blame-hang-timeout 120s
```

Private evidence is retained under `artifacts/runtime-diagnosis-20261026/`:
`caller-resolve-v3-source.json`, both `caller-resolve-v3-bench-*.json` files,
`caller-resolve-v24-build-source.json`, the working patch and untracked-test
archive, payload hashes, `caller-resolve-default-validation.json`, `caller-resolve-final-source.json`,
`caller-resolve-final-validation.json`, and
`caller-resolve-results/*.trx`. Each runtime directory contains its selected
window, screenshots, complete timestamped log, input/stop markers, memory
samples, thread CPU snapshots, `stationary-fps.json` and `caller-v24-summary.json`.
Commercial game data and raw game logs are not committed.


## Final suite triage

The first final-policy Metal run reported **1,638 passed, one failed and seven
existing skips**. `ACachedImageKeepsOneTilerViewUntilItsTextureIsReleased`
observed a nonzero retained view where it expected none. Inspection found that
the volume and sRGB-view test fixtures constructed owning `MetalCachedImage`
objects without disposing them, while their harness released the texture.
That left stale registrations keyed by a reusable native handle.

A temporary isolated registration-balance diagnostic called the volume fixture
with caller resolution explicitly **disabled**. It failed deterministically:
zero registrations before, one afterward. With the fixture ownership fixed it
passed (one test, zero failures). The temporary diagnostic source and both TRXs
are archived privately and removed from the shipped test assembly. The volume
and sRGB fixtures now retain a reference for the image and dispose it before
the harness; the existing tiler lifetime test also uses scoped disposal so an
assertion failure cannot leak its image registrations. Existing pixel/view and
lifetime assertions are retained. This is a test-fixture cleanup correction;
it does not change production texture ownership or claim a runtime memory win.


## Final validation result

| Final configuration | Passed | Failed | Existing skips |
|---|---:|---:|---:|
| Libs x64, all feature overrides unset | 9,100 | 0 | 5 |
| Metal ARM64, required device, feature overrides unset | 1,639 | 0 | 7 |
| Metal ARM64, explicit caller-resolution fallback | 21 | 0 | 0 |

These are test executions per configuration, not a count of unique tests. The
Libs run preceded the Metal-only fixture cleanup; production sources and Libs
tests did not change afterward. The final Metal assembly excludes the temporary
diagnostic. Both runtime publishes, REUSE and whitespace validation passed.
Earlier failure and reproduction results are retained above, not overwritten.
