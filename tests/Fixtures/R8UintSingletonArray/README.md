# R8UintSingletonArray origin reference

Independent public AMD AddrLib driver, PAL `c5e800072a32f68b6ccc4422936d96167c6e0728`, GFX10.3 Navi21 rev0x28/GB_ADDR_CONFIG0x244, TEX2D, mode24, 8bpp, 1x1, one slice/mip/sample, unordered/noMetadata and pipeBankXor0.

Actual CPU oracle reports block256x256x1, full65536-byte surface/slice/alignment, zero mip/macro/tail offsets, equation50 and origin byte0/bit0. The qualified descriptor has a64KiB-aligned address and only the origin texel/layer0 is admitted. This is not actual PS5 topology or arbitrary tiled placement qualification.

```sh
clang++ -std=c++17 -I pal/src/core/imported/addrlib/inc oracle.cpp build/libaddrlib.a -o oracle
./oracle 0x28 0x244 > offset.csv 2> geometry.txt
```

Driver SHA256 `fb0e5f982d55dc7d5d608ef795b336b9ed060fde1e315f1bba38543579061f4f`.
CSV SHA256 `bbecac12e2562db32f1443ee5c8e9b9883fe503056c88b4a8666b0ac3729d18a`.
AMD license and exact output retained alongside driver.
