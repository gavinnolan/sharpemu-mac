#!/usr/bin/env bash
# Copyright (C) 2026 SharpEmu Emulator Project
# SPDX-License-Identifier: GPL-2.0-or-later
#
# Repeatable Silent Hill (sh) cutscene benchmark: one run through run-game.sh with fixed settings,
# then perf_report.py over the run directory, so every perf change reports numbers from one setup.
#
#   scripts/macos/perf-bench.sh <app-dir> <tag> [plain|prof] [ENV=VALUE ...]
#
#   app-dir  the Contents/MacOS directory of the SharpEmu bundle to measure (SHARPEMU_APP_DIR)
#   tag      run directory suffix: artifacts/runs/<timestamp>-sh-<tag>
#   plain    (default) nothing extra: the numbers with no profiling cost
#   prof     adds SHARPEMU_PROFILE_RENDER=1 SHARPEMU_METAL_V2_PROFILE_PASSES=1 (worker phase table,
#            per-encoder GPU time; both cost frame time, so compare prof runs with prof runs) and a
#            20 s managed thread-time trace at 320 s (SHARPEMU_TRACE_AT=320 SHARPEMU_TRACE_SECONDS=20,
#            trace-0320.*; an ENV=VALUE argument replaces either)
#   ENV=VALUE  further variables for the game, e.g. SHARPEMU_LOG_MEMORY=1 for GC pauses in the spike list
#
# Fixed: SHARPEMU_GAME_ENTRY_RUN=1, at most 600 s (the plan's stop marker ends the run after the idle segment),
# SHARPEMU_MEM_LIMIT_MB=17000 (SH reaches 16.5 GB), and the keyplan below (PERF_BENCH_KEYPLAN replaces it).
# SHARPEMU_FRAME_TIMES is left to run-game.sh.
# Before launching it waits until no dotnet build/test, MSBuild, VBCSCompiler or compiler server is busy
# (PERF_BENCH_WAIT_S, default 3600, bounds the wait), so builds do not perturb the measurement; do not
# start one while the run is going. Afterwards the run directory has perf-report.md and perf-report.json
# (window 150 s to the end, PERF_BENCH_FROM / PERF_BENCH_TO change it; see perf_report.py for the metrics
# and its --baseline Before / After table), and perf-report-cutscene.* / perf-report-idle.* for the plan's two
# measured segments (perf_report.py --segment). Exit status is run-game.sh's.
set -uo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
# The keyplan follows the game, not the clock (flips are counted from the title movie's AVPLAYER log line, so a
# faster or slower build meets the same content). Scene flow, flips after that line, measured on main 021538538:
#   Konami / auto-save notice / content warning (Enter every 6 s until the title movie is opened)
#   +0   title menu "NEW GAME"; Enter every 6 s: warning page 2, then the intro text "Seeking a connection..."
#   +260 intro text (marker intro-text, Enter stops: more presses would open the cutscene's Skip prompt)
#   +385 cutscene-start: shrine, Maya, the dark room with the phone in hand (heavy 3D, no input)
#   +540 cutscene-end: the phone message "Maya: can you come to the villa?" is shown; Close is Circle, so Esc every 5 s
#   +600 idle-start: the player stands still in the room, no input; idle-end 120 s later, then stop
# perf_report.py --segment cutscene / idle reads the markers; a plan without them gets no segment reports.
DEFAULT_KEYPLAN="0:rep:enter:6;@log=UI_TitleMovie01|0:mark:title,0:rep:enter:6;@flips=260|0:mark:intro-text;@flips=125|0:mark:cutscene-start;@flips=155|0:mark:cutscene-end,0:rep:esc:5;@flips=60|0:mark:idle-start;@after=120|0:mark:idle-end,1:mark:stop"
KEYPLAN="${PERF_BENCH_KEYPLAN:-$DEFAULT_KEYPLAN}"
SECONDS_LIMIT=600

usage() { sed -n '5,27p' "$0" >&2; exit 2; }
[[ $# -ge 2 ]] || usage
APP_DIR="$1"
TAG="$2"
MODE="${3:-plain}"
shift 2; [[ $# -gt 0 ]] && shift
case "$MODE" in plain|prof) ;; *) echo "mode must be plain or prof, got '$MODE'" >&2; usage ;; esac
[[ -x "$APP_DIR/SharpEmu" ]] || { echo "no SharpEmu in $APP_DIR (run scripts/macos/build-app.sh)" >&2; exit 2; }
APP_DIR="$(cd "$APP_DIR" && pwd)"
EXTRA=()
if [[ "$MODE" == prof ]]; then
  EXTRA+=(SHARPEMU_PROFILE_RENDER=1 SHARPEMU_METAL_V2_PROFILE_PASSES=1)
  # run-game.sh reads the trace schedule from its own environment, not from the game's ENV=VALUE arguments.
  export SHARPEMU_TRACE_AT="${SHARPEMU_TRACE_AT:-320}" SHARPEMU_TRACE_SECONDS="${SHARPEMU_TRACE_SECONDS:-20}"
fi
EXTRA+=("$@")

# A busy build is a dotnet build/test/publish/restore/pack/msbuild/vstest command, or a resident MSBuild node,
# compiler server or test host using CPU. The process list is read first and matched afterwards, so this
# script's own command line can never match.
busy_builds() {
  local snapshot pid cpu cmd argv0 rest found=1
  snapshot="$(ps -axo pid=,pcpu=,command=)"
  while read -r pid cpu cmd; do
    [[ -n "${cmd:-}" ]] || continue
    argv0="${cmd%% *}"
    rest=" ${cmd#* } "
    if [[ "${argv0##*/}" == dotnet ]]; then
      case "$rest" in
        *" build "*|*" test "*|*" publish "*|*" restore "*|*" pack "*|*" msbuild "*|*" vstest "*)
          echo "$pid dotnet ${rest:0:80}"; found=0; continue ;;
      esac
    fi
    case "$cmd" in
      *MSBuild.dll*|*VBCSCompiler*|*csc.dll*|*testhost*|*dotnet-build-server*)
        if awk -v c="$cpu" 'BEGIN { exit !(c > 5.0) }'; then echo "$pid ${cmd:0:100}"; found=0; fi ;;
    esac
  done <<< "$snapshot"
  return $found
}

waited=0
limit="${PERF_BENCH_WAIT_S:-3600}"
while busy="$(busy_builds)"; do
  (( waited % 60 == 0 )) && { echo "perf-bench: waiting for a build to finish (${waited}s):"; echo "$busy" | head -3; }
  (( waited >= limit )) && { echo "perf-bench: a build was still busy after ${limit}s, not measuring" >&2; exit 3; }
  sleep 10; waited=$(( waited + 10 ))
done

RUNS="$REPO_ROOT/artifacts/runs"
mkdir -p "$RUNS"
STAMP="$(mktemp "${TMPDIR:-/tmp}/perf-bench-stamp.XXXXXX")"
trap 'rm -f "$STAMP"' EXIT
export SHARPEMU_GAME_ENTRY_RUN=1
export SHARPEMU_MEM_LIMIT_MB="${SHARPEMU_MEM_LIMIT_MB:-17000}"
export SHARPEMU_APP_DIR="$APP_DIR"
"$REPO_ROOT/scripts/macos/run-game.sh" sh "$TAG" "$SECONDS_LIMIT" "$KEYPLAN" "${EXTRA[@]+"${EXTRA[@]}"}"
RC=$?

RUN_DIR="$(find "$RUNS" -maxdepth 1 -type d -name "*-sh-$TAG" -newer "$STAMP" | sort | tail -1)"
[[ -n "$RUN_DIR" ]] || { echo "perf-bench: no run directory for tag $TAG under $RUNS" >&2; exit "$RC"; }
echo "perf-bench: run directory $RUN_DIR"
python3 -I "$REPO_ROOT/scripts/macos/perf_report.py" "$RUN_DIR" --from "${PERF_BENCH_FROM:-150}" \
  ${PERF_BENCH_TO:+--to "$PERF_BENCH_TO"} --json "$RUN_DIR/perf-report.json" --md "$RUN_DIR/perf-report.md" \
  || echo "perf-bench: perf_report.py failed" >&2
for SEG in cutscene idle; do
  grep -q "$(printf '\t')$SEG-start\$" "$RUN_DIR/markers.tsv" 2>/dev/null || { echo "perf-bench: no $SEG-start marker, no $SEG report" >&2; continue; }
  python3 -I "$REPO_ROOT/scripts/macos/perf_report.py" "$RUN_DIR" --segment "$SEG" \
    --json "$RUN_DIR/perf-report-$SEG.json" --md "$RUN_DIR/perf-report-$SEG.md" > /dev/null \
    || echo "perf-bench: perf_report.py --segment $SEG failed" >&2
done
exit "$RC"
