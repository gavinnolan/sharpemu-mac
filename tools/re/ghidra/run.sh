#!/usr/bin/env bash
# Query a saved Ghidra project headlessly (read-only, no re-analysis).
#
# Usage: run.sh decompile <addr> [addr ...]   C-like text of the function containing each address
#        run.sh xrefs <addr> [addr ...]       every reference to each address (from, type, function)
#   addr: hex; below 0x800000000 = app+X (ELF vaddr), otherwise an absolute runtime address.
# Env: PROJECT_DIR (default <main checkout>/artifacts/re/ghidra), PROJECT (default DS),
#      PROGRAM (default eboot.elf), GHIDRA_HOME, JAVA_HOME, GHIDRA_HEADLESS_MAXMEM (default 4G).
# Ghidra locks a project while open: don't run two queries (or a query during import.sh) at once.
# Output is the script's text only (Ghidra's own log goes to $PROJECT_DIR/run.log).
set -euo pipefail

if [ $# -lt 2 ]; then sed -n '2,10p' "$0"; exit 2; fi
CMD=$1
shift
case $CMD in
  decompile) SCRIPT=DecompileFunction.java ;;
  xrefs) SCRIPT=DumpXrefs.java ;;
  *) echo "unknown command $CMD" >&2; exit 2 ;;
esac

HERE=$(cd "$(dirname "$0")" && pwd)
MAIN=$(cd "$HERE/../../.." && pwd)
case $MAIN in */.claude/worktrees/*) MAIN=${MAIN%%/.claude/worktrees/*} ;; esac
PROJECT_DIR=${PROJECT_DIR:-$MAIN/artifacts/re/ghidra}
export JAVA_HOME=${JAVA_HOME:-/opt/homebrew/opt/openjdk@21/libexec/openjdk.jdk/Contents/Home}
export GHIDRA_HEADLESS_MAXMEM=${GHIDRA_HEADLESS_MAXMEM:-4G}
GHIDRA_HOME=${GHIDRA_HOME:-/opt/homebrew/opt/ghidra/libexec}

OUT=$(mktemp)
trap 'rm -f "$OUT"' EXIT
"$GHIDRA_HOME/support/analyzeHeadless" "$PROJECT_DIR" "${PROJECT:-DS}" \
  -process "${PROGRAM:-eboot.elf}" -noanalysis -readOnly \
  -scriptPath "$HERE" \
  -postScript "$SCRIPT" "$OUT" "$@" > "$PROJECT_DIR/run.log" 2>&1 || {
  echo "ghidra failed, see $PROJECT_DIR/run.log" >&2
  tail -20 "$PROJECT_DIR/run.log" >&2
  exit 1
}
cat "$OUT"
