# Superliminal depth comparison entry blocker

Preserved source `aa38ff5a5f18e46ba6e0a8e40feb97667715a7b4` has 18/18 managed rows per CLR and a signed native-only 239-file bundle. Actual run `20261008-202353-superl-color-view-entry-aa38ff5a` rendered title/loading only and exited 134 after 71s, peak3924MiB; no SIGKILL, residual processes or shared lock. It did not reach the earlier color-view mismatch, so that runtime fix remains unproven.

The first fatal (game.log6974 onward) is `ValidateDepthComparisonTexture`, descriptor2/0/type11, offscreen sequence2540 dcb.graphics/submission84. Raw descriptor, dimensions and compiled shape were absent from this old message. The new failure-only diagnostic retains the same acceptance predicate, adds only owned scalar/raw metadata, and falls back to the original refusal if formatting fails. The one new managed refusal fixture passes device0/fakeCB1 only through the pre-native validation path; it is unbuilt/unrun.

## Concrete production-path discrepancy

`MetalCommandStreamHost.Rendering.cs` marks `image.Cube` as arrayed. `ResourceMaterializer.DescriptorDimension` uses Dim2DArray for cube, and MSL emits `depth2d_array<float>` because guest V_CUBE* instructions have already resolved direction into face coordinates. However `TryCreateGuestDrawTexture` only selects complete array uploads for descriptor types12/13. Non-null cube type11 falls through with ArrayLayers1, unlike the existing native cube fixture which directly supplies six layers and bypasses production snapshots. This discrepancy can explain the fatal; the expanded actual tuple must confirm there is no additional contradiction.

A correction should retain the full raw-declared face count (depth field decoded as lastSlice+1), complete per-face mip-chain stride, layer-major/mip-major upload ranges and full original content observation. It must reject incomplete/non-square cube geometry, base-array/view ambiguity and failed detiling before reads/native allocation, rather than treating raw tiled bytes as linear or manufacturing missing faces. Known GPU-written resident faces need original ordered native producer ownership or canonical completed bytes; CPU upload is not a substitute for that debt.

## Primary MSL semantics

Apple [Metal Shading Language Specification](https://developer.apple.com/metal/Metal-Shading-Language-Specification.pdf), 2026-06-04, sections6.13.11 (page274) and6.13.14 (page280): depth2d_array comparison takes float2 coordinates, an explicit array index and reference; depthcube takes a float3 direction and reference. Changing declarations alone would change the operand contract. Apple [compareFunction](https://developer.apple.com/documentation/metal/mtlsamplerdescriptor/comparefunction) documents the actual sampler comparison function. The local PDF/transcript is retained under artifacts/validation/depth-cube-entry-source, not compiled.

Read-only references refreshed: REmu HEAD8460648beb2e3ffae2610bff87228340373f2c57 (one existing untracked note), sampled.rs1013 and depth_array.rs retain cube faces as2D-array/layered native depth; Kairo HEAD755e7657820161dd90ac93a93eb80d833eecc394 with existing dirty cube.rs, layout requires square geometry and face count multiple6, one full allocation mip-chain per face. They are design references, not independent runtime proof.

No build, native execution, game run or source policy activation was performed for this successor. Preserve aa38 source/payload/evidence unchanged.
