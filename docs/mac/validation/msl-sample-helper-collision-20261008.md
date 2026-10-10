# Metal sample modifier name collision

The signed87616e29 Superliminal run passed the sampled R8 volume preparation,
but its fragment compiler rejected `bias(...)` because the same generated
function already declared the per-buffer `uint bias[...]` array. The retained
log at `artifacts/runs/20261008-061545-superl-typed-textures-87616e29/game.log`
also reports duplicate `sampled_float_2d` members and later a live storage view
mismatch. Those are separate blockers; fixing name lookup alone does not
establish successful shader creation or game rendering.

Texture sample modifier constructors now explicitly use the `metal::` namespace
for bias, level and2D/3D gradients, including emulated1D gather's explicit level.
This preserves every operand and sampling operation. The constructors are
defined by Apple's [Metal Shading Language specification](https://developer.apple.com/metal/Metal-Shading-Language-Specification.pdf)
(2026-06-04, sections6.1 and6.13.1). The official PDF and extracted text are
retained in the isolated worktree's ignored `artifacts/validation/msl-helper-primary`.

Four managed controls translate actual fragment MIMG programs and retain the
real buffer bias local. Separate required-device sources compile those actual
shaders with additional same-scope level/gradient names. These added names are
adversarial parser qualification, not captured guest variables. The exact final source and native qualification are recorded below. No pixel
output, full captured game shader or gameplay claim is implied.

## Exact source qualification

Clean source `f73f7b6a8992bfb961da11b046ccc7b4c86ed3e6` built both test
projects with zero errors. Each CLR passed119 genuine compiler/layout controls
and six actual pre-native storage mismatch guards, with identical cases and zero
skips. Five additional required-device0 cases generated/parser-checked their
sources without touching a device; they are not native execution. The initial
493f6635 test build failure (a failure diagnostic passed as a nonstring assert
argument) and its fixture-only correction remain archived.

A separately authorized numerical batch passed all five actual Metal fragment
parser cases on ARM64 and the same five on Rosetta with required-device1, frozen
Debug assembly and no rebuild. Four cover helper-name collisions; one compiles
actual normalized ordinary Float2D and separate Dref2D argument arrays. This
proves native parsing only, not image numerical semantics, rendering, gameplay,
FPS or complete compilation of the original Superliminal shader.

The unchanged strict storage view veto now prints captured observed/requested
native width, height, format, type, depth and array-length metadata, plus guest
format/number/type/mip/numeric class. Six pre-native controls prove that every
original mismatch still rejects and leaves the registered image unchanged. No
texture reinterpretation or lifetime repair is introduced.

Evidence: `artifacts/validation/integrated-20261007/msl-name-collisions-f73f7b6a/`.
Source/managed manifest SHA256:
`31ce021bc8e97c1f997de7929bd8eaa04f9384ab00602733bb6201bb76ce0d03`.
Native manifest SHA256:
`1d920d018eaf34a098f1b837cc373ac3f4dd7a6c768da976c3b445bfa1a1ca21`.
Both owned worker groups were absent and the shared lock was released.
