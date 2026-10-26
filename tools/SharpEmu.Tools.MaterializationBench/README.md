<!-- Copyright (C) 2026 SharpEmu Emulator Project
SPDX-License-Identifier: GPL-2.0-or-later -->

# Synthetic materialization benchmark

Build with `dotnet build tools/SharpEmu.Tools.MaterializationBench -c Release` and run
`dotnet artifacts/bin/Release/net10.0/SharpEmu.Tools.MaterializationBench.dll`.
Use the same SDK, process architecture and executable/dependencies for every arm.
On Apple Silicon the audit used an x64 .NET host under Rosetta. No GPU or captured
shader/game data is needed.

The harness warms each workload for 1,500 ms, calibrates batches and reports five
150 ms run samples as median/p95 ns/op and managed bytes/op. `PA_RUNS` and
`PA_RUN_MS` override those sampling settings. `PA_WARMUP_MS=5000` extends warmup;
`PA_SCENARIOS=3c` selects an individual workload for closer alternating arms. `DOTNET_TieredCompilation=0` gives a
separate diagnostic arm; normal tiered/PGO results must be measured separately.
`SHARPEMU_PROFILE_MATERIALIZATION=1` adds phase/read counters; do not compare those
timings against an uninstrumented arm.

| ID | Workload |
|---|---|
| 3a | Uncached full walk: 8 images, 2 samplers, 4 buffers |
| 3b | Repeated hit with fresh resident-byte validation |
| 3c | Distinct descriptors/addresses cycling 4,096 tables through an 8-entry generation |
| 3d | Changed descriptors at the same address; includes table writes |
| 3f | Eight repeated table pointers with equal descriptor contents |
| 3g | 32 materials moved through a large ring with unchanged descriptor contents; includes writes |
| 3e | Table-write control |

For source comparisons, build each compiler revision into a separate build profile.
Run the same benchmark executable against each compiler assembly, with the common
HLE/logging dependencies fixed. Record the source revisions and assembly hashes.
Alternate arms in forward and reverse order, retain every run and report ranges
as well as central values. Monitor the full-walk and table-write controls for host
load drift. Cache counters must distinguish actual hits from rebuilds; correctness
and invalidation remain the responsibility of the regression suites.

These are synthetic per-operation timings, not game frame-time projections.
