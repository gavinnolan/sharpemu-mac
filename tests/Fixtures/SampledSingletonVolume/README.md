This independently checks the only addressed voxel of Superliminal's observed
sampled T#: `70152880 08200000 00000000 A0500FAC 00000000 00700000 00000000 00000000`.
The complete Prospero decoder yields RGBA8sRGB1x1x1,type10,mode5,one mip and
identity components at a4KiB-aligned address. Only the four origin bytes are used;
block padding is not promoted as initialized texture content.

Primary oracle: AMD GPUOpen-Drivers/PAL `c5e800072a32f68b6ccc4422936d96167c6e0728`,
`src/core/imported/addrlib`. The driver calls the public AddrLib API for
GFX10.3/Navi21 revision0x28/config0x244, texture3D32bpp,mode5/ADDR_SW_4KB_S,
1x1x1,one mip,sampled/noMetadata. It reports4KiB allocation/alignment, zero
mip/macro/tail offsets and origin0. Bank-XOR0,1,3,15 leaves origin0 unchanged.
This desktop oracle is not guest allocation ownership, Metal capability or
general3D qualification.

Reproduce with the pinned PAL checkout:

```sh
clang++ -std=c++17 -I pal/src/core/imported/addrlib/inc oracle.cpp build/libaddrlib.a -o oracle
./oracle 0x28 0x244 > offset.csv
```

Driver SHA256 `3ebc64f8c252b869c5319ec8af42ce4997af7de6cc4aaeb4028691ed8544fc35`;
CSV SHA256 `bbecac12e2562db32f1443ee5c8e9b9883fe503056c88b4a8666b0ac3729d18a`.
AMD MIT license retained. No production decoder formula generates this oracle.
