# Native Metal correctness and remaining gaps

Updated 2026-10-07. These are compiler and native GPU conformance results;
none establishes game progression or visual correctness by itself. macOS uses
Metal directly. Unsupported resources do not select Vulkan or MoltenVK.

## Verified shader behavior

`SharpEmu.ShaderCompiler.Metal.Tests` passed **351/351** with
`SHARPEMU_TEST_REQUIRE_DEVICE=1` under the exclusive device/game lock. This
includes actual MSL compilation and native compute/texture readbacks, rather
than only assertions on generated shader strings:

- 22 representative ALU execution fixtures check mixed precision FMA, packed
  half operations, packed integer operations, bit extraction, first-bit,
  signed 24-bit multiply-add, and DOT2C destination accumulation. Packed FMA
  includes a rounding midpoint that distinguishes native half FMA from a
  double-rounded float implementation. This is representative coverage, not
  exhaustive IEEE-754 or modifier conformance.
- 11 texture sample/load fixtures distinguish base and nonzero mip levels,
  array layers, depth slices, cube faces, and logical 1D coordinates.
- Five storage fixtures check the whole destination texture after a write,
  including untouched sentinel texels/layers.
- Four integer load fixtures check that negative/out-of-range coordinates,
  array layers, and volume slices produce zero instead of edge replication.
- Two gather fixtures check component ordering on 2D and array resources.

The logical 1D resource uses a height-one Metal 2D backing because native
Metal 1D textures do not support mipmaps. Shader coordinates and gradients
are expanded accordingly. Cubes currently use face layers of a 2D array.
Sampler mip filtering and min/max LOD are now passed to Metal.

Semantics were checked against the [AMD RDNA 2 ISA](https://www.amd.com/content/dam/amd/en/documents/radeon-tech-docs/instruction-set-architectures/rdna2-shader-instruction-set-architecture.pdf)
(mixed/packed arithmetic, DOT2C and operand selectors) and Apple's
[Metal Shading Language specification](https://developer.apple.com/metal/Metal-Shading-Language-Specification.pdf)
(FMA rounding, texture read/write bounds, gather ordering and 1D restrictions).

## Production materialization

The separate `SharpEmu.Gpu.Metal.Tests` project invokes the real production
`CreateDrawTexture` / `ReplaceDrawTextureContents` implementation and reads
actual Metal texture bytes. It covers singleton arrays, singleton volumes,
multiple layers/depth slices, cube faces, supplied mip ranges, explicitly null
resources, and deferred tiled snapshots with deliberately permuted source
blocks. The fixture oracle constructs the permutation independently of the
production detiler.

Deferred tiled snapshots must reach the detile step before cache recovery.
Previously an empty linear snapshot with a nonempty tiled source triggered a
raw base-level guest read first, preventing detiling. Cache recovery now reads
all linear base-level slices and explicitly rejects tiled/mip-offset recovery
that cannot be reconstructed faithfully. Successfully read all-zero storage
snapshots retain their full source bytes. Null resources allocate bytes for
the actual native format and declared shape; failed reads of real resources
are not replaced with fabricated null texels.

All **19/19** production materialization cases passed with
`SHARPEMU_TEST_REQUIRE_DEVICE=1` under the exclusive lock. Seventeen perform
native allocation/upload or allocation rejection checks; two check early
MSAA rejection without touching a native device. This verifies the synthetic
host fixtures, not arbitrary game textures.

## Explicit unsupported cases and unqualified behavior

- Host CPU snapshots do not carry per-sample MSAA data. Guest texture types
  14/15 fail explicitly before a single-sample texture can be bound as MSAA.
  MSL multisample declarations compiling does not establish host MSAA support.
- Volume mip chains with depth greater than one need per-level depth metadata
  and are rejected. Supplied 2D/array mip chains are uploaded; the existence
  and correctness of every guest mip snapshot is a separate translator task.
- Incomplete array/depth or mip ranges fail explicitly. BVH instructions fail
  compilation rather than manufacture all-zero miss results.
- Cube face selection has readback coverage, but seam filtering across faces
  is not equivalent to native cube filtering and is not qualified.
- Gather has 2D/array coverage only. Comparison/PCF, sampler LOD bias, integer
  image format conversion, resource aliasing across dimensions, image atomics,
  divergent derivatives and broader compute synchronization still need targeted
  conformance tests and game evidence.
- The native upload fixtures qualify synthetic linear/block-permuted source
  layouts. They do not establish every guest tiling equation, volume tiling,
  mip-tail placement, compressed texture edge case or array base view offset.
- Several inherited unknown guest format mappings still select RGBA8. That
  is not format parity and should be replaced with validated format support or
  explicit rejection in a later bounded change.

## Frame timing and game evidence

The Metal headline FPS counts unique guest frames encoded into committed
presentation command buffers. `[METAL-PRESENT]` separately reports guest frame
cadence and host redraw cadence. This avoids measuring host timer redraws or
submitted guest work as completed guest frames. It does not measure display
scanout or GPU completion time.

The preserved Void Terrarium baseline at
`artifacts/runs/20261007-182951-void-base` has severe horizontal striping,
including text; no successful gameplay is established by that screenshot.
The deferred-detile ordering bug is a concrete candidate, not a verified
explanation for that run. Fresh raw/linear texture captures and integrated
screenshots must establish any improvement.

Integrated Sarah startup/menu/intro and Demon's Souls main-loop results are
tracked by the parent runtime audit. A shader opcode no longer aborting is
separate from progression, nonzero guest FPS, or correct rendering. The
Demon's Souls integrated run still reported incomplete layered snapshots;
the resource descriptor is now included in that error to diagnose the source.

## Reproducing conformance

Use bounded workers and disable node reuse/shared compilation. The x64 SDK
is currently the verified runner; qualification requires a real macOS Metal
device and `SHARPEMU_TEST_REQUIRE_DEVICE=1`. Both test projects and games must
use the same exclusive `sharpemu-compat/.gamelock`. Do not run device tests
concurrently with a game. Without the environment gate the suites run managed
checks and must not be reported as native GPU qualification.

Evidence logs: `/tmp/sharpemu-metal-device3.log` (351 shader tests) and
`/tmp/sharpemu-metal-production-device.log` (production texture readbacks).
These local paths are transient; the runtime audit should preserve the logs
with its final evidence artifacts.
