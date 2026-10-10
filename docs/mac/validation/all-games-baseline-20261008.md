# Native macOS game baseline — 2026-10-08

All nine installed titles were exercised sequentially through `scripts/macos/run-game.sh` using the same signed, clean-source Metal/AppKit bundle built from `8c19cd79b0be563ea8923df92b15ec2b6fce0cd7`. Root checkout `defa2b0` adds documentation only. Host: Apple M4, 24 GB, macOS 27.2, x64 .NET 10.0.3 under Rosetta. No Vulkan/MoltenVK run is included.

This is a compatibility and first-blocker baseline, **not a 60 FPS acceptance result**. Source builds ran concurrently with eight runs. Dreaming Sarah's preceding input/burst run used the same binary with compilers paused. Runs have a 150-second limit and 12 GB RSS ceiling. Inputs start at fixed delays, so elapsed process lifetime is not a fast-loading measurement. Movie cadence and repeated host redraws are not gameplay FPS.

| Title | Observed result | First actionable blocker / qualification limit | Peak RSS MiB |
|---|---|---|---:|
| Demon's Souls | Aborted after 47 s; no guest frame | Storage texture 512×256, fmt 4/7, tile 9, selected/base mip 1 in a nine-mip resource rejected. The older alias failure is downstream. | 2490 |
| Dreaming Sarah | Alive for 150 s; movement, airborne jump and landing visually checked | Gameplay reached. No controlled 60 FPS or loading acceptance. Host stop works; guest `pthread_join` teardown error remains. | 1208 |
| OCTOPATH TRAVELER II | Aborted after 27 s | Singleton array storage texture, type 13, 1×1, fmt 10/0, tile 27 rejected. | 2093 |
| Poppy Playtime Chapter 1 | SIGBUS after 25 s | Unrecovered write fault at RIP `0x80135FB0B`, streaming vector-store instruction. Native fault ownership/order is not established by this log. | 1684 |
| Quake | Intro movie rendered; aborted after 67 s | 8192×8192 fmt 4/7 tile 24 texture requires 256 MiB snapshot, exceeding the 128 MiB cap. Tiling topology and filtered depth comparison also need independent validation. | 2647 |
| SILENT HILL The Short Message | Aborted after 19 s | Same singleton array storage shape as Octopath. Correct native array texture/consumer required. | 1753 |
| Superliminal | Alive for 150 s; final screenshot remains on loading screen | Command stream fails on sampled tiled 3D texture at `0x7015288000`. Subsequent `sceAgcSuspendPoint` cancellation polling is a symptom, not an API to throttle. | 4237 |
| The Oregon Trail | Aborted after 19 s | Same singleton array storage shape as Octopath/Silent Hill. | 1965 |
| void tRrLM(); //Void Terrarium | Alive for 150 s; final screenshot shows a dim scene with a creature | Scene/intro visible; responsive gameplay and visual correctness unverified. Logged guest FPS varies around 0–12.5 under concurrent host load. | 2240 |

The three surviving runs stopped with TERM, without KILL. After the sweep there was no `SharpEmu` process and the shared game lock was released. Crashing titles did not establish gameplay. A visible scene alone does not establish correct shaders, depth, texture layout, audio, input or frame pacing.

## Evidence

Ignored, machine-local evidence is retained under `artifacts/validation/all-games-20261008/`:

- `inventory.json`: installed-title inventory; the Oregon decrypted executable is another executable for the same title.
- `runs.json`: exact run directories, bundle provenance, results and screenshot paths for all nine titles.
- `run-file-manifest.json`: SHA-256 hashes for 117 retained run files. Verify before relying on mutable working artifact paths.
- Each run has `game.log`, `result.txt`, `run.env`, `binary-provenance.json` and copied `build-info.json`; surviving/windowed runs also have screenshots and input records.

## Delivery order and acceptance

1. **Correctness:** selected storage mip placement and ownership; native singleton array storage; qualified 3D sampling; Quake large-image materialization with correct topology/filtered comparison; Poppy fault recovery. Preserve raw descriptor evidence and fail explicitly when unsupported. Raising caps or suppressing errors is not qualification.
2. **Reach gameplay:** title-specific input scripts, repeatable save/scene, visible input response, independent reference screenshots. Track menu, movie, loading and gameplay separately.
3. **Measure:** pause competing builds; repeat cold and warm launch/load measurements; collect unique guest presentation commit intervals and CPU producer/render/GPU timings separately. Host redraw frequency is never guest FPS. Commit timing is not display scanout or GPU execution time.
4. **Optimize:** target measured allocation/copy/interop/JIT/compilation/queue costs. Compare the same scene/settings/thermal conditions before and after. At 60 FPS, a frame has 16.67 ms; use p50/p95/p99 intervals, stutters and load duration, not just an average counter. Report hardware and title caps. Some guest content intentionally runs below 60 FPS.
5. **Regressions:** rerun all nine titles after each compatibility batch, followed by longer gameplay and visual comparisons. Windows/Linux compile checks and managed fixtures do not qualify their native runtimes or games.

No title has yet passed the complete requested combination of correct rendering, gameplay, 60+ FPS and fast loading.
