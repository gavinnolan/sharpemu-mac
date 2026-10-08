# Game entry takes priority

The current milestone is to reach responsive, controllable gameplay in every
title in `/Volumes/Untitled/games`. Performance, loading-time optimization and
visual polish are deferred. Renderer changes remain in scope when they remove
a crash or prevent progression. Intro playback, an automatic demo and process
survival do not satisfy this milestone.

## Quake: texture-budget crash cleared, gameplay not accepted

Isolated source `decd4910d3f2ac482d60de8ab9e41ca3d5fe7929`, based on main
`662ac366`, admits a complete 256 MiB single-mip extended 2D snapshot. Arrays,
volumes and mip chains retain the 128 MiB limit. Both logical and padded
physical byte counts are checked before the source read. No content,
ownership, mapping-currentness or completion checks are removed.

The focused build succeeded. Both actual ARM64 and Rosetta/x64 host runtimes
passed the same 210 managed cases with zero skips. These are budget and
read-attempt controls, not proof of texture layout or scene correctness.
The native-only osx-x64 app was signed and all 239 bundle file hashes matched
before and after the replay. Guest CPU execution remains x64/Rosetta.

Actual run:
`.claude/worktrees/root-quake-snapshot-budget/artifacts/runs/20261008-200941-quake-game-entry-decd4910`.
Qualification receipts: `artifacts/validation/quake-entry-decd4910`.

The game remained alive at 150 seconds, peak RSS 4422 MiB. The previous
8192-by-8192 R32Float snapshot-budget fatal did not recur. The engine loaded
`demo1.dem` and The Necropolis and produced a few level frames. The inspected
120-second screenshot is visibly corrupted. Scheduled menu and movement
inputs do not establish player control; automatic demo loading is not gameplay
acceptance. This candidate has not been integrated into main.

Shutdown did not finish within the harness's ten-second TERM grace period,
so the owned game required KILL. The shutdown log also records command-stream
synchronization failure. The wrapper returned zero and retained stable bundle
hashes, but that is not a clean game-shutdown result. No owned process group
or shared game lock remained. An unrelated emulator process was present and
was left untouched; this run makes no performance claim.

## Next entry checks

- Superliminal: qualify isolated `c5beabf1`, which separates selected color
  mip 0 from the disjoint storage view that currently causes startup failure,
  then replay the signed game with the opt-in enabled.
- Octopath: isolated `3d49ba8e` passed managed and native publication controls,
  but its real game replay still failed before the first canonical producer.
  Diagnostic successor `ddba460f` records the exact other image whose original
  provenance is missing. Identify its actual creation route before changing
  admission.
- Demon’s Souls and Silent Hill: replay the qualified original-owner path
  against the observed global mapping-epoch rejection.
- Poppy Playtime and Oregon Trail: resolve the previously observed CPU-store
  fault and sampled-image ownership rejection respectively.
- Void Terrarium: drive the game after loading and prove movement/progression;
  allow a longer bounded entry run when intro/loading consumes the short run.

Dreaming Sarah is the only title with prior verified directional control.
No additional title is accepted by this checkpoint. The original rendering,
loading and sustained-frame-rate goal remains outstanding after entry works.

## Octopath: actual missing owner identified

Diagnostic source `ddba460f249dc4e1cd6ff60707940271f458b0a2` passed the same
52 focused managed cases on ARM64 and Rosetta/x64, then was published as a
signed native-only app. Actual replay
`artifacts/runs/20261008-201706-octo-canonical-cohort-offender-ddba460f`
exited 134 after 34 seconds, peak RSS 2231 MiB. It does not qualify gameplay.

The bounded original-cache diagnostic identifies the other image at
`0x8030BE0000`: initialized, GPU-written, content version 2, 1×1×1,
native type 7, native format 73. Its original creation was storage dispatch
24, queue `dcb.graphics`, submission 2, mode 27, guest format 10/4, type 10;
raw descriptor `[8030BE00,03C00000,00000000,A1B00FAC,00000000,00700000,00000000,00000000]`.
The failure branch is `origin-missing`, not a demonstrated mutation of the
target's original protected token. The next fix must capture original mapping
and ownership at this singleton volume's creation; it must not manufacture
provenance at lookup or ignore this GPU writer.
