# Actual Metal storage publication checkpoint

Isolated source `7ddb8050be763578d99c3ad416c61d8f9c49072b` now qualifies a
complete controlled GPU-to-guest publication route. Production finite writeback
and the texture source graph remain inactive by default. This source is not
integrated into main and has not been replayed in a game.

The fixture starts with a real Mac mapping, PVM/Tracked memory, no existing
tracker token and a complete varied 128 KiB backing. Production snapshot creation
captures the original mapping/content/session observation for a mode27 R32Uint
1x1 array view with a 64 KiB physical footprint. It does not pre-arm tracking or
inject an observation, publisher or native completion status.

Real `SubmitComputeDispatch` and drain execute a controlled native shader, typed
staging, status4 completion, checked canonical texel scatter and debt
finalization. A deferred end-of-pipe label lies in the same backing, outside the
original image footprint. A second actual dispatch and label repeat the route.
Every one of the 131072 canonical bytes is checked after each phase, including
untouched padding. Successful own publication preserves original mapping
segments and content/session observation; completed disjoint participating
writes do not manufacture a fresh baseline.

The conflict control inserts an actual overlapping ordinary CPU write before
the second dispatch. Publication refuses that conflict, preserves the CPU byte
and first label, and does not acknowledge the second work or end-of-pipe label.

On each actual .NET10.0.3 ARM64 and Rosetta x64 CLR:

| Checks | Passed | Scope |
| --- | ---: | --- |
| Staging managed | 32 | Finite layout, scatter and refusal controls |
| Typed CPU managed | 22 | Original copy, session, layout and replacement controls |
| Required actual Metal | 2 | Two successful dispatches and an overlapping-write refusal |

The four native rows run in separate fresh workers. They assert actual CLR,
Apple M4, 16 KiB provider kernel pages and real status4 receipts. All managed
and native rows pass with zero skips/failures, unchanged source/DLL/dylib hashes,
no test cleanup signals and no remaining owned workers or shared lock.

The first build failed on a missing sampler-decoder import. The corrected
library compiler leader succeeds with 129 warnings/zero errors in 40.09 s;
residual owned compiler children require TERM and the strict wrapper declines
that build phase. Its payload is retained and tested without rebuilding it.
A separate GPU-only build succeeds with 13 warnings/zero errors and no signals.
Those compiler receipts are preserved; test workers all exit normally.

Evidence:
`artifacts/validation/all-games-20261008/finite-storage-two-publications-7ddb8050`.
The original qualification manifest is
`18ae06d2df8afbd5959aee9aa4f763f311f8b51cd0db191ac7574759ba546520`;
the 76-file root transfer manifest is
`d8588fb80152e0e32f2bebd6ca403e76089265dfb1afe80bc391f1eef5182a13`.

This numerical result qualifies the controlled ordinary-memory provider route
and real Metal publication ordering. It does not qualify native/JIT guest
writers, general PageGuard protection changes, guest shader translation,
Octopath's different image format, game rendering, loading or sustained FPS.
The [Quake completion regression](quake-completion-regression-20261008.md) also
remains unresolved; correct ordinary command-memory ordering is separate work.
