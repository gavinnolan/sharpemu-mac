#!/usr/bin/env python3
"""Write the function start addresses recorded in a flattened ELF's PT_GNU_EH_FRAME table.

Usage: funcstarts.py <flat.elf> <functions.txt>

One ELF vaddr (app+X) per line, ascending. Used by callsites.py (caller lookup) and by the Ghidra
SeedFunctions.java pre-script (PS5 ELFs have no section headers, so Ghidra cannot find these itself).
"""
import sys

import pslib


def main():
    if len(sys.argv) != 3:
        raise SystemExit(__doc__)
    starts = pslib.Elf(sys.argv[1]).function_starts()
    with open(sys.argv[2], 'w') as f:
        f.write(''.join(f'0x{a:x}\n' for a in starts))
    print(f'{len(starts)} function starts', file=sys.stderr)


if __name__ == '__main__':
    main()
