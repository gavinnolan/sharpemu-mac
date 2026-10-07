The CSV is the independent AMD AddrLib output, in z/y/x order, for the captured R16Uint T# descriptor:

`7AC109C0 C0B00000 0003C003 A0500004 0000000F 00700000 00000000 00000000`

Primary source: GPUOpen-Drivers/pal commit `c5e800072a32f68b6ccc4422936d96167c6e0728`, `src/core/imported/addrlib` (AMD MIT license in the AddrLib source headers; retained here as `AMD-MIT-LICENSE.txt`). The standalone `oracle.cpp` calls the public AddrLib API; it does not use the production decoder formula. CSV SHA256: `f2f46cb04772b76e8d15823a98be422c1e1ac3074965a4e0ce551afe00bed690`.

Reproduce from that PAL checkout:

```sh
cmake -S pal/src/core/imported/addrlib -B build -DCMAKE_BUILD_TYPE=Release -DADDR_GFX10_BUILD=ON -DADDR_GFX11_BUILD=OFF -DADDR_GFX12_BUILD=OFF
cmake --build build -j1
clang++ -std=c++17 -I pal/src/core/imported/addrlib/inc oracle.cpp build/libaddrlib.a -o oracle
./oracle 0x28 0x244 > offsets.csv
```

GFX10.3 Navi21 revision0x28, mode5/ADDR_SW_4KB_S, texture3D,16bpp,16x16x16,one mip,texture+unordered+noMetadata flags: block8x16x16; pitch16,height16,slices16;8192-byte surface;4096-byte base alignment; mip/macro/tail offsets0. All4096 addresses unique, even,0..8190. pipeBankXor values0,1,3,15 produce identical offsets (non-XOR mode).

Independent executions with Navi21 GB_ADDR_CONFIG0x142/0x244/0x343, Navi22 revision0x32 config0x244, Navi23 revision0x3c config0x142 and Navi10 revision1 config4 produced byte-identical CSVs. These are oracle configurations, not a claim that the guest is any particular desktop ASIC.

The format extraction is `(word1 >> 20) & 0x1FF`: `C0B00000` decodes to0x0B/R16Uint, not0xB0/BC4. Native image load uses the actual DstSelect4/X000 component mapping. This fixture qualifies the exact single-mip metadata-zero layout only; no general3D swizzle, compressed resource, mip tail, array alias or CPU/GPU lifetime certification.
