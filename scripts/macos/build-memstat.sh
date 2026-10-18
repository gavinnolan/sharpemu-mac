#!/usr/bin/env bash
# Copyright (C) 2026 SharpEmu Emulator Project
# SPDX-License-Identifier: GPL-2.0-or-later
#
# Builds scripts/macos/memstat.c into artifacts/macos/memstat (native arm64; it inspects the game
# process from outside, so it does not need to be x86_64). Rebuilds only when the source is newer.
#   scripts/macos/build-memstat.sh [--force]   prints the path of the binary
set -euo pipefail
REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
SRC="$REPO_ROOT/scripts/macos/memstat.c"
OUT="${SHARPEMU_MEMSTAT_BIN:-$REPO_ROOT/artifacts/macos/memstat}"
if [[ "${1:-}" == "--force" || ! -x "$OUT" || "$SRC" -nt "$OUT" ]]; then
  mkdir -p "$(dirname "$OUT")"
  cc -O2 -Wall -Wextra -o "$OUT" "$SRC"
fi
echo "$OUT"
