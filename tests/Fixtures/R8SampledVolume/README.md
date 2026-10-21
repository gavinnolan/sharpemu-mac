# Exact R8Unorm 4x4x16 sampled volume reference

Primary AMD PAL AddrLib revision `c5e800072a32f68b6ccc4422936d96167c6e0728`,
public32-bit API driver `oracle.cpp`, mode5 `ADDR_SW_4KB_S`, TEX3D8bpp,
4x4x16, one mip/sample/slice range, sampled/noMetadata. Navi10 and five Navi21 modeled
configurations produced identical256-voxel CSVs. This does not identify actual PS5 topology or prove backing ownership.

`offsets.csv` is the independently emitted Navi21/16pipe/4packer CSV, x/y/z/byte
offset. The4KiB physical block has16x16x16 padded geometry; logical4x4x16 has256
bytes, max addressed offset1215. Origin0, z1offset4, z8offset1024 prove true XYZ
addressing. PAL equation70 (Navi10)/equation80 (Navi21) orders bits
x0,x1,z0,y0,z1,y1,x2,z2,y2,x3,z3,y3.
Kairo's equation118 matches all256 offsets, while a hypothetical stacked-XY
implementation disagrees on248. No production decoder generates the fixture.

The actual guest descriptor is
`701529A0 C0100000 0000C000 A0500800 0000000F 00700000 00000000 00000000`:
R8Unorm4x4x16/pitch4, native3D, mode5, one base mip, PERF_MOD7, `000R` channels.
Production qualification remains exact and read-only, rather than admitting
general tiling, reinterpretation, cache/live GPU backing or concurrent writer
lifetimes. AMD MIT license retained.
