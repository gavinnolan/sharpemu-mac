# Superliminal live storage view mismatch

The clean signed d756a2b3 run reached compute work29836 on dcb.graphics,
submission1503, after the prior MSL errors were absent. It rejected address
706B7D0000: observed512x512 RGBA16Float(native115) versus requested128x64,
same nativeformat115/type2/depth1/layers1, guest12/7/type9/base0/selected0.
The retained log only contains this rejection; it does not contain the original
creation descriptor, producer work, pitch/tiling, or incoming raw words. No
successful rendering or allocation replacement can be inferred from it.

`SHARPEMU_METAL_IMAGE_PROVENANCE=1` is a cached opt-in diagnostic. Original
storage creation records all8 words by value, geometry/pitch/tiling/mip/source
byte metadata and the executing sequence/queue. Color creation records the
immutable color-target register DTO; display creation records available scalar
metadata. Unknown fields remain unavailable/zero, not invented resource words.
Explicit storage/draw/blit/guest-pixel/clear changes retain the last encoded
mutation context. These records observe existing code paths; they do not prove
native completion, guest allocation identity, remap lifetime, dirty-byte coverage
or serialization. Retired image objects keep their own metadata. There is no
separate unbounded history or global address log.

On the same existing six-field strict mismatch, opt-in output adds creation and
last mutation metadata, initialized/GPU-written/content-version flags, plus
incoming raw words/pitch/tiling/source byte count/generation/access. Formatting
is best effort: optional diagnostic failure returns the original exception.
Success paths, mismatch predicate, native lookups/bindings, cache retirement and
CPU upload policy are unchanged. Default off avoids capture, thread-local context reads/writes and failure suffix.
Diagnostic Drain context saves/restores the exact previous value for nested
work scopes; these scopes are observations, not queue or native ownership.
The old parameterless content marker remains available for existing fixtures.

Reference checkouts were inspected read-only at fresh REmu e343053222cb67bf291d9ee7ec46240aab1b0b9f
(one untracked audit note; tracked tree clean) and Kairo755e7657820161dd90ac93a93eb80d833eecc394(clean).
These differ from historical audit revisions. REmu resident_map.rs uses complete
descriptor keys and publication/transfer GPU-debt receipts to authorize dirty
retirement. Kairo render/resident_image.rs requires exact dimensions plus native
layout/level compatibility for a live alias and checks the authored epoch. Their
mechanisms do not authorize address-only reuse or replacement in SharpEmu.

Managed helper/actual pre-native mismatch controls are pending. No device or
game run is authorized by this diagnostic. Next run must capture original and
incoming descriptors before any view, guest-arena replacement or lifetime fix
is designed; stale CPU bytes cannot replace GPU-written contents by assumption.
