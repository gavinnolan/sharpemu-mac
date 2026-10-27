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

The existing negative label page filter, exact materialization comparison, dense
register lookup, mip-layout construction and target memo fixes remain enabled.
The three promoted shader properties also default on in explicitly constructed
`Gen5MslCodegenOptions`; reference tests set those properties to `false`.

## Retained opt-in experiments

| Setting | Reason |
| --- | --- |
| `SHARPEMU_REGISTER_IDENTICAL_SCALARS` | Always-changing packets regress: native 101.40 → 103.08 ns; Rosetta 164.85 → 178.94 ns. Only 0.805% of eligible packets reused in the observed room. Identical-packet synthetic gains do not outweigh this demonstrated mixed-workload cost. |
| `SHARPEMU_MSL_GEOMETRY_CAPACITY` | Volume pass 39.888 → 57.027 ms, changed image, no accepted broad correctness/performance result. |
| `SHARPEMU_MSL_MESH_LDS_FORWARDING` | Same-binary shadow time 34.238 → 36.422 ms; title median 7.97 → 7.47 FPS. |
| `SHARPEMU_MSL_MESH_HOT_LDS` | Same-binary total encoder time 108.483 → 109.451 ms; no benefit established. |
| `SHARPEMU_MSL_MESH_EXIT_JOIN`, `SHARPEMU_MSL_UNOBSERVED_ALU` | Correctness coverage exists, but isolated performance evidence is inconclusive and their combined run had an unresolved mapping failure. |
| Command thread / parallel resolution / command-thread QoS | Earlier overlap runs were slower than serial resolution. Queued writes and recycling apply when overlap is requested; this change does not enable overlap globally. |
| Concurrent tiling and floating-point contraction | Representative writeback regressions or numerical differences remain; the original audit did not accept them. |

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
| Gpu.Metal, required device, default synchronous mode | 1,635 | 0 | 7 |
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
