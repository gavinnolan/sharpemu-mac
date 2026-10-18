#!/usr/bin/env python3
"""Extract the raw guest shader code from a SharpEmu shader-corpus record (.corpus) into a .bin.

Usage: corpus2bin.py <record.corpus> <out.bin>

Corpus records are written by SHARPEMU_SHADER_CORPUS_DIR (src/SharpEmu.Libs/Gpu/Pipelines/ShaderCorpus.cs):
  u32 magic 'SECP' (0x50434553), u32 version 1, i32 codeLength, <code record>, <JSON body>
  code record (ShaderPrewarmList.SerializeCode): u64 hash, u32 codeSize, u64 address, i32 generation,
  u8 fused [+ 3 x u64], i32 rangeCount, rangeCount x (u64 address, i32 length, bytes).
The ranges are written back to back in record order; the guest address and range layout go to stderr.
"""
import struct
import sys


def extract(data: bytes):
    magic, version, code_len = struct.unpack_from('<IIi', data, 0)
    if magic != 0x50434553 or version != 1:
        raise SystemExit('not a version 1 shader corpus record')
    p = 12
    code_hash, code_size, address, generation = struct.unpack_from('<QIQi', data, p)
    p += 24
    fused = data[p]
    p += 1 + (24 if fused else 0)
    count, = struct.unpack_from('<i', data, p)
    p += 4
    ranges = []
    for _ in range(count):
        addr, n = struct.unpack_from('<Qi', data, p)
        p += 12
        ranges.append((addr, data[p:p + n]))
        p += n
    return code_hash, code_size, address, generation, bool(fused), ranges


def main():
    if len(sys.argv) != 3:
        raise SystemExit(__doc__)
    with open(sys.argv[1], 'rb') as f:
        code_hash, code_size, address, generation, fused, ranges = extract(f.read())
    blob = b''.join(r[1] for r in ranges)
    with open(sys.argv[2], 'wb') as f:
        f.write(blob)
    print(f'hash=0x{code_hash:016x} codeSize={code_size} address=0x{address:x} generation={generation} fused={fused} '
          f'ranges={[(hex(a), len(b)) for a, b in ranges]} -> {len(blob)} bytes', file=sys.stderr)


if __name__ == '__main__':
    main()
