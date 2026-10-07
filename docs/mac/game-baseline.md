# macOS game baseline

Runs use `scripts/macos/run-game.sh` (osx-x64 under Rosetta 2, Apple M4, macOS 27.2,
native Metal/AppKit/CoreAudio stack). Run directories are under `artifacts/runs/` and are
not committed.

## 2026-10-07, main at e9a3bee (before Metal parity work)

| Game | Length | Result | Notes |
| --- | --- | --- | --- |
| Quake | 90 s | abort at 69 s | Warning screen renders; level load: `ImageSample: image dimension Dim2DArray is not supported on Metal` (pixel) |
| Dreaming Sarah | 90 s | alive, graceful stop | Title screen renders correctly. `pthread_join: guest execution stopped while joining thread`, repeating import loop forced unwind |
| Demon's Souls | 120 s | abort at 60-77 s | `VFmaMixloF16: unsupported vector opcode` (compute). One earlier run froze on the splash with no log output after presenter start and ignored SIGTERM (intermittent, not yet diagnosed) |
| Silent Hill: The Short Message | 120 s | abort at 19 s | `ImageStore: image dimension Dim2DArray is not supported on Metal` (compute) |
| Superliminal | 90 s | abort at 84 s | `ImageSample: image dimension Dim3D is not supported on Metal` (pixel) |
| Octopath Traveler II | 90 s | abort at 41 s | `ImageStore: Dim2DArray` (compute) |
| Poppy Playtime Ch. 1 | 90 s | abort at 46 s | `ImageStore: Dim3D` (compute) |
| The Oregon Trail | 90 s | abort at 22 s | `ImageStore: Dim2DArray` (compute) |
| Void Terrarium | 90 s | alive, graceful stop | Title screen is horizontal-stripe garbage (likely detile/format) |

All titles show `FPS 0,0` in the window title on Metal (counter not fed).

Every abort is a Metal shader-translation gap that the Vulkan/MoltenVK path handled; with no
fallback these are now fatal. Tracked in the Metal parity workstream.
