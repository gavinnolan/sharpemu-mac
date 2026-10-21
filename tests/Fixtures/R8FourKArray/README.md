# Captured 4K R8 array layout

Pinned public AMD PAL AddrLib c5e800072a32f68b6ccc4422936d96167c6e0728, Navi21/GFX10.3 rev0x28, GB_ADDR_CONFIG0x344, texture+unordered/noMetadata/TEX2D, mode24/8bpp, one mip/layer/sample and pipeBankXor0. Independent Addr2 API calls emit every8,294,400 coordinate; SHA256 B285D9309BEB0FA82956EA8ECA786EFB54F82C167FD06609DF4354810DD7A8BF. The test-only equation transcript must reproduce that full digest. Production Gnm offsets and both detilers matched every logical byte on ARM64 and Rosetta in the separate linked-source proof.

Geometry is pitch3840, padded height2304,15x9 blocks256²/64KiB, total8,847,360B. The8,294,400B logical seed excludes552,960B physical padding; snapshot ownership still requires the entirephysical allocation. Padding/source are never published or altered by GPUfixture writes.

The alternative0x244 four-packer geometry is the same but addresses disagree at(32,0):PAL3072 versusGnm2048. That failing numerical proof is retained. This matches the existing SharpEmu16-pipe/8-packer model; no actualPS5 topology or general mode24/array/mip/pitch support is established. Shared tiling equations are unchanged.

Captured SH andOctopath raw8 words differ only in address:[801A8000 or805B4500,C0500000,021BC3BF,D1800204,0,00700000,0,0]. Only3840x2160/pitch3840/mode24/R8Uint/type13/X001/PERF7/write-only/singlemip/layerzero/64KiBaligned is admitted. Other metadata, typed classes, selectors, dimensions or access intent stayoutside this addition. A fullsuccessfuldetile is mandatory; no raw-as-linear/zero-fabricated fallback.

Native fixtures are not yet executed. Their completeimagebyte/readback, translatedinterior+lastpixelstore, out-of-width/layer bounds and explicitX001sampledview oracle do not qualify production sampled aliases, GPUtiledGuestRAMpublication or whole games. Actual game replay remains required.
