#!/usr/bin/env bash
# Copyright (C) 2026 SharpEmu Emulator Project
# SPDX-License-Identifier: GPL-2.0-or-later
#
# Runs one game on the macOS build for a bounded time and records evidence.
#
#   scripts/macos/run-game.sh <game|path/to/eboot.bin> <tag> <seconds> [keyplan] [ENV=VALUE ...]
#
#   game     ds | sarah | octo | poppy | quake | sh | superl | oregon | void (under $SHARPEMU_GAMES_DIR),
#            or a path to an eboot.bin
#   seconds  run length, capped at 150
#   keyplan  comma list "t:key[:hold]" pressed through SHARPEMU_INPUT_FILE, e.g. "20:enter,30:down:1",
#            or "-" for none
#
# Only one game runs on the machine at a time: runs take a machine-wide lock directory
# ($SHARPEMU_GAME_LOCK) shared with other harnesses, and wait while any SharpEmu process is alive.
# The game is stopped with SIGTERM first, because SIGKILL with GPU work in flight has been
# linked to an IOGPU kernel panic; SIGKILL is used only if it is still alive 10 s later.
#
# Output: artifacts/runs/<timestamp>-<game>-<tag>/ with game.log, result.txt, keys.log,
# screenshots (when a window-id helper is available) and run.env.
set -uo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
GAMES_DIR="${SHARPEMU_GAMES_DIR:-/Volumes/Untitled/games}"
APP_DIR="${SHARPEMU_APP_DIR:-$REPO_ROOT/artifacts/macos/SharpEmu.app/Contents/MacOS}"
COMPAT_DIR="${SHARPEMU_COMPAT_DIR:-$HOME/Projects/xnetcat/sharpemu-compat}"
if [[ -z "${SHARPEMU_GAME_LOCK:-}" ]]; then
  if [[ -d "$COMPAT_DIR" ]]; then SHARPEMU_GAME_LOCK="$COMPAT_DIR/.gamelock"; else SHARPEMU_GAME_LOCK="$HOME/.sharpemu/gamelock"; fi
fi
WINID="${SHARPEMU_WINID:-$COMPAT_DIR/scripts/winid}"
MEM_LIMIT_MB="${SHARPEMU_MEM_LIMIT_MB:-12288}"

usage() { sed -n '5,14p' "$0" >&2; exit 2; }
[[ $# -ge 3 ]] || usage

case "$1" in
  ds) EBOOT="$GAMES_DIR/Demon Souls/PPSA01342-app0/eboot.bin" ;;
  sarah) EBOOT="$GAMES_DIR/Dreaming Sarah/PPSA02929-app0/eboot.bin" ;;
  octo) EBOOT="$GAMES_DIR/OCTOPATH TRAVELER II/eboot.bin" ;;
  poppy) EBOOT="$GAMES_DIR/Poppy Playtime Chapter 1/eboot.bin" ;;
  quake) EBOOT="$GAMES_DIR/Quake/eboot.bin" ;;
  sh) EBOOT="$GAMES_DIR/SILENT HILL The Short Message/PPSA10112-app/eboot.bin" ;;
  superl) EBOOT="$GAMES_DIR/Superliminal/PPSA06084-app0/eboot.bin" ;;
  oregon) EBOOT="$GAMES_DIR/The Oregon Trail/PPSA19244-app0/eboot.bin" ;;
  void) EBOOT="$GAMES_DIR/void tRrLM Void Terrarium/eboot.bin" ;;
  *) EBOOT="$1" ;;
esac
GAME="$(basename "$1" .bin)"
TAG="$2"
SECONDS_LIMIT="$3"
PLAN="${4:--}"
shift 3; [[ $# -gt 0 ]] && shift

[[ -f "$EBOOT" ]] || { echo "eboot not found: $EBOOT" >&2; exit 2; }
[[ -x "$APP_DIR/SharpEmu" ]] || { echo "no SharpEmu in $APP_DIR (run scripts/macos/build-app.sh)" >&2; exit 2; }
(( SECONDS_LIMIT <= 150 )) || { echo "refusing ${SECONDS_LIMIT}s (cap 150)" >&2; exit 2; }

OUT="$REPO_ROOT/artifacts/runs/$(date +%Y%m%d-%H%M%S)-$GAME-$TAG"
mkdir -p "$OUT"
KEYS="$OUT/keys.state"
: > "$KEYS"
{
  echo "eboot=$EBOOT"
  echo "app=$APP_DIR"
  echo "commit=$(git -C "$REPO_ROOT" rev-parse --short HEAD)$(git -C "$REPO_ROOT" diff --quiet || echo -dirty)"
  echo "commit_scope=checkout (use build-info.json for binary source provenance)"
  echo "keyplan=$PLAN"
  for e in "$@"; do echo "$e"; done
} > "$OUT/run.env"
# A preexisting bundle may predate provenance metadata. Never equate the current
# checkout with that binary: retain the exact payload hashes and label it unknown.
python3 - "$APP_DIR" "$OUT" <<'PY'
import hashlib, json, pathlib, sys
app, out = map(pathlib.Path, sys.argv[1:])
info = app.parent / "Resources" / "build-info.json"
record = {
    "buildInfo": json.loads(info.read_text()) if info.exists() else None,
    "binarySourceKnown": info.exists(),
    "payloads": [],
}
for p in sorted(app.glob("SharpEmu*")):
    if p.is_file():
        record["payloads"].append({"file": p.name, "sha256": hashlib.sha256(p.read_bytes()).hexdigest()})
(out / "binary-provenance.json").write_text(json.dumps(record, indent=2) + "\n")
if info.exists():
    (out / "build-info.json").write_bytes(info.read_bytes())
PY

# Machine-wide single-game lock; stale owners are cleared.
mkdir -p "$(dirname "$SHARPEMU_GAME_LOCK")"
LOCK_HELD=0
while :; do
  if mkdir "$SHARPEMU_GAME_LOCK" 2>/dev/null; then echo $$ > "$SHARPEMU_GAME_LOCK/pid"; LOCK_HELD=1; break; fi
  owner="$(cat "$SHARPEMU_GAME_LOCK/pid" 2>/dev/null || true)"
  if [[ -n "$owner" ]] && ! kill -0 "$owner" 2>/dev/null; then rm -rf "$SHARPEMU_GAME_LOCK"; continue; fi
  echo "waiting for game lock held by ${owner:-unknown}" >&2
  sleep 5
done
while pgrep -x SharpEmu >/dev/null; do echo "waiting for another SharpEmu to exit" >&2; sleep 5; done

GPID=""
stop_game() {
  local pid="$1" i
  kill -0 "$pid" 2>/dev/null || return 0
  kill -TERM "$pid" 2>/dev/null
  for i in $(seq 1 40); do
    kill -0 "$pid" 2>/dev/null || { echo "stop=graceful after $((i / 4))s" >> "$OUT/result.txt"; return 0; }
    sleep 0.25
  done
  echo "stop=SIGKILL (alive 10s after SIGTERM)" >> "$OUT/result.txt"
  kill -KILL "$pid" 2>/dev/null
}
cleanup() {
  [[ -n "$GPID" ]] && stop_game "$GPID"
  GPID=""
  if (( LOCK_HELD )) && [[ "$(cat "$SHARPEMU_GAME_LOCK/pid" 2>/dev/null)" == "$$" ]]; then rm -rf "$SHARPEMU_GAME_LOCK"; fi
  LOCK_HELD=0
}
trap cleanup EXIT INT TERM

shot() {
  [[ -x "$WINID" ]] || return 0
  local id
  id="$("$WINID" "$GPID" 2>/dev/null)" && [[ -n "$id" ]] && screencapture -x -o -l"$id" "$1" 2>/dev/null
}

(
  cd "$APP_DIR" || exit 1
  for e in "$@"; do export "${e?}"; done
  export SHARPEMU_INPUT_FILE="$KEYS"
  exec ./SharpEmu ${SHARPEMU_ARGS:-} "$EBOOT"
) > "$OUT/game.log" 2>&1 &
GPID=$!
start=$(date +%s)
next_shot=5
shot_every="${SHARPEMU_SHOT_EVERY:-10}"
IFS=',' read -ra STEPS <<< "$PLAN"
step=0
peak_mb=0
status=""
while :; do
  now=$(( $(date +%s) - start ))
  if ! kill -0 "$GPID" 2>/dev/null; then wait "$GPID"; status="exited rc=$? after ${now}s"; GPID=""; break; fi
  (( now >= SECONDS_LIMIT )) && break
  rss_mb=$(( $(ps -o rss= -p "$GPID" 2>/dev/null || echo 0) / 1024 ))
  (( rss_mb > peak_mb )) && peak_mb=$rss_mb
  if (( rss_mb > MEM_LIMIT_MB )); then status="memory limit ${rss_mb}MB > ${MEM_LIMIT_MB}MB at ${now}s"; break; fi
  if (( now >= next_shot )); then shot "$OUT/shot-$(printf %04d "$now").png"; next_shot=$(( now + shot_every )); fi
  while (( step < ${#STEPS[@]} )) && [[ "${STEPS[$step]}" != "-" ]]; do
    IFS=':' read -r t key hold <<< "${STEPS[$step]}"
    (( now >= t )) || break
    ( echo "$key" > "$KEYS"; sleep "${hold:-0.4}"; : > "$KEYS" ) &
    echo "$now $key" >> "$OUT/keys.log"
    step=$(( step + 1 ))
  done
  sleep 1
done
if [[ -n "$GPID" ]]; then
  shot "$OUT/shot-final.png"
  status="${status:-alive at ${SECONDS_LIMIT}s}"
  # SHARPEMU_CAPTURE_STACKS=1 records managed (dotnet-stack, x64 runtime) and native (sample) stacks.
  if [[ "${SHARPEMU_CAPTURE_STACKS:-0}" == "1" ]]; then
    DOTNET_ROOT="${SHARPEMU_X64_DOTNET_ROOT:-$HOME/.dotnet-x64}" timeout 60 \
      "$HOME/.dotnet/tools/dotnet-stack" report -p "$GPID" > "$OUT/stacks.txt" 2>&1 || true
    sample "$GPID" 3 -file "$OUT/sample.txt" >/dev/null 2>&1 || true
  fi
fi
echo "$status" > "$OUT/result.txt"
echo "peak_rss_mb=$peak_mb" >> "$OUT/result.txt"
cleanup
echo "$OUT"
cat "$OUT/result.txt"
