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
MIMG loads and translated origin sampling use the native argument buffer.
An appended independent native MSL consumer checks all256 integer reads, sample
centres and point/linear weighted neighborhoods against CPU weights with explicit
clamp-to-edge sampler state. The all-voxel filter kernel is controlled native MSL,
not guest ISA lowering or the actual Pixel draw pipeline. Reused native texture
in-flight ordering, shader sampler qualification, backing leases and native
command-buffer teardown remain separate. Existing GetOrCreateSampler descriptor
retention is outside this bounded texture proof; fixture removes its exact cached
sampler handles without claiming leak-free sampler lifecycle.

Sources await build and separately authorized device execution. No managed count,
native capability, title correctness or FPS is inferred from this source alone.
