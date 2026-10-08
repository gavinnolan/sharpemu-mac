# Sampled singleton volume candidate

Fresh Superliminal8c19cd7 fails at Pixel image2, shader19794A92F9402062, on sampled
tiled3D raw `70152880 08200000 00000000 A0500FAC 00000000 00700000 00000000 00000000`.
This candidate admits only that non-address field class: RGBA8sRGB1x1x1,mode5,
type10,base/selected0,one resource/view mip,identityFAC,zero metadata/flags/BC and
full compiled static Float sampled Read volume shape. Other tiled sampled
volumes remain explicit unsupported.

[Mesa's primary GFX10 register definition](https://chromium.googlesource.com/chromiumos/third_party/mesa/+/refs/heads/stabilize-13982.70.B-chromeos-amd/src/amd/registers/gfx10-rsrc.json)
lists unified130 as RGBA8sRGB and word5 bits20–22 as PERF_MOD. The observed word5
preserves7 there; the gate rejects neighboring flags. Desktop metadata is a
field reference, not authority to change Prospero width/mip decoding.

The pinned independent AMD AddrLib32bpp oracle in
`tests/Fixtures/SampledSingletonVolume` reports origin0 and4KiB
allocation/alignment. Preparation owns only the four addressed voxel bytes,
uses bounded copy-stability retries and preserves original observation and raw
metadata. It does not use XY detiling, empty snapshots, address reconstruction,
zero fill or cache promises. Upload remains native3D/depth1/RGBA8sRGB/one level;
validation precedes any captured-source shortcut. Native format/allocation
failure must fail work; no substitute pixel format is introduced.

Known GPU advertisements/native/depth/source records in the origin's4KiB block
and overlapping reported extents veto preparation and upload. Logical/observed
extents do not prove universal tiled footprints, memory-domain identity or
detection of unknown writers. Counter equality is not copy serialization,
kernel permission or a backing lease. The ordinary graph-disabled owned-copy
policy remains; the immutable graph remains hardfalse. No allocation-wide
CPU/GPU alias coherence or producer readiness is qualified here.

Sixteen managed production controls cover owned data/words, exact four-byte read,
native3D shape, malformed raw/metadata/neighbor flags, unknown numeric/shape/mip/
access, short memory, same-block advertisement, earlier overlapping reported
extent and empty/deferred upload rejection. Clones carry existing CPU provenance.

Four required-device cases exercise actual production
snapshot→CreateDrawTexture must produce type3D/depth1/format71 and stored seed
bytes before translated MIMG argument-buffer execution with sentinel output.
Cases are point sample0.5³, origin load, negativeX and one-pastZ. Independent
RGB for seed128/64/32 is0.2158605001/0.0512694584/0.0144438436; alpha192 stays
192/255. RGB tolerance1e-4,alpha1e-6. Outside loads return four zeros. All4096
guest bytes and no late guest reads are checked. This controlled Compute
consumer uses shared image translation; it is not a native Pixel draw/game or
general sampler-filter qualification.

[Apple's pixel-format reference](https://developer.apple.com/documentation/metal/mtlpixelformat)
documents linear alpha for sRGB formats; its
[RGBA8sRGB definition](https://developer.apple.com/documentation/metal/mtlpixelformat/rgba8unorm_srgb)
describes normalized RGBA conversion. Managed/compiled fixtures alone do not
establish native capability, Superliminal progression, visuals,60FPS or teardown.

## Exact-source native numerical evidence

Clean `42be6c7eadf7c3ca03b31ab64c7786883b3ee7ca` passed all four actual native
cases on Apple M4 ARM64 and then Rosetta/x64, required-device1, fresh serialized
workers under the shared machine lock. The frozen hashed Debug/net10.0 DLLs were
not rebuilt for device execution. Native3D type/depth/format and stored bytes
were checked before translated point sample/load execution. All four output
components matched the independent sRGB/linear-alpha or signed-bound zero
oracle, all4096 guest bytes stayed unchanged, and no late guest reads occurred.
Eight cases executed, zero failed/skipped/notExecuted. Owned worker groups were
absent and the shared lock released after completion.

`artifacts/validation/sampled-singleton-volume/42be6c7e/manifest.json` preserves
the16 managed and4 pre-device-codegen controls per CLR separately. Its
`native/native-manifest.json` indexes actual commands, timestamps, hardware,
TRX/log markers and pre/post comm inventories, verifies unchanged DLL hashes,
and references the original managed manifest. Parent allowed recorded foreign
REmu activity and concurrent CPU-only build work for this numerical batch;
the final foreign inventories are retained. This is not quiet-host performance
evidence, actual game Pixel-stage rendering, general filtering or native
command-buffer teardown qualification. Actual Superliminal descriptor acceptance
and progression remain an integration test.
