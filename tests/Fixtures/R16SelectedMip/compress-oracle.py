#!/usr/bin/env python3
# Copyright (C) 2026 SharpEmu Emulator Project
# SPDX-License-Identifier: GPL-2.0-or-later
# Lossless encoding of retained primary PAL output; no production tiling calls.
import array, hashlib, json, pathlib, struct, sys
source = pathlib.Path(sys.argv[1]).read_bytes()
if len(source) != 5570560 * 4 or hashlib.sha256(source).hexdigest() != "203b9747579bb8b43d144a5c792b4767634c1e20b9390965520339b19ff45945":
    raise SystemExit("Wrong retained primary transcript")
coordinates = array.array("I"); coordinates.frombytes(source)
if sys.byteorder != "little": coordinates.byteswap()
block = [coordinates[y * 2048 + x] - 0x2B0000 for y in range(128) for x in range(256)]
origins = []; cursor = 0; checked = 0
for mip in range(4):
    width = 2048 >> mip; columns = width // 256
    first = len(origins)
    for block_y in range(width // 128):
        for block_x in range(columns):
            origins.append(coordinates[cursor + block_y * 128 * width + block_x * 256])
    for y in range(width):
        row = cursor + y * width
        for x in range(width):
            origin = origins[first + (y // 128) * columns + x // 256]
            if coordinates[row + x] != (origin ^ block[(y % 128) * 256 + x % 256]):
                raise SystemExit(f"Compression differs at mip{mip}/{x}/{y}")
            checked += 1
    cursor += width * width
assert len(origins) == 170 and checked == 5570560
out = pathlib.Path(sys.argv[2]); out.mkdir(parents=True, exist_ok=True)
for name, payload in [("block-offsets.bin", struct.pack("<32768H", *block)),
                      ("block-origins.bin", struct.pack("<170I", *origins))]:
    (out / name).write_bytes(payload)
    print(json.dumps({"file": name, "bytes": len(payload), "sha256": hashlib.sha256(payload).hexdigest()}))
print(json.dumps({"checkedCoordinates": checked, "origins": len(origins), "sourceSha256": hashlib.sha256(source).hexdigest(),
                  "scope": "Lossless retained PAL transcript compression, not new AddrLib execution or topology qualification"}))
