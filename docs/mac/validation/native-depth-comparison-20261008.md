# Native Metal depth comparison qualification

Clean source `8783b897c585a98e9636e6113115ed307b6ab1b6` is integrated on local
main after source review, bounded checks and the signed Sarah replay below.
No remote push, PR or upstream merge ran. This is a depth-comparison fix; Quake's
256 MiB tiled image remains blocked by the unchanged 128 MiB snapshot limit.

## Changed behavior

Normalized depth-reference binding classes now declare `depth2d<float>` or
`depth2d_array<float>` and emit `sample_compare` / `gather_compare`. Ordinary
color bindings retain their existing texture declarations and IDs/byte offsets.
The comparison C marker is removed before decoding LOD, derivatives and bias,
so CL consumes the trailing level and CD consumes its derivatives.

The host validates the compiled Float, sampled, 2D/array shape against explicit
R32Float or R16Unorm descriptors before materialization. Contradictory storage,
volume, integer, 1D, array, cube and unknown-type metadata is rejected. Cube
faces use the existing shader-resolved 2D-array convention with complete
six-face groups. Command buffers must retain encoded input references.

An ordinary source is resolved through the existing ownership path. Point
sampling converts its red values into private float staging, then buffer blits
populate actual private Depth32Float storage, including every layer and mip.
All conversion encoders and the consumer use the same command buffer; there is
no guest reread, CPU round trip, commit or completion wait inside conversion.
The guest sampler's comparison function is decoded from word0 bits12–14.
Metal performs comparison before filtering instead of comparing an already
filtered color value. The checkerboard fixture distinguishes the correct 0.5
PCF result from the old binary result.

The snapshot pool now includes type, layer count, mip count and private storage
in its texture identity; resources stay pending until their consuming command
buffer is tagged/completed. Pipelines are cached per device and array shape.
Temporary input/library/function/options retains preserve the first failure
while attempting all independent releases. A pipeline is published only after
its temporary owned objects are released.

The GPU timeline conversion follows the approach inspected in read-only
`/Users/xnetcat/Projects/xnetcat/REmu/crates/remu-gpu-metal/src/presenter/texture/comparison_gpu.rs`.
Kairo's sampler decoder independently maps the same eight guest comparison
codes to Metal. Apple's [sampler comparison property](https://developer.apple.com/documentation/metal/mtlsamplerdescriptor/comparefunction)
and [MSL specification](https://developer.apple.com/metal/Metal-Shading-Language-Specification.pdf)
are the native API references. Reference repositories were not changed.

## Exact checks

Apple M4, macOS 27.2, SDK 10.0.103 / runtime .NET 10.0.3. Reviewed clean source builds
passed: GPU project 20.12 s, 13 warnings/zero errors; shader project 2.67 s,
zero errors. On each actual ARM64 and Rosetta x64 CLR:

| Suite | Passed | Scope |
| --- | ---: | --- |
| Managed GPU | 128 | Prior 117 plus 11 new shape/command-buffer refusal rows |
| Managed shader | 8 | Separate color/depth fields and comparison operand families |
| Required native Metal | 68 | Prior 55 plus 13 new actual-device rows |
| Required native fragment compiler | 6 | Mixed color/depth plus CL/CD/CB/CLzO/Gather4C |

All exact case unions passed with zero skips, failures, timeouts or forced
signals, matching source and DLL hashes before/after. The new native rows are
12 semantic GPU executions and one real unretained-command-buffer veto. They
cover eight comparison functions, R16 conversion, explicit mip/array layer,
cube face and comparison gather. This does not prove all comparison opcode
variants, native source-currentness routes, original console tile addressing,
GPU-to-guest texture publication, gameplay or a performance gain.

Initial source848 failed on an upload-offset width mismatch; source bd301 failed
on missing static imports after review. Both terminal build logs are retained.
The corrected 83 and 702 builds passed; 702's earlier 11 GPU cases and regressions
passed separately. Only the final 878 source supplies the table above. A publish
driver preparation invocation used the wrong relative evidence path and declined
before any build; the corrected driver uses absolute evidence paths.

## Signed runtime regression

The source-specific app is retained at
`.claude/worktrees/root-native-depth-compare/artifacts/macos/compat-depth-8783b897/SharpEmu.app`.
Publish took 38.52 s; deep/strict ad-hoc signature and 239-file native-only closure
checks passed. Bundle manifest SHA256:
`51be0cdcbd66def6c21bbb943976f84e4525d7e7fd8111329c2f75d9c79cd1d9`.

Dreaming Sarah stayed alive 90 s, peak RSS 910 MiB. Snapshots 63/66 show its waking
sequence and standing on the raised starting platform. Snapshots 68/73 show
rightward travel to the lower ground; 77/79 show the leftward return and facing
change after the late A input. An airborne pose appears at 68, but it can be a
fall from the platform; this does not conclusively prove the requested jump.
Early directional inputs overlap the introductory sequence.

Short late reported guest-frame windows range 58.99–62.45 Hz after earlier
47.79–56.27 Hz windows. These are not sustained scanout FPS, a quiet-host
benchmark, a measured benefit of the comparison change or a complete loading
measurement. Dense 1 s screenshots and focus/simulation cadence limit comparison.
No owned compiler/test/native/game jobs overlapped this game. The app hashes
remain unchanged. Owned TERM returned immediately without KILL; the host-stop
CPU_TRAP/guest-join diagnostics do not certify clean guest shutdown.

## Evidence and remaining work

`artifacts/validation/native-depth-compare-8487d2c2/` preserves the earlier
source checks and failures; `reviewed-8783b897/` contains final commands, exact
case identities, TRXs, logs, eight frozen source paths, 12 frozen DLLs, signed
bundle hashes, selected screenshots and the original runtime file index.

Quake's large tiled resource needs a qualified physical placement and bounded
materialization/ownership path before this new comparison can be exercised in
that title. The existing conditional desktop address oracle does not identify
the custom console configuration. Color comparison of non-depth-equivalent
formats remains on its existing path. Typed CPU synchronization, canonical GPU
texture publication, sampled aliases, fault recovery and the other title
blockers remain in scope. This source was not replayed across all nine titles.
