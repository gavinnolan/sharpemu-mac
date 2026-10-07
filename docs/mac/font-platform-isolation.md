# Guest font packages (W21)

Guest ABI exports, guest handles and instance ownership, scales, glyph metrics
packing, clipping and guest surface compositing remain in `SharpEmu.Libs.Font`.
`SharpEmu.Fonts.Abstractions` owns only the scalar/glyph-ID, pixel metrics,
owned top-down Alpha8 bitmap and face/factory contracts. It has no platform,
HLE, guest-memory, Skia or native handle dependencies.

The CLI chooses exactly one guest font implementation from its target RID:
`SharpEmu.Fonts.CoreTextMac` on macOS, `SharpEmu.Fonts.Skia` on Windows/Linux.
Factory registration is explicit and cannot be replaced after composition;
missing composition is an error. The Mac guest font adapter has no Skia path.
It uses CoreText for face matching, TTC/OTF/TTF descriptors and Unicode coverage,
and an owned CPU CoreGraphics Alpha8 context for glyph rasterization.

Each opened guest font owns one reference; instances retain the original face,
closing one handle releases only that handle, and the last close disposes the
native face and owned memory-font data. Registry reads, instance publication
and close are serialized; a failed instance output does not replace an existing
face. Default guest rendering without a registered handle retains one explicit
process-lifetime default face. Opaque guest handle allocations keep the existing
allocator policy. Font-data input is copied before a factory returns; native
font/data/context/fallback objects never escape the platform adapter. Glyph
results copy their coverage and survive face disposal.

Unicode input must be a scalar, including supplementary scalars encoded as a
surrogate pair for CoreText. Glyph IDs belong only to the selected face and do
not trigger fallback. Scalar fallback reports whether another face was used;
missing glyphs may render `.notdef` with `HasGlyph=false`. Invalid font data or
collection index returns no face, without a system-font substitute. Finite
positive pixel width/height scale independently; height selects font size and
width/height scales horizontal geometry and advance. Output is single-glyph
rasterization, without shaping, bidi layout or kerning (guest kerning remains
zero). Metrics and antialiasing can differ between CoreText and Skia; this is a
native implementation change, not a pixel-equivalence promise.

The native test selection requires `SHARPEMU_TEST_REQUIRE_NATIVE_FONTS=1` and
macOS, and fails if a required installed fixture font is absent. Otherwise its
native cases return early and must not be reported as executed qualification.
Tests include system traits, copied font bytes after input mutation, TTC and
invalid indices, supplementary Unicode, actual fallback, glyph-ID equivalence,
independent horizontal scaling and an asymmetric Arial F top-down row oracle.
No GPU, application window or game is required.

This extraction removes direct Skia from the shared guest engine. The current
Mac **launcher** still references Avalonia Desktop/Skia through `SharpEmu.GUI`;
therefore this work does not make the complete Mac app publish Skia-free.
W25 native AppKit launcher/settings composition is the prerequisite to remove
that remaining launcher dependency. Existing launcher assets must remain until
its replacement is qualified. No game performance or full font compatibility
claim follows from these package/native tests alone.
