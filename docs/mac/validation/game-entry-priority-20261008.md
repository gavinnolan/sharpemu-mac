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

## Current entry blockers after the latest signed replays

The latest observations below supersede the older next-step list. Dreaming
Sarah remains the only title with prior verified directional control; none of
these runs accepts an additional title.

- **Octopath and Silent Hill:** successor `1610948d` captures original display
  ownership at the initial copy and passed 150 managed cases on each host
  architecture. Both signed game replays passed the former display-provenance
  refusal. Octopath retained its first native storage producer at `0x80302C0000`,
  then exited 134 after 35 seconds because provider publication preparation
  was rejected. Silent Hill completed two native-producer-to-canonical
  publications before its parser rejected an original selected storage seed's
  first-use receipt. It remained alive for 60 seconds with a black capture and
  zero guest frames. Bounded provider diagnostics are being qualified to
  identify the actual rejection before changing admission rules.
- **Demon's Souls:** the same signed `9e3adfcb` bundle remained alive for 60
  seconds but had zero guest frames and a black capture. Shutdown exposed a
  prior worker failure: selected storage mip copy changed during all three
  attempts at `0x7092C80000`. Process survival does not establish progression.
- **Superliminal:** `f4ebd249` passed the original six-face CPU depth-cube
  snapshot and real translated comparison controls on both host architectures.
  Its 150-second game replay still failed because the sampled cube overlaps an
  existing native depth producer. The successor must retain each original
  selected depth face and its ordered native producer; it cannot substitute a
  CPU copy over that producer. Cold, previously untracked faces must retain
  independent original content tokens. The actual captured cube descriptor is
  1024-by-1024, with 2 MiB per D16 face. The earlier native successor's finite
  256-by-256 qualifier rejected that descriptor. Corrected source `82186483`
  has passed semantic review; native qualification and game replay remain pending.
- **Void Terrarium:** the 600-second entry run and a separate 300-second shader
  diagnostic run did not verify control. The actual rejected shader contains
  LDS operations, six barriers, GS allocation and primitive export target 20.
  It needs native mesh translation. Transport candidate `d2639278` passed real
  mesh rasterization on both host runtimes, including both triangles and every
  pixel of the complete test target. Guest translation and gameplay remain pending.
- **Quake:** the bounded 256 MiB texture candidate passed the earlier budget
  blocker. A further 300-second run used the correct Options mapping (Tab) and
  movement inputs but did not establish player control; captures remain corrupt.
  Shutdown required the harness's owned KILL after its TERM grace period. An
  initial wrapper cleanup assertion saw a brief owned zombie; a separate fresh
  check found the group absent. The original rejection is retained.
- **Poppy Playtime:** actual ordinary-provider testing exposed Rosetta's
  4 KiB placement inside the measured 16 KiB kernel page. Final candidate
  `d9d1dc8e` retains a doubled original Mach allocation and protects a contained
  aligned page. Its 24 managed controls passed on each host runtime, and three
  fresh x64 native workers passed actual permission restore, SFENCE, lifetime
  drain, full 16 KiB byte checks and real 128/129 receipt capacity. Genuine
  guest signal continuation and gameplay remain pending; these constructed
  ordinary frame inputs do not qualify signal activation.
- **Oregon Trail:** sampled-image ownership remains an entry blocker.

Current immutable evidence:
`artifacts/validation/all-games-20261008/octopath-volume-publication-watch-9e3adfcb-qualified`,
`artifacts/validation/all-games-20261008/display-original-1610948d-prepared`,
`artifacts/validation/all-games-20261008/depth-cube-entry-f4ebd249-qualified`,
`artifacts/validation/void-skipped-shader-400e5c64`,
`artifacts/validation/native-mesh-entry-d2639278`,
`artifacts/validation/quake-entry-decd4910`, and
`artifacts/validation/all-games-20261008/poppy-native-permission-receipt-d9d1dc8e-qualification`.

## Earlier entry checks (superseded by the observations above)

- Superliminal: qualify isolated `aa38ff5a`, which separates selected color
  mip 0 from the disjoint storage view that currently causes startup failure,
  then replay the signed game with the opt-in enabled. The successor guards
  complete crossing-start image spans before seed consumption/allocation/read;
  its 18 focused managed cases passed on both host architectures. Actual
  game entry remains unqualified.
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

## Superliminal: next depth-comparison entry blocker

Signed native-only `aa38ff5a5f18e46ba6e0a8e40feb97667715a7b4` passed its
18 managed cases per host architecture and retained all 239 bundle hashes
across the actual game replay. Run
`.claude/worktrees/agent-metal-color-physical-view/artifacts/runs/20261008-202353-superl-color-view-entry-aa38ff5a`
exited 134 after 71 seconds, peak RSS 3924 MiB. The inspected 55-second
screenshot shows the title and loading indicator over a corridor scene;
controllable gameplay was not reached.

The first fatal is now depth-comparison admission at graphics sequence 2540,
submission 84: guest format 2/0, type 11 is rejected by the current
Float-R32/Unorm-R16 2D/array-only gate. The color/storage conflict did not
appear before that failure, but execution of the revised color-view path
has not been established. Native depth-cube support is the next entry target;
this observation does not authorize treating a cube as an ordinary 2D image.

The wrapper returned zero despite the game's exit 134. The owned game exited
without KILL and no owned process group or shared lock remained. No new title
is accepted by this replay.
