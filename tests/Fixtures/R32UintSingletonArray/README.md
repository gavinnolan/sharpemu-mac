# R32Uint singleton array origin reference

The public AMD PAL AddrLib driver uses pinned PAL revision
`c5e800072a32f68b6ccc4422936d96167c6e0728`, GFX10.3 Navi21 revision0x28,
GB_ADDR_CONFIG0x244, mode27 `ADDR_SW_64KB_R_X`, TEX2D,32bpp `ADDR_FMT_32`,
1x1, one slice/mip/sample, unordered/noMetadata. It is independent of SharpEmu
placement/detiling code. Only pipeBankXor0 is supplied; the bounded aligned
descriptor has no embedded within-block bits. This does not identify the actual
PS5 hardware topology or qualify non-origin tiled offsets.

The driver source and AMD license are retained here. Oracle execution and emitted
CSV are pending the compiler window; no address result is claimed yet.
