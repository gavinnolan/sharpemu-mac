#!/usr/bin/env bash
# Regenerate every Demon's Souls (PPSA01342) static-analysis output from scratch.
#
# Usage: tools/re/ds-setup.sh [--no-ghidra] [--no-analysis]
#   --no-ghidra    only the ELF / imports / callsites / modules (seconds); skip the Ghidra project
#   --no-analysis  import into Ghidra but skip auto-analysis (minutes instead of a long run)
# Env:   DS_APP0   game dir with eboot.bin (default /Volumes/Untitled/games/Demon Souls/PPSA01342-app0)
#        RE_OUT    output root (default <main checkout>/artifacts/re; artifacts/ is git-ignored)
#
# Outputs under $RE_OUT:
#   ds/eboot.elf        flattened eboot (runtime address = 0x800000000 + ELF vaddr)
#   ds/imports.tsv      import slots -> PLT stub, NID, resolved name, library, module
#   ds/callsites.tsv    every direct reference to an import (+ containing function start)
#   ds/functions.txt    function starts from the eboot's eh_frame table
#   ds/modules/*        flattened bundled sce_module/*.prx + their imports/exports TSVs
#   ghidra/DS.gpr       Ghidra project (program eboot.elf, base 0x800000000, analysed, saved)
set -euo pipefail

HERE=$(cd "$(dirname "$0")" && pwd)
MAIN=$(cd "$HERE/../.." && pwd)
case $MAIN in */.claude/worktrees/*) MAIN=${MAIN%%/.claude/worktrees/*} ;; esac
DS_APP0=${DS_APP0:-/Volumes/Untitled/games/Demon Souls/PPSA01342-app0}
RE_OUT=${RE_OUT:-$MAIN/artifacts/re}
GHIDRA=1
GHIDRA_ARGS=()
for a in "$@"; do
  case $a in
    --no-ghidra) GHIDRA=0 ;;
    --no-analysis) GHIDRA_ARGS=(-noanalysis) ;;
    *) echo "unknown option $a" >&2; exit 2 ;;
  esac
done

OUT=$RE_OUT/ds
mkdir -p "$OUT/modules" "$RE_OUT/ghidra"
cd "$HERE"

echo "== 1/5 flatten eboot"
python3 fself2elf.py "$DS_APP0/eboot.bin" "$OUT/eboot.elf" | tail -2

echo "== 2/5 NIDs and import map"
python3 nidmap.py "$OUT/eboot.elf" "$OUT/imports.tsv"

echo "== 3/5 call sites"
python3 callsites.py "$OUT/eboot.elf" "$OUT/imports.tsv" "$OUT/callsites.tsv"
python3 funcstarts.py "$OUT/eboot.elf" "$OUT/functions.txt"

echo "== 4/5 bundled modules"
for prx in "$DS_APP0"/sce_module/*.prx "$DS_APP0"/sce_module/*.sprx; do
  [ -e "$prx" ] || continue
  name=$(basename "$prx")
  name=${name%.*}
  python3 fself2elf.py "$prx" "$OUT/modules/$name.elf" > /dev/null
  echo "-- $name"
  python3 nidmap.py "$OUT/modules/$name.elf" "$OUT/modules/$name.imports.tsv" --exports "$OUT/modules/$name.exports.tsv" 2>&1 | grep -E '^(imports|exports)'
done

if [ "$GHIDRA" = 1 ]; then
  echo "== 5/5 Ghidra project (auto-analysis of a 48 MB ELF takes a long time)"
  "$HERE/ghidra/import.sh" "$OUT/eboot.elf" "$OUT/imports.tsv" "$OUT/functions.txt" "$RE_OUT/ghidra" DS \
    ${GHIDRA_ARGS[@]+"${GHIDRA_ARGS[@]}"} > "$RE_OUT/ghidra/import.log" 2>&1
  tail -3 "$RE_OUT/ghidra/import.log" | cut -c1-200
  echo "== check: decompile the DS job worker loop (app+0x86ea30)"
  PROJECT_DIR="$RE_OUT/ghidra" "$HERE/ghidra/run.sh" decompile 0x86ea30 | head -15
else
  echo "== 5/5 Ghidra skipped (--no-ghidra)"
fi
echo "done: $OUT"
