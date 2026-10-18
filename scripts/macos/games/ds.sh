#!/usr/bin/env bash
# Copyright (C) 2026 SharpEmu Emulator Project
# SPDX-License-Identifier: GPL-2.0-or-later
#
# Demon's Souls (PPSA01342): boot straight into gameplay (or to the title menu) with the
# fewest seconds of input, on a private copy of a known save, through run-game.sh.
#
#   scripts/macos/games/ds.sh <tag> [seconds] [ingame|walk|title|boot] [ENV=VALUE ...]
#
#   ingame  (default) skip the intro movie, press past the title, CONTINUE the saved
#           character and "Continue Offline", then stop pressing so gameplay stays still
#   walk    as ingame, then hold forward at +260 s to walk into the Outpost Passage tunnel,
#           where the reference video's t00548 frame is (to compare the same camera)
#   title   skip the intro movie and stop at the title menu
#   boot    no input (load-time measurements)
#
# Key times are relative to the first "Bink IO" guest thread (the intro movie, which only
# starts once the logos have drawn), so they hold however long shader prewarm, boot and a
# cold shader cache's first compiles take. The save is copied from
# artifacts/saves/ds-golden (seeded from the user's SharpEmu savedata on first use) into
# the run's own directory, so runs never write the user's real saves and always start
# from the same place. SHARPEMU_DS_SAVE=<dir> picks another golden save root.
#
# Each checkout (main and every worktree) keeps its own data root in artifacts/data-root,
# cloned from the user's data root on first use, so builds tested side by side never
# rewrite each other's shader caches and prewarm lists. SHARPEMU_DATA_ROOT overrides it.
# A changed translator still starts cold: SHARPEMU_DS_WARMUP=1 plays the same plan once
# first (tag <tag>-warm) so the measured run starts from a warm cache, for A/B timing.
set -uo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../../.." && pwd)"
[[ $# -ge 1 ]] || { sed -n '5,20p' "$0" >&2; exit 2; }
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

# Seconds after the intro movie starts: Cross held through the movie,
# then single presses for "press any button", CONTINUE and "Continue Offline".
case "$MODE" in
  ingame|walk)
    PLAN="0:enter:2,10:enter:2,20:enter:2,30:enter:2,40:enter:2,48:enter:2"
    # Cross keeps coming every 8 s for a while: the menus' timing moves with machine load,
    # and in gameplay Cross only interacts, which the starting spot has nothing to.
    PLAN+="$(printf ',%d:enter' $(seq 58 8 202))"
    [[ "$MODE" == walk ]] && PLAN+=",260:w:3" ;;
  title)
    PLAN="0:enter:2,10:enter:2,20:enter:2,30:enter:2,40:enter:2,48:enter:2,58:enter" ;;
  boot) PLAN="-" ;;
  *) echo "unknown mode $MODE (ingame|walk|title|boot)" >&2; exit 2 ;;
esac

USER_ROOT="$HOME/Library/Application Support/SharpEmu"
DATA_ROOT="${SHARPEMU_DATA_ROOT:-$REPO_ROOT/artifacts/data-root}"
if [[ ! -d "$DATA_ROOT/user/pipeline_cache" ]]; then
  mkdir -p "$DATA_ROOT/user/pipeline_cache"  # ampr-index and shader-analysis stay shared (content-keyed)
  for part in user/pipeline_cache/PPSA01342; do
    [[ -d "$USER_ROOT/$part" ]] && cp -cR "$USER_ROOT/$part" "$DATA_ROOT/$part" 2>/dev/null \
      || cp -R "$USER_ROOT/$part" "$DATA_ROOT/$part" 2>/dev/null || true
  done
fi
export SHARPEMU_DATA_ROOT="$DATA_ROOT"

export SHARPEMU_GAME_ENTRY_RUN=1
export SHARPEMU_INPUT_START_AFTER_LOG="${SHARPEMU_INPUT_START_AFTER_LOG:-Scheduled guest thread 'Bink IO'}"
# run-game.sh reads the memory limit itself, before the ENV=VALUE arguments reach the game.
for arg in "$@"; do [[ "$arg" == SHARPEMU_MEM_LIMIT_MB=* ]] && export "$arg"; done
export SHARPEMU_MEM_LIMIT_MB="${SHARPEMU_MEM_LIMIT_MB:-18000}"
run() {
  rm -rf "$SAVES/PPSA01342" && cp -R "$GOLDEN/PPSA01342" "$SAVES/"
  "$REPO_ROOT/scripts/macos/run-game.sh" ds "$1" "$SECONDS_LIMIT" "$PLAN" \
    SHARPEMU_SAVEDATA_DIR="$SAVES" SHARPEMU_LOG_VIDEOOUT_FPS=1 "${@:2}"
}
[[ "${SHARPEMU_DS_WARMUP:-0}" == 1 ]] && run "$TAG-warm" "$@"
run "$TAG" "$@"
