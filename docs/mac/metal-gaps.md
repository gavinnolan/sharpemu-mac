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
The subsequent managed suite passed **31/31** after adding known-format,
unsupported-format, real-invalid-resource, all-zero/null-contract and
undersized-source checks. Those additional checks do not execute on a GPU.

## Explicit unsupported cases and unqualified behavior

- Host CPU snapshots do not carry per-sample MSAA data. Guest texture types
  14/15 fail explicitly before a single-sample texture can be bound as MSAA.
  MSL multisample declarations compiling does not establish host MSAA support.
- Volume mip chains with depth greater than one need per-level depth metadata
  and are rejected. Supplied 2D/array mip chains are uploaded; the existence
  and correctness of every guest mip snapshot is a separate translator task.
- Incomplete array/depth or mip ranges fail explicitly. BVH instructions fail
  compilation rather than manufacture all-zero miss results (the Vulkan path
  has `Gen5SpirvTranslator.RayIntersect.cs`; Metal has no port yet).
- Vertex shaders stay single-lane: MSL documents SIMD-group functions only for
  kernel and fragment functions. Pixel, compute and mesh stages run guest waves
  on real SIMD-groups.
- Per-vertex pixel inputs (P10/P20, pull model) need `metal::vertex_value`,
  which only Apple10 GPUs execute; earlier GPUs use the interpolated fallback,
  as Vulkan does without barycentric support.
- Cube face selection has readback coverage, but seam filtering across faces
  is not equivalent to native cube filtering and is not qualified.
- Gather has 2D/array coverage only. Comparison/PCF, sampler LOD bias, integer
  image format conversion, resource aliasing across dimensions, image atomics,
  divergent derivatives and broader compute synchronization still need targeted
  conformance tests and game evidence.
- The native upload fixtures qualify synthetic linear/block-permuted source
  layouts. They do not establish every guest tiling equation, volume tiling,
  mip-tail placement, compressed texture edge case or array base view offset.
- Unknown real guest texture formats now fail explicitly, including format
  zero. Only an explicitly null guest resource converts format zero to guest
  RGBA8/UNORM (format 10/number type 0) to create its zero image. Known mappings
  have managed regression coverage; every numeric interpretation and compressed
  format still needs native format-specific qualification.
- Guest RGB32 (format 13, 12 bytes per texel) is rejected until an explicit
  conversion to Metal RGBA32's 16-byte storage exists.

## Dense buffer atomics (follow-up to the integrated corpus)

The clean `9498713` Silent Hill corpus stopped at compute shader
`0xED96FD5D77F7BBD3`, PC `0x05D8`, with unsupported `BufferAtomicSwap`.
The native atomic-expression builder already supported exchange, but the
dense-buffer opcode allowlist omitted it. That path now accepts the decoded
32-bit SWAP and the actual decoded `BufferAtomicUmax` spelling. It captures
the byte address before modifying VDATA, uses native exchange/unsigned max,
returns the pre-operation word into VDATA only with GLC, and honors EXEC.
Its four-byte bounds check avoids overflowing `address + 4`; an active
out-of-range returning atomic returns zero and performs no write.

`Gen5MslBufferAtomicTests` passed **14/14** in both the managed run and the
exclusive required-device run. The required-device run contains **nine real
MSL compilation/execution/readbacks**, comparing the entire sentinel-filled
buffer: seven SWAP address/GLC/EXEC/boundary cases, one SWAP with address and
VDATA aliased, and one decoded unsigned-max case whose sign-bit-set old word
distinguishes unsigned from signed max. The other five cases check decoding,
code generation, and explicit rejection of Inc/Dec/X2. In the managed run
seven execution-only cases return at the device gate; the two other native
cases still perform their decoder/codegen checks before that gate.

These fixtures encode independent RDNA2 instructions and reference the
operand/address rules in ISA sections 8.1 and 12.14. The original game's raw
shader bytes were not captured, so this qualifies the operation and addressing
fixtures rather than that entire compute shader or game progression. The
misalignment case verifies the existing host safety policy (drop/zero); it
does not establish hardware behavior for an unaligned AMD atomic. Tests use
one invocation, not a contention or cross-dispatch synchronization test.
Swizzled addressing, other descriptor OOB modes, byte bias, and broad alias
coherence still require separate qualification.

Buffer Inc/Dec and 64-bit X2 atomics have since been implemented
(`sharpemu_atomic_step` in `Gen5MslTranslator.Resources.cs`; X2 forms in
`Gen5MslTranslator.cs`) and run on the device in `Gen5MslBufferAtomicTests`.
Inc/Dec wrap at +1/-1 like the SPIR-V path's `AtomicIIncrement`; neither
backend yet applies AMD's DATA-bounded wrap, which the MSL image atomics do. Evidence and exact
source/test provenance are preserved in
`artifacts/validation/integrated-20261007/metal-atomics/`.

## Frame timing and game evidence

The Metal headline FPS counts unique guest frames encoded into committed
presentation command buffers. `[METAL-PRESENT]` separately reports guest frame
cadence and host redraw cadence. This avoids measuring host timer redraws or
submitted guest work as completed guest frames. It does not measure display
scanout or GPU completion time.

The preserved Void Terrarium baseline at
`artifacts/runs/20261007-182951-void-base` has severe horizontal striping,
including text; no successful gameplay is established by that screenshot.
The integrated 9498713 menu capture at
`artifacts/runs/20261007-201452-void-final9498713-corpus/shot-0025.png` has
readable text and coherent artwork, but New Game still failed on a skipped
tiled/mip snapshot. Twenty raw/linear captures are retained for follow-up.
Unknown provenance of the older bundle prevents attributing the visual
difference to a single commit.

The [integrated runtime audit](validation/integration-20261007.md) records
Sarah's verified outdoor movement and the remaining eight-title blockers.
A shader opcode no longer aborting is separate from progression, nonzero
guest FPS, or correct rendering. Demon's Souls still produced zero counted
guest frames before its skipped tiled/mip snapshot failure.

The b557990 follow-up's 365 required-device shader tests and 31 production
resource cases pass without framework skips. Eight serialized game reruns show
the former atomic abort cleared in Silent Hill, Octopath and Oregon. Faithful
queued snapshots clear the earlier cache-loss abort in Superliminal and Void,
but their visual/progression and stop outcomes remain incomplete. Demon's Souls
still aborts on an empty sampled GPU alias: an address advertised as available
is not a lease on a compatible initialized producer. Current-state diagnostics
are added separately; they do not change alias policy. See the
[ownership acceptance specification](validation/sampled-gpu-alias-20261007.md)
and source-linked outcomes in the integrated audit.

## Reproducing conformance

Use bounded workers and disable node reuse/shared compilation. The x64 SDK
is currently the verified runner; qualification requires a real macOS Metal
device and `SHARPEMU_TEST_REQUIRE_DEVICE=1`. Both test projects and games must
use the same exclusive `sharpemu-compat/.gamelock`. Do not run device tests
concurrently with a game. Without the environment gate the suites run managed
checks and must not be reported as native GPU qualification.

Evidence logs are preserved in
`artifacts/validation/20261007-metal-conformance/`: `sharpemu-metal-device3.log`
(351 shader tests), `sharpemu-metal-production-device.log` (19 production
texture cases with the required-device gate), and
`sharpemu-metal-production-managed-final4.log` (31 subsequent managed cases).

### Selected storage mip1 candidate (2026-10-08, isolated)

Fresh DS8c19 fails first on static2D storage512x256 fmt4/7 tile9 with
base/selected mip1 and9 backing levels. The isolated candidate selects resource
placement1 from full base geometry, owns/detiles those bytes, and binds native
one-level256x128. Exact domain+resource+mip native storage reuse is separate from
address-only images. Unsupported overlapping/base/sampled aliases fail explicitly;
no whole-allocation coherence or graph activation is claimed. See
[scope, placement and fixture limits](validation/selected-storage-mip-20261008.md).
Clean cdc source passed4/4 required-device cases on ARM64 and4/4 under Rosetta,
including translated selected-level stores/loads, all-texel/neighbor oracles and
native publication rollback. Actual DS descriptor acceptance/progression and
downstream sampled aliases remain pending; this does not qualify gameplay/FPS.

### Exact sampled singleton RGBA8sRGB volume (2026-10-08)

Observed Superliminal type10/mode5 RGBA8sRGB1x1x1 now has an owned four-byte
origin path, true native3D/depth1 upload and known-GPU-alias vetoes. Clean42 source
passed4/4 actual required-device cases on ARM64 and4/4 under Rosetta: translated
point sample/load, linear alpha and signed bounds. GeneralXYZ tiling, unknown
writer/backing ownership, sampledGPUaliases, game Pixel draws/visual progression
and performance remain unqualified. See
[exact descriptor, primary oracle and proof limits](validation/sampled-singleton-volume-20261008.md).
