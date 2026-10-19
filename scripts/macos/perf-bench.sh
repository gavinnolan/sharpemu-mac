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
# Fixed: SHARPEMU_GAME_ENTRY_RUN=1, 480 s, SHARPEMU_MEM_LIMIT_MB=17000 (SH reaches 16.5 GB), and the keyplan
# below (Enter every 10 s from 70 s to 470 s, stored in artifacts/perf-rework/sh-keyplan.txt of the main
# checkout; PERF_BENCH_KEYPLAN replaces it). SHARPEMU_FRAME_TIMES is left to run-game.sh.
# Before launching it waits until no dotnet build/test, MSBuild, VBCSCompiler or compiler server is busy
# (PERF_BENCH_WAIT_S, default 3600, bounds the wait), so builds do not perturb the measurement; do not
# start one while the run is going. Afterwards the run directory has perf-report.md and perf-report.json
# (window 150 s to the end, PERF_BENCH_FROM / PERF_BENCH_TO change it; see perf_report.py for the metrics
# and its --baseline Before / After table). Exit status is run-game.sh's.
set -uo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
DEFAULT_KEYPLAN="70:enter,80:enter,90:enter,100:enter,110:enter,120:enter,130:enter,140:enter,150:enter,160:enter,170:enter,180:enter,190:enter,200:enter,210:enter,220:enter,230:enter,240:enter,250:enter,260:enter,270:enter,280:enter,290:enter,300:enter,310:enter,320:enter,330:enter,340:enter,350:enter,360:enter,370:enter,380:enter,390:enter,400:enter,410:enter,420:enter,430:enter,440:enter,450:enter,460:enter,470:enter"
KEYPLAN="${PERF_BENCH_KEYPLAN:-$DEFAULT_KEYPLAN}"
SECONDS_LIMIT=480

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
exit "$RC"
