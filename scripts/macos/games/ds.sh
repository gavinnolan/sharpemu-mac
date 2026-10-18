#!/usr/bin/env bash
# Copyright (C) 2026 SharpEmu Emulator Project
# SPDX-License-Identifier: GPL-2.0-or-later
#
# Demon's Souls (PPSA01342) test harness: scripted runs on a private copy of a known save,
# through run-game.sh, each ending in <run>/metrics.json and metrics.md (ds-metrics.py).
#
#   scripts/macos/games/ds.sh <tag> [seconds] [ingame|full|walk|title|boot] [ENV=VALUE ...]
#
#   ingame  (default) fast path: hold Cross through the logos, CONTINUE the saved character
#           and "Continue Offline", keep pressing through the stage-entry cinematic until the
#           game autosaves on entering Outpost Passage (the save-data commit in the log), then
#           stand still at that spawn point for 30 s and stop the run. Default 600 s.
#   full    full-coverage run for the 60 FPS target: boot logos and intro movies unskipped,
#           title menu, the Settings menu (screenshots of every tab, where the Cinematic /
#           Performance setting lives), CONTINUE, the stage-entry cinematic, pause and inventory
#           menus, then a traversal of Outpost Passage and Forlorn Outpost. Segments are
#           named plan markers; the report says which are calibrated. Default 600 s; use
#           SHARPEMU_GAME_SESSION_RUN=1 for up to 2400 s.
#   walk    as ingame's old timed plan, then hold forward at +260 s into the Outpost Passage
#           tunnel (the reference video's t00548 frame)
#   title   skip the intro movie and stop at the title menu (timed plan)
#   boot    no input (load-time measurements)
#
# Defaults, each overridable by an ENV=VALUE argument: SHARPEMU_DISPLAY_RESOLUTION=1080p and
# SHARPEMU_TITLE_DISPLAY_PATCH=mode (the guest display and the internal scene are both
# 1920x1080: metrics.md lists the resolution evidence from the log), SHARPEMU_LOG_SAVEDATA=1
# (the autosave line that marks controllable gameplay) and SHARPEMU_LOG_VIDEOOUT_FPS=1.
# SHARPEMU_DS_DRY_RUN=1 prints the validated key plan and exits. SHARPEMU_SHOT_EVERY=<s> changes the 10 s interval screenshots; every plan marker also takes one.
#
# Plans are anchored on log lines, not wall-clock, where the game gives one (see input-plan.py):
# 'Starting main loop' (the engine is up), 'Scheduled guest thread 'Bink IO'' (a boot movie
# starts: three of them precede the title menu) and the save-data commit. Timed plans (walk,
# title) are relative to the first 'Bink IO'. The save is copied from artifacts/saves/ds-golden
# (the main checkout's when a worktree has none; seeded from the user's SharpEmu savedata on
# first use) into the run's own directory, so runs never write the user's real saves and always
# start from the same place. SHARPEMU_DS_SAVE=<dir> picks another golden save root.
#
# Each checkout (main and every worktree) keeps its own data root in artifacts/data-root,
# cloned from the user's data root on first use, so builds tested side by side never
# rewrite each other's shader caches and prewarm lists. SHARPEMU_DATA_ROOT overrides it.
# A changed translator still starts cold: SHARPEMU_DS_WARMUP=1 plays the same plan once
# first (tag <tag>-warm) so the measured run starts from a warm cache, for A/B timing.
set -uo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../../.." && pwd)"
[[ $# -ge 1 ]] || { sed -n '5,44p' "$0" >&2; exit 2; }
TAG="$1"
MODE="${3:-ingame}"
case "$MODE" in ingame) SECONDS_LIMIT="${2:-600}" ;; full) SECONDS_LIMIT="${2:-1200}" ;; *) SECONDS_LIMIT="${2:-300}" ;; esac
shift $(( $# < 3 ? $# : 3 ))

MAIN_ROOT="$(cd "$(git -C "$REPO_ROOT" rev-parse --git-common-dir 2>/dev/null)/.." 2>/dev/null && pwd || echo "$REPO_ROOT")"
GOLDEN="${SHARPEMU_DS_SAVE:-}"
if [[ -z "$GOLDEN" ]]; then
  GOLDEN="$REPO_ROOT/artifacts/saves/ds-golden"
  [[ -d "$GOLDEN/PPSA01342" ]] || { [[ -d "$MAIN_ROOT/artifacts/saves/ds-golden/PPSA01342" ]] && GOLDEN="$MAIN_ROOT/artifacts/saves/ds-golden"; }
fi
USER_SAVES="$HOME/Library/Application Support/SharpEmu/user/savedata"
if [[ ! -d "$GOLDEN/PPSA01342" ]]; then
  [[ -d "$USER_SAVES/PPSA01342" ]] || { echo "no Demon's Souls save in $GOLDEN or $USER_SAVES" >&2; exit 2; }
  mkdir -p "$GOLDEN" && cp -R "$USER_SAVES/PPSA01342" "$GOLDEN/"
fi
SAVES="$(mktemp -d "${TMPDIR:-/tmp}/ds-saves.XXXXXX")"
trap 'rm -rf "$SAVES"' EXIT

ANCHOR_LEGACY=0
BINK="@log=Scheduled guest thread 'Bink IO'"
# Keys (see PadExports.ReadKeyboardButtons): enter Cross, esc Circle, v Triangle, c Square,
# q/e L1/R1, r/f L2/R2, tab Options, arrows D-pad, w/a/s/d left stick, i/j/k/l right stick.
case "$MODE" in
  ingame)
    # Hold Cross (2 s every 8 s) from the first boot movie: it skips the movies, confirms CONTINUE
    # and "Continue Offline" (the title menu's first item and the dialog's default), and holding
    # it skips the stage-entry cinematic. The in-world HUD (run-game.sh's hud marker) ends the
    # presses; the character stands at the spawn point for 30 s and the run stops.
    PLAN="$BINK|0:mark:skip-logos,0:rep:enter:8:2"
    PLAN+=";@mark=hud~560|0:mark:spawn-idle,30:mark:end,31:mark:stop" ;;
  full)
    # Segments are named plan markers (metrics.md has one row each). Status of each is in the
    # comments; "pending" ones have inputs derived from research but could not be timed because
    # in-world gameplay still runs at ~0-2 fps.
    #
    # Boot, calibrated on 20261018-082109-ds-cal1-full: Bink IO #1 is the PlayStation Studios
    # movie, #2 the ~107 s intro movie ("upon Earth was planted an irrevocable poison", not
    # skippable by Cross in the first minutes), #3 the Bluepoint logo (~10 s), after which the
    # title menu is drawn. The movies play in real time, so these anchors hold at any speed.
    PLAN="@log=Starting main loop|0:mark:logos"
    PLAN+=";$BINK~240|0:mark:logo-movie"
    PLAN+=";$BINK~300|0:mark:intro-cinematic"
    PLAN+=";$BINK~300|0:mark:bluepoint-logo"
    # Title menu: CONTINUE, LOAD GAME, NEW GAME, (gap) SETTINGS. Idle 5 s, walk down to SETTINGS
    # and look through it (R1 / Down, a screenshot per step: where Visual Mode lives is what the
    # shots are for), back out, return to CONTINUE.   [title: calibrated; settings tour: pending]
    PLAN+=";@after=11~11|0:mark:title,5:down,6.5:down,8:down,10:mark:settings-select,10.5:enter"
    PLAN+=",14:mark:settings-0"
    for step in 1 2 3 4; do
      PLAN+="$(printf ',%d:e,%d:mark:settings-r1-%d' $((14 + step * 4 - 3)) $((14 + step * 4)) "$step")"
    done
    for step in 1 2 3 4 5 6; do
      PLAN+="$(printf ',%d:down,%d:mark:settings-down-%d' $((30 + step * 3 - 2)) $((30 + step * 3)) "$step")"
    done
    PLAN+=",52:esc,56:mark:settings-closed,57:up,59:mark:cursor-up-1,60:up,62:mark:cursor-up-2,63:up,65:mark:cursor-up-3"
    # CONTINUE, then Continue Offline once the dialog is up (~10 s), then keep pressing Cross
    # through the loading screen until the HUD shows (holding it skips the stage-entry cinematic).
    PLAN+=",66:mark:continue,66:enter,78:enter,84:enter"
    PLAN+=";@after=20|0:mark:load.stage,0:rep:enter:8:2"
    # In the world (pending): idle, open the pause menu (Options) and look at what it offers, enter one
    # page, back out, then walk the tunnel forward.
    PLAN+=";@mark=hud~600|0:mark:spawn-idle,10:mark:pause-menu,10:tab,20:mark:pause-menu-a,20:down"
    PLAN+=",30:mark:pause-menu-b,30:down,40:mark:pause-menu-c,40:enter,55:mark:menu-page,55:esc"
    PLAN+=",65:mark:menu-back,65:esc,75:mark:menu-closed,80:mark:traverse,80:w:20,110:mark:end,111:mark:stop" ;;
  walk)
    ANCHOR_LEGACY=1
    PLAN="0:enter:2,10:enter:2,20:enter:2,30:enter:2,40:enter:2,48:enter:2"
    PLAN+="$(printf ',%d:enter' $(seq 58 8 202))"
    PLAN+=",260:w:3" ;;
  title)
    ANCHOR_LEGACY=1
    PLAN="0:enter:2,10:enter:2,20:enter:2,30:enter:2,40:enter:2,48:enter:2,58:enter" ;;
  boot) PLAN="-" ;;
  *) echo "unknown mode $MODE (ingame|full|walk|title|boot)" >&2; exit 2 ;;
esac

python3 "$REPO_ROOT/scripts/macos/input-plan.py" --plan "$PLAN" --validate || exit 2
# SHARPEMU_DS_DRY_RUN=1 prints the validated plan and stops, to check a plan edit without a run.
[[ "${SHARPEMU_DS_DRY_RUN:-0}" == 1 ]] && { echo "$PLAN"; exit 0; }

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
[[ "$MODE" == full ]] && export SHARPEMU_GAME_SESSION_RUN=1   # the full route outlasts the 600 s entry cap
case "$MODE" in ingame|full) export SHARPEMU_HUD_WATCH=1 ;; esac
if (( ANCHOR_LEGACY )); then
  export SHARPEMU_INPUT_START_AFTER_LOG="${SHARPEMU_INPUT_START_AFTER_LOG:-Scheduled guest thread 'Bink IO'}"
fi
# run-game.sh reads the memory limit itself, before the ENV=VALUE arguments reach the game.
for arg in "$@"; do [[ "$arg" == SHARPEMU_MEM_LIMIT_MB=* ]] && export "$arg"; done
export SHARPEMU_MEM_LIMIT_MB="${SHARPEMU_MEM_LIMIT_MB:-18000}"
run() {
  rm -rf "$SAVES/PPSA01342" && cp -R "$GOLDEN/PPSA01342" "$SAVES/"
  # The defaults come first so an ENV=VALUE argument (exported later, in order) wins.
  "$REPO_ROOT/scripts/macos/run-game.sh" ds "$1" "$SECONDS_LIMIT" "$PLAN" \
    SHARPEMU_SAVEDATA_DIR="$SAVES" SHARPEMU_LOG_VIDEOOUT_FPS=1 SHARPEMU_LOG_SAVEDATA=1 \
    SHARPEMU_DISPLAY_RESOLUTION=1080p SHARPEMU_TITLE_DISPLAY_PATCH=mode "${@:2}"
}
summarise() {
  local out="$1"
  [[ -d "$out" ]] || return 0
  python3 "$REPO_ROOT/scripts/macos/ds-metrics.py" "$out" || echo "ds-metrics.py failed for $out" >&2
  [[ -f "$out/metrics.md" ]] && { echo "--- $out/metrics.md"; cat "$out/metrics.md"; }
}
run_and_summarise() {
  local log; log="$(mktemp "${TMPDIR:-/tmp}/ds-run.XXXXXX")"
  run "$@" | tee "$log"
  summarise "$(grep -m1 '/artifacts/runs/' "$log")"
  rm -f "$log"
}
[[ "${SHARPEMU_DS_WARMUP:-0}" == 1 ]] && run "$TAG-warm" "$@"
run_and_summarise "$TAG" "$@"
