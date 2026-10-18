#!/usr/bin/env bash
# Import a flattened PS5 ELF into a Ghidra project, name its imports, seed function starts, run
# auto-analysis and save.
#
# Usage: import.sh <flat.elf> <imports.tsv> <functions.txt> <project-dir> <project-name> [-noanalysis]
#
# Convention: the program is loaded at image base 0x800000000 (SharpEmu's eboot base), so a Ghidra
# address is the runtime address and "app+X == ELF vaddr X" == Ghidra 0x800000000+X.
# Env: GHIDRA_HOME (default /opt/homebrew/opt/ghidra/libexec), JAVA_HOME (default openjdk@21),
#      GHIDRA_HEADLESS_MAXMEM (default 8G), MAX_CPU (default 8).
set -euo pipefail

if [ $# -lt 5 ]; then sed -n '2,12p' "$0"; exit 2; fi
ELF=$(cd "$(dirname "$1")" && pwd)/$(basename "$1")
IMPORTS=$(cd "$(dirname "$2")" && pwd)/$(basename "$2")
FUNCS=$(cd "$(dirname "$3")" && pwd)/$(basename "$3")
PROJDIR=$4
PROJNAME=$5
EXTRA=("${@:6}")

HERE=$(cd "$(dirname "$0")" && pwd)
export JAVA_HOME=${JAVA_HOME:-/opt/homebrew/opt/openjdk@21/libexec/openjdk.jdk/Contents/Home}
export GHIDRA_HEADLESS_MAXMEM=${GHIDRA_HEADLESS_MAXMEM:-8G}
GHIDRA_HOME=${GHIDRA_HOME:-/opt/homebrew/opt/ghidra/libexec}

mkdir -p "$PROJDIR"
rm -f "$PROJDIR/$PROJNAME.gpr"
rm -rf "$PROJDIR/$PROJNAME.rep"

"$GHIDRA_HOME/support/analyzeHeadless" "$PROJDIR" "$PROJNAME" \
  -import "$ELF" \
  -loader ElfLoader -loader-imagebase 0x800000000 \
  -processor x86:LE:64:default -cspec gcc \
  -scriptPath "$HERE" \
  -preScript SeedFunctions.java "$FUNCS" \
  -preScript ApplyImports.java "$IMPORTS" \
  -max-cpu "${MAX_CPU:-8}" \
  ${EXTRA[@]+"${EXTRA[@]}"}
