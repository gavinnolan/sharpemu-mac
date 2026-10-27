<!-- Copyright (C) 2026 SharpEmu Emulator Project
SPDX-License-Identifier: GPL-2.0-or-later -->

# Dense register-range experiment: rejected as a default

The nine validated [performance defaults](performance-defaults.md) remain enabled.
A separate raw-register range-copy prototype was tested and removed from production:
faster isolated warm writes did not establish a safe overall speed improvement.
No game was launched for this follow-up, and it establishes no FPS gain or 30-FPS result.

The prototype proved all destination entries present before copying a contiguous
shader-register packet. Missing entries, short packets and invalid ranges retained
the scalar path, including partial raw writes before a fatal invalid tail. Variant 1
used a variable-length span copy. Variant 2 specialized 4/8/16/32-word copies with
constant lengths. Both preserved insertion order, presence, counts and snapshots.
All pre-existing commits remain intact; neither prototype is a new runtime default
or a retained opt-in feature.

## Method and limitations

Baseline source: `f70ce37a7fc2854ebc78b305fb0e427320bd7153`. Each comparison
used frozen ARM64 .NET 10.0.3 self-contained payloads with ReadyToRun disabled;
hashes confirmed only `SharpEmu.Libs.dll` differed within each pair. Each arm ran
twice in A/B/B/A order. The five PM4 interpreter scenarios each used 5 seconds
of warmup followed by five approximately 150-ms samples. Shader blocks contain
eight changing words; fake host callbacks isolate interpretation from rendering.
The allocation result remained 0.1 B/synthetic draw in both arms.

Initial wall-only comparisons were unsuitable for acceptance: one unchanged
control moved from 834.3 to 1770.0 ns/draw. Isolated map tests covered warm/cold
entries, 1/2/4/8/16/32 words and starts at 12/63, on ARM64 and x64, with normal
and disabled tiering. Warm range writes looked favorable, but even bypass controls
varied substantially. Those short elapsed-time samples are not treated as stable
speedup estimates.

The benchmark now optionally records calling-thread CPU time alongside elapsed
time (`SHARPEMU_BENCH_THREAD_CPU=1`, macOS). This excludes descheduling and other
threads' work but does not eliminate core-placement, frequency, cache or JIT drift.
All 300 paired samples below had positive CPU intervals no greater than their
elapsed intervals. Separate disassembly captures confirmed optimized Tier1 code for the baseline
and variant 1; those captures are not timing evidence.

## CPU-time results

Values below are nanoseconds per synthetic draw. Each cell averages the two
per-run medians, giving the two process runs equal weight. Positive deltas are
slower. These are component measurements, not measured game-frame costs.

| Scenario | Variant 1, tiering disabled: baseline → candidate | Delta | Variant 1, normal tiering: baseline → candidate | Delta |
| --- | ---: | ---: | ---: | ---: |
| 20 context registers, block reads | 887.7 → 845.0 | -4.8% | 557.7 → 620.7 | +11.3% |
| Same stream, per-packet reads | 1110.8 → 997.3 | -10.2% | 552.7 → 616.3 | +11.5% |
| Indexed draws, block reads | 1071.3 → 1014.6 | -5.3% | 578.8 → 589.1 | +1.8% |
| 5 context registers, block reads | 374.8 → 324.7 | -13.4% | 202.5 → 225.6 | +11.4% |
| 25 context registers, block reads | 1152.7 → 1109.4 | -3.8% | 639.4 → 700.4 | +9.5% |

Variant 1 therefore did not qualify under the application's normal tiering policy.
Variant 2 requested constant-length copies for common packet sizes, but still did
not qualify. Its complete normal-tiering comparison follows; cells retain the
individual run medians in execution order within each arm.

| Scenario | Baseline CPU ns (A1, A4) | Candidate CPU ns (B2, B3) | Delta of arm means |
| --- | ---: | ---: | ---: |
| 20 context registers, block reads | 634.1, 447.8 | 588.6, 461.5 | -2.9% |
| Same stream, per-packet reads | 643.3, 671.2 | 593.8, 658.8 | -4.7% |
| Indexed draws, block reads | 504.4, 489.6 | 552.8, 574.9 | +13.5% |
| 5 context registers, block reads | 256.2, 266.8 | 210.5, 224.7 | -16.8% |
| 25 context registers, block reads | 775.7, 558.7 | 548.7, 672.2 | -8.5% |

The indexed case is 13.5% slower while its two controls differ by about 3%.
Other cases retain appreciable drift. This is sufficient to reject broad default
acceptance; it does not prove a game regression or identify a unique JIT cause.
No further default was enabled on the strength of the isolated copy benchmark.

## Correctness and retained changes

Variant 1 passed 76 focused register tests. Its 16 range-test cases also passed
on ARM64 and x64 with normal and disabled tiering (repeated configuration checks,
not 64 additional distinct tests). Variant 2 passed 84 focused tests, including
adjacent-register sentinels around every fixed-size copy. The rejected source,
tests, payload hashes, raw samples and commands are retained privately under
`artifacts/runtime-diagnosis-20261026/`: `dense-range-*.json`, associated logs,
`dense-range-v1.patch` and `rejected-dense-range-v2.patch`.

The existing interpreter gained two additional regression cases for a longer
invalid-tail packet and repeated writes across a retained snapshot. Production
register-map and interpreter source were restored exactly to the baseline. The
rebuilt `SharpEmu.Libs.dll` also matches the baseline byte-for-byte (SHA-256
`b487a9ed68a6424e3d8959bd038c66805046951aebc5bdcf107054695338e148`).
The new optional CPU-time diagnostic is retained in the synthetic benchmark tool;
see its [measurement instructions](../../tools/SharpEmu.Tools.PerfAudit/README.md).

A separate [CI failure](https://github.com/sharpemu/sharpemu-mac/actions/runs/40574121112/job/122436124389)
reported a zero-millisecond polling interval against a 20-ms minimum. Its writer
was started before the stopwatch and could finish while the caller was descheduled.
The fixture now starts timing before starting the writer and joins it in `finally`
before freeing shared memory. Production polling behavior and timing bounds are
unchanged. An initial full GPU-remote run passed 56/0/0.

Final restored-source Release validation, with no `SHARPEMU_*` overrides and
serial runsettings: **Libs 9,070 passed / 0 failed / 5 existing Opus skips**;
**GPU-remote 56 passed / 0 failed / 0 skipped**. Combined: **9,126/0/5**.
The Libs test phase took 3m17s; GPU-remote took 8s (build times excluded).
Commands used the shared machine lock and the x64 .NET host:

```sh
dotnet test tests/SharpEmu.Libs.Tests/SharpEmu.Libs.Tests.csproj -c Release --no-restore --settings docs/performance/shader-serial.runsettings --blame-hang-timeout 120s
dotnet test tests/SharpEmu.GpuRemote.Tests/SharpEmu.GpuRemote.Tests.csproj -c Release --no-restore --settings docs/performance/shader-serial.runsettings --blame-hang-timeout 120s
```

Full commands, TRX hashes and counts: `dense-range-final-validation.json` in the
private diagnostic directory. These checks validate the retained tests/tooling
and restored library; they are separate from prototype timing and game FPS.

REUSE passed **2,869/2,869 files**, and whitespace checks passed.
