# Finite 1024² BGRA array reference

Pinned AMD PAL public AddrLib `c5e800072a32f68b6ccc4422936d96167c6e0728`, Navi21/GFX10.3 rev0x28, TEX2D, mode27/32bpp, one slice/mip/sample, unordered/noMetadata, pipeBankXor0. `oracle.cpp` emits all1,048,576 row-major coordinate addresses as four-byte little-endian offsets, using Addr2ComputeSurfaceAddrFromCoord, not a copied SharpEmu equation. Geometry and representative block boundaries are retained here; full binary and linked-production proof are validation artifacts.

Matching existing SharpEmu model: GB_ADDR_CONFIG0x344 =16 pipes,8 pixel packers,256-byte pipe interleave. Block128²,8×8 blocks, pitch/height1024,4,194,304 surface/slice bytes,64KiB alignment,mip/macro/tail offsets0. All-coordinate SHA256 `f9547a1fc8de057f80dfe2b8bdcd7c66a5b16d3ec6866dd114f03e5221eca964`.

Negative topology: GB_ADDR_CONFIG0x244 =16 pipes,4 packers has same geometry/equation index67 but different equation contents. SHA `5bb28f2d63af4a071a5c8b9b767a411569a7140c10dca4151ac5341f136f5ec8`; at(32,0),0x244 offset3072 vs matching0x344/Sharp2048. Equation index alone is not a portable layout identity. Kairo generated RX61/REmu formula is also distinct (full offset SHA `0b3d3843dca06cac85da1219df33f2768f1ed94d3c1cd1a7800eacee5534eebc`). No equation has been changed or actual PS5 topology measured.

```sh
clang++ -std=c++17 -I pal/src/core/imported/addrlib/inc oracle.cpp build/libaddrlib.a -o oracle
./oracle 0x28 0x344 > offsets-8packers.bin 2> geometry-8packers.txt
./oracle 0x28 0x244 > offsets-4packers.bin 2> geometry-4packers.txt
```

Bgra1024Oracle.cs is test-only and transcribes the emitted0x344 equation; its complete address digest is checked against the independently executed library. Production GetDetileParams/TryDetile/DetileWithParams must agree with every coordinate and full coordinate-distinct seed. This finite qualification excludes mips,layers>0,otherdimensions,pitches,formats/selectors,hints,metadata,nonzero base/XOR address bits and other topology. Native sampled-view fixture is explicit and does not qualify general production sampled-image alias coherence.
