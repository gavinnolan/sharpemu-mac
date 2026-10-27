<!-- Copyright (C) 2026 SharpEmu Emulator Project
SPDX-License-Identifier: GPL-2.0-or-later -->

# Local deferred draw allocation

The interpreter's local deferred queue now retains at most 256 empty draw
wrappers. Completed wrappers are returned only after restoring the current
submission and resolver state, including when rendering throws. Every payload
reference is cleared before retention. Draw arguments and snapshots are assigned
again on acquisition. Producer/sink-owned work never borrows or returns these
wrappers; register snapshots, geometry snapshots and public resolver jobs retain
their original ownership and allocation behavior.

`SHARPEMU_LOCAL_DRAW_REUSE=1` enables reuse; unset or `0` selects the allocating
reference path. The implementation reduces allocation pressure, but component
timings are mixed and the runtime comparison remains unresolved. It therefore
stays opt-in. The nine previously promoted performance defaults remain enabled.

## Component measurements

`LocalDeferredDrawReuseTests.MeasureLocalDrawReuseWhenRequested` exercises the
real translation queue and background resolver with unavailable fake graphics
programs, excluding native compilation and rendering. Every submitted job must
resolve and be consumed, with zero refused or abandoned jobs. Each arm gets 32
warm-up batches, followed by nine alternating measurement rounds. Each round has
`max(16, 2048 / burst)` batches. Allocation is measured on the calling render
thread only; worker-thread allocations are excluded. Timing includes resolver
coordination and is not exclusive CPU time.

The implementation was tested on .NET 10.0.3, native ARM64 and Rosetta x64. Median
nanoseconds per draw, allocating reference → reuse:

| Burst | ARM64, normal tiering | x64, normal tiering | ARM64, tiering disabled | x64, tiering disabled |
| ---: | ---: | ---: | ---: | ---: |
| 1 | 9,816.711 → 9,437.541 | 12,942.932 → 13,854.004 | 7,042.114 → 7,082.479 | 9,430.339 → 9,611.166 |
| 5 | 4,337.143 → 4,351.426 | 5,711.899 → 5,644.947 | 2,046.414 → 2,052.404 | 2,782.885 → 2,801.650 |
| 32 | 1,737.956 → 1,995.056 | 4,103.455 → 3,788.086 | 1,129.903 → 1,134.196 | 2,005.229 → 2,045.919 |
| 512 | 663.589 → 714.071 | 2,486.888 → 1,894.023 | 995.204 → 985.464 | 1,650.589 → 1,661.560 |

Normal tiering results are mixed, including regressions of approximately 7% for
x64 burst 1 and 15% for ARM64 burst 32. Disabling tiering with
`DOTNET_TieredCompilation=0` keeps median timing differences within approximately
2.1% in either direction. Neither set establishes a consistent timing win.

With tiering disabled, both architectures measured **536 → 352 B/draw** for
bursts 1, 5 and 32: **184 B/draw removed**. Burst 512 measured
**536.169 → 444.169 B/draw**, removing 92 B/draw because only half the wrappers
fit in the 256-entry bound. Normal-tiering runs also removed approximately
184 B/draw within the bound and 92 B/draw above it, with additional JIT/measurement
variation. These are component allocations, not total emulator memory savings.

To reproduce, run the Libs fixture serially with an absolute output path:

```sh
SHARPEMU_LOCAL_DRAW_REUSE_BENCH_OUTPUT=/absolute/path/result.json \
  dotnet test tests/SharpEmu.Libs.Tests/SharpEmu.Libs.Tests.csproj -c Release \
  --filter FullyQualifiedName~LocalDeferredDrawReuseTests \
  --settings docs/performance/shader-serial.runsettings \
  --blame-hang-timeout 120s
```

Select the native or x64 `dotnet` installation explicitly and optionally set
`DOTNET_TieredCompilation=0`. Use the shared validation/game lock on this host.
The fixture supplies both reuse modes itself, independent of the process default.

## Correctness and source evidence

Serial Release validation of the initial default-enabled candidate passed:

- Libs x64: **9,068 passed, 0 failed, 5 existing skips**.
- Required-device Metal x64: **1,637 passed, 0 failed, 7 existing skips**.
- Focused fixture: **8/0/0 each** on ARM64 with normal tiering, ARM64 with tiering
  disabled, and x64 with tiering disabled. The normal-tiering x64 fixture is
  included in the full Libs run; these are configuration executions, not distinct
  added tests.

Coverage includes indexed/automatic argument order, distinct register snapshots,
nested drain refusal, failure recovery with later queued work intact, actual
non-null geometry snapshot clearing, producer/sink ownership exclusion, bounded
retention, and render-thread allocation reduction. The optional benchmark was
explicitly enabled for the reported fixture executions.

The benchmarked and runtime-tested candidate production file SHA-256 is
`9bb4e03a42320d21896200e1bfd8075a0c70a5a2bceba472c8cd96982c12bdef`;
the correctness fixture is
`c3fc0836ca1bb3899ab1c3c1371667d2ef751271355eb73e6ce6d8012d54171f`;
the benchmark fixture is
`7fe6f7bdaa650f78184005fe381086e0d895aa34df804265bcb21fadc6c49309`.
These are changes on parent `37358ee0b8aee94537a3c5c8df92f052ea31d460`.
After the runtime controls below, the only production difference from that
candidate is requiring `=1` instead of enabling reuse when unset. The benchmark
constructs both modes explicitly, so its measured implementation is unchanged.
The final opt-in production file SHA-256 is
`709d0165a6461a27910b5d25ed74aa6ad3ac2bafdf96e69229e834676167e345`.
Fresh serial Release Libs validation with all `SHARPEMU_*` overrides removed
again passed **9,068/0/5**, with a 232.769-second launcher duration including
build/test startup. The optional benchmark was not requested in that final run;
the unchanged explicit-mode benchmark measurements above remain separate.
Private commands, TRX files, source hashes and raw rounds are retained under
`artifacts/runtime-diagnosis-20261026/local-draw-reuse-*`. Raw filenames containing
`tier0` mean **tiering disabled**, not execution using the Tier 0 compiler.

## Silent Hill enabled / disabled / enabled control

One immutable v22 payload was published as an x64 CLI and native ARM64 GPU host;
both publishes passed and all 471 payload hashes were verified before each run.
Its source is the candidate hash above, with reuse enabled when unset. All three
runs cloned the same warmed seed, used 1920×1080 / scale 1 / VSync on, retained
the nine promoted defaults and background program resolution, and kept command
threading off. Both processes explicitly inherited the application's existing
64 MiB GC budget. Prewarming and detailed render/pass/memory profiling were off.
Only the second arm set `SHARPEMU_LOCAL_DRAW_REUSE=0`. GeForce NOW settings were
not altered. Builds, tests and games were serialized under the shared lock.

The 250–285-second FPS window was selected before each game reached the room.
Saved frames confirm the same stationary shelf/lamp view before and during the
window. No movement was sent. Intro/phone dismissal occurred at different times;
the return arm's pre-window room screenshot was reviewed afterward. These are
sequential captures with different host conditions, not randomized repeated
measurements.

| Arm | Median FPS | Range | Reports | System swap-in / swap-out pages in window |
| --- | ---: | ---: | ---: | ---: |
| Enabled first | 5.97 | 3.50–7.49 | 17 | 31,519 / 24,556 |
| Disabled | 7.98 | 6.98–8.98 | 18 | 12,712 / 7,056 |
| Enabled return | 6.485 | 3.49–9.99 | 18 | 27,312 / 110,664 |

All seven host-memory samples per window reported pressure level 2. Compression
counts were 1,749,882 / 866,836 / 1,864,095 pages respectively. These are
system-wide measurements, not emulator-attributed I/O. The enabled return began
its window near 9–10 FPS and fell to 3.49 FPS; its lower whole-window median is
retained. Both enabled medians are lower than the disabled median. The paging,
warm-up and sequential-session differences prevent assigning that difference to
reuse, but **the return does not exonerate the change or establish an FPS win**.
The allocation benefit alone is insufficient to promote this candidate given
mixed component timings and unresolved runtime results. It remains opt-in.

Event-only traces followed separate ten-second thread-CPU windows, starting
around 302 seconds, outside the FPS windows. The trace had no managed sampling
profiler. Weighted allocation bytes estimate contributions rather than exact
object counts; asynchronous allocations are not attributable to individual
frames from these records.

| Post-window trace | Enabled first | Disabled | Enabled return |
| --- | ---: | ---: | ---: |
| Allocation ticks | 6,052 | 10,247 | 4,714 |
| Total sampled weighted bytes | 645,999,352 | 1,095,048,576 | 503,227,056 |
| PendingDraw weighted bytes | 5,561,120 | 51,362,560 | 5,769,584 |
| Guest frame timestamps within allocation-tick interval | 59 | 84 | 39 |
| Paired GC-related suspension intervals | 12 | 19 | 10 |
| Total suspension time, ms | 188.702 | 226.866 | 349.637 |
| Median / maximum interval, ms | 15.876 / 29.965 | 11.841 / 19.493 | 29.821 / 97.248 |

The return includes nine `SuspendForGC` intervals totaling 315.577 ms and one
`SuspendForGCPrep` interval of 34.059 ms. No arm has unmatched suspension pairs
or `SuspendOther` intervals. Different traced throughput prevents comparing raw
total allocation volume as equal work. The component benchmark establishes the
exact bounded wrapper allocation saving; these traces only support the expected
reduction in that type's contribution.

At approximately 292–302 seconds, CPU-seconds on the GPU worker / resolver /
guest RHI / audio-event thread were respectively **3.787 / 1.229 / 2.256 / 4.706**,
**4.944 / 1.726 / 4.114 / 7.981**, and **3.510 / 1.190 / 2.368 / 5.898**. These
post-window samples do not measure exclusive frame costs or identify the cause
of scheduling differences.

Private run directory suffixes are `room-local-draw-reuse-v22-on-1`,
`room-local-draw-reuse-v22-off-2`, and `room-local-draw-reuse-v22-return-3`, with
timestamps `20261027-203642`, `20261027-204216` and `20261027-204756`. Runs stopped
at their own markers after 314 / 313 / 313 seconds, with four-second controlled
termination and process/wrapper exit 0. Peak main-process footprint/RSS was
8,829/2,986, 9,015/4,529 and 9,102/4,157 MiB; shared mappings prevent summing
process footprints as physical usage. CPU traps occurred after stop, at
316.032 / 314.836 / 317.387 seconds, and remain unresolved teardown evidence.
**30 FPS remains unachieved.**
