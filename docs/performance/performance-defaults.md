<!-- Copyright (C) 2026 SharpEmu Emulator Project
SPDX-License-Identifier: GPL-2.0-or-later -->

# Performance defaults

The maintainer requested default enablement based on correctness and positive
component benchmarks, without requiring an isolated Silent Hill FPS win for each
option. The following settings now turn on when their environment variable is
unset. Set the named variable to `0` to retain its reference path. No resolution,
VSync, guest quality, or unrelated application setting is changed.

| Default enabled | Environment variable | Evidence and limits |
| --- | --- | --- |
| Native visibility counts | `SHARPEMU_METAL_V2_NATIVE_OCCLUSION` | Same-binary first-room median 4.485 → 13.43 FPS; ordered/reset/sparse/overlap device tests. The visibility audit requires native queries disabled. |
| Packed format word runs | `SHARPEMU_MSL_PACKED_FORMAT_LOADS` | Same-binary shadow encoders 40.768 → 34.326 ms; overall title median 7.49 → 7.50 FPS. Aligned, unaligned and partial-element device comparisons retain fallback decoding. |
| Static private scratch slots | `SHARPEMU_MSL_STATIC_SCRATCH` | Eligible compute dispatch roughly 16.35 → 7.2 ms across historical diagnostic runs; not an isolated FPS comparison. Alias, wrap, masks and base-lifetime proofs gate transformation. |
| Scalar-only mesh buffers in constant space | `SHARPEMU_MSL_SCALAR_CONSTANT_BUFFERS` | Same-binary diagnostic encoder totals 114.585 → 112.686 ms and 7.50 → 7.97 FPS; host-confounded, no broad speedup claim. Bounds, mixed-pointer ABI, coherent/write/vector refusal tests. |
| Graphics binding reuse | `SHARPEMU_METAL_V2_GRAPHICS_BINDING_REUSE` | 31.66% of eligible native bindings skipped in one room window; changed textures, encoder restart and stage-key tests. No isolated FPS attribution. |
| Unchanged-frame presentation reuse | `SHARPEMU_METAL_V2_REUSE_PRESENTED_FRAME` | Avoids repeated host presentations of an unchanged guest frame; movie updates, output changes, restoration and animated overlays invalidate the memo. Guest FPS is distinct from host Hz. |
| Audio queue query leaf dispatch | `SHARPEMU_AUDIO_OUT2_QUEUE_LEAF` | Synthetic dispatch about 369 → 84.5 ns and 40 → 0 B/call; guest audio thread remains busy. Result/error and shutdown tests retain full-path behavior. |
| Asynchronous command-item reuse | `SHARPEMU_COMMAND_ITEM_REUSE` | Native two-thread median 389.965 → 354.5175 ns; allocation 80.0008 → 0.026 B/item. Bounded 64-slot return ring; synchronous and failed items retain original ownership. No accepted runtime FPS gain. |
| Queued CP writes | `SHARPEMU_METAL_V2_QUEUE_CP_WRITES` | Removes synchronous handoffs for eligible writes when command threading is explicitly enabled. Pending ranges, prefetch gates and CPU-read fallbacks preserve ordering; producer-thread device tests cover byte/qword/sparse/predication cases. |
| Bounded vertex-layout history | `SHARPEMU_VERTEX_LAYOUT_HISTORY` | Exact byte-verified reuse across alternating layouts; adaptive backoff for low reuse, 512 slots and 2 MiB additional payload budget. Component timing/allocation evidence and miss costs are in [the measurements](vertex-layout-history.md); no isolated game-FPS claim. |
| Caller resolution for short queues | `SHARPEMU_METAL_V2_CALLER_RESOLVE` | At most eight outstanding jobs; FIFO, gates, pause and ownership checks retained. Single-job ARM64 2,874 → 542 ns, x64 3,160 → 972 ns; ARM64 empty-program burst 32 costs about 27 ns/job. Same-payload room medians 16.49 enabled / 16.48 disabled, no isolated FPS gain. [Full measurements](caller-resolution.md). |

The existing negative label page filter, exact materialization comparison, dense
register lookup, mip-layout construction and target memo fixes remain enabled.
The later [dense register-range experiment](dense-register-range-experiment.md)
was rejected: isolated warm-copy gains did not carry across representative
interpreter workloads under normal tiering. It adds no new default or switch.
The three promoted shader properties also default on in explicitly constructed
`Gen5MslCodegenOptions`; reference tests set those properties to `false`.

## Retained opt-in experiments

| Setting | Reason |
| --- | --- |
| `SHARPEMU_LOCAL_DRAW_REUSE` | Removes 184 B/draw within its bounded cache, but component timings are mixed. Enabled/disabled/enabled room medians were 5.97 / 7.98 / 6.485 FPS under differing paging pressure. This does not isolate a regression, but it also does not establish a speed benefit. Kept opt-in pending better timing evidence; [full measurements and fallback](local-draw-reuse.md). |
| `SHARPEMU_REGISTER_IDENTICAL_SCALARS` | Always-changing packets regress: native 101.40 → 103.08 ns; Rosetta 164.85 → 178.94 ns. Only 0.805% of eligible packets reused in the observed room. Identical-packet synthetic gains do not outweigh this demonstrated mixed-workload cost. |
| `SHARPEMU_MSL_GEOMETRY_CAPACITY` | Volume pass 39.888 → 57.027 ms, changed image, no accepted broad correctness/performance result. |
| `SHARPEMU_MSL_MESH_LDS_FORWARDING` | Same-binary shadow time 34.238 → 36.422 ms; title median 7.97 → 7.47 FPS. |
| `SHARPEMU_MSL_MESH_HOT_LDS` | Same-binary total encoder time 108.483 → 109.451 ms; no benefit established. |
| `SHARPEMU_MSL_MESH_EXIT_JOIN`, `SHARPEMU_MSL_UNOBSERVED_ALU` | Correctness coverage exists, but isolated performance evidence is inconclusive and their combined run had an unresolved mapping failure. |
| Command thread / command-thread QoS | Earlier command-thread overlap runs were slower than serial command-stream interpretation. Queued writes and recycling apply when command threading is requested; this change does not enable it globally. |
| Concurrent tiling and floating-point contraction | Representative writeback regressions or numerical differences remain; the original audit did not accept them. |

Background program resolution is a separate existing default: it remains enabled
unless `SHARPEMU_METAL_V2_PARALLEL_RESOLVE=0`. It can run while command-stream
interpretation stays on the GPU worker. The earlier table incorrectly grouped
it with opt-in command threading; no runtime policy is changed by this correction.

Diagnostics, detailed tracing, scripted-only input and visibility auditing stay
off. Portable-buffer policy, device LDS placement threshold and asynchronous
library compilation policy are unchanged. These defaults do not establish
30 FPS or full-game compatibility. See [the runtime report](native-occlusion-runtime.md)
for source versions, sample windows, host-pressure limitations and failed runs.


## Validation of the new defaults

Fresh serial Release runs with all `SHARPEMU_*` overrides removed:

| Suite | Passed | Failed | Existing skips |
| --- | ---: | ---: | ---: |
| ShaderCompiler.Metal, required device | 3,589 | 0 | 0 |
| Gpu.Metal, required device, serial command stream | 1,635 | 0 | 7 |
| Libs, x64 | 9,052 | 0 | 5 |
| Total | 14,276 | 0 | 12 |

Additional focused runs passed **56/56** with command threading and parallel
resolution enabled, then **56/56 Metal, 114/114 Metal shader and 29/29 Libs**
with all nine promoted switches set to `0`. These 255 executions are additional
configuration checks, not additional distinct tests. The query-default scene
checks actual sample counts in both modes; scalar-buffer tests check default
translation, explicit disablement and unchanged raw-read refusal semantics.
The synthetic-query audit/fallback fixture explicitly selects conservative mode.

The first default-promotion shader run passed 3,587 cases and failed one stale
assertion that constructor defaults must disable scalar constant buffers. Its
reference case now explicitly disables the feature; the subsequent full run
above passed. No numerical or pixel oracle was relaxed. REUSE passed
2,864/2,864 files, and whitespace checks passed. Private command/TRX records:
`pr23-defaults-final-validation.json`, `pr23-defaults-final-results`,
`pr23-default-controls.json` and `pr23-default-controls-results` in the runtime
diagnostic directory. The earlier ShaderCompiler core suite passed 2,192/0/1
before this defaults-only follow-up and was not rerun here.

This validation establishes tested configuration paths, not a first-room FPS
measurement of the new default combination. A clean `fb717072a` x64 CLI/native
ARM64 GPU-host runtime passed both publishes and verification of all 471 payload
hashes. Its bounded 900-second Silent Hill diagnostic reached the opening
cutscene, but did not reach the stationary comparison room. Cold pipeline
compilation and long zero-frame intervals remain visible; this run does not
establish a gain or regression against the earlier approximately 14-FPS room.
It ended at the harness deadline with process exit 0. Peak main-process physical
footprint was 9,460 MiB (RSS 2,095 MiB); these are not combined physical-memory
figures for the two processes. Prewarming was disabled for this diagnostic,
while all nine promoted switches were unset. Runtime investigation toward
30 FPS continues separately.


A subsequent clean `eabcd9f65` build reached the stationary shelf/lamp room with
all nine switches unset. Its detailed-profile window (375–410 seconds) reported
**11.95 FPS median, 7.99–14.96 range, 17 reports**. This is not a matched isolated
comparison with the earlier 14-FPS build. See the v20 section of the runtime
report for CPU/GPU attribution and measurement limits. Background program
resolution was enabled, while command threading remained disabled.

A same-payload control with detailed render/pass profiling disabled then measured
**15.48 FPS median (13.96–16.97, 17 reports)** in the same stationary room.
Sequential cache warmth and host-load changes prevent assigning the entire
difference to profiling. This confirms operation of the default combination in
that scene; it still does not establish 30 FPS or isolate each setting's gain.


The subsequent same-seed control with background program resolution disabled
measured 10.99 FPS (6.49–14.47, 17 reports); its existing enabled default remains.
A completion-read follow-up uses the existing serial-hint switch to avoid native
queries once all submitted work is known complete. The guarded getter measured
3.020 → 1.319 ns on ARM64 and 10.002 → 2.297 ns on x64 for completed work.
Pending work retains live queries: ARM64 measured 3.241 → 2.981 ns, x64
5.864 → 6.209 ns. Both paths allocate zero bytes; the runtime report documents its
device ordering tests and the absence of an isolated game-FPS result.

Later v21 controls measured 8.97 FPS with the diagnostic GC budget unset and
7.97 FPS with the application's existing 64 MiB budget. Returning to the
unchanged v20 binary measured 8.47 FPS under the later host conditions. These
captures do not reproduce the earlier 15.48 FPS result or isolate a code
regression; all are retained in the runtime report. The private diagnostic
launcher had omitted the GC environment setting already supplied by the bundle
and standard game launcher. The parity control reduced traced GC pause count
and total pause time, but established no FPS gain. The nine promoted defaults
remain enabled; the 30-FPS objective remains open.

The later v23 capture used all ten promoted defaults, including vertex history.
It measured 1.0 FPS median (0.0–4.49, 18 reports) during heavy system paging;
there was no matched reference-path run, so this does not isolate a regression
or establish a gain. History reuse occurred in the game, while occlusion dumps
accounted for 83.6% of the sampled draw-queue drains. The runtime report retains
the exact source, windows, counters and limitations. The defaults retain their
component/test evidence; the 30-FPS objective remains open.


The v24 caller-resolution follow-up adds the eleventh default based on component
wins and correctness checks. Its same-payload Silent Hill medians were 16.49 FPS
enabled and 16.48 disabled; this establishes no game-FPS gain. The full component
table, larger-batch cost, run windows and paging limitations are retained in
[the caller-resolution report](caller-resolution.md).
