# R32Uint singleton array candidate

The clean df173172 title runs pass the previous RGBA8 PERF_MOD7/F2E gate and stop
at a different typed descriptor in SH, Octopath and Oregon. Their raw words differ
only by address: `[80107000/8030B200/80115300,01400000,0,D1B00204,0,00700000,0,0]`.
This means unified20, data4/number4 R32Uint,1x1 base0/mip0/pitch1/layer0,
type13 mode27, Uint storage, X001 and PERF_MOD7. Passing the previous gate was
progress through preparation, not gameplay or successful frame rendering.

The isolated successor admits only this exact typed alternative. The existing
RGBA8 Float alternative is unchanged. Full raw8 equality, alignment, metadata,
shape, access, mip, layer and adjacent hint-bit checks remain. Integer contents
are never converted to Float or RGBA8. Production already decodes R32Uint to
Metal pixel format53 with four bytes per texel; the same actual physical detiler,
owned64KiB snapshot, native2DArray/one-slice materializer and owner lifecycle are
used. No graph activation, shared ABI change, general tiled-array or live alias
coherence is introduced.

Managed sources replay all three actual descriptors with zero/nonzero uint seed
bytes and test detached copies, exact format/shape/byte layout, short/empty/stale
seed rejection and malformed typed metadata rejection before guest reads. Native
fixture sources use seed0x87654321, store0xDEADBEEF, actual translated Uint MIMG
store/load and explicit X001 sampled view. They compare the complete physical
word and four integer channels `[value,0,0,1]`; negative X/slice and signed-negative
slice stores preserve the seed. The65532 other guest block bytes stay unchanged.
The sampled view is explicit controlled fixture setup, not game binding or
cross-queue/coherence qualification. Public primary AddrLib32bpp/mode27 origin
reference source is retained under `tests/Fixtures/R32UintSingletonArray`.

The independent pinned AddrLib driver at source1e996df4 reports a65536-byte
physical surface/alignment,128x128 block/pitch and origin byte offset0. This is
modeled source/layout evidence, not PS5 topology or native execution. CSV/source
hashes and reproduction command are in the retained fixture README. Managed
fixtures separately exercise the typed native path; no title progression is
claimed from source/oracle alone.

## Exact-source managed and native proof

Clean source59fac2ba built Libs with154 existing warnings/zero errors and Metal
fixtures with zero warnings/errors. The genuine managed selection passed93/93
ARM64 and93/93 Rosetta x64, including12 new exact Uint replay/rejection controls.
The four fixture codegen cases also passed on each CLR with required-device0;
these early returns are explicitly separate from device coverage. Managed
manifest SHA256 `bf1a484ec256c9448649a1249eb9405f9417d5d72e6af4b7a6ced7763451506a`
is retained under `artifacts/validation/r32uint-array/59fac2ba/manifest.json`.

A separately granted shared-lock batch used the exact frozen Debug/net10.0 DLL,
fresh ARM and Rosetta workers, required-device1 and60-second watchdogs. Actual
4/4 cases passed on each architecture with zero failed/notExecuted cases. Native
type3/arrayLength1/format53/usage3 were asserted. The high-bit uint seed survived
detached upload, valid store produced0xDEADBEEF, all four load channels matched
X001, and negative/slice1/X1 stores preserved0x87654321. Each TRX retains four
actual execution markers; all65532 guest neighbor bytes remained unchanged.
The sampled X001 view is explicit test setup, not game resolver or shared
coherence qualification. No actual title scene/FPS or lifetime lease is claimed.

Source/DLL/helper/import/hardware/command/log/TRX hashes and owned process scans
are retained in `artifacts/validation/r32uint-array/59fac2ba/native/native-manifest.json`
(SHA256 `aceea5ce4cf44314d1e09815dd3226b768da281aaff25f78563c3cb0ed27de15`).
Both workers exited0 without watchdog intervention; owned groups8516/8524 and
the shared lock were absent afterwards. Pre/post comm-only scans found no other
emulator. This remains numerical correctness evidence, not a performance run.
