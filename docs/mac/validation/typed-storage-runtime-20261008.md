# Typed storage and mapping publication checkpoint

Local main remains at the qualified native depth source `8783b897`, followed by
documentation `d953f969`. The typed synchronization candidate below is isolated;
the affected-game replay does not justify its integration or a game repair.

## Typed synchronization candidate

Clean `3986109a9910bd27c37dd9684c60017e7b330187` combines current main with
`7ccf8969`, `e8deec02`, `4c2fc3db` and the missing guest sampler decoder import.
It stores the original typed creation layout and mapping/session observation,
uploads typed rows through a fresh native texture, accepts actual zero clears,
and preserves the original image until replacement is validated. It refuses
unpublished GPU writes, stale original mappings, session resets and unknown
native cleanup. It does not establish raw/native writer completion.

On each actual .NET 10.0.3 ARM64 and Rosetta x64 host CLR:

| Checks | Passed | Scope |
| --- | ---: | --- |
| New typed managed | 22 | Original copy, session, layout, zero data and refusal paths |
| Library regressions | 489 | Exact previously qualified case union |
| GPU managed regressions | 128 | Includes qualified native depth shape controls |
| Shader managed regressions | 8 | Existing depth/color compiler controls |
| Required native Metal | 72 | Prior 68 plus four full-byte typed replacement cases |
| Required native shader compiler | 6 | Existing depth operand/compiler controls |

All test unions passed with zero skips, failures or forced signals and unchanged
source/DLL hashes. The four new device cases verify a varied nonzero replacement,
then a second zero replacement, every byte and native shape; the original and
varied images remain unchanged. They test allocation/upload rather than the
complete production dirty-sync transaction.

The first `58372cfa` build failed on a missing static sampler-decoder import.
Corrected GPU build passed in 13.59 s, 13 warnings; library build leader exited
successfully in 26.51 s with 115 warnings. Its residual owned process group was
terminated after the leader exited, causing the strict wrapper to decline the
build phase. The built payload was preserved and tested without rebuilding it.
The shader build passed separately in 1.21 s. No test worker needed a signal.

The signed source-specific app published in 44.00 s, with deep/strict ad-hoc
signature and 239-file native-only closure checks. Bundle manifest SHA256:
`5c6d110080dccf8525b1f5f6c3e454ac72a29d819926aacc180cdb62a1a2f3ee`.

Dreaming Sarah remained alive for 90 s, peak RSS 849 MiB. Viewed screenshots
73/79 show rightward travel and the leftward return/facing change. Late short
reported windows span approximately 55–63 Hz; sustained 60+, full fidelity,
loading duration and a performance benefit are unproved. Owned host TERM needed
no KILL; host shutdown diagnostics do not prove clean guest joins.

Octopath exited 134 after 32 s, peak RSS 3144 MiB, with a black final scene.
It fails before the prior dirty-array error, while capturing an original
64 KiB footprint for a write-only 1x1 RGBA8 Float array image at `8030B00000`.
The candidate remains isolated because unit/device checks do not override this
runtime result.

Root archive `artifacts/validation/all-games-20261008/typed-cpu-main-3986109a`
contains 142 indexed files, frozen source and DLLs, exact commands/TRXs, failures,
signed bundle hashes, runtime indexes and selected screenshots. Manifest SHA256:
`0e463bb5ab29da628eead40d87ac0bf8d15877e0bf6a0f8139dbcd383a42a39c`.

## Precise runtime diagnostic

Separate clean source `7e46c80373e900c71f8ae947686b810417642823` adds bounded,
opt-in original depth producer diagnostics plus cold-path typed rejection
details. It preserves the snapshot cap, original-copy results, short-circuit
currentness checks and every existing admission veto. It grants no source or
write authority. Its 22 typed, four depth diagnostic and 128 GPU managed rows
pass on both host CLRs, zero skips. No new native qualification is claimed.

Its first build failed on a missing test fixture namespace, then the corrected
GPU build passed in 15.01 s. The library build leader exited successfully;
residual owned children again required TERM. The signed app published in 34.84 s,
239 native-only files; bundle manifest SHA256:
`d5fd70819b65e06afb73802b0114498ca2320cf266ce19ccf03272672a5b6714`.

Octopath again exited 134 after 32 s, peak 3186 MiB. At `8030F00000`, the actual
provider returned **Rejected with no mapping observation**. The original
protected content token remained identical (identity 2, generation 0, complete
65536-byte coverage); the read snapshot also remained stable. Aggregate epoch
changed, but graph-disabled typed admission already ignores unrelated aggregate
drift. This is a provider rejection, not a post-detile content veto.

Source inspection identifies a relevant protection lifecycle gap: canonical
Mac protection changes revoke intersecting recorded views before mprotect,
then update shadow permissions on success without publishing ledger successors.
That can prevent future mapping-bound copies until remap. It does not yet prove
which operation caused this exact game rejection. The next fix must publish
fresh immutable segments from the original recorded mappings only after the
actual successful protection operation, retaining conservative failed-operation
revocation. Query reconstruction and fallback after Rejected remain excluded.

Quake on this diagnostic source remained a live host for 100 s, peak 1222 MiB,
but guest code called abort around 8 s after only three guest presentations.
The final screenshot is black. It did not reach the large depth image or produce
the intended DB trace. Host survival is not game progression. The unchanged,
previously signed `8783b897` bundle was then replayed for 100 s with the earlier
20/40/60 Enter plan. It also called guest abort after only three presentations,
remained a live host, and peaked at 1213 MiB. Its 239-file closure matched the
original qualification manifest before the run and stayed unchanged afterward.
The early failure therefore also occurs without the typed candidate. It is
not yet attributed to a specific change since the older all-nine source, or
distinguished from an environment-dependent failure.

Subsequent [signed Quake controls](quake-completion-regression-20261008.md)
now narrow that failure interval: the unchanged 2ad and 115 apps still reach
the large depth-image limit, while the 724 completion app reproduces the
early guest abort. The particular completion operation remains unidentified.

The diagnostic and prior-main control archive is
`artifacts/validation/all-games-20261008/game-resource-trace-7e46c803`, manifest
`29845a2e779de7527fea6eaa31333f170bfdd51cdc599c3f7af581e5d22d3b33`.
It retains frozen diagnostic source and DLLs, qualification commands, both game
runs and the unchanged signed-main control with selected screenshots.

## Canonical publication work

Separate first-use provider source `3c10cae5` with corrected test-only successor
`1b3d5e0a` passes 30 managed and four fresh actual Mac memory cases on each host
CLR. Native cases verify exact 1/4/8-byte canonical writes through two aliases,
one-use continuity, retirement exclusion, cancellation and unmap/remap ABA.
Actual kernel pages are 16 KiB on both CLRs; provider geometry is 16 KiB on ARM64
and 4 KiB on x64. Source/payload hashes match; no owned workers or shared lock
remain. The prior aborted fixture run is preserved.

Its native archive manifest is
`8c41b60106119fc23feddf0fb7ffa9ab6518d0d33e35d3d1b4e90d8c1cd2ecfe`
in `agent-canonical-short-publication/artifacts/validation/canonical-short-publication/1b3d5e0a`.
This qualifies the scoped ordinary-memory provider, not guest native/JIT stores,
Metal writeback activation or a game.

Range-history successor `615ecc17` is separately source-reviewed and now passes
44 managed rows and four fresh actual Mac memory workers on each host CLR,
zero skips/failures. The native scatter, continuity, reservation and ABA workers
exit normally, verify actual architecture and whole owned Mach cleanup, and
leave no owned groups or shared lock. Frozen source/DLL hashes match before and
after. It preserves the original observation while
distinguishing disjoint participating writes from overlapping texel writes;
the full original mapping footprint still excludes retirement. Its native
archive manifest is
`1fa1dbd0960c0f6c1ab90ddc7a6ded097f48c945238f864179039a5d71521360`
in `agent-canonical-range-publication/artifacts/validation/canonical-range-publication/615ecc17`.
Native/JIT writer participation remains necessary work; these ordinary-memory
controls do not certify a game. The subsequent isolated
[actual two-dispatch Metal checkpoint](finite-storage-two-publications-20261008.md)
now verifies controlled canonical publication and disjoint end-of-pipe ordering
on both CLRs, while refusing an overlapping ordinary write.

Successful-protection candidate `c9dc5d9f` is source-reviewed but unexecuted.
Original read-snapshot epoch ordering correction `34eedda4` is now included in
the actual cold two-dispatch checkpoint linked above. That correction
captures the original epoch after read tracking is armed and before original
observation/bytes; it does not restamp a copied result. A genuine cold-entry
two-dispatch Metal fixture is prepared without manual pre-arming. Its tracker
uses direct libc mprotect, so the canonical protection successor is not a
demonstrated prerequisite for that fixture. Separately, PageGuard protection
shares the canonical base-permission path and currently conflates temporary
host restrictions with guest logical write rights. Its fault-entered callers
also require allocation-free handling. Those paths need explicit intent and
context, retaining rejection of genuinely guest-readonly mappings.

The platform optimization direction continues to favor GPU-resident texture
copies and reduced managed copying. Apple's [mipmap copying documentation](https://developer.apple.com/documentation/metal/copying-data-into-or-out-of-mipmaps)
describes Metal blits between existing resources; Microsoft's [unsafe-code guidance](https://learn.microsoft.com/en-us/dotnet/standard/unsafe-code/best-practices)
requires explicit pointer lifetimes and pinning. Neither reference proves a
performance improvement in these runs. REmu/Kairo remain read-only references.

No all-nine gameplay, sustained 60+ FPS, fast-loading, remote publication or
upstream merge acceptance follows from this checkpoint.
