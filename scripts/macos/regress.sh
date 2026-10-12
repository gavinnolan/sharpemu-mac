#!/usr/bin/env bash
# Copyright (C) 2026 SharpEmu Emulator Project
# SPDX-License-Identifier: GPL-2.0-or-later
#
# Runs the games that already work through run-game.sh with fixed keyplans, so a candidate build
# can be compared against a baseline build run the same way.
#
#   scripts/macos/regress.sh <tag> [game ...]      (default games: sarah quake void oregon)
#
# SHARPEMU_APP_DIR picks the build. Prints one summary line per run: median guest fps over the
# second half of the run, result, and the run directory (whose shot-final.png is the comparison image).
set -uo pipefail
REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
TAG="$1"; shift
GAMES=("$@"); [[ ${#GAMES[@]} -eq 0 ]] && GAMES=(sarah quake void oregon)

plan_for() {
  case "$1" in
    sarah) echo "60 -" ;;
    quake) echo "150 30:enter,40:enter,50:enter,60:enter,80:up:2" ;;
    void) echo "150 40:enter,60:enter,80:enter,100:up:2,110:right:2" ;;
    oregon) echo "200 100:enter,115:enter,130:enter" ;;
    superl) echo "150 40:enter,55:enter,70:enter,90:up:3" ;;
    ds) echo "150 -" ;;
    *) echo "120 -" ;;
  esac
}

for game in "${GAMES[@]}"; do
  read -r secs plan <<< "$(plan_for "$game")"
  SHARPEMU_GAME_ENTRY_RUN=1 "$REPO_ROOT/scripts/macos/run-game.sh" "$game" "$TAG" "$secs" "$plan" >/dev/null 2>&1
  run="$(/bin/ls -d "$REPO_ROOT"/artifacts/runs/*-"$game"-"$TAG" 2>/dev/null | tail -1)"
  vals="$(grep -o 'guest_fps=[0-9.]*' "$run/game.log" 2>/dev/null | cut -d= -f2)"
  n=$(printf '%s\n' "$vals" | grep -c .)
  fps="$(printf '%s\n' "$vals" | tail -n $(( (n + 1) / 2 )) | sort -n | awk '{a[NR]=$1} END {print (NR ? a[int((NR+1)/2)] : "n/a")}')"
  printf '%-7s fps_median_2nd_half=%-6s %s %s\n' "$game" "${fps:-n/a}" "$(head -1 "$run/result.txt" 2>/dev/null)" "$run"
done
