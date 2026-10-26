<!-- Copyright (C) 2026 SharpEmu Emulator Project
SPDX-License-Identifier: GPL-2.0-or-later -->

# Combined synthetic performance audit

This integrates the round-one audit (`cde04b713e60de7ab02dfa092e8f91d086df0c83`)
and the valid round-two changes. Validation uses fabricated guest programs,
synthetic descriptors and real Metal device tests. No commercial game was run for
that integration. A later user-authorized diagnostic and the resulting generic
[portable vertex-fetch follow-up](shadow-mesh-fetch.md) are documented separately.
Microkernel, byte-work and microbenchmark results do not establish game performance.

See the [change and benchmark index](benchmark-index.md) for round-one coverage,
source-inventory exclusions, runnable benchmark locations and a measurement summary.

## Integration and correctness

The combined wire protocol is revision **7**. Independent revisions 4 (frame boundary),
5 (input/display reservation) and 6 (pending writeback pages) are incompatible and
rejected. Both processes use the same combined shared-state layout. Compatibility
and pinned x64-to-arm64 smoke tests cover the boundary.

Host completion work stays pending while its callback executes. Waiting through a
committed serial includes every callback at that serial, including a second callback
whose publication has not finished. Monitor waits and producer ordering are retained.
GPU-written image claims survive retirement and transfer to in-flight writeback claims.
The pending bitmap is cleared only when the image cache publishes completed ownership.

The six former Libs failures are corrected without removing their behavioral checks:

- Import result sampling constructs the current private import entry and invokes the
  current `in ImportStubEntry` API; every occurrence/result assertion remains.
- Fabricated POSIX frames use a fully initialized backend with process handler
  installation disabled. The fixture owns/disposes it and restores the previous
  registration. Register, ABI, instruction-pointer and extended-vector checks remain.
- Two vector stores can share one grouped trampoline. The fixture tests that form
  and a branch-separated two-trampoline form. Both must expand two stores, use no
  red zone, report no failed sites and preserve every source/destination byte.

The scattered-view fixture aligns backing inside its own reserved mapping. The lossy
footprint fixture uses a texture larger than Metal's minimum allocation page. Neither
production backing refusals nor strict footprint/data assertions were relaxed.
The pending-writeback fake-store fixture now drains prior CPU-write records before
its direct render calls. Real GPU renders enter the ordered worker stream; the
fixture's out-of-stream render could otherwise be invalidated by an older record.
The in-flight blocking assertion, exact bytes and zero-lost-bytes checks remain.

The combined leak suite exposed an allocator-counter defect that also reproduces
as seven failures on unchanged `cde04b713`. On this translated macOS 27.2 host,
`malloc_zone_statistics` reported 9,137 more blocks for 3,000 label flushes while
independent `heap` enumeration fell from 85,055 to 84,982 live nodes; live command
buffers grew from one to two, rather than by thousands. One aggregate counter also
wrapped through 32 bits. Leak tests now enumerate the allocator's actual in-use
ranges. The original per-flush and release-fraction assertions/thresholds remain.
A separate isolated-zone regression verifies exactly 32 allocated blocks and their
complete release. This is a measurement correction, not a claim that a production
leak was fixed.

## Cold materialization

Descriptor evaluation and flattened-table evaluation usually record two ordered runs.
The recorder now merges those runs in linear time instead of sorting the complete
list. Already ordered reads stay untouched; irregular graphs retain the general sort.
The merge keeps every read, clean requirement and descriptor/table flag. Entry range
construction, fresh validation, descriptor-content keys, bounds, bias, user-data and
mapping checks are unchanged. Retained snapshots remain immutable.

After actual generation/variant evictions and 32 consecutive misses, a bounded
512-tag admission history declines retention for novel contents. Every draw still
performs a full walk. Recurring contents require a second, fully recorded walk
before normal admission and fresh validation can produce a hit. Tag collisions can
only add work, never authorize a hit. A hit resets the miss streak. Set
`SHARPEMU_RESOURCE_CACHE_ADMISSION=0` to disable this policy.

The preliminary walk owns its user-data array and keeps its outputs local. A failed
recorded rewalk leaves the caller's original snapshot and specialization intact;
a successful rewalk publishes only its own results. Regression tests cover both
failure atomicity and reuse of the caller's input array, in addition to cold scans,
recurrence, resident unreadability, replacement mappings and retained snapshots.

An instrumented diagnostic run recorded 361,272 merged lists, zero already ordered
lists and zero general-sort fallbacks for the synthetic materialization workload.
Sorting/merging accounted for 121.372 ms of 479.539 ms in entry building in that run.
Those instrumented timings are diagnostic, not the unprofiled A/B result.

The standalone harness is `tools/SharpEmu.Tools.MaterializationBench`. It measures the
same 8-image/2-sampler/4-buffer workloads for uncached walks, repeated hits, distinct
cold descriptors, stale descriptors at the same address, repeated pointers and moving
rings. The general tests also compare reordered read shapes with fresh walks and
check descriptor/bounds changes and unreadable resident memory.

## Workstream dispositions

| Workstream | Integrated or rejected |
|---|---|
| K1 | Disabled continuation-trace guards integrated. Temporary ELF survey field/harness omitted; speculative spin-policy changes rejected. |
| K2 | Content-key/pointer improvements and regressions integrated, with the additional linear read merge. Fresh-read safety is retained; see A/B results below. |
| K3 | No additional GC/tiering policy change: no general benefit established. |
| K4 | Event-driven input and one-use display reservation reuse; owner and intervening-slot invalidation tests retained. Input benchmark is explicit opt-in. |
| K5 | Existing frame-boundary commit and gate tests integrated; the gate is released before the boundary wait. |
| K6 | Lock-free image lookup rejected without a reader-lifetime protocol; no recovered source to integrate. |
| K7 | GPU-pristine staging snapshots and exact-byte/concurrent-CPU-write regressions retained with their kill switch. |
| K8 | Compile-thread QoS and asynchronous preparation retained; synthetic throughput is a priority tradeoff, not a universal gain. Command-thread suspension rejected. |
| K9 | Early-depth decoding, side-effect handling and device regressions retained. |
| K10 | Exact store/barrier tracking plus completion-publication correction and held-second-callback regression retained. No blanket cross-pass elision. |
| K11 | Further fence/depth-store elision rejected without producer/consumer proof. |
| K12 | Existing alias-transition commit plus narrow consumer runs, GPU-write CPU-dirty marking and four exact-data device regressions retained. |
| K13 | Owned cached texture views retained. Concurrent tiling remains opt-in: representative GPU writebacks were slower; serial is the default. Alias property reads use the shared texture-info helper. |
| K14 | Write-fence/prefetch work and aligned scattered-view fixtures retained; fatal/refusal behavior unchanged. |
| K15 | Historical group8 binary/evidence artifacts remain outside the PR. No unsupported latency claim or new runtime launch. |
| K16 | Parallel planning rejected without immutable descriptor/mapping lifetime proof; no recovered source to integrate. |
| K17 | Mapping-generation-aware translator and helper-thread work retained. Zero-allocation fixture measures before assertions. Single-view latency and per-translator storage remain tradeoffs. |
| K18 | Pending-writeback summary and real retirement/fenced-completion tests retained in combined protocol 7. Watch-arm mprotect overlap rejected. |
| K19 | Waiter registration, bounded lost-wake regression, audio/environment/disabled-formatting work retained. Decoder-result pooling and shorter unnotified polls rejected. |
| K20 | Disabled formatting subset is in K1. Continuation pooling and ABI conversion rejected without ownership/crash proof. Snapshot cloning retained. |
| K21 | Raw dword loads, min/max and reciprocal work, boundary/wrap parity tests and strict precise-sqrt regression retained. Contraction remains opt-in. Threadgroup hints/table padding are configurable experiments without an accepted speedup claim. No captured-corpus replay or wave-barrier skip claim. |

## Measurements and validation

The historical compiler controls used the same benchmark executable/dependencies,
x64 .NET 10.0.3 on an Apple Silicon host, 5 s warmup and seven 200 ms samples.
The sequence was main/cde/R2/merge/merge/R2/cde/main. Each cell below contains both
run medians, in ns/op, for distinct cold descriptors (3c). Main is
`42eec4aa76b85e3e03d6e12a4bb29adb7a1a56fa`; cde is the round-one revision above.
R2 is the recovered compiler before the linear merge; merge predates admission.

| Compiler | Tiering disabled | Tiered/PGO |
|---|---:|---:|
| Historical main | 10,152.5 / 10,650.1 | 5,646.6 / 6,714.4 |
| Round one (cde) | 9,470.3 / 11,532.4 | 6,415.2 / 6,810.3 |
| Recovered R2 | 9,580.9 / 10,153.5 | 6,665.2 / 6,588.7 |
| Linear merge | 10,158.7 / 14,378.2 | 6,957.1 / 12,913.1 |

All four retained 8,264 B/op with tiering disabled, 8,160 B/op tiered. The merge
alone did not establish a timing win. The earlier R2 repeated-pointer improvement
and cold regression therefore remain qualified; this PR does not claim all cold
latency regressions against historical main are resolved.

Other recovered measurements are narrower comparisons: K17 strided/fragmented
translation removed about 45-47 B/op, while its single warm-view case moved from
4.3-4.5 to 4.9-5.0 ns and its inline storage costs about 40 KiB per translator.
K13 cached serial texture views reduced selected CPU transfer costs; concurrent
GPU writeback regressed representative depth/RGBA8/R8 shapes and stays opt-in.
K21 grouped loads/minmax/reciprocal microkernels improved selected GPU timestamps,
with precise sqrt retained; those are not whole-shader or game measurements.
K8 Utility QoS reduces background compile throughput and has no established
foreground win. K4's two transport halves in one process reduced synthetic input
p50 from 3.1-3.5 to about 0.1 ms; this is not guest draw latency.

Reproduce the final policy comparison with the checked-in benchmark, holding the
executable, architecture and dependencies fixed:

```sh
~/.dotnet-x64/dotnet build tools/SharpEmu.Tools.MaterializationBench -c Release -m:2 -p:UseSharedCompilation=false -nr:false
# Run 0,1,1,0 in separate processes, then repeat with DOTNET_TieredCompilation=1.
SHARPEMU_RESOURCE_CACHE_ADMISSION=0 DOTNET_TieredCompilation=0 DOTNET_TieredPGO=1 PA_WARMUP_MS=5000 PA_RUNS=7 PA_RUN_MS=200 ~/.dotnet-x64/dotnet artifacts/bin/Release/net10.0/SharpEmu.Tools.MaterializationBench.dll
```

Validation command forms (build/test commands ran sequentially under the shared
machine lock; the device-required flag was confined to Metal suites):

```sh
~/.dotnet-x64/dotnet build SharpEmu.slnx -c Release -m:2 -p:UseSharedCompilation=false -nr:false
~/.dotnet-x64/dotnet build tools/SharpEmu.Tools.GpuProcessSmoke -r osx-x64 -c Release -m:2
~/.dotnet-x64/dotnet test tests/PROJECT -c Release -m:2 -p:UseSharedCompilation=false -nr:false --blame-hang-timeout 120s --logger trx
SHARPEMU_TEST_REQUIRE_DEVICE=1 ~/.dotnet-x64/dotnet test tests/SharpEmu.ShaderCompiler.Metal.Tests -c Release -m:2 --settings docs/performance/shader-serial.runsettings --blame-hang-timeout 120s
SHARPEMU_TEST_REQUIRE_DEVICE=1 ~/.dotnet-x64/dotnet test tests/SharpEmu.Gpu.Metal.Tests -c Release -m:2 --blame-hang-timeout 120s
reuse lint
python3 -m unittest discover -s tests/scripts -v
# Also run each scripts/macos/test-*.py with python3 and -v.
```

Raw logs, TRX, controls, source/assembly hashes and interrupted diagnostics remain
local audit evidence, excluded from the PR. The tables below summarize final
source-bound results; historical/interrupted runs are not counted as final passes.


The opt-in native-memory pass pins x64, .NET 10.0.3 and 16,384-byte kernel pages.
Set `SHARPEMU_TEST_REQUIRE_MAPPED_COPY_NATIVE`, `SHARPEMU_TEST_REQUIRE_MAPPED_PUBLICATION_NATIVE`,
`SHARPEMU_TEST_REQUIRE_MAPPED_COPY_PROTECTION_NATIVE`, `SHARPEMU_TEST_REQUIRE_BACKING_NATIVE`,
`SHARPEMU_TEST_REQUIRE_OWNED_NATIVE`, `SHARPEMU_TEST_REQUIRE_GUEST_MIRROR_PROCESS` and
`SHARPEMU_TEST_REQUIRE_PVM_MAPPED_COPY_NATIVE` to `1`, with
`SHARPEMU_TEST_EXPECT_ARCH=X64`, `SHARPEMU_TEST_EXPECT_FRAMEWORK='.NET 10.0.3'` and
`SHARPEMU_TEST_EXPECT_KERNEL_PAGE=16384`, then run Host.Memory and Core.Memory separately.
The final runs pass 517 and 7 tests respectively, with zero skips. All six guest
replica process variants complete, including full/split-size shm and chunked
backings, remapping and exact shared-byte checks. The earlier 120 s aborted run
and failed stack collection remain local evidence: no reproducible production
fault was localized, so this is a successful rerun, not a claimed stall fix.


| Final project run | Passed | Failed | Skipped |
|---|---:|---:|---:|
| Libs | 8,943 | 0 | 5 |
| ShaderCompiler | 2,167 | 0 | 1 |
| ShaderCompiler.Metal (device required, serial) | 3,296 | 0 | 0 |
| Gpu.Metal (device required) | 1,573 | 0 | 7 |
| Gpu.Metal.LeakTests (device required) | 19 | 0 | 0 |
| GpuRemote (including pinned x64/arm64 smoke) | 56 | 0 | 0 |
| GpuSplit (after fixture ordering correction) | 34 | 0 | 0 |
| Application | 80 | 0 | 0 |
| Fonts | 13 | 0 | 0 |
| SourceGenerators | 36 | 0 | 0 |
| Host.Memory (native enabled) | 517 | 0 | 0 |
| Core.Memory (native enabled) | 7 | 0 | 0 |
| **Total, one final run per project** | **16,741** | **0** | **13** |

The five Libs skips need unavailable native FFmpeg plugins; ShaderCompiler's skip
needs an unavailable captured fused-program fixture. Metal's seven existing skips
cover depth/stencil, shadow precision, HZB, line-width and packed-float cases; no
new skip was added. The default Host.Memory run separately passed 486 with six
opt-in skips; default Core.Memory ran no cases and skipped its native theory.
Those are not counted again in the total. Split's original 33-pass/1-fail run is
retained; the corrected full run and five independent repeats of the affected
blocking case pass without changing its 100 ms/10 s assertions.

The Release solution build completed with zero errors and 347 warnings (including
existing platform/volatile-field analyzer warnings). The pinned osx-x64 smoke build
completed with zero errors and warnings. The final project runs in the table exited zero;
the earlier aborted native run and initial Split failure are separate evidence.

Final policy comparison: A=admission disabled, B=enabled, in ABBA order.
Each median cell retains both process medians (ns/op); p95 cells show the range
of the two reported per-process p95 samples. These are tails of seven run samples,
not per-operation latency percentiles. B/op is measured on the benchmark thread;
GC is forced between samples, not controlled during a sample. No instrumented
phase profiling was enabled.

| Mode / workload | A medians ns | B medians ns | A / B p95 ranges ns | A / B bytes/op |
|---|---:|---:|---:|---:|
| tieroff: 3a uncached walk | 4,852.5 / 3,992.3 | 3,805.0 / 6,371.7 | 4,107.3-6,109.8 / 4,097.1-11,755.5 | 3,152.0 / 3,152.0 |
| tieroff: 3b warm hit | 244.0 / 254.9 | 657.7 / 337.0 | 308.7-311.8 / 404.5-822.9 | 64.0 / 64.0 |
| tieroff: 3c cold distinct | 17,693.7 / 17,421.2 | 12,938.9 / 11,394.1 | 20,498.7-31,232.0 / 14,505.8-14,541.2 | 8,264.0 / 6,379.9-6,382.6 |
| tieroff: 3d stale same address + writes | 30,318.0 / 19,327.3 | 15,891.0 / 11,926.6 | 20,554.5-49,633.8 / 13,029.8-18,005.8 | 8,392.2-8,392.3 / 7,032.6-7,035.4 |
| tieroff: 3f repeated pointers | 568.6 / 816.0 | 555.8 / 435.1 | 624.2-1,461.0 / 481.3-648.2 | 64.0 / 64.0 |
| tieroff: 3g moving ring + writes | 1,757.0 / 905.4 | 1,003.9 / 853.2 | 1,067.5-2,184.9 / 993.1-1,022.0 | 856.0 / 856.0 |
| tieroff: 3e write-only control | 243.3 / 223.3 | 184.0 / 165.2 | 255.5-353.6 / 187.3-203.4 | 688.0 / 688.0 |
| tiered: 3a uncached walk | 2,112.1 / 2,802.2 | 2,570.7 / 2,030.8 | 2,306.1-3,574.2 / 2,256.5-3,211.5 | 3,152.0 / 3,152.0 |
| tiered: 3b warm hit | 178.0 / 531.4 | 154.4 / 168.9 | 204.5-1,342.4 / 177.4-189.1 | 64.0 / 64.0 |
| tiered: 3c cold distinct | 9,735.4 / 40,677.9 | 4,209.6 / 4,339.9 | 11,797.7-61,512.0 / 4,308.5-4,370.5 | 8,160.0 / 6,378.9-6,384.2 |
| tiered: 3d stale same address + writes | 11,998.3 / 25,968.5 | 5,689.3 / 5,803.2 | 14,114.2-48,384.9 / 6,639.0-8,241.3 | 8,392.2 / 7,032.6-7,037.8 |
| tiered: 3f repeated pointers | 312.0 / 948.0 | 270.1 / 225.8 | 359.5-1,239.4 / 237.2-280.3 | 64.0 / 64.0 |
| tiered: 3g moving ring + writes | 672.0 / 3,886.5 | 700.4 / 507.5 | 726.6-15,023.1 / 585.1-784.2 | 856.0 / 856.0 |
| tiered: 3e write-only control | 141.6 / 687.8 | 125.1 / 113.3 | 153.5-1,320.9 / 116.5-135.4 | 688.0 / 688.0 |

The consistent result is lower cold/stale allocation. Warm hits and the repeated
pointer/ring workloads retain their allocations and steady-state hits. Host load
and the uncached/write-only controls drift substantially; the latency numbers
are qualified synthetic observations, not proof of a general timing win over
historical main/cde. Admission deliberately adds a second walk for recurring
contents during a saturated miss streak. Fresh-walk regression comparisons, not
benchmark hit counters alone, establish descriptor/readability/snapshot correctness.


REUSE 3.3 lint passes for all 2,787 files, with zero missing/invalid licenses or
read errors. Python/macOS script checks finish 52 passed and three existing
`task_for_pid` permission skips (passwordless sudo is unavailable); no assertion
was removed or new skip introduced. Staged and unstaged whitespace checks pass.

Final validation and benchmark source trees (Git tree IDs, so the code remains
identifiable independently of this report's later commit):

- `src`: `8ff3e09deb2a43995515ad2da4ea6c4f5abaff17`
- `tests`: `4bfe34bfe4a09626ce885f6a7405d0c90ce43076`
- `tools/SharpEmu.Tools.MaterializationBench`: `008a7ca713e6ca860699d7e908552e70dffd5038`

The final benchmark uses the same x64 .NET 10.0.3 host and executable in all eight
arms. Its SHA256 is `f9dfe18115e593cd9f7e30ff2b58572254572eb6ff9cbb4e2141111f963773bc`;
the compiler assembly SHA256 is `88c2a3f45f50435215a507fc418a93f46e5fa45ea6d7665aaee7cf7be1091b0b`.


The first additional admission-disabled ShaderCompiler run stalled after 170 passes
and aborted without assertion failures. Its native sample, unresponsive diagnostic
IPC and failed hang-dump collection are retained locally; only its verified owned
test host was terminated after SIGTERM failed to stop it. No production change is
claimed to fix that unlocalized intermittent test-host stall. A separate retry uses
the same assertions, detailed logging, the 120 s inactivity bound and an outer
180 s process-group bound. This is separate from the enabled final run above.

The detailed parallel retry also reached its outer 180 s bound. The subsequent
full admission-disabled run with `docs/performance/shader-serial.runsettings`
passed 2,167 / failed 0 / skipped 1 in 12.4 s. No assertions, explicit concurrency
inside tests or thresholds changed. Parallel-suite stall diagnosis remains open;
it is not counted as a resolved regression or substituted into the successful
project totals. The final incremental Release solution build passes with zero
warnings and errors on the exact source trees listed above.
