<!--
Copyright (C) 2026 SharpEmu Emulator Project
SPDX-License-Identifier: GPL-2.0-or-later
-->

# macOS benchmarks

Results for the first release of SharpEmu-mac, on Metal V2 (the only macOS
backend). Each game was run once, alone on the machine, through
`scripts/macos/run-game.sh` (see [Testing on macOS](testing.md)). The
screenshots in the [README](../../README.md#games) come from the same runs.

## Results

| Game | Scene measured | Status | Avg fps (window) | 1% low | First frame | Peak memory |
| --- | --- | --- | ---: | ---: | ---: | ---: |
| Dreaming Sarah | Platformer level | Playable | 58.2 (40–100 s) | 35.4 | 7.2 s | 1.0 GB |
| void tRrLM(); //Void Terrarium++ | Title (14–38 s), intro cave (70–150 s) | Playable | 51.7 title, 58.1 gameplay | 23.3, 26.5 | 5.4 s | 2.6 GB |
| Quake | Level start (44–58 s), menu (62–150 s) | In-game, with issues | 20.8 level, 56.8 menu | 1.6, 31.7 | 9.9 s | 3.5 GB |
| The Oregon Trail | Independence town (188–203 s), hub and map (210–270 s) | Menus | 11.5 town, 58.1 map | 0.9, 35.2 | 18.5 s | 3.5 GB |
| Superliminal | Induction hallway (110–150 s) | In-game, slow | 16.4 | 13.1 | 14.5 s | 3.6 GB |
| Poppy Playtime Chapter 1 | Welcome Center lobby (180–260 s) | In-game, slow | 18.1 | 10.2 | 19.6 s | 4.2 GB |
| Grand Theft Auto V | Story menu (95–118 s), prologue load (130–178 s) | Crashes after the menu | 60.0 menu, 26.8 loading | 57.4, 6.9 | 14.8 s | 13.3 GB |
| SILENT HILL: The Short Message | Opening cutscene (500–600 s) | Cutscenes, about 1 fps | 0.9 | 0.17 | 37.0 s | 13.3 GB |
| Demon's Souls | In-world (450–580 s) | Loads, unplayable speed | 0.04 | 0.02 | 55.7 s | 12.3 GB |

## Known issues per game

- **Dreaming Sarah**: none seen.
- **Void Terrarium++**: a stall of about 3 s when gameplay starts. The intro cave
  is very dark, which makes it hard to judge rendering there.
- **Quake**: the level start runs at about 20 fps for its first 15 s. In a second
  run the game stopped presenting frames about 50 s after the level loaded,
  although the process stayed alive. Without a network connection the game shows
  a Bethesda.net connection error dialog.
- **The Oregon Trail**: the town screen runs at about 11 fps with stalls of up to
  7 s; the hub and map run at full speed.
- **Superliminal**: loading screens take about 60 s; the hallway runs at 16–17 fps.
- **Poppy Playtime Chapter 1**: the mural in the lobby looks blotchy and low
  resolution; loading screens run at 0–2 fps.
- **Grand Theft Auto V**: the process stops at about 178 s, while the prologue
  loads, with `[GPU][FATAL] The shader resources could not be materialized:
  stage=compute ... reason=IncompatibleImageCandidates`. No gameplay frame is
  reached.
- **SILENT HILL: The Short Message**: about 7 minutes of logo and warning screens
  at 0–2 fps before the opening cutscene, which then runs at about 1 fps.
- **Demon's Souls**: boot and the intro movie run at about 20 fps; the world
  reached the HUD only after about 580 s, at a fraction of a frame per second.
  The first run with a cold shader cache took 200 s to its first frame.

## Method

- **Machine**: Apple M4, 24 GB, macOS 27.2. The app is the `osx-x64` build of
  `SharpEmu.app` from `scripts/macos/build-app.sh`, running under Rosetta 2.
- **Output**: windowed, 1920x1080 guest display.
- **Frame rate**: `SHARPEMU_FRAME_TIMES` writes one line per guest flip. Average
  fps is `(flips - 1) / (last - first)` over the stated window, in seconds since
  launch, chosen as the steady part of the scene. The 1% low is `1000 / p99` of
  the flip-to-flip intervals in the same window.
- **First frame**: time from launch to the first guest flip. Shader and archive
  caches were warm, except where noted.
- **Peak memory**: the largest `phys_footprint` sampled once per second.
- **Conditions**: the machine had unrelated background load (one-minute load
  average 6–14, several GB of swap in use), so the frame rates and boot times are
  on the pessimistic side. Games ran strictly one at a time.
- **Inputs**: each run used a fixed key plan with a 150–600 s cap. The scene
  reached is limited by those plans and caps, not necessarily by the emulator.
