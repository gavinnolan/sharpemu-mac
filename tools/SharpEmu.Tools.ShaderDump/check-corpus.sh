#!/bin/sh
# Copyright (C) 2026 SharpEmu Emulator Project
# SPDX-License-Identifier: GPL-2.0-or-later
#
# End-to-end check of SHARPEMU_SHADER_CORPUS_DIR and the corpus command: the pipeline unit tests
# submit their synthetic shaders with the corpus on, then every record they wrote is translated
# through the Metal path (and xcrun metal) again. Run from the repository root.
set -eu

dir=$(mktemp -d)
trap 'rm -rf "$dir"' EXIT

SHARPEMU_SHADER_CORPUS_DIR="$dir" dotnet test tests/SharpEmu.Libs.Tests \
    --filter "FullyQualifiedName~ShaderProgramCacheTests|FullyQualifiedName~ShaderPrewarmListTests"

count=$(find "$dir" -name '*.corpus' | wc -l | tr -d ' ')
if [ "$count" -eq 0 ]; then
    echo "check-corpus: the tests wrote no corpus records" >&2
    exit 1
fi

dotnet run --project tools/SharpEmu.Tools.ShaderDump -- corpus "$dir" --metal
