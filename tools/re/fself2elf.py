#!/usr/bin/env python3
"""Flatten a fake-signed PS4/PS5 SELF (eboot.bin, *.prx, *.sprx) into a plain ELF.

Usage: fself2elf.py <input.self> <output.elf>

Fake-signed SELFs keep their segments in plaintext, so flattening is a copy: the embedded ELF
header + program headers are lifted out and every SELF segment that carries data is written to
the file offset its ELF program header names. The result has the same layout the loader sees:
runtime address = image base + ELF p_vaddr (SharpEmu maps the eboot at base 0x800000000, so
"app+X == ELF vaddr X"). Real (encrypted/compressed) SELFs are not supported.
"""
import struct
import sys

SELF_MAGIC = 0x1D3D154F


def flatten(data: bytes, log=print) -> bytes:
    magic, = struct.unpack_from('<I', data, 0)
    if magic != SELF_MAGIC:
        raise SystemExit('already an ELF, nothing to flatten' if data[:4] == b'\x7fELF'
                         else f'not a SELF (magic 0x{magic:08x})')
    num_entries, = struct.unpack_from('<H', data, 0x18)
    entries = [struct.unpack_from('<QQQQ', data, 0x20 + i * 0x20) for i in range(num_entries)]
    elf_off = 0x20 + num_entries * 0x20
    if data[elf_off:elf_off + 4] != b'\x7fELF':
        raise SystemExit('no ELF header after the SELF segment table')
    e_phoff, = struct.unpack_from('<Q', data, elf_off + 0x20)
    e_phentsize, e_phnum = struct.unpack_from('<HH', data, elf_off + 0x36)
    phdrs = [struct.unpack_from('<IIQQQQQQ', data, elf_off + e_phoff + i * e_phentsize)
             for i in range(e_phnum)]  # type, flags, offset, vaddr, paddr, filesz, memsz, align
    hdr_len = e_phoff + e_phnum * e_phentsize
    out = bytearray(max([hdr_len] + [p[2] + p[5] for p in phdrs]))
    out[0:hdr_len] = data[elf_off:elf_off + hdr_len]
    filled = 0
    for props, off, filesz, memsz in entries:
        if not (props >> 11) & 1:  # bit 11 = segment has blocks (data); otherwise a metadata entry
            continue
        idx = (props >> 20) & 0xFFF  # index of the matching ELF program header
        p = phdrs[idx]
        n = min(filesz, p[5])
        out[p[2]:p[2] + n] = data[off:off + n]
        filled += 1
        log(f'segment {idx}: type=0x{p[0]:x} vaddr=0x{p[3]:x} filesz=0x{n:x} -> elf off 0x{p[2]:x}')
    log(f'{len(out)} bytes, {filled} data segments, {e_phnum} program headers')
    return bytes(out)


def main():
    if len(sys.argv) != 3:
        raise SystemExit(__doc__)
    with open(sys.argv[1], 'rb') as f:
        data = f.read()
    out = flatten(data)
    with open(sys.argv[2], 'wb') as f:
        f.write(out)
    print(f'wrote {sys.argv[2]}')


if __name__ == '__main__':
    main()
