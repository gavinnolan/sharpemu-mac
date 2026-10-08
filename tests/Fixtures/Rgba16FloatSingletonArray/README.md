# RGBA16Float singleton array origin reference

This independent driver uses pinned AMD PAL revision `c5e800072a32f68b6ccc4422936d96167c6e0728`, public AddrLib GFX10.3 Navi21 revision0x28, GB_ADDR_CONFIG0x244, mode27 `ADDR_SW_64KB_R_X`, TEX2D, 64bpp `ADDR_FMT_16_16_16_16`, 1x1, one slice/mip/sample, unordered/noMetadata and pipeBankXor0. The format is eight bytes per texel, not eight bits per texel.

The bounded source descriptor requires64KiB-aligned address and no embedded within-block bits. Only origin X/Y/slice0 is qualified; no actual PS5 topology or arbitrary tiled placement is implied. This driver is independent of SharpEmu format placement and detiling.

Actual public API execution reports a128x64x1 physical block/pitch128,65536-byte surface/slice/alignment, zero mip/macro/tail offsets, equation68 and origin byte0/bit-position0. Only that single origin texel is qualified. Reproduce using the pinned PAL checkout and public API library:

```sh
clang++ -std=c++17 -I pal/src/core/imported/addrlib/inc oracle.cpp build/libaddrlib.a -o oracle
./oracle 0x28 0x244 > offset.csv
```

The AMD license, exact driver, emitted geometry and offset CSV are retained here.

Driver SHA256 `bb3b8957b5e51dda5fdc36f6e095d8175936e6b45522e21d6f9aee17ff16bbdf`;
CSV SHA256 `bbecac12e2562db32f1443ee5c8e9b9883fe503056c88b4a8666b0ac3729d18a`.
