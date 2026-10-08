# Finite R16Float selected storage mips

This increment admits the captured SH/Octo 2048×2048 R16Float resource only as four independent static, write-only selected storage views. It preserves the original guest base and eight-word descriptor; mip 0 is physically after the smaller mips and must use the selected physical upload path too. Mips 4..10, sampled views, allocation-wide aliases, dynamic selection, and other raw descriptor classes remain unsupported.

The exact admitted raw words are `[address>>8, C0D00000 | address>>40, 01FFC1FF, 91B00204 | mip<<12 | mip<<16, 0, 007000A0, 0, 0]`, for a nonzero 64 KiB-aligned base without chain-end overflow. Decoded requirements additionally pin type 9, mode 27, pitch 2048, depth 1, backing 11, X001 destination selection, single selected view, zero metadata/flags/LOD/BC swizzle, Float storage shape and exactly Write access. The native view is R16Float, 2D, one native mip.

| Selected mip | Native width/height | Original physical byte offset | Physical bytes | Source observation prefix |
| --- | --- | --- | --- | --- |
| 0 | 2048 | `0x2B0000` | `0x800000` | `0xAB0000` |
| 1 | 1024 | `0xB0000` | `0x200000` | `0x2B0000` |
| 2 | 512 | `0x30000` | `0x80000` | `0xB0000` |
| 3 | 256 | `0x10000` | `0x20000` | `0x30000` |

The existing full-chain interval is `0xAB0000` bytes. The prefix observations retain the existing selected-storage machinery; they are conservative veto metadata, not mapping leases or permission authority. Only each selected physical interval is copied and detiled. No zero seed, failed-detile linear fallback, descriptor rebase, snapshot-cap increase, or extra retry is introduced.

## Independent placement evidence

The preserved CPU archive `artifacts/validation/all-games-20261008/r16float-selected-mip-source-audit` records AMD PAL AddrLib commit `c5e800072a32f68b6ccc4422936d96167c6e0728`, GFX10 revision 0x28, GB_ADDR_CONFIG 0x344, TEX2D/ADDR_SW_64KB_R_X, 16bpp, 2048×2048, backing 11, single slice/sample/fragment and pipeBankXor0. All 5,570,560 selected coordinates match the existing shared Gnm equation, and both production detilers reproduce varied seeds on ARM64 and x64. Counter-configuration 0x244 differs at 5,218,304 coordinates. The actual PS5 ASIC topology remains unknown; this is conditional qualification of the existing modeled equation, not a general layout change.

The archive manifest SHA256 is `41e5bde5420a0562ee02bce9b12d1e9dad3208c11357bd6aa3000264160548e1`. Its full0x344 coordinate transcript SHA256 is `203b9747579bb8b43d144a5c792b4767634c1e20b9390965520339b19ff45945`. The compact test fixture derives the first256×128 block independently from that transcript, with all 32768 even byte offsets and SHA256 `9a618f4312dd6c904f38db9798aa2bbe42176f9aa453a6dafcf012384cc5bf6c`. The companion170-entry block-origin table is680 bytes with SHA256 `888284d426e295e4811eb4a64f0a04791d66021f5bc63ec5304604eb9ff6dc2c`. A retained-transcript compression script verifies every5,570,560 offset reconstructs as recorded origin XOR local table; origins retain coordinate-dependent low XOR bits. Test seed construction uses these independently verified tables rather than a copied production tiling formula. Existing fixture license attribution is retained.

## Admission and ownership limits

The selected association retains ICpuMemory identity, complete original resource identity, selected mip, presenter session, global mapping epoch and content token/observation. Global epoch, content, session, producer ordering, logical/native queue and native-owner validation remain unchanged. Compatible views differ only in BaseLevel/LastLevel and have independently proven disjoint selected intervals in the same canonical chain. Temporary views within one dispatch participate in conflict checks before queue publication. Conflicting ordinary images, known spanning extents and unknown extents reject before native construction and are checked again before publication. These extents are conservative presenter metadata, not kernel allocation authority.

R16 native construction keeps its +1 retain private until post-hook session/epoch/content validation and exact current object identity succeed. Rollback removes only that exact construction object. Accepted R16 ownership is consumed before release callbacks, preventing reentrant close or later cleanup from retrying an uncertain retain. The first uncertain release cause remains sticky, rejects future R16 reuse, and preserves a primary construction failure plus secondary cleanup context. A later DS cleanup exception cannot replace an already-recorded R16 cause.

The older DS selected-mip publication/unknown-release behavior is not repaired by this R16-specific increment. A DS exception can still leave incomplete shared metadata close; no mixed-class full cleanup or DS lifecycle certification is claimed. Native retain ownership itself supplies no guest mapping/content/writer lease. Ordinary backing access, Poppy recovery, the optional mapped-copy contract and the default-disabled GPU source graph remain separate and unchanged.

## Controls prepared; execution pending

Source controls cover genuine producer reads/detiles and every independent Half byte; exact physical offsets for 0..3; near-raw descriptor and shape rejection before any read; existing three-attempt content-change exhaustion; global epoch/content/session veto; same-chain disjoint coexistence; same-dispatch overlapping R16 chains and mixed DS/R16 in both orders; known/unknown ordinary extents; cross-queue rejection; exact-owner publication replacement/close; consumed-once reentrant release and sticky uncertainty. Scalar retain seams are ordinary safe-side tests and do not prove native refcounts.

Four opt-in device cases are prepared for actual production native materialization of all four independent views, full varied R16Float seed readback, translated Float ImageStore at last-valid/out-of-width/negative coordinates, every other native texel unchanged, guest chain/padding untouched and live reuse without rereading CPU bytes. The translated load-only resource is exercised through an explicit native X001 sampled view; it does not qualify production sampled-mip admission, storage ImageLoad or full ComputeDispatch ordering/coherence. Each native process must assert its actual CLR architecture and required device flag. These candidate controls have not been compiled or executed at this source checkpoint; existing independent CPU placement evidence above is separate.

## Preserved first qualification failures

The original9d build failed solely on a test uint→IReadOnlyList integer index and is archived unchanged. The501 ARM selection then passed54/59 genuine cases, with no x64/device run: four seed comparisons exposed the incomplete first-block-only fixture encoding, and the unchanged three-attempt repeated-write control exposed a separate existing managed-watch generation bug. Neither result qualified native production materialization.

The fixture now carries independently recorded macro origins and verified lossless compression of all retained PAL coordinates. A separate production commit advances managed-watch generation on every intersecting notification even when Dirty is already set; Dirty consumption remains coalesced. This restores observational retry revocation without supplying write-completion exclusion, raw/native alias coverage or mapping leases. The failing R16 retry assertion is preserved unchanged. Fresh exact-source build/managed/device execution is still pending at this source checkpoint.
