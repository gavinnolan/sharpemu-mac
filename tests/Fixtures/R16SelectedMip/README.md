# Independent finite R16Float selected-mip oracle

AMD PAL `c5e800072a32f68b6ccc4422936d96167c6e0728` AddrLib public API, GFX10 revision0x28/GB_ADDR_CONFIG0x344, TEX2D/ADDR_SW_64KB_R_X/16bpp/2048×2048/backing11/single slice/sample/fragment/pipeBankXor0. All 5,570,560 selected0..3 coordinates were independently emitted and matched actual shared Sharp placement and BOTH detilers on ARM64+x64. The source-linked full proof is root `artifacts/validation/all-games-20261008/r16float-selected-mip-source-audit`, manifest SHA256 `41e5bde5420a0562ee02bce9b12d1e9dad3208c11357bd6aa3000264160548e1`.

`block-offsets.bin` is the first256×128 block from that independent full-coordinate transcript: little-endian uint16 BYTE offset at(x,y), subtract selected0 macro offset0x2B0000. It has32768 unique even positions0..65534,65536 bytes. SHA256 `9a618f4312dd6c904f38db9798aa2bbe42176f9aa453a6dafcf012384cc5bf6c`.

`block-origins.bin` contains170 little-endian uint32 original-chain absolute byte offsets at each256×128 block origin, ordered mip0..3, then block row/column. Mip starts are0/128/160/168. It is680 bytes, SHA256 `888284d426e295e4811eb4a64f0a04791d66021f5bc63ec5304604eb9ff6dc2c`. These origins include coordinate-dependent low XOR bits. The first fixture at501 omitted them and genuine producer byte tests failed; that failure is retained, not dismissed as production qualification.

`compress-oracle.py` checks the retained full transcript SHA256 `203b9747579bb8b43d144a5c792b4767634c1e20b9390965520339b19ff45945`, extracts both tables, and verifies **every one of5,570,560 byte offsets** equals recorded block origin XOR first-block local offset. This is lossless encoding of already-retained primary output, not a new production tiling formula, PAL execution, or topology inference. Run with the retained transcript path and output directory. The test seed uses only these verified tables to write independent varied finite Half values, without production Gnm calls.

Full chain0xAB0000; selected0..3 offsets2B0000/B0000/30000/10000, sizes800000/200000/80000/20000. The64KiB tail is sentinel padding in these tests, not qualified tail data. Counter-config0x244 differs5,218,304 positions. Actual PS5 ASIC topology is unknown; this fixture qualifies only the existing modeled0x344 equation. Native/guest coherence is separate.

Origin of generated numeric data: AMD AddrLib, MIT; see existing `Tile5Volume/AMD-MIT-LICENSE.txt` for retained upstream license.

The native fixture separates translated storage-write bounds from sampled-view
channel defaults. Every write compares all bytes of all four native mip views;
out-of-width and negative-X writes must leave those bytes unchanged. The separate
translated load-only shader reads an explicit R16Float X001 sampled texture view,
so its expected vector is `[inBounds ? 42.5 : 0, 0, 0, 1]`. This tests that native
view's constant alpha, not production sampled-mip admission or storage ImageLoad
semantics. Apple's [Metal Shading Language specification](https://developer.apple.com/metal/Metal-Shading-Language-Specification.pdf),
2026-06-04 edition, pages239–240, distinguishes ignored out-of-bounds writes from
out-of-bounds reads: format-specified components return zero, while unspecified
components keep their defaults, including floating alpha1.0. The retained PDF SHA256
is `41538b30d2f1140a5b2a0c84ce0a9f7b67bf0c707e224cfea0bfe5a44aa26cf5`
at root `artifacts/validation/all-games-20261008/optimization-research-20261008/Metal-Shading-Language-Specification.pdf`.

The original a38 fixture expected all four sampled channels to become zero outside
the width. Its four ARM failures are preserved. Diagnostic-only b507 also retains
four failures with exact coordinates, full channel vectors, and generated MSL:
valid reads returned `[42.5, 0, 0, 1]`; first out-of-width reads returned
`[0, 0, 0, 1]` after complete-byte store checks passed. Their archives are root
`artifacts/validation/integrated-20261007/cpu-r16-selected-{a38a1c58,b5078585}`.
The corrected expectation still requires a fresh native run; the failed runs did
not reach negative-X or post-loop reuse and are not full fixture qualification.
