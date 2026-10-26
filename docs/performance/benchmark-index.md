<!-- Copyright (C) 2026 SharpEmu Emulator Project
SPDX-License-Identifier: GPL-2.0-or-later -->

# Audit changes and benchmark index

The combined PR contains the complete round-one integration ending at
`cde04b713e60de7ab02dfa092e8f91d086df0c83` (37 commits above the audit's main
baseline), the two round-two committed changes and the accepted round-two WIP.
The original combined implementation is `18323b71870897ea657939d621a1bacfbde83a2b`.
The later benchmark packaging adds reusable source and documentation; it does not
turn historical component measurements into measurements of the combined build.

## Implementation coverage

| Area | Changes included |
|---|---|
| CPU and HLE crossings | AGC builder leaf path, native clock stubs, host-thread pthread identity, shared thread state, in-place large memset, expected-result logging and disabled-trace formatting guards |
| Guest scheduling | Direct wake handoff/shared resume stub, owner-aware bounded mutex spinning, per-fiber lock stripes, targeted ULT wakes, TLS caching, sleep/yield backoff and thread/exit behavior |
| Draw preparation | Pipeline/shader identity memoization with fresh-memory fallback, table/color-target lookup reuse, scratch index expansion, unchanged register snapshot reuse and diagnostic-only write history |
| Resource materialization | Content-keyed reuse across moving descriptors, pointer-key costs, linear merge of recorded reads and bounded cold-scan retention; descriptor, mapping, bounds and snapshot invalidation tests |
| Metal storage/binding | Page-granular staged refresh, reusable retile staging, binding/residency reuse, GPU-pristine snapshots, owned texture-view cache and alias/narrow-consumer transitions |
| Synchronization | Exact GPU-written word fences, host completion publication through callbacks at the same serial, tracked store/barrier handling and waiter registration/lost-wake coverage |
| Split transport | One protocol-7 layout, frame-boundary waits outside the submission gate, event-driven input, one-use display reservations, pending-writeback page summary and cross-architecture smoke coverage |
| Shader translation | Relaxed-math/early-depth handling, global-record reuse, grouped raw loads, native min/max and reciprocal lowering; precise sqrt retained; contraction remains opt-in |
| Audio and host overhead | AJM disabled-formatting path, environment lookup and audio context work, helper/compiler thread policy and archive/prewarm handling |
| Regression fixes | Six Libs fixture/API failures, scattered-view alignment, lossy allocation footprint, enumerated native leak counters and ordered fake-store writes |

The [K1–K21 disposition table](combined-audit.md#workstream-dispositions) records
what was accepted, rejected or left opt-in. Concurrent tiling is not the default;
GC/tiering changes, unsupported lock-free/parallel planning, extra barrier/store
elision, decoder pooling and ABI changes were not accepted.

A source inventory check found 124 distinct recovered changed paths: 122 are
tracked in the combined PR. The two other paths are the temporary input harness
(replaced by `InputDeliveryBenchmarks.cs`) and `SpinNapElfSurvey.cs` (an ELF survey,
not an accepted emulator fix). K1's survey-only loader field was also omitted.
This is an accounting of accepted implementation, not a claim that every discarded
experiment or local artifact belongs in the PR. Original synthetic eboot binaries,
commercial captures, raw logs, build outputs and machine diagnostics stay outside it.

## Runnable benchmark sources

| Benchmark | Source / instructions |
|---|---|
| CPU, queue, memory, translator, pipeline and synthetic Metal paths | [PerfAudit harness](../../tools/SharpEmu.Tools.PerfAudit/README.md) |
| Final cold/stale/hit/ring comparison | [Materialization harness](../../tools/SharpEmu.Tools.MaterializationBench/README.md) |
| Split input latency | [InputDeliveryBenchmarks](../../tests/SharpEmu.GpuSplit.Tests/InputDeliveryBenchmarks.cs), `SHARPEMU_INPUT_BENCH=1` |
| Grouped loads, min/max, reciprocals, contraction and hints | [MslTranslatorOverheadBenchTests](../../tests/SharpEmu.ShaderCompiler.Metal.Tests/MslTranslatorOverheadBenchTests.cs) |
| Relaxed math, global records and early depth | [MslGpuShaderTimeBenchTests](../../tests/SharpEmu.ShaderCompiler.Metal.Tests/MslGpuShaderTimeBenchTests.cs) |
| Texture transfer and alias CPU/GPU costs | [MetalImageTransferTests.Timing](../../tests/SharpEmu.Gpu.Metal.Tests/MetalImageTransferTests.Timing.cs) |
| Narrow consumer transfer work and exact bytes | [ConsumerRunSceneTests](../../tests/SharpEmu.Gpu.Metal.Tests/Scenes/ConsumerRunSceneTests.cs) |

Run the shader benchmarks with `SHARPEMU_GPU_BENCH=1 SHARPEMU_TEST_REQUIRE_DEVICE=1`,
Release configuration, a class filter and `shader-serial.runsettings`. For example:

```sh
SHARPEMU_GPU_BENCH=1 SHARPEMU_TEST_REQUIRE_DEVICE=1 ~/.dotnet-x64/dotnet test tests/SharpEmu.ShaderCompiler.Metal.Tests -c Release -m:2 --settings docs/performance/shader-serial.runsettings --filter FullyQualifiedName~MslTranslatorOverheadBenchTests --logger 'console;verbosity=detailed'
SHARPEMU_INPUT_BENCH=1 ~/.dotnet-x64/dotnet test tests/SharpEmu.GpuSplit.Tests -c Release -m:2 --filter FullyQualifiedName~InputDeliveryBenchmarks --logger 'console;verbosity=detailed'
```

## Measurements and limits

These are different experiments with different baselines; they must not be added
together or converted into a game-FPS estimate. Final admission figures are from
alternating runs of the combined build. Other figures are historical isolated
component comparisons retained from the recovery audit.

| Experiment | Before | After | Interpretation |
|---|---:|---:|---|
| Final cold allocation, tiering disabled | 8,264 B/op | 6,379.9–6,382.6 B/op | About 23% less allocation; timing remains inconclusive |
| Final cold allocation, tiered/PGO | 8,160 B/op | 6,378.9–6,384.2 B/op | About 22% less allocation; host controls drifted |
| Final stale descriptor allocation, tiering disabled | 8,392.2–8,392.3 B/op | 7,032.6–7,035.4 B/op | About 16% less allocation |
| K17 strided translator, x64 tiering-disabled ABBA | 29.9 / 25.9 ns; 47 B/op | 4.9 / 5.3 ns; 0 B/op | Historical component result |
| K17 fragmented translator, same experiment | 153.1 / 155.8 ns; 45.2 B/op | 20.8 / 22.2 ns; 0 B/op | Single-view case instead slowed slightly; ~40 KiB inline storage per translator |
| K4 input delivery p50 | 3.48 / 3.11 ms | 0.10 / 0.10 ms | Two transport halves in one process; not guest draw latency |
| K12 narrow linear consumer retile | 16,588,800 bytes | 15,360 bytes | Deferred work; later full consumption still does the remaining work |
| K12 narrow tiled consumer retile | 16,711,680 bytes | 983,040 bytes | Exact-byte correctness checked; not a timing result |
| K21 grouped-load GPU kernel | 9.173 ms | 4.972 ms | Best of three warmed synthetic GPU measurements |
| K21 min/max GPU kernel | 13.700 ms | 8.215 ms | Same narrow methodology |
| K21 reciprocal/rsqrt GPU kernel | 8.628 ms | 6.303 ms | Precise sqrt in both arms; not a whole-shader result |

[The full report](combined-audit.md#measurements-and-validation) retains both
medians and p95 ranges for every final A/B arm, historical main/cde cold controls,
allocation results, source identities, test counts and reproducible commands.
The linear merge alone did not prove a timing improvement. Concurrent tiling
regressed representative writebacks and remains opt-in. Compiler QoS and shader
hints have no established general speedup. Two admission-disabled parallel test-host
stalls remain unresolved; the full serial run passes unchanged assertions.

The original combined validation totals are **16,741 passed, 0 failed, 13 existing
skips**, plus REUSE, script checks and successful macOS CI at `18323b718`.
No commercial game was run for that original integration. Benchmark smoke checks
for this packaging change are reported separately from those full-suite results.

## Benchmark packaging validation

On x64 .NET 10.0.3/macOS, the standalone project builds with zero errors.
The documented short runtime invocation completes **75 benchmark rows** across
10 selectors. These short-run numbers are smoke evidence, not new A/B claims.
The generated Metal triangle completes and asserts the exact `0xFF0000FF` pixel;
a mismatch exits with an error. REUSE passes for all 2,816 files and whitespace
checks pass. The emulator algorithms are unchanged by this packaging follow-up;
the Metal project only grants the uniquely named tool access to its internals.
Full-suite/CI counts above belong to the original combined implementation.

## Portable vertex-fetch follow-up

See [shadow mesh fetch measurements](shadow-mesh-fetch.md) for the subsequent runtime
localization, generic formatted-load optimization, descriptor/bounds tests, synthetic
GPU benchmark commands and fallback costs. The subsequent matched Silent Hill
rooftop/title comparison observes 2.49 → 3.98 median guest FPS and 56.509 → 26.753 ms
mean shadow encoders. Its conditions, failed captures and remaining bottlenecks are
listed in that report; this is not a full-game gameplay benchmark.
