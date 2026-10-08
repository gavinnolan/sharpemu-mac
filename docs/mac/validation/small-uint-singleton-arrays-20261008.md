# Captured R8Uint / RGBA8Uint singleton storage arrays

The signed d756 title runs reached two new exact storage descriptor classes. This candidate adds only those classes to the existing singleton-origin admission. Native fixture execution is pending; no title progression, visual correctness or performance result is inferred from source/managed qualification.

| Title | Raw eight words | Interpreted class |
| --- | --- | --- |
| Silent Hill | `80107800 00500000 0 D1800204 0 00700000 0 0` | R8Uint, unified5, data1/num4, mode24, X001 |
| Octopath | `8030BA00 00500000 0 D1800204 0 00700000 0 0` | Same R8Uint class |
| Oregon | `80115C00 03C00000 0 D1B00FAC 0 00700000 0 0` | RGBA8Uint, unified60, data10/num4, mode27, FAC |

Oregon's earlier RGBA16Uint label was incorrect. The pinned [AMD GFX10 enum](https://github.com/GPUOpen-Drivers/pal/blob/c5e800072a32f68b6ccc4422936d96167c6e0728/src/core/hw/gfxip/gfx9/chip/gfx9_plus_merged_enum.h#L5318) names `0x3C` as `IMG_FMT_8_8_8_8_UINT__GFX10`. True RGBA16Uint is `0x45`, data12/num4, eight bytes, and remains unsupported by this bounded predicate. The existing shared format decoder and Metal decoder agree: R8Uint native13/one byte; RGBA8Uint native73/four bytes. No format reinterpretation or signed storage policy changes occur.

Admission retains exact eight-word decode equality, 64KiB-aligned nonzero origin, type13/native2DArray even for layer1, 1x1/pitch1, base/selected/max/last mip0, layer0, zero metadata/flags and exact PERF_MOD7. New formats require their own tile modes and selectors; moving the tile check into each typed predicate preserves mode27 for every prior class. Unknown/atomic access and contradictory compiled shape/numeric class still reject before source capture/read. Full physical blocks are copied and successfully detiled; only detached one-byte/four-byte origin payloads become native seeds. No raw tiled fallback, zero fabrication, general array/mip/format support, source graph activation or cache alias relaxation is added.

Independent CPU public AddrLib execution uses pinned PAL `c5e800072a32f68b6ccc4422936d96167c6e0728`, Navi21 rev0x28/GB_ADDR_CONFIG0x244, TEX2D, one slice/mip/sample, unordered/noMetadata, pipeBankXor0:

| Class | Mode/bpp | Physical block/pitch/height | Surface/slice/alignment | Equation | Origin |
| --- | --- | --- | --- | --- | --- |
| R8Uint | 24 / 8bpp | 256x256x1 /256/256 | 65536 bytes each | 50 | byte0/bit0 |
| RGBA8Uint | 27 /32bpp | 128x128x1 /128/128 | 65536 bytes each | 67 | byte0/bit0 |

Both report zero mip/macro/tail offsets. Exact independent drivers, CSV, full equation output and AMD license are in `tests/Fixtures/R8UintSingletonArray` and `tests/Fixtures/Rgba8UintSingletonArray`. This bounded aligned-origin proof is not actual PS5 topology or arbitrary tiled coordinate qualification.

Managed controls replay all three title descriptors and complete zero/nonzero owned seeds, verify full64KiB reads/detached raw words, typed byte layout/native array shape, incomplete/unstable seed rejection, numeric and layout contradictions, and raw/shape/flag near misses with zero guest reads. Native fixtures are prepared for each class with actual production snapshot/storage materialization followed by translated MIMG ImageStore/ImageLoad, asymmetric Uint channels, x/slice/outside-layer negatives, full physical byte oracle and every neighboring guest byte unchanged. R8Uint uses seedA7/storeD9 and X001 integer constants0/0/1; RGBA8Uint uses seed17/53/95/E3 and storeCD/2B/91/07. The load uses an explicit test-only same-format native sampled swizzle view. This does not qualify the production sampled resolver, Drain, cross-queue ordering, guest initialization intent or actual title execution. Require-device0 returns after translation; such passes are codegen-only.

The isolated candidate builds passed: incremental Libs test build111 existing warnings/0errors and native fixture project0/0. Final frozen production/test source passed123/123 managed cases on both native ARM64 and Rosetta/x64, plus24/24 translated codegen cases on each CLR with require-device0. The latter include eight new fixture cases (four per class) but no GPU execution. An initial fixture-only build error (slicing IReadOnlyList with Range) was corrected and its failed log retained; no failed runtime result was replaced. Evidence/source/DLL hashes are archived separately before any device grant.

After separate root grant and exact-source peer review, clean `0c2c1f1bd8c48bf0002183d63a6ec439d11e539d` passed all8 actual native cases on ARM64 and all8 on Rosetta/x64 with require-device1, no-build, zero skips and8 actual native proof markers per CLR (four each class). Native physical Uint seed/store and every loaded component matched, all x/layer negatives preserved the original texel, and65535/65532 guest-neighbor bytes stayed unchanged. Seven exact DLL hashes matched the pregrant archive before/after. The shared lock was held for the bounded serialized workers then released in finally; no timeout/KILL or owned runtime survivors remained. These results qualify only the controlled numerical scope above, not title progression or performance.

Managed/source evidence: `artifacts/validation/integrated-20261007/package-agent/smalluint-array-0c2c1f1b` (44 hashes; manifest `bcfa77900248b99145c3520a0887161d8c4f71328f1db7c9e1fdaac08c58f728`). Actual device evidence/source/DLL/TRX/logs: sibling `smalluint-array-native-0c2c1f1b`. The source and evidence are frozen; this paragraph is documentation only.
