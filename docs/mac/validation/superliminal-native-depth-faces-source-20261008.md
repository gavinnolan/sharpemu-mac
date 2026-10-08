# Superliminal original native depth faces — source checkpoint

This isolated successor of f4ebd249 is unbuilt and unexecuted. It is default off
(`SHARPEMU_METAL_NATIVE_DEPTH_CUBE=1` opts in). It does not qualify gameplay,
PS5 tile topology, all-alias CPU writer participation, or native teardown.

## Actual entry failure

The f4eb replay first rejects the actual cube at 0x707B7D0000 because an existing
native depth producer overlaps its CPU snapshot. Pixel hash31B6963E7997EF6E,
image5, raw `[707B7D00,C0700000,00FFC0FF,B1800924,00000005,00700000,0,0]`:
256-square R16Unorm/type11/mode24, six faces, pitch256, one mip, select0x924.
The existing native producer must be preserved; guest CPU bytes cannot replace it.
The preceding DB raw registers have not yet been observed in this run. This
checkpoint must explicitly reject any DB shape that its normalized contract does
not cover. The earlier color placement path was not reached/qualified by this run.

## Production route

Host GuestDepthOf now projects the selected original normalized DB layer into
its physical address without mutating its raw registers or original resource.
The finite qualifier requires actual 256-square Z16/mode24 single selected layer,
complete normalized backing/pitch/layout agreement, single mip, no stencil.
Its 0x20000 face placement follows that actual normalized backing/view tuple.
The original provider mapping observation covers the COMPLETE normalized backing;
its discarded eight-byte probe lies in the original selected physical face and
never supplies sampled depth. Content protection covers that SAME complete selected
face, not an expanding prefix at a shared base; every distinct original face keeps
its own immutable protected token. Mapping-footprint and selected-content scopes
are deliberately different and neither is inferred from a later cube lookup. Original existing presenter-owner lifetime (`_selectedStorageMipSession`,
advanced by actual owner release) is captured before setup, epoch after BeginReadSnapshot but before observation/bytes,
and the original protected content/mapping checks remain immutable.

Ordinary graphfalse public draw admission records exact original work, sequence,
and logical queue. Render execution seals the actual native Depth32 2D handle,
content version and retaining command buffer only after successful endEncoding.
Encoded/Committed are not successful GPU completion. Assembly uses later encoder
order on the SAME uncommitted CB, or actual status4 on the ORIGINAL prior batch,
never a logical Complete or synthetic status override. Six captured originals
become a private Depth32 2D-array via six GPU blits. Existing cube face-resolved
`depth2d_array` comparison remains unchanged. No CPU shadow read or color-to-depth
reinterpretation occurs in this route.

Pending/taken sibling writers, known materialized images and original normalized
color/depth spans are checked; absent/unknown extents refuse. This is not a complete
canonical all-writer/retired-alias authority. Original mapping/content/session and
exact image/handle/version are checked before/after assembly. Later producer
replacement cannot be adopted by a captured source.

Exact finite image retains are consumed before retirement callbacks. Real status4
releases the original CB retain; unknown submitted batches remain bounded retained
quarantine during actual render-owner unwind, without claiming successful drain.
Unsubmitted CBs are released without committing partial work. Existing generic
renderer cleanup limitations and general concurrent graphfalse shutdown remain
separate; this adds no successful guest completion from close.

## Prepared controls and next qualification

15 genuine Libs rows: six actual normalized selected-face placements, seven forged
normalized placement rejections, default-off/no-probe behavior, and missing provider
rejection before CPU reads. These are source/helper controls, not GPU producer proof.
No build/test/native/game execution has occurred at this checkpoint.

Next native fixture must use real Mac/PVM original mapped backing, actual normalized
DB requests through public draw admission, real production Drain and cube
CreateTextureSnapshots, then actual six-face producer-to-layered-consumer readback.
Distinct face depths and all 393216 texels must be checked, plus version/session/
wrong-queue/refused-owner controls and consumed-retain cleanup. A fixture cannot
inject CWT origins or prearm away a cold observation defect. A real signed title
replay is required to confirm actual DB geometry and entry advancement.


Two prepared actual-device Facts run in separate fresh workers: cold six-face
public producer admission and production Drain/snapshot/assembly on the same
command buffer, and the same flow after original successful native completion.
Neither prearms nor enrolls managed-writer coverage on the six source faces.
After each new selected capture every earlier original token is revalidated.
The initial CPU backing is 0xED; the source wrapper refuses CPU shadow reads,
while all six native faces use distinct nonzero depth clears. All 393216 native
Depth32 texels (1572864 bytes) are read back after actual status4. Wrong logical
queue and original-session retirement remain refusal controls.

The fixture uses handwritten controlled VS/PS through the production draw path;
it does not claim guest shader translation or the actual title's DB metadata.
Actual face mapping/protection and native handles are required. Unknown native
completion or cleanup quarantines owned objects until that fresh worker exits;
there is no retry. Known completed finite faces, command-buffer retains, pooled
assembly, transient target, fixture libraries/pipelines/readback and backing are
consumed independently with the original exception preserved. Preexisting generic
renderer depth-stencil descriptor/state cleanup limitations remain outside this
finite source fixture and it makes no total renderer leak/teardown qualification.
The existing presenter session is deliberately not reset after retirement.
No prepared native row is included in the genuine managed count.
