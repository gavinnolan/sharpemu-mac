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

Builds, oracle execution and native tests are pending a serialized window. No
test count, native capability or title progression is claimed from source alone.
