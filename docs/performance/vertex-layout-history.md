<!-- Copyright (C) 2026 SharpEmu Emulator Project
SPDX-License-Identifier: GPL-2.0-or-later -->

# Bounded vertex-layout history

`SHARPEMU_VERTEX_LAYOUT_HISTORY` is enabled when unset. Set it to `0` to retain
only the previous last-layout memo. This is a component-benchmark acceptance;
no new game-FPS result or 30-FPS claim accompanies it. The nine earlier accepted
performance settings remain enabled, including component wins without an
isolated Silent Hill FPS improvement.

## Behavior and ownership

Alternating meshes previously decoded and allocated layout arrays whenever a
shader's current tables differed from its immediately preceding draw. The new
history reuses older arrays only after rereading guest attribute and buffer tables
and checking shader metadata identity, hash, and every table byte. A hash is only
a candidate selector. Moving equal tables can reuse their layout; modifying bytes,
changing shader identity, or losing memory readability cannot return stale data.
Each draw still gets its own mutable `VertexInputInfo` wrapper. Retained arrays
follow the existing immutable-layout contract and are never pooled or overwritten.
The pipeline's existing resolve lock owns the cache.

There are 512 direct-mapped slots and a conservative 2 MiB retained-payload budget,
plus the approximately 12 KiB slot array. This limits the additional history,
not the pre-existing per-shader last-layout memos. History is allocated lazily and
cleared when the 4,096-entry shader-metadata cache is cleared. Each metadata entry
adds 24 bytes of hash/backoff fields on the measured runtimes. A successful cold
table parse now avoids a redundant metadata object; empty and failed parses
preserve the public non-null out-parameter contract.

After 32 changed-layout probes, fewer than 16 history hits suppress the next 4,096
history probes for that shader. Fresh guest reads and last-layout comparisons
continue. Probation then resumes, allowing a later repeating working set to
recover. This trades delayed recovery after churn for much lower miss overhead.
`SHARPEMU_PROFILE_VERTEX_LAYOUTS=1` reports hits, decodes, refusals, suppression,
entries, retained bytes and evictions every 4,096 lookups; diagnostics remain off
by default.

## Measurement method

The optional `VertexInputResolverTests.BenchmarkVertexLayoutHistory` exercises
real table reads, decoding, buffer merging and wrapper construction over fake
guest memory. Both modes run in one process. It warms the first scenario for ten
seconds and later scenarios for five seconds, then measures nine alternating
rounds of 200,000 resolutions per mode. Checksums verify stream addresses and
semantic mappings. Normal .NET 10.0.3 tiering is retained. Managed allocation and
CPU time count only the calling thread; elapsed time is recorded separately.
These measurements exclude native rendering and are not frame-time estimates.

Scenarios cover one unchanged layout, cycles of 2/8/64/1,024 layouts, a descriptor
that changes every operation, and a new shader identity every operation. The
latter intentionally stresses cold metadata creation and bounded-cache clearing.
The default and explicit `0` path use the public constructor; no timed environment
changes occur. The full-suite benchmark still runs serially with other tests.

The preserved pre-change library has SHA-256
`b487a9ed68a6424e3d8959bd038c66805046951aebc5bdcf107054695338e148`.
Frozen 1,444-file test payloads were identical except for `SharpEmu.Libs.dll`.
A baseline run between two candidate runs confirmed repeated-layout savings and
exposed the redundant cold metadata allocation. Comparing separate runs remains
subject to host/core/frequency drift; even the unchanged last-layout path moved.
Earlier short-warmup measurements included tier transitions and are not acceptance
numbers. The first history design also penalized miss-heavy workloads; adaptive
backoff was added before promotion.

## Final measurements

Median nanoseconds per resolution, history disabled → enabled. Each cell uses
all nine rounds. All rows, including slowdowns and bypass controls, are retained.

| Workload | ARM64 CPU ns | x64 CPU ns | ARM64 elapsed ns | x64 elapsed ns | B/op, both architectures |
| --- | ---: | ---: | ---: | ---: | ---: |
| Unchanged last layout | 126.35 → 116.91 | 88.53 → 89.45 | 249.57 → 227.78 | 88.59 → 89.49 | 216.00 → 216.00 |
| Cycle of 2 | 214.25 → 122.31 | 193.78 → 109.91 | 240.18 → 137.42 | 193.82 → 110.04 | 488.00 → 216.00 |
| Cycle of 8 | 249.32 → 148.12 | 191.52 → 108.61 | 358.67 → 195.95 | 192.55 → 109.32 | 488.00 → 216.00 |
| Cycle of 64 | 243.22 → 144.08 | 202.94 → 127.16 | 257.22 → 145.73 | 207.38 → 128.92 | 488.00 → 233.00 |
| Cycle of 1,024 | 270.69 → 270.91 | 199.28 → 209.76 | 308.18 → 483.72 | 200.74 → 212.99 | 488.00 → 487.46 |
| Always changing | 284.18 → 298.10 | 195.99 → 197.81 | 379.95 → 468.83 | 196.06 → 198.15 | 488.00 → 488.00 |
| Cold shader identity | 456.40 → 464.73 | 419.38 → 404.39 | 602.64 → 761.45 | 427.21 → 412.68 | 584.00 → 584.00 |

Cycles of 2–64 reduce calling-thread CPU medians by **37–43%** and save
**255–272 B/resolution**. Larger working sets and unique descriptors establish
no speed win: CPU median changes in the final paired runs span approximately
**+0.1% to +5.3%**, with substantial per-round variation. Their elapsed medians
can be much worse under host contention, as the table shows. The unchanged-layout
and cold cases also drift although the history is not queried there. Acceptance
therefore rests on repeated-layout wins, bounded miss work and correctness, not a
claim that every workload improves.

The native return run before the cold-parser cleanup measured 118.99 → 70.56 ns
for two layouts, 118.61 → 77.01 ns for 64, 121.39 → 122.20 ns for 1,024, and
126.63 → 128.96 ns for unique descriptors. The baseline library measured about
115–117 ns for cycling/changing cases in the intervening run. This shows a small
common-path cost can exist even with the new history disabled; the kill switch
is a functional reference, not a promise of byte-identical old-code timing.

The final cold path allocates **584 B/resolution**, compared with **600 B** in
the preserved pre-change library and **648 B** before removing the redundant
metadata allocation. This is cold shader metadata/layout resolution, not the
separate resource-cache benchmark. Sequential CPU timings do not establish a
cold-path speedup. The final native run began at a host load average of about
18.7; host conditions
were not controlled by closing applications or changing GeForce NOW.

## CPU clock correction

The original optional CPU timer used `CLOCK_THREAD_CPUTIME_ID`. A native C busy-loop
control found that both its nanosecond and timespec APIs returned unscaled ticks
under Rosetta on this host. The same interval reported 3,459,532 purported ns but
144,153,000 ns through Mach thread counters, versus 148,448,000 ns elapsed.
Native ARM64 reported 261,802,584 ns versus 261,807,000 ns through Mach.

The benchmark tool and this fixture now share `DarwinThreadCpuClock`, using
`THREAD_BASIC_INFO` seconds/microseconds on both architectures. It borrows the
pthread Mach port and creates no send-right leak. Old Rosetta clock samples must
not be interpreted as nanoseconds. The published dense-register CPU comparisons
ran natively on ARM64 and are unaffected. CPU timing excludes descheduling and
other threads, but still reflects JIT, cache, core-placement and frequency changes.

## Reproduction

```sh
SHARPEMU_VERTEX_LAYOUT_BENCH_OUTPUT=/absolute/path/layout-results.json \
  dotnet test tests/SharpEmu.Libs.Tests/SharpEmu.Libs.Tests.csproj -c Release \
  --filter FullyQualifiedName~VertexInputResolverTests \
  --settings docs/performance/shader-serial.runsettings \
  --blame-hang-timeout 180s
```

Use the native or x64 .NET host explicitly and the shared validation lock on this
machine. Raw rounds, full commands, source/payload hashes and TRX files are retained
under `artifacts/runtime-diagnosis-20261026/vertex-history-*`.

## Final validation

- Full serial Release Libs suite on x64: **9,084 passed, 0 failed, 5 existing
  Opus skips**; test phase 2m45s. Performance switches were unset, with only the
  optional benchmark output requested.
- Final focused ARM64 resolver/pipeline suite: **109 passed, 0 failed, 0 skipped**,
  including the optional benchmark; test phase 52s. These overlap the full-suite
  cases and are configuration checks, not 109 additional distinct tests.
- Coverage includes the public unset/1/0 selection, unchanged retained arrays,
  fresh reads on history hits, unreadable-table refusal, moving descriptors,
  shader identity, forced same-slot ownership/hash collisions, byte/entry bounds,
  backoff recovery, metadata-cache clearing and non-null empty/error outputs.
- Release benchmark-tool build passed with four existing platform-analysis warnings.
  Native and Rosetta PM4 timing smoke runs each produced 15 positive Mach CPU
  intervals bounded by elapsed time. All 252 final layout benchmark intervals
  were also positive and bounded by elapsed time.
- REUSE passed **2,874/2,874 files** and whitespace checks passed.

No game was launched or new application bundle published for this follow-up.
The tests and component measurements do not demonstrate a Silent Hill FPS gain,
and **30 FPS remains unachieved**. GeForce NOW and other application settings
were unchanged. The PR remains draft and unmerged.
