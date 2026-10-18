#!/usr/bin/env python3
"""List every direct reference to an import in a flattened PS4/PS5 ELF.

Usage: callsites.py <flat.elf> <imports.tsv> <callsites.tsv>

imports.tsv is nidmap.py's output. PS5 code reaches an import through a PLT stub (`jmp [rip+slot]`),
so real callers are `E8 rel32` (call) / `E9 rel32` (tail jump) aimed at the stub. Code can also
call/jmp through the GOT slot directly (`FF 15` / `FF 25`), load the slot (`mov r64,[rip+slot]`,
used for GLOB_DAT imports) or take a stub's address (`lea r64,[rip+stub]`, i.e. a function pointer).
Data references (abs64 relocations that bind a slot inside a vtable/table) are listed too.

callsites.tsv columns (tab separated, '#' header): all addresses are ELF vaddrs (app+X).
  call_addr  address of the referencing instruction (or of the data slot for kind=data)
  kind       call | tail | call_got | jmp_got | load_got | lea_stub | data
  caller     start of the containing function (from PT_GNU_EH_FRAME's table), or '-' if unknown
  name       resolved import name ('?' unresolved)
  nid        NID
  library    import library
  plt        PLT stub address ('-' if the import has none)
The scan is a byte-pattern scan: a hit needs a pattern *and* a target that is exactly a stub/slot, so
false positives are negligible, but call sites inside data embedded in .text are not distinguished.
"""
import re
import struct
import sys
from collections import Counter

import pslib


def main():
    if len(sys.argv) != 4:
        raise SystemExit(__doc__)
    elf = pslib.Elf(sys.argv[1])
    plt, got, info = {}, {}, {}  # stub -> slot, slot -> slot, slot -> (nid, name, library, reloc)
    with open(sys.argv[2]) as f:
        for line in f:
            if line.startswith('#') or not line.strip():
                continue
            slot, p, reloc, nid, name, lib = line.rstrip('\n').split('\t')[:6]
            slot = int(slot, 16)
            info[slot] = (nid, name, lib, reloc, None if p == '-' else int(p, 16))
            got[slot] = slot
            if p != '-':
                plt[int(p, 16)] = slot
    starts = elf.function_starts()
    rows = []

    def add(addr, kind, slot):
        nid, name, lib, reloc, p = info[slot]
        fn = pslib.containing_function(starts, addr)
        rows.append((addr, kind, fn, name, nid, lib, p))

    rel32 = re.compile(rb'(?=([\xe8\xe9])(....))', re.S)
    ff = re.compile(rb'(?=\xff([\x15\x25])(....))', re.S)
    lea = re.compile(rb'(?=[\x48\x4c]([\x8b\x8d])([\x05\x0d\x15\x1d\x25\x2d\x35\x3d])(....))', re.S)
    for va, off, size in elf.exec_ranges():
        buf = elf.data[off:off + size]
        for m in rel32.finditer(buf):
            d, = struct.unpack('<i', m.group(2))
            tgt = va + m.start() + 5 + d
            if tgt in plt:
                add(va + m.start(), 'call' if m.group(1) == b'\xe8' else 'tail', plt[tgt])
        for m in ff.finditer(buf):
            d, = struct.unpack('<i', m.group(2))
            tgt = va + m.start() + 6 + d
            if tgt in got:
                if va + m.start() in plt:
                    continue  # the PLT stub itself
                add(va + m.start(), 'call_got' if m.group(1) == b'\x15' else 'jmp_got', tgt)
        for m in lea.finditer(buf):
            d, = struct.unpack('<i', m.group(3))
            tgt = va + m.start() + 7 + d
            if m.group(1) == b'\x8b' and tgt in got:
                add(va + m.start(), 'load_got', tgt)
            elif m.group(1) == b'\x8d' and tgt in plt:
                add(va + m.start(), 'lea_stub', plt[tgt])
    for slot, (nid, name, lib, reloc, p) in info.items():
        if reloc == 'abs64':
            rows.append((slot, 'data', None, name, nid, lib, p))
    rows.sort(key=lambda r: (r[0], r[1]))

    with open(sys.argv[3], 'w') as f:
        f.write('#call_addr\tkind\tcaller\tname\tnid\tlibrary\tplt\n')
        for addr, kind, fn, name, nid, lib, p in rows:
            f.write(f'0x{addr:x}\t{kind}\t{"0x%x" % fn if fn is not None else "-"}\t{name}\t{nid}\t{lib}\t{"0x%x" % p if p else "-"}\n')
    kinds = Counter(r[1] for r in rows)
    code = [r for r in rows if r[1] != 'data']
    print(f'{len(rows)} references ({dict(kinds)}); {len(code)} in code, '
          f'{sum(1 for r in code if r[2] is None)} without a known caller function; '
          f'{len({r[4] for r in code})} distinct imports referenced from code; {len(starts)} function starts known',
          file=sys.stderr)


if __name__ == '__main__':
    main()
