#!/usr/bin/env bash
# Copyright (C) 2026 SharpEmu Emulator Project
# SPDX-License-Identifier: GPL-2.0-or-later
#
# Demon's Souls (PPSA01342): boot straight into gameplay (or to the title menu) with the
# fewest seconds of input, on a private copy of a known save, through run-game.sh.
#
#   scripts/macos/games/ds.sh <tag> [seconds] [ingame|title|boot] [ENV=VALUE ...]
#
#   ingame  (default) skip the intro movie, press past the title, CONTINUE the saved
#           character and "Continue Offline", then stop pressing so gameplay stays still
#   title   skip the intro movie and stop at the title menu
#   boot    no input (load-time measurements)
#
# Key times are relative to the guest's "[MSG-Init] Starting main loop" log line, so they
# hold however long shader prewarm and boot take. The save is copied from
# artifacts/saves/ds-golden (seeded from the user's SharpEmu savedata on first use) into
# the run's own directory, so runs never write the user's real saves and always start
# from the same place. SHARPEMU_DS_SAVE=<dir> picks another golden save root.
set -uo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../../.." && pwd)"
[[ $# -ge 1 ]] || { sed -n '5,18p' "$0" >&2; exit 2; }
TAG="$1"
SECONDS_LIMIT="${2:-300}"
MODE="${3:-ingame}"
shift $(( $# < 3 ? $# : 3 ))

GOLDEN="${SHARPEMU_DS_SAVE:-$REPO_ROOT/artifacts/saves/ds-golden}"
USER_SAVES="$HOME/Library/Application Support/SharpEmu/user/savedata"
if [[ ! -d "$GOLDEN/PPSA01342" ]]; then
  [[ -d "$USER_SAVES/PPSA01342" ]] || { echo "no Demon's Souls save in $GOLDEN or $USER_SAVES" >&2; exit 2; }
  mkdir -p "$GOLDEN" && cp -R "$USER_SAVES/PPSA01342" "$GOLDEN/"
fi
SAVES="$(mktemp -d "${TMPDIR:-/tmp}/ds-saves.XXXXXX")"
trap 'rm -rf "$SAVES"' EXIT
cp -R "$GOLDEN/PPSA01342" "$SAVES/"

# Seconds after "Starting main loop": Cross held through the logos and the intro movie,
# then single presses for "press any button", CONTINUE and "Continue Offline".
case "$MODE" in
  ingame)
    PLAN="10:enter:2,20:enter:2,30:enter:2,40:enter:2,50:enter:2,60:enter:2,70:enter:2,80:enter:2"
    PLAN+=",90:enter,98:enter,106:enter,114:enter,122:enter,130:enter,138:enter" ;;
  title)
    PLAN="10:enter:2,20:enter:2,30:enter:2,40:enter:2,50:enter:2,60:enter:2,70:enter:2,80:enter:2,90:enter" ;;
  boot) PLAN="-" ;;
  *) echo "unknown mode $MODE (ingame|title|boot)" >&2; exit 2 ;;
esac

export SHARPEMU_GAME_ENTRY_RUN=1
export SHARPEMU_INPUT_START_AFTER_LOG="${SHARPEMU_INPUT_START_AFTER_LOG:-Starting main loop}"
export SHARPEMU_MEM_LIMIT_MB="${SHARPEMU_MEM_LIMIT_MB:-18000}"
"$REPO_ROOT/scripts/macos/run-game.sh" ds "$TAG" "$SECONDS_LIMIT" "$PLAN" \
  SHARPEMU_SAVEDATA_DIR="$SAVES" SHARPEMU_LOG_VIDEOOUT_FPS=1 "$@"
