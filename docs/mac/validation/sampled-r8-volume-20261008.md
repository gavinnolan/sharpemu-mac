# Sampled R8 volume candidate

The signed df173172 Superliminal run passed the previous RGBA8sRGB singleton
descriptor but then failed at the full R8Unorm4x4x16 sampled descriptor in Pixel
shader0xB0F377109F23A0E6. This remains preparation progress, not scene/gameplay
acceptance. The exact raw words and primary source oracle are retained in
`tests/Fixtures/R8SampledVolume`.

The isolated candidate requires all eight original words, exact normalized
descriptor agreement,4KiB address alignment, mode5/R8Unorm/pitch4/depth16/base0,
PERF_MOD7/000R, known static Float readonly3D shape and no metadata/flags/BC,
arrays/dynamic mip/atomics. It owns the full4096-byte physical copy then applies
the independently qualified XYZ equation to all256 logical bytes. Short copy,
unknown class or failed stability never produces zero/raw/XY fallback. Snapshot
source-byte accounting remains4096 while native linear upload rows are4 bytes,
image stride16 and depth16. Prepared metadata/descriptor copies remain owned.

The actual production materializer validates before its captured-source shortcut,
bypasses content-cache reuse, creates native3D R8Unorm with000R component
selection and rejects failed native creation explicitly. The same known GPU
block/extent veto applies before copy and materialization. Those registries are
logical/observed extents, not proof that unknown GPU writers cannot overlap or
that a guest allocation has a lifetime lease. The private source graph remains
hardfalse. No shared/native ABI is changed.

Managed fixtures use the primary emitted CSV to seed all256 voxels distinctly,
verify owned data/raw copies and reject malformed metadata/access/mips/shape,
short/partial/empty/deferred source and known alias before guest/native reads.
Native fixture sources separately assert every uploaded voxel/native type10
pixel format and actual production000R view. Two actual translated signed/outside
MIMG loads and translated nonzero interior sampling use the native argument buffer
(voxel2,1,7/index118: selected alpha118/255, RGB zero).
An appended independent native MSL consumer checks all256 integer reads, sample
centres and point/linear weighted neighborhoods against CPU weights with explicit
clamp-to-edge sampler state. The all-voxel filter kernel is controlled native MSL,
not guest ISA lowering or the actual Pixel draw pipeline. Reused native texture
in-flight ordering, shader sampler qualification, backing leases and native
command-buffer teardown remain separate. Existing GetOrCreateSampler descriptor
retention is outside this bounded texture proof; fixture removes its exact cached
sampler handles without claiming leak-free sampler lifecycle.

Frozen source `c554bf5e1cfc61f854ae79bf641937a50a983f56` built in Debug/net10.0
with36 existing warnings and zero errors. Both ARM64 and Rosetta x64 passed33
genuine managed cases (17 new R8 controls and16 existing singleton controls),
without skips. Four source/codegen cases per CLR are recorded separately; these
return before device query with RequireDevice disabled. Initial30-case results
are retained because their filter accidentally omitted three pre-native guards.

The separately authorized numerical batch then executed all four native cases
on each CLR with `SHARPEMU_TEST_REQUIRE_DEVICE=1`:4/4 ARM64 and4/4 x64, zero
failures/skips, no timeout. The unchanged DLL and every original source/helper
hash were checked before and after execution. Real native readbacks verified
all256 uploaded voxels and all256 controlled load/point/linear sample results,
including clamped edge neighborhoods; the translated interior sample returned
alpha118/255 with RGB zero. Signed-negative and z16 translated MIMG loads
returned zero. Only one full4096-byte guest read occurred per case, and native
read-only work left the caller-mutated guest block unchanged.

Evidence is retained under
`artifacts/validation/sampled-r8-volume/c554bf5e/` in the isolated
`agent-metal-sampled-r8-volume` worktree. Managed/source manifest SHA256:
`485baf944fe5e00a0f3dc51c45a65563ba58f8909a0ed749416ff6494cef259e`.
`native/native-manifest.json` SHA256:
`90e7356f81b3db84ec8bfa3080543356dba9e3888001019f2070310b6a13fcf0`.
Test DLL SHA256:
`327bb5a1fe7cf5462d6d279fddee27ef8ec84f669b2f2c7aaaf52bc962374b4f`.
The manifests retain commands, actual gate output, hardware/kernel metadata,
TRX/log hashes and comm-only process scans. The shared lock was released and
both owned worker groups were absent afterward. No foreign REmu, SharpEmu or
testhost was observed at either boundary; existing launcher daemons were left
alone. No performance, actual Pixel draw, gameplay or60FPS claim follows from
these numerical fixtures. This documentation successor does not change the
tested runtime source or activate the private graph.
