# GOAL.md Phase 1 report: memory usage (DRAFT, in progress)

Status: STOPPED by the user on 2026-10-18 (game launches drive the machine OOM). Batch 3 merged (main 630fc822b) but never measured; Phase 1 exit criteria not met.

## Exit criteria (GOAL.md)
Peak and steady-state footprint substantially lower; no growth over a 30-minute session; no memory-pressure swapping/compression during gameplay; before/after numbers and the effect on boot time and FPS.

## Measurement method
- `memstat` (scripts/macos/memstat.c): walks the game's VM map and counts each VM object once, so the guest RAM object (one shm object mapped thousands of times) is not counted per mapping the way `footprint`/phys_footprint does (~5 GB overstatement measured in Phase 1 analysis). Categories: guest_shm, anon_untagged (≈ .NET heap), graphics_iokit (shared Metal buffers), rosetta, malloc, mapped files. Private Metal textures are not in the VM map; the image cache reports their `allocatedSize`.
- `--zero-scan`: counts all-zero resident guest pages (Metal no-copy buffers fault in every page they cover).
- `[MEM]` telemetry lines (SHARPEMU_LOG_MEMORY=1), `[ALLOC-PROFILE]` (managed allocation by type/thread), dotnet-gcdump.
- Machine state is recorded at launch (`system-at-launch.txt`); idle .NET build servers are stopped first (161 idle MSBuild nodes held ~8 GB on 2026-10-18 and skewed run p1-b2).

## Results so far

| metric | Phase 0 baseline2 (full) | p1-heapdiag (before batch 2) | p1-b2 (batch 2) |
|---|---|---|---|
| peak phys_footprint | 17.2 GB | 16.2 GB | 17.2 GB |
| memstat unique (resident + compressed) | n/a | n/a | 12.7 GB |
| Metal guest bytes named per command buffer | 8.2 GB avg / 9.5 max | — | 1.8 GB avg / 3.0 max |
| image cache | 4.25-4.45 GB | — | 2.3-3.1 GB |
| .NET live heap | 1.9 GB | 2.1 GB | 2.3 GB (≈0.7 GB MSL text) |
| in-world FPS | 1.0 | 1.1 | 1.8 |
| launch → gameplay | 198 s (ingame) | 190 s | 451 s (cold shader cache) |

## Changes (batches 1-3)
- Batch 1: Objective-C autorelease pools on every Metal thread (leak), direct-memory budget per PS5 retail (12 GiB direct + 448 MiB flexible), memory instrumentation.
- Batch 2: granule guest windows (32 MiB, non-overlapping, one GPU address per guest byte per command buffer — two no-copy buffers over the same pages are not coherent within one command buffer on Apple GPUs), host-driven image budget + age retirement, GPU-measured LOD statistics, BASE_LEVEL-sized sampled textures, managed allocation cuts.
- Batch 3: LOD stats as a runtime per-slot check (no shader variants), MSL text released after library build, scattered guest ranges read through guest views (no staging copies; no 64 MiB truncation), per-level BASE_LEVEL shrink + WARN floor at the in-use set + budget from system available memory, per-draw allocation round 2.

## Pending
- 30-minute in-world session with movement (SHARPEMU_DS_INWORLD_MOVE=1), zero scans and gcdumps; blocked 2026-10-18 ~17:20 by a wedged Rosetta (x86_64 processes hang unkillable after the T3 server restart killed a running game; needs a reboot or root to restart oahd).
