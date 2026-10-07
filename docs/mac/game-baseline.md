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

The recorded aborts identify Metal shader-translation gaps. Their corresponding
Vulkan translations are useful references, but these runs do not establish a
matched Vulkan/MoltenVK runtime comparison. Tracked in the Metal parity workstream.

## 2026-10-07, integrated native-stack checks

The rebuilt bundle embeds its source revision in `Contents/Resources/build-info.json`;
each run copies that file and records SharpEmu payload hashes. A checkout revision
alone does not establish the provenance of an older preexisting bundle.

- `20261007-192042-sarah-before-cpu-split`: 75 seconds, first outdoor scene
  after Enter input, peak RSS 655 MB; process stopped two seconds after SIGTERM.
  Binary source revision unknown (preexisting bundle). Guest shutdown reported
  forced unwind; this is not a clean guest exit.
- `20261007-193737-sarah-integrated-cpu-metal`: source `72f634a`, 90 seconds,
  correctly rendered startup, menu and new-game intro. Guest frame counter is
  nonzero; repeated host redraws are measured separately. Peak RSS 922 MB;
  SIGTERM stopped the process in two seconds, with the same guest forced unwind.
  Movement was not verified within this window; some input preceded the scene.
- `20261007-193925-ds-integrated-cpu-metal`: source `72f634a`, 120 seconds,
  reached the guest main loop without the former `VFmaMixloF16` abort but
  produced zero counted guest frames. Compute work failed with an incomplete
  layered-texture snapshot; the old drain logged and swallowed the failure.
  Peak RSS 2515 MB; SIGTERM did not stop it within ten seconds, requiring SIGKILL.
  This is a failed runtime check, not game progression or an FPS improvement.

These runs overlap compilation in other worktrees. They are compatibility and
failure checks, not quiet-host performance measurements.

## 2026-10-07, source-linked nine-title corpus at 9498713

The [integrated validation report](validation/integration-20261007.md) records
the final native bundle, required-device test results, cross-builds, game phases,
per-title failure causes and shutdown limits. Each run includes source metadata
and actual binary hashes.

Sarah now has screenshot-verified directional movement in the outdoor scene.
Void's title menu is readable instead of the older broad striping, but New Game
fails. Quake reaches its warning screen and Superliminal its title/loading room.
The remaining titles have no verified gameplay. Failures include a missing buffer
atomic instruction, skipped tiled/mip snapshots, an unsupported real texture
snapshot and an unrecovered Poppy guest write fault. These are explicit follow-up
items rather than whole-game success or matched performance comparisons.

## 2026-10-07, source-linked follow-up at b557990

Eight follow-up runs retain matching app payload hashes and separate
`game-followup-b557990/REPORT.md` evidence under the integrated validation artifacts.
The atomic abort no longer occurs in Silent Hill, Octopath or Oregon during
90-second runs. Oregon reaches welcome/legal UI, Octopath an animated loading
crest, and Silent Hill remains a black guest window. These do not establish
gameplay. Superliminal reaches menu labels against an almost white background
and later stalls; SIGTERM times out and requires SIGKILL. Its visual correctness
is unqualified. Void reaches a readable menu and then a near-black transition;
its former New Game FailFast does not recur within 90 seconds.

Sarah repeats verified Right/D and Left/A movement in a coherent scene during
150 seconds. Poppy still exits on SIGBUS. Demon's Souls still has zero guest
frames and aborts on a skipped tiled/mip texture alias. Quake is not rerun;
its unchanged 256 MiB snapshot remains above the 128 MiB per-texture cap.
Reported FPS periods describe these phases, not a gameplay performance comparison.
Host process stop and clean guest exit remain separate checks.
