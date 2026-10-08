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
adversarial parser qualification, not captured guest variables. Build and native
execution remain pending; no pixel output, duplicate-binding fix or gameplay
claim is implied.
