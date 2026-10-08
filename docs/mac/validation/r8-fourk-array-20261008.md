# Captured 4K R8 singleton array

Candidate based on `2adf328d`; production at `3d8a6b94`, corrected fixtures and qualification at `594dfbb5`.

Silent Hill and Octopath expose an exact 3840 × 2160 R8Uint, mode24, type13, X001, write-only singleton array. Admission requires all eight captured descriptor words and matching typed/scalar metadata. A complete padded 8,847,360-byte allocation is read and tracked; the owned 8,294,400-byte detiled seed supplies every logical native texel. Partial reads, changed metadata, other layers/mips/access/format and fabricated zero seeds remain rejected. Existing shared tiling equations and alias/coherence policies are unchanged.

Public AMD PAL AddrLib `c5e800072a32f68b6ccc4422936d96167c6e0728`, Navi21 config0x344, produces the same 8,294,400 offsets as actual SharpEmu Gnm source on ARM64 and Rosetta. Both production detilers match every logical byte without mutating source/padding. The alternate four-packer config0x244 disagrees at (32,0). This proves agreement with the existing emulator model; actual console topology remains unqualified. Test transcript, geometry and pinned API driver are in `tests/Fixtures/R8FourKArray`.

The same frozen neutral DLLs passed, separately on both CLRs:

- 277 genuine Libs checks: 21 new R8 rows and 256 existing regressions.
- 77 genuine GPU metadata, ownership and admission controls.
- Four actual Metal cases with required device and architecture assertion. Full seed/post-write readback, translated Uint writes at interior/last texel, invalid width/layer bounds and explicit X001 sampled output all match.

The actual native path is production snapshot → `EnsureStorageImage` plus translated shaders. It does not establish production sampled-alias resolution, whole presenter ordering, guest tiled-RAM publication, gameplay or FPS. DLL hashes match before/after; all runners returned0 with no skips/timeouts. An initial missing test import build failure is retained; final Libs build has126 existing warnings/0errors and GPU build0warnings/0errors.

Evidence: `artifacts/validation/all-games-20261008/r8-fourk-594dfbb5` (full source, DLLs, TRXs, logs, manifest), `r8-4k-oracle-preparation` (all-coordinate primary oracle and linked-source proof), and `integrated-20261007/cpu-r8-fourk-review-3d8a6b94` (source review). A clean signed candidate `073fb937` was replayed separately in Silent Hill and Octopath. Both pass the 4K R8 rejection and reach the next unsupported operation: static mip1 of a2048² R16Float mode27 eleven-level chain, in a dispatch writing mip0/1/2/3. Silent Hill exits134 after31s, peak2277MiB. Octopath’s failed GPU stream survives150s, peak3506MiB, final screenshot black; owned TERM completes without KILL. This is advancement past one descriptor guard, not gameplay or scene correctness. Neither title meets rendering/FPS/loading acceptance. Original logs, screenshots, process snapshots and manifest are retained at `artifacts/validation/all-games-20261008/r8-fourk-game-replays-073fb937`.

The candidate remains limited to the qualified R8 descriptor; no additional mip/alias/mapping policy was relaxed. All-nine acceptance remains incomplete.

Descriptor decode clarifies original resource2048×2048, with selected views0/1/2/3 at2048²/1024²/512²/256². The initial report inferred1024² from selected mip1; raw words are retained unchanged.
