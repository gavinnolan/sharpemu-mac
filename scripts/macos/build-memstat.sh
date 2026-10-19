#!/usr/bin/env bash
# Copyright (C) 2026 SharpEmu Emulator Project
# SPDX-License-Identifier: GPL-2.0-or-later
#
# Builds scripts/macos/memstat.c into artifacts/macos/memstat (native arm64; it inspects the game
# process from outside, so it does not need to be x86_64). Rebuilds only when the source is newer.
# It is signed with com.apple.security.cs.debugger: task_for_pid on a hardened-runtime target fails
# even as root unless the caller holds it (the target must also carry get-task-allow, see build-app.sh).
#   scripts/macos/build-memstat.sh [--force]   prints the path of the binary
set -euo pipefail
REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
SRC="$REPO_ROOT/scripts/macos/memstat.c"
OUT="${SHARPEMU_MEMSTAT_BIN:-$REPO_ROOT/artifacts/macos/memstat}"
if [[ "${1:-}" == "--force" || ! -x "$OUT" || "$SRC" -nt "$OUT" ]]; then
  mkdir -p "$(dirname "$OUT")"
  cc -O2 -Wall -Wextra -o "$OUT" "$SRC"
  ENTITLEMENTS="$(mktemp)"
  cat > "$ENTITLEMENTS" <<'EOF'
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0"><dict><key>com.apple.security.cs.debugger</key><true/></dict></plist>
EOF
  codesign -f -s - --entitlements "$ENTITLEMENTS" "$OUT" 2>/dev/null
  rm -f "$ENTITLEMENTS"
fi
echo "$OUT"
