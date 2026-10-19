#!/usr/bin/env bash
# Disassemble a raw PS5 (RDNA, GFX10) shader binary with LLVM's AMDGPU backend.
#
# Usage: amdgcn-disasm.sh <shader.bin|record.corpus> [--mcpu gfx1013] [--encoding]
#   shader.bin      raw little-endian instruction dwords (e.g. SharpEmu shader dumps)
#   record.corpus   SharpEmu shader-corpus record; its code bytes are extracted first (corpus2bin.py)
#   --mcpu CPU      LLVM target CPU (default gfx1013, see note; gfx1030 = plain RDNA2)
#   --encoding      also print each instruction's encoding bytes
# Note: gfx1013 is LLVM's PS5-class target (GFX10.1 ISA with RDNA2 extras): a real 1.4 KB SharpEmu pixel
# shader gives 8 invalid-encoding warnings with gfx1010/1013 but 59 (desynced garbage) with gfx1030.
# Output: llvm-mc disassembly on stdout. Undecodable bytes (literal data / SharpEmu fused-shader
# headers) are reported by llvm-mc as "invalid instruction encoding" warnings on stderr.
# Env: LLVM_BIN (default /opt/homebrew/opt/llvm@21/bin).
set -euo pipefail

if [ $# -lt 1 ]; then sed -n '2,13p' "$0"; exit 2; fi
IN=$1
shift
MCPU=gfx1013
ENC=()
while [ $# -gt 0 ]; do
  case $1 in
    --mcpu) MCPU=$2; shift 2 ;;
    --encoding) ENC=(--show-encoding); shift ;;
    *) echo "unknown option $1" >&2; exit 2 ;;
  esac
done

LLVM_BIN=${LLVM_BIN:-/opt/homebrew/opt/llvm@21/bin}
HERE=$(cd "$(dirname "$0")" && pwd)
TMP=$(mktemp -d)
trap 'rm -rf "$TMP"' EXIT

BIN=$IN
if [ "${IN##*.}" = corpus ]; then
  python3 "$HERE/corpus2bin.py" "$IN" "$TMP/code.bin"
  BIN=$TMP/code.bin
fi

# llvm-mc --disassemble reads textual bytes; hex-dump the file as "0xNN" tokens.
od -An -v -tx1 "$BIN" | tr -s ' \n' '\n\n' | sed -e '/^$/d' -e 's/^/0x/' | tr '\n' ' ' > "$TMP/bytes.txt"
"$LLVM_BIN/llvm-mc" --disassemble --triple=amdgcn -mcpu="$MCPU" ${ENC[@]+"${ENC[@]}"} "$TMP/bytes.txt" 2> "$TMP/err.txt" || true
# llvm-mc echoes the whole input line with each warning; reduce to byte-offset lines.
awk -F: '/warning:/ { printf "warning:%s at byte offset 0x%x\n", $5, int(($3 - 1) / 5) }' "$TMP/err.txt" >&2
