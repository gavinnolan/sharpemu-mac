# GOAL.md Phase 0 report: setup (2026-10-18)

Main at the end of Phase 0: `07c1e902c`. Test machine: Apple M4 (4P+6E), 24 GB, macOS 27.2. The app is still an osx-x64 build running under Rosetta.

## 1. Static-analysis toolchain (merged 0491ff00c)
- `tools/re/` (see its README): `fself2elf.py`, `nidmap.py`, `callsites.py`, `funcstarts.py`, Ghidra headless `ghidra/import.sh` + `ghidra/run.sh decompile|xrefs`, `amdgcn-disasm.sh` (default `gfx1013`: gfx1030 desyncs on real PS5 shaders), `ds-setup.sh` regenerates everything.
- Outputs (git-ignored) in `artifacts/re/`: `ds/eboot.elf`, `imports.tsv` (1102/1105 unique NIDs resolved, 99.9% of slots), `callsites.tsv` (108,461 references, 104,306 in code with known callers), `functions.txt` (133,738 eh_frame function starts), `ghidra/DS.gpr` (full auto-analysis, 1245 s; image base 0x800000000 = runtime addresses).
- PS5 system modules are encrypted SELFs and are not on disk; only the title's own fake-signed modules (`libc.prx`, `libSceNpCppWebApi.prx`) could be flattened.

## 2. Harness (merged 74416b541, 07c1e902c)
- 1080p is the default guest display again (revert of c0124c10e). `ds.sh` passes `SHARPEMU_DISPLAY_RESOLUTION=1080p` and `SHARPEMU_TITLE_DISPLAY_PATCH=mode` (the title's own display mode 2: 1920x1080 internal and output); both are overridable, and the other resolutions remain selectable.
- New emulator instrumentation: `SHARPEMU_FRAME_TIMES=<path>` (one timestamp per guest flip, zero cost when unset), and the `[METAL-PRESENT]` line now reports `gpu_alloc_mb` (MTLDevice.currentAllocatedSize, includes no-copy guest windows, so an upper bound), `gpu_images`, `gpu_image_cache_mb`.
- `scripts/macos/games/ds.sh <tag> [s] ingame|full`: `ingame` holds Cross from the first movie straight into the save's spawn (Outpost Passage tunnel, 1-1); `full` plays the boot logos and the 107 s intro movie unskipped, the title menu, CONTINUE, the stage load, then pause/inventory pages and a traversal. Segments are plan markers; `scripts/macos/ds-metrics.py` writes `metrics.json`/`metrics.md` (launch → first frame / title / HUD, FPS avg, 1%-low, p99 frame time, hitches > 50 ms, peak footprint/RSS/Metal, crash/hang, screenshots per marker). `SHARPEMU_GAME_SESSION_RUN=1` allows 2400 s sessions.
- Performance (60 FPS) mode: Settings > DISPLAY > Visual Mode already reads "Performance" in the golden save, so no input changes it.
- Pending in `full` (cannot be timed at today's ~1 fps in-world): pause/inventory pages, traversal, an unskipped in-engine cinematic, the Nexus (needs a death to the Vanguard from the tutorial, i.e. a NEW GAME route). These get calibrated as soon as Phase 1–3 make the world playable.

## 3. Reference video
`artifacts/reference/ds-RFNNktqjyEo/` (1080p60, 5.6 h full playthrough): `frames/all` every 2 s, `frames/aoi` every 1 s for 2:00–6:00, contact sheets, `scenes.md`, and `harness-scenes.md` mapping harness scenes to frames (t00548 = the save's Outpost Passage spawn, t00440–00530 = lore cinematic, t00580+ = tutorial traversal). Older frames from two other videos (title/menus/character creation) are in `artifacts/reference/ds/`.

## 4. Port survey (full reports: `port-survey-upstream.md`, `port-survey-kyty.md`)
Upstream tip 8bb85e09 is fully merged; the candidates are reverted, open, draft or closed PRs. Ranked list grouped by the phase that will act on it:

| Phase | Item | Source |
|---|---|---|
| 1 memory | Metal never records GPU-measured mip/LOD statistics; the interpreter reports mip 0 for every texture, so DS's streamer keeps full detail resident | upstream #1029/#1082 (Vulkan only) |
| 1 | Hold streamed textures only from the T# BASE_LEVEL (~40% fewer texture bytes in 1-1 on Kyty) | Kyty ec151901 |
| 1 | Age-based retirement of idle images/buffers (30-min no-growth criterion) | Kyty 6c6cac42 |
| 1 | Texture-pool layer views share one image (#731: 16 identical 320 MiB bindings in one DS dispatch) | Kyty c3a332ba, upstream #731 |
| 2 loading | Chunked libc string HLE (strlen/strcmp/strcasecmp ~2300 DS call sites read guest bytes one at a time) + allocation-free strlen | upstream #636/#633/#632 |
| 2 | In-place view-region splice; binary searches in MacHostMemory | upstream #958, #638 |
| 2 | Static shader scan/seed + Metal render-pipeline-state prewarm (~4.5 s of PSO creation per warm run) | upstream #1074 |
| 2 | Pooled read() buffers; file-open follow-ahead | upstream #1054; Kyty 4254fb56 |
| 3 perf/stability | CPU-flip ordering deadlock (DS boot-splash hang ~1 in 3 upstream) | upstream #1051 |
| 3 | Continuation stub per resume → cached per thread; scePthreadSelf early-out; nanosleep Sleep stub | upstream #1031 |
| 3 | memcpy ≥ 4 KiB takes the managed path (write-fault storm) | upstream #1055 |
| 3 | Fiber idle-publication race | upstream #1061 88f17d61c |
| 3 | CFG jump-table resolver (1395 refused DS sites) | upstream #926/#1097 |
| 3 | Interpolated-attribute cache + materialization bypass | upstream #1039 |
| 3 | Barrier-free consecutive compute dispatches, sequential write-fault window, memmove compare-before-write | Kyty b2c60bdc, 87280083, 454e9ec4 |
| 4 rendering | START_INST_LOC for indirect draws | Kyty 3b351932 |
| 4 | ColorControl.Mode == 0, draw-modifier unlock | upstream #1025 |
| 4 | HTile clear/rewrite fidelity, indirect selector bounds, flat/smooth PS slot sharing, stencil-replace equivalence, sceAgcCbNop, S# FILTER_MODE | upstream #1097, #1100, #926, #1079 |

Rejected: Kyty's `PackageCmdLineArgs.txt` overlay that turns off `doIndexBufferCulling` (it changes the title's own configuration for one title: a title-specific hack, against GOAL's "no shortcuts"). #1097's Atrac9 rework (GPL-2.0-only file, incompatible with our GPL-2.0-or-later, and the reported source of upstream's DS audio regression).

## 5. Baseline (run `artifacts/runs/20261018-095643-ds-p0-baseline2`, `full` mode, 1080p/1080p internal)

| metric | value |
|---|---|
| launch → first frame | 47.8 s |
| launch → title screen (movies unskipped) | 272.8 s |
| launch → controllable gameplay (`full`) | 429.4 s |
| launch → controllable gameplay (`ingame` fast path, run 20261018-090402-ds-cal3-ingame) | ~198 s |
| intro movie FPS avg / 1% low | 42.5 / 23.5 |
| title + menus FPS avg | 27–33 |
| stage load FPS avg (126 s) | 4.5 |
| in-world FPS avg / 1% low / p99 frame time | 1.0 / 0.5 / ~1.7 s (traversal: 0.3 fps, max frame 12.5 s) |
| peak physical footprint | 17.2 GB (in world, still rising 16.5 → 17.2 GB over 110 s) |
| peak RSS | 8.4 GB (RSS ≪ footprint: compressed/swapped) |
| peak Metal allocated | 23.0 GB (includes guest windows) |
| crashes / hangs | none |

The first attempt (`20261018-093723-ds-p0-baseline`) is kept as a second data point for boot and menus (title 339 s; 900 s snapshot: 8.7 GB untagged, 2.6 GB Metal-owned, 1.0 GB malloc-tiny, 364 MB IOAccelerator, 174 MB Rosetta JIT). It entered character creation because the title-menu probe landed on NEW GAME; fixed in 07c1e902c. Character creation draws no 3D character model (Phase 4 item).

## Decisions
- Internal 1080p comes from `SHARPEMU_TITLE_DISPLAY_PATCH=mode`, which selects the title's own HD display mode 2 (no 4K site rewritten). This is a test configuration the user built and the only way to get 1080p internal rendering without rewriting the title; it stays an opt-in loader option, defaulted on in `ds.sh` only.
- Phase 0 used 4 calibration runs + 2 baseline runs (the first baseline was invalidated by a harness bug).

## Open issues carried forward
- `full` route: in-world segments, cinematic and Nexus calibration (after Phase 1–3).
- HUD detection works (`hud` at 429 s); pause-menu inputs are dropped at 1 fps.
- Character-creation 3D model missing; in-world rendering looks close to the reference at the spawn.
