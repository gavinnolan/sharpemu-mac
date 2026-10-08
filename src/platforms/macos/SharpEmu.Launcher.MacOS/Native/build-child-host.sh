#!/bin/bash
# Copyright (C) 2026 SharpEmu Emulator Project; SPDX-License-Identifier: GPL-2.0-or-later
set -euo pipefail
compiler=(/usr/bin/clang)
# CLT tooling on Apple silicon may have only an ARM64 slice even when the
# managed build driver runs translated. The output remains x86_64-only.
if [ "$(/usr/sbin/sysctl -n hw.optional.arm64 2>/dev/null || true)" = "1" ]; then
    compiler=(/usr/bin/arch -arm64 /usr/bin/clang)
fi
mkdir -p "$(dirname "$2")"
"${compiler[@]}" -arch x86_64 -mmacosx-version-min=15.0 -Wall -Wextra -Werror "$1" -o "$2"
