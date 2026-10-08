# Selected storage mip candidate — 2026-10-08

This isolated candidate addresses the earliest Demon's Souls failure in source
8c19cd7, not the older downstream DS alias failure and not gameplay acceptance.
The production graph remains hardfalse. No shared DTO or native ABI is changed.

## Descriptor geometry and byte placement

The actual normalized binding supplies the original resource descriptor and an
absolute selected mip (`ViewBaseLevel + binding mip`). Width512/height256/pitch512
are resource-base dimensions. Static storage MSL read/write takes no LOD operand:
resource mip1 must be a native one-level256x128 texture whose coordinates are
relative to native level0. Merely accepting BaseMipLevel1 while uploading base0
would read and write the wrong surface.

The existing `GnmTiling.TryGetMipChainPlacement` receives original512x256 geometry,
4-byte elements, mode9 and9 resource levels. The qualified placement is:

| Part | Offset | Physical bytes |
|---|---:|---:|
| shared mip tail (mip3 onwards) | 0 | 65536 |
| mip2 | 65536 | 65536 |
| selected mip1 | 131072 | 131072 |
| mip0 | 262144 | 524288 |

The whole chain occupies786432 bytes. The snapshot owns only the selected131072
physical bytes, detiles them into131072 native linear bytes, and records an
observed prefix extent262144. No mip-tail or failed-detile/raw fallback is admitted.
Raw8 words are decoded and compared with actual normalized descriptor metadata
before reuse/read. This candidate requires aligned mode9 R32Float, base/last1,
resource9,512x256/pitch512, non-array2D Float storage, static mip, and zero
metadata/flags/BC/base-array/min-LOD fields. Other nonzero mip classes fail precisely.

Kairo reference (`755e7657820161dd90ac93a93eb80d833eecc394`, read-only):
`crates/kairo-gpu-metal/src/texture.rs:1075` computes the complete chain before
selecting visible levels; `:1369` reads `img.base + level.offset` and detiles using
that level's layout. This transfer is a placement/read pattern, not a borrowed
RAM lifetime authorization. SharpEmu continues using fully owned selected bytes.

## Native view ownership and supported consumers

A private CWT maps the exact DTO instance to resource+absolute-mip+ICpuMemory
reference-domain identity. Clones carry that association. No address-only native
resource promise is emitted for a selected write. A private selected-view registry
owns the native texture separately from `_guestImages`, validates actual native
format/type/dimensions and initialized content, and increments that view's content
version after successful dispatch encoding. Repeat exact storage bindings preserve
native GPU content; preparation consults the live exact view without re-reading
stale guest bytes. Base, differently formatted, overlapping, sampled/draw/blit or
display aliases are explicit unsupported operations rather than raw-memory recovery.

Same logical-queue FIFO may consume an earlier selected writer. An outstanding
producer on another logical queue, immediate follow-up insertion, or a different
native command queue is rejected. Encoding completion is not GPU completion.
Pipeline failure/compute skip cannot advertise successful selected work. Submitted
Metal command buffers retain the bound texture through native completion; the
registry's retain is released by presenter finalization. The close session stamp
revokes old preparation associations. Registry metadata/native owners cap at128
qualified views (at most16MiB of selected native image payload), without dropping
an authoritative GPU writer and falling back to guest bytes.

The original copied seed retains before-copy tracker identity/generation and
mapping epoch. Native view selection/reuse conservatively vetoes a changed token,
tracker ABA or process mapping epoch. Exact live selected-view reuse additionally
requires the original complete observed-state token to remain observed and equal;
ordinary watch-only, unknown or failed-arm identity alone cannot authorize reuse.
This restriction is selected-view-only: ordinary/array base0 storage behavior is
unchanged. First creation still uses the existing graph-disabled owned-copy
consistency checks. These observations are not backing leases,
kernel receipts, or copy/writer serialization. Managed writer/fault publication
can precede the actual write. This candidate does not qualify arbitrary concurrent
CPU replacement or enable the broader immutable-source graph. A real-game run
must expose whether raw fields meet the bounded descriptor gate and whether a
later supported storage consumer remains in the qualified ordering/observation
class. Full allocation-wide sampled alias coherence remains unresolved.

## Fixtures and evidence levels

Managed fixtures construct the entire786432-byte chain independently of production
placement/detiling. A separate mode9 bit equation encodes all32768 selected texels;
other physical ranges contain0xCD. Actual `CreateTextureSnapshots` must match every
selected byte and retain them after source mutation. Additional production-path
controls reject malformed raw metadata, unknown geometry/dynamic mip, incomplete
selected source, cross-queue admission, tracker lifetime ABA, old presenter session,
native-queue mismatch, and overlapping alias reads before guest-memory access.

The four required-device cases are compiled but initially unexecuted. Two
store/load cases explicitly establish managed-writer coverage of serial owned
test memory; this is a test premise, not native guest store coverage. The
store/load fixture It uses actual
production snapshot→`EnsureStorageImage`, then actual translated MIMG store/load
through a native argument buffer. It compares all32768 selected texels, every
unchanged guest-chain byte, a repeated native view after the GPU store, and zero
new guest reads during exact live-view preparation. A second case addresses
(300,200), outside256x128 but inside512x256 base geometry, to detect accidentally
binding mip0. The fixture is not a runtime Drain/ISA-game corpus proof and does not
claim native command-buffer teardown qualification from managed cleanup controls.

## Storage publication exception-safety successor

`EnsureStorageImage` precomputes extent/capacity before allocating a native handle.
One installed-state `finally` releases the newly created texture after upload,
raced-lookup validation, or registry publication failure. `PublishStorageImageLocked`
rolls back only the exact entries from this construction, preserving prior extent
and previously admitted selected metadata. The array candidate can add its native
ArrayLength fields without changing this cleanup authority.

Two managed controls inject a real helper failure after the native registry entry
but before metadata publication, verifying exact rollback and original cause.
Two additional required-device cases run production allocation/upload, inject the
same publication failure, verify no ghost owner, independently retain/read every
uploaded byte, release that test retain, and retry successfully. The test retain
keeps its probe safe: reference-count/deallocation timing is not measured, and
native command-buffer teardown is not claimed from this construction test.
