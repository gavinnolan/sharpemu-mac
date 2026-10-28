<!-- Copyright (C) 2026 SharpEmu Emulator Project
SPDX-License-Identifier: GPL-2.0-or-later -->

# Cached pixel-input analysis

The graphics resolver repeatedly scanned every decoded pixel instruction to find
which inputs need per-vertex values. The existing weak attribute cache now also
stores that instruction-only bitmask. Every draw still maps the set attributes
through its live input controls. Cache entries use decoded-program object
identity and weak lifetime; copied programs receive separate analysis. The public
compiler routine remains unchanged for callers with editable instruction arrays.

`SHARPEMU_PIXEL_INPUT_ANALYSIS` defaults on; `0` restores the per-draw compiler
scan. The attribute cache still has the additional analysis field in that mode.
This adds 32 bytes per cold program in the measured cases, with no warm-path
allocation. It changes no shader arithmetic or rendering quality.

## Why this path

The v25 native sample of the existing caller-resolution payload resolved 5,028
of 5,191 previously unnamed address lines using JIT method ranges from the same
process. The resolver had 3,621 snapshots, including 2,507 condition-wait samples;
`PerVertexParameterMask` had 190 self and 211 inclusive samples and was its
leading identified non-wait leaf. These are sampled stack counts, not exclusive
CPU percentages. The eight-second native sample and twelve-second JIT/Loader
capture followed the FPS window; no managed SampleProfiler was enabled.

That unchanged-payload room window, 375–410 seconds, reported 16.935 FPS median
(10.47–17.99, 18 reports). It is diagnostic context, not a before/after result.
Its source was the immutable v24 candidate payload with caller resolution
explicitly enabled, equivalent to the later default policy at `f2bd8c7a7`;
the checkout hash in `run.env` alone does not identify the binary.

## Component measurements

Apple M4, .NET 10.0.3, Release with normal tiering. Seven alternating rounds
follow two seconds of warmup per case. Warm rounds use 100,000 operations for
16/512 instructions and 10,000 for 4,096; cold rounds use 1,024 distinct keys
after 64 warmup keys per mode. Sparse cases contain ALU instructions
and either zero or eight per-vertex interpolation inputs. Dense cases are an
interpolation-heavy stress test. Neither is a captured commercial shader.
Warm measurements compare the unchanged compiler scan to cached live mapping.
Cold measurements include the exact previous attribute-cache factory plus the
compiler scan, versus the combined cache plus live mapping. Each cold operation
has a distinct program identity; key creation is outside the measured region.
Wall time, current-thread CPU and allocation are recorded separately. These are
component measurements and establish no game-FPS improvement.

### Warm sparse cases

| Architecture | Instructions | Active inputs | Wall reference → cache ns/op | CPU reference → cache ns/op | Allocation reference → cache B/op |
| --- | ---: | ---: | ---: | ---: | ---: |
| arm64 | 16 | 0 | 97.74 → 4.45 | 97.65 → 4.46 | 0.00 → 0.00 |
| arm64 | 16 | 8 | 184.91 → 29.72 | 137.45 → 29.74 | 0.00 → 0.00 |
| arm64 | 512 | 0 | 2608.39 → 4.47 | 2608.21 → 4.48 | 0.00 → 0.00 |
| arm64 | 512 | 8 | 2601.72 → 8.53 | 2601.68 → 8.54 | 0.00 → 0.00 |
| arm64 | 4096 | 0 | 21035.27 → 4.54 | 21034.50 → 4.60 | 0.00 → 0.00 |
| arm64 | 4096 | 8 | 20413.52 → 8.54 | 20412.60 → 8.70 | 0.00 → 0.00 |
| x64 | 16 | 0 | 207.74 → 7.42 | 207.70 → 7.43 | 0.00 → 0.00 |
| x64 | 16 | 8 | 222.60 → 13.64 | 222.61 → 13.64 | 0.00 → 0.00 |
| x64 | 512 | 0 | 5730.62 → 7.56 | 5726.68 → 7.57 | 0.00 → 0.00 |
| x64 | 512 | 8 | 5680.88 → 13.66 | 5680.48 → 13.66 | 0.00 → 0.00 |
| x64 | 4096 | 0 | 44808.09 → 7.62 | 44807.60 → 7.70 | 0.00 → 0.00 |
| x64 | 4096 | 8 | 44856.74 → 13.60 | 44856.30 → 13.70 | 0.00 → 0.00 |

### Cold sparse cases

| Architecture | Instructions | Active inputs | Wall reference → cache ns/op | CPU reference → cache ns/op | Allocation reference → cache B/op |
| --- | ---: | ---: | ---: | ---: | ---: |
| arm64 | 16 | 0 | 988.28 → 1009.47 | 987.30 → 1009.77 | 344.00 → 376.00 |
| arm64 | 16 | 8 | 1978.03 → 1995.80 | 1978.52 → 1996.09 | 1000.00 → 1032.00 |
| arm64 | 512 | 0 | 4297.07 → 3818.75 | 4296.88 → 3819.34 | 344.00 → 376.00 |
| arm64 | 512 | 8 | 4500.39 → 4037.70 | 4500.98 → 4039.06 | 1000.00 → 1032.00 |
| arm64 | 4096 | 0 | 33679.00 → 30562.89 | 33678.71 → 30558.59 | 344.00 → 376.00 |
| arm64 | 4096 | 8 | 34802.54 → 30533.20 | 34802.73 → 30499.02 | 1000.00 → 1032.00 |
| x64 | 16 | 0 | 1832.13 → 1873.34 | 1832.03 → 1875.00 | 344.00 → 376.00 |
| x64 | 16 | 8 | 2773.73 → 2804.20 | 2776.37 → 2804.69 | 1000.00 → 1032.00 |
| x64 | 512 | 0 | 9051.46 → 7666.70 | 9031.25 → 7667.97 | 344.00 → 376.00 |
| x64 | 512 | 8 | 9706.54 → 8251.95 | 9708.01 → 8252.93 | 1000.00 → 1032.00 |
| x64 | 4096 | 0 | 70264.75 → 59116.21 | 70265.62 → 59115.23 | 344.00 → 376.00 |
| x64 | 4096 | 8 | 70745.51 → 59719.34 | 70731.45 → 59712.89 | 1000.00 → 1032.00 |

The smallest cold cases cost 0.9–2.3% wall time and 32 bytes per
program. Larger cold cases improved in this run, but comparisons between the
old factory reproduced in the test and the production factory also include
JIT/call-layout differences. The robust result is removal of repeated scanning
on a warm cache, not a general shader-compilation speedup. Normal tiering and
host scheduling remain visible (especially the first ARM64 active-input case).

The interpolation-dense stress cases also improved on the warm path. They are
retained in the raw JSON and are not treated as representative shader mixes.
The earlier v2 benchmark contained only that dense stress shape; v3 supersedes
it for acceptance. The cached arm allocated zero bytes in every warm case. The
reference arm allocated zero except its first 16-instruction round on each
architecture (0.0024 B/op, a one-time 240 B over 100,000 operations).

Reproduce with `SHARPEMU_PIXEL_INPUT_BENCH_OUTPUT=<absolute-json-path>` and
`dotnet test tests/SharpEmu.Libs.Tests -c Release --settings
 docs/performance/shader-serial.runsettings --filter
 FullyQualifiedName~PixelInputAnalysisCacheTests --blame-hang-timeout 300s`.
Use native ARM64 and Rosetta x64 hosts separately and serialize execution with
the machine-wide validation lock. Without the environment variable the optional
benchmark fact returns immediately; it is not run during ordinary CI.

Private evidence lives under `artifacts/runtime-diagnosis-20261026/`:
`pixel-input-v3-source.json`, `pixel-input-v3-{arm64,x64}.{json,log}` and
`pixel-input-results/pixel-input-v3-{arm64,x64}.trx`. Source parent is
`f2bd8c7a7717924d3252eb501f28ee7ef1ab2682`; the source manifest records hashes
for both production files and both test files.

| Raw benchmark | SHA-256 |
| --- | --- |
| `pixel-input-v3-arm64.json` | `396e3b1f7a24fcfb130c114a9129d44a38b64e6c14490a2f2c9d7fd9bf945e68` |
| `pixel-input-v3-x64.json` | `56b78d2b492d60aed5d4196335c95a5d340abba86c30b7d585bf86048d45e311` |

## Validation and runtime

Focused native and Rosetta runs each passed 79 tests, zero failures or skips.
Full default-on x64 Libs passed **9,106/0/5**, native required-device Metal passed
**1,639/0/7**, and the fallback (`SHARPEMU_PIXEL_INPUT_ANALYSIS=0`) pipeline
selection passed **79/0/0** (passed/failed/skipped). The five new semantic facts
cover changing controls, program copies, interpolation/modifier equivalence,
concurrent readers and the unchanged public editable-IR routine. The sixth fact
is the optional benchmark. Existing attribute-cache and pipeline tests also ran.
REUSE passed 2,881/2,881 including this report; `git diff --check` passed.
Full commands, environment overrides, timings and TRX hashes are retained in
`pixel-input-final-validation.json` and `pixel-input-final-results.json`.

### Same-payload Silent Hill control

Both publishes passed (x64 CLI and native ARM64 GPU host, self-contained Release,
ReadyToRun disabled). The immutable `runtime-pixel-input-v26` payload has 471
verified file hashes. Binary source is parent `f2bd8c7a7` plus tracked patch
`8007ab029bd25281f28fec279133277f2d3cbf813d45616b8899c2b250fc47a7`;
source snapshot, patch and untracked-test archive are preserved. Production and
test hashes match the v3 benchmark manifest. Later documentation does not change
the tested executable.

Both runs used fresh copies of `runtime-data-room-warm-defaults-v20-1`, 1920×1080,
scale 1, VSync, unchanged quality and the eleven previous effective defaults.
Command threading, local draw reuse, shader prewarming and detailed profiling
stayed off; the application's existing 64 MiB GC budget was applied to both
processes. Only `SHARPEMU_PIXEL_INPUT_ANALYSIS=1/0` differed. Automatic Enter
stopped at 258 s; Escape holds at 258.15, 261.20 and 309.59 s reproduced the
cutscene skip and phone-close sequence. The shelf/lamp room matched visually.
GeForce NOW and its settings were left unchanged.

Each 375–410 s FPS window was selected at process startup, before reading its
results. Ten seconds of thread CPU sampling followed at approximately 417–427 s;
no native or managed stack profiler ran during either comparison.

| Cache | Run directory prefix / suffix | FPS median | Range | Reports | Resolver CPU s | GPU-worker CPU s | Guest RHI CPU s |
| --- | --- | ---: | ---: | ---: | ---: | ---: | ---: |
| On | `20261028-004544` / `room-pixel-input-v26-on-1` | 16.930 | 11.97–18.97 | 17 | 2.510 | 8.241 | 6.791 |
| Off | `20261028-005314` / `room-pixel-input-v26-off-2` | 16.465 | 10.98–17.97 | 18 | 3.052 | 8.269 | 6.790 |

The resolver's post-window CPU time fell by about 0.54 s over ten seconds, while
GPU-worker and guest RHI CPU were almost unchanged. Those samples are separate
from the FPS window and are not a fixed-work instruction benchmark. In-window
PERF reports summed 6,850.5 ms background resolution / 813.9 ms caller resolution
with the cache, versus 9,237.0 / 972.5 ms without it. Report durations differ
(34.28 versus 36.44 s), so raw totals are not equal-work comparisons. Both modes
reported zero host-wait milliseconds and zero refused resolutions.

This pair does not establish a reliable FPS gain: the ranges overlap and host
paging differs. Seven system-memory samples in each FPS window recorded
10,354/11,156 swap-in pages and 30,256/0 swap-out pages (on/off), with memory
pressure levels 1–2 in both. Compression/decompression also differed; these are
whole-system counters, not emulator-only attribution. The cache is accepted as
a default for its measured warm-path benefit and tested semantics, with the
cold-entry cost retained. **30 FPS remains unachieved.**

Both harnesses stopped at their own marker after 427 s and exited 0 after four
seconds. CPU_TRAP results occurred only after the requested stops (428.998 s on,
429.022 s off); they remain shutdown diagnostics, not clean-shutdown assertions.
No runtime fatal or unsolicited CPU trap preceded either stop. All four game
processes exited. Main-process peak footprint/RSS was 9,883/4,310 MiB on and
9,831/5,012 MiB off; these are not combined physical-memory figures for the split
processes. Full results are retained in `pixel-input-v26-runtime-results.json`
and each run's FPS, memory, thread-CPU, input, screenshot and result files.
