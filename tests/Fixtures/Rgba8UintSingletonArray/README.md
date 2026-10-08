# Rgba8UintSingletonArray origin reference

Independent public AMD AddrLib driver, PAL `c5e800072a32f68b6ccc4422936d96167c6e0728`, GFX10.3 Navi21 rev0x28/GB_ADDR_CONFIG0x244, TEX2D, mode27, 32bpp, 1x1, one slice/mip/sample, unordered/noMetadata and pipeBankXor0.

Actual CPU oracle reports block128x128x1, full65536-byte surface/slice/alignment, zero mip/macro/tail offsets, equation67 and origin byte0/bit0. The qualified descriptor has a64KiB-aligned address and only the origin texel/layer0 is admitted. This is not actual PS5 topology or arbitrary tiled placement qualification.

```sh
clang++ -std=c++17 -I pal/src/core/imported/addrlib/inc oracle.cpp build/libaddrlib.a -o oracle
./oracle 0x28 0x244 > offset.csv 2> geometry.txt
```

Driver SHA256 `aca01f06db7807f12c3ebc6f32d96644ac85421a8547c14d6bc15c5a183036ee`.
CSV SHA256 `bbecac12e2562db32f1443ee5c8e9b9883fe503056c88b4a8666b0ac3729d18a`.
AMD license and exact output retained alongside driver.
