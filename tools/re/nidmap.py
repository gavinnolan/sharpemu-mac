#!/usr/bin/env python3
"""Resolve a flattened PS4/PS5 ELF's imports (and exports) to NIDs, names, libraries and modules.

Usage: nidmap.py <flat.elf> <imports.tsv> [--exports exports.tsv] [--names FILE ...]

imports.tsv columns (tab separated, header line starts with '#'):
  slot     GOT slot vaddr (app+X) the loader fills in (R_X86_64_JUMP_SLOT / GLOB_DAT / 64 target)
  plt      vaddr of the `jmp [rip+slot]` PLT stub for that slot ('-' if none, e.g. data imports)
  reloc    jump_slot | glob_dat | abs64
  nid      11-char NID from the symbol name "NID#libid#modid"
  name     resolved export name, or '?' if no database knows the NID
  via      where the name came from: ps5_names.txt | hle-label | hle-comment | hle-string | ref-kyty | -
  library  import library name (DT_SCE_IMPORT_LIB, e.g. libkernel)
  module   module name (DT_SCE_NEEDED_MODULE, e.g. libkernel)
  addend   relocation addend (non-zero for abs64 slots that point inside an import)

Name sources: scripts/ps5_names.txt (hashed with the PS5 NID algorithm), HLE exports under src/
(SysAbiExport pairs, NID comments, identifier string literals), Kyty LIB_FUNC registrations under
~/Documents/ps5emu/refs if present, plus any --names files (bare names, or NID/name pairs).
Names marked via=hle-label / ref-kyty can be working labels rather than the real export name.
Prints a resolution summary on stderr. A slot that several relocations target keeps its first entry.
"""
import argparse
import sys
from collections import Counter

import pslib

RELOC_KINDS = {pslib.R_X86_64_JUMP_SLOT: 'jump_slot', pslib.R_X86_64_GLOB_DAT: 'glob_dat', pslib.R_X86_64_64: 'abs64'}


def split_symbol(raw):
    parts = raw.split('#')
    if len(parts) == 3:
        return parts[0], parts[1], parts[2]
    return raw, None, None


def lookup_id(table, text):
    if text is None:
        return '?'
    try:
        return table.get(pslib.decode_id(text), '?')
    except ValueError:
        return '?'


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument('elf')
    ap.add_argument('imports_out')
    ap.add_argument('--exports', help='also write exports.tsv (value nid name library module)')
    ap.add_argument('--names', action='append', default=[], help='extra NID/name list (repeatable)')
    args = ap.parse_args()

    elf = pslib.Elf(args.elf)
    modules, imp_libs, exp_libs = elf.modinfo()
    db = pslib.default_namedb(args.names)
    print(f'name db: {len(db.map)} NIDs from {db.sources}', file=sys.stderr)

    syms = {i: (raw, info, shndx, value) for i, raw, info, shndx, value in elf.symbols()}
    slots = {}  # slot vaddr -> (kind, sym index, addend)
    for kind, off, rtype, sym, addend in elf.relocs():
        k = RELOC_KINDS.get(rtype)
        if k is None or sym == 0:
            continue
        if sym in syms and syms[sym][2] != 0:  # defined in this module (export): not an import slot
            continue
        slots.setdefault(off, (k, sym, addend))

    plt = pslib.plt_stubs(elf, [s for s, v in slots.items() if v[0] == 'jump_slot'])
    rows = []
    for slot, (k, sym, addend) in sorted(slots.items()):
        raw = syms[sym][0]
        nid, lib_id, mod_id = split_symbol(raw)
        name = db.get(nid) or '?'
        rows.append((slot, plt.get(slot), k, nid, name, lookup_id(imp_libs, lib_id), lookup_id(modules, mod_id), addend,
                     db.source_of.get(nid, '-')))

    with open(args.imports_out, 'w') as f:
        f.write('#slot\tplt\treloc\tnid\tname\tlibrary\tmodule\taddend\tvia\n')
        for slot, p, k, nid, name, lib, mod, addend, via in rows:
            f.write(f'0x{slot:x}\t{"0x%x" % p if p else "-"}\t{k}\t{nid}\t{name}\t{lib}\t{mod}\t{addend}\t{via}\n')

    uniq = {r[3] for r in rows}
    resolved = {r[3] for r in rows if r[4] != '?'}
    by_kind = Counter(r[2] for r in rows)
    print(f'imports: {len(rows)} slots {dict(by_kind)}, {len(uniq)} unique NIDs, '
          f'{len(resolved)} resolved ({100.0 * len(resolved) / max(1, len(uniq)):.1f}% of unique NIDs, '
          f'{100.0 * sum(1 for r in rows if r[4] != "?") / max(1, len(rows)):.1f}% of slots); '
          f'{sum(1 for r in rows if r[1])} slots have a PLT stub', file=sys.stderr)
    unresolved = Counter(r[5] for r in rows if r[4] == '?')
    if unresolved:
        print('unresolved by library:', dict(unresolved.most_common(10)), file=sys.stderr)

    if args.exports:
        n = 0
        with open(args.exports, 'w') as f:
            f.write('#value\tnid\tname\tlibrary\tmodule\n')
            for i, (raw, info, shndx, value) in sorted(syms.items()):
                if shndx == 0 or not raw:
                    continue
                nid, lib_id, mod_id = split_symbol(raw)
                name = db.get(nid) or '?'
                f.write(f'0x{value:x}\t{nid}\t{name}\t{lookup_id(exp_libs, lib_id)}\t{lookup_id(modules, mod_id)}\n')
                n += 1
        print(f'exports: {n}', file=sys.stderr)


if __name__ == '__main__':
    main()
