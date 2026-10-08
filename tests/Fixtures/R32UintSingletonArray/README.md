# R32Uint singleton array origin reference

The public AMD PAL AddrLib driver uses pinned PAL revision
`c5e800072a32f68b6ccc4422936d96167c6e0728`, GFX10.3 Navi21 revision0x28,
GB_ADDR_CONFIG0x244, mode27 `ADDR_SW_64KB_R_X`, TEX2D,32bpp `ADDR_FMT_32`,
1x1, one slice/mip/sample, unordered/noMetadata. It is independent of SharpEmu
placement/detiling code. Only pipeBankXor0 is supplied; the bounded aligned
descriptor has no embedded within-block bits. This does not identify the actual
PS5 hardware topology or qualify non-origin tiled offsets.

The actual public API execution reports a128x128x1 physical block/pitch128,
65536-byte surface/slice/alignment, zero mip/macro/tail offsets, equation67 and
origin byte offset0 with bit position0. Only this addressed texel is qualified.
The driver, emitted `offset.csv` and AMD license are retained here.

```sh
clang++ -std=c++17 -I pal/src/core/imported/addrlib/inc oracle.cpp build/libaddrlib.a -o oracle
./oracle 0x28 0x244 > offset.csv
```

Driver SHA256 `7708e6a88ae96497a169b5129b221bef6a39f47779d9291dd1336691410cac24`;
CSV SHA256 `bbecac12e2562db32f1443ee5c8e9b9883fe503056c88b4a8666b0ac3729d18a`.
