<!--
Copyright (C) 2026 SharpEmu Emulator Project
SPDX-License-Identifier: GPL-2.0-or-later
-->

# Testing on macOS

There are three layers: the .NET test projects, Python tests for the build and
harness scripts, and a game harness that runs real games for a bounded time and
records evidence. Only the first two run in CI. The game harness needs game dumps
that are not in the repository.

## .NET tests

```sh
dotnet test SharpEmu.slnx -c Release
```

| Project | Covers |
| --- | --- |
| `tests/SharpEmu.Libs.Tests` | HLE and shared GPU engine: command stream, image requests, pipeline cache, video out. |
| `tests/SharpEmu.Gpu.Metal.Tests` | The Metal backend: image cache, transfers, guest windows, command-stream ordering, pipelines. macOS only. |
| `tests/SharpEmu.Gpu.Metal.LeakTests` | Native memory growth across many flushes, with tiered compilation off. macOS only. |
| `tests/SharpEmu.ShaderCompiler.Metal.Tests` | MSL translation, numerical execution of translated shaders, golden MSL output, and an RDNA2 ALU conformance suite. macOS only. |
| `tests/SharpEmu.ShaderCompiler.Tests` | The shared shader decoder and IR. |
| `tests/SharpEmu.Core.Memory.Tests`, `tests/SharpEmu.Host.Memory.Tests` | Guest memory and the macOS host memory layer. |
| `tests/SharpEmu.Application.Tests`, `tests/SharpEmu.Fonts.Tests`, `tests/SharpEmu.SourceGenerators.Tests` | Launcher logic, fonts, source generators. |

`tests/Fixtures` holds data the tests read, and `tests/SharpEmu.Application.Persistence.Worker`
is a helper process, not a test project.

Many tests that encode work on the Metal device, or use native host memory, run
only when `SHARPEMU_TEST_REQUIRE_DEVICE=1`. Without it such a test is skipped or
returns early and counts as passed, so a plain `dotnet test` stays green on a
machine without a usable GPU but does not exercise the device paths. To run them:

```sh
SHARPEMU_TEST_REQUIRE_DEVICE=1 dotnet test tests/SharpEmu.Gpu.Metal.Tests -c Release
SHARPEMU_TEST_REQUIRE_DEVICE=1 dotnet test tests/SharpEmu.ShaderCompiler.Metal.Tests -c Release
```

The device tests share one default Metal device, so their collections run one at
a time. Other gating variables, such as `SHARPEMU_TEST_REQUIRE_OWNED_NATIVE`
for the native host memory workers, are named in the test that reads them.

Variables for the conformance suite and the throughput tests:

| Variable | Effect |
| --- | --- |
| `SHARPEMU_CONFORMANCE_REPORT=<dir>` | Writes the per-opcode table and failures of the ALU conformance run. |
| `SHARPEMU_CONFORMANCE_CATEGORY=<a,b>` | Runs only those opcode categories. |
| `SHARPEMU_TEST_THROUGHPUT_ROUNDS=<n>` | Repeats the image-transfer throughput measurement. |

## Python tests

```sh
python3 -m unittest discover -s tests/scripts -v
```

These test the macOS build tooling: build metadata and provenance, the output
policy script, and `place-host-stack.py`.

The harness scripts have their own tests, run directly:

```sh
python3 -I scripts/macos/test-input-plan.py
python3 -I scripts/macos/test-ds-metrics.py
python3 -I scripts/macos/test-memstat.py
python3 -I scripts/macos/test-native-fault-trace.py
```

`tools/SharpEmu.DebuggerFrontend/tests` tests the debugger frontend.

## CI

`.github/workflows/workflow.yml` runs, on every push and pull request, in this
order:

1. REUSE compliance (`reuse lint`).
2. On a macOS runner: `dotnet restore SharpEmu.slnx`, then
   `python3 -m unittest discover -s tests/scripts -v`, then
   `dotnet build SharpEmu.slnx -c Release`, then
   `dotnet test SharpEmu.slnx -c Release`.
3. A CLI publish for `osx-x64`, then `scripts/macos/build-app.sh`, and an upload
   of the app bundle zip.

Pushes that only change Markdown or images do not start the workflow. CI does not
set `SHARPEMU_TEST_REQUIRE_DEVICE`, so the device-gated tests do not exercise the
device there. Run them locally before changing the Metal backend. Tags that match `v*` also
publish a release; see [Release use](../release-use.md).

## Game harness

The scripts below are developer tools. They launch a built `SharpEmu.app`, so
build one first ([Building](building.md)).

### `scripts/macos/run-game.sh`

```sh
scripts/macos/run-game.sh <game|path/to/eboot.bin> <tag> <seconds> [keyplan] [ENV=VALUE ...]
```

It runs one game for a bounded time and writes
`artifacts/runs/<timestamp>-<game>-<tag>/`:

- `game.log`, and `game.tlog` (the same lines prefixed with seconds since launch),
- `result.txt` (how the run ended), `run.env` (the settings used),
  `keys.log` (keys sent), `binary-provenance.json`,
- `footprint.log` (physical footprint and resident size once a second),
  `system-mem.log` (paging and memory pressure),
- `frame-times.txt` (one line per guest flip),
- screenshots, when a window-id helper is available (`SHARPEMU_WINID`).

Details:

- The build under test is `SHARPEMU_APP_DIR`
  (default `artifacts/macos/SharpEmu.app/Contents/MacOS`).
- The game aliases `ds`, `sarah`, `octo`, `poppy`, `quake`, `sh`, `superl`,
  `oregon` and `void` map to fixed folder names under `SHARPEMU_GAMES_DIR`
  (default `/Volumes/Untitled/games`). Pass a path to an `eboot.bin` to run any
  game.
- `seconds` is capped at 150. `SHARPEMU_GAME_ENTRY_RUN=1` raises the cap to 600
  and `SHARPEMU_GAME_SESSION_RUN=1` to 2400.
- The key plan is a comma list of `t:key[:hold]` entries, or `-` for none. It is
  delivered through `SHARPEMU_INPUT_FILE`, so the game needs no focus. Phases
  anchored to log lines or flip counts, and named markers, are described in
  `scripts/macos/input-plan.py`.
- Only one game runs at a time on the machine. The script takes a lock
  directory (`SHARPEMU_GAME_LOCK`), waits for other `SharpEmu` processes, and waits
  for the one-minute load average to drop below `SHARPEMU_MAX_LOAD` (default: the
  CPU count) for up to `SHARPEMU_LOAD_WAIT_SECONDS` (default 1800).
- `SHARPEMU_MEM_LIMIT_MB` (default 12288) stops the run when the physical
  footprint exceeds it.
- The game is stopped with SIGTERM, and with SIGKILL only if it is still alive
  10 seconds later. The script notes that a SIGKILL with GPU work in flight has been
  linked to an IOGPU kernel panic, so do not kill a running game by hand.
- `SHARPEMU_MEMSTAT=1` adds a de-duplicated memory breakdown (`memstat.log`). It
  needs `task_for_pid`: either a bundle built with `SHARPEMU_MAC_GET_TASK_ALLOW=1`
  or passwordless `sudo`. The `memstat` tool is built on demand
  (`scripts/macos/build-memstat.sh`).

### `scripts/macos/regress.sh`

```sh
SHARPEMU_APP_DIR=<build> scripts/macos/regress.sh <tag> [game ...]
```

Runs a fixed set of games (default `sarah quake void oregon`) through
`run-game.sh` with fixed key plans and prints one line per game: the median guest
frame rate over the second half of the run, the result, and the run directory.
Run a baseline build and a candidate build with the same games and compare the
lines and the final screenshots (`shot-final.png`).

### `scripts/macos/perf-bench.sh`

```sh
scripts/macos/perf-bench.sh <app-dir> <tag> [plain|prof] [ENV=VALUE ...]
```

One run of the `sh` game through `run-game.sh` with a fixed key plan, followed
by `perf_report.py` over the run directory. It writes `perf-report.md` and
`perf-report.json` for the measured window, and per-segment reports. `prof` mode
adds `SHARPEMU_PROFILE_RENDER=1` and `SHARPEMU_METAL_V2_PROFILE_PASSES=1`, which
cost frame time, so compare `prof` runs only with `prof` runs. The script waits
until no `dotnet build`, `dotnet test`, MSBuild or compiler server is busy, so
do not start a build while it measures.

### Other tools

| Tool | Purpose |
| --- | --- |
| `scripts/macos/games/ds.sh` | Scripted Demon's Souls runs on a private copy of a save, ending in `metrics.json` and `metrics.md` (`ds-metrics.py`). |
| `scripts/macos/perf_report.py` | Turns a run directory into the performance table; `--baseline` adds a before and after comparison. |
| `scripts/macos/analyze_pass_profile.py` | Aggregates `[METAL-V2][PASS-PROF]` lines from a `game.log`. |
| `scripts/macos/analyze_dstrace.py` | Analyses a `DSTRACE` run directory. |
| `scripts/macos/footprint.py`, `memstat.py`, `memstat.c` | Physical footprint and de-duplicated memory sampling. |
| `scripts/macos/hud-detect.py` | Detects whether the Demon's Souls in-world HUD is on screen, from a screenshot. |
| `scripts/macos/read-native-fault-trace.py` | Reads the native fault trace of a stopped process. |
| `scripts/macos/metal-submit-latency.m` | Standalone probe of Metal command-buffer scheduling latency. |
| `tools/SharpEmu.Tools.ShaderDump` | Shader decode and dump tool; `check-corpus.sh` retranslates a recorded shader corpus through the Metal path and `xcrun metal`. |
| `tools/SharpEmu.Tools.ShaderCompileBench` | Shader compile benchmark. |
| `tools/SharpEmu.Tools.MacWindowSmoke` | Manual smoke test of the native window and `CAMetalLayer`. |
