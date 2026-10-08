# Oregon sampled alias first refusal

Source-only diagnostic successor to frozen `0e0e76a4`. The old Oregon run
`20261008-081153-oregon-typed-shader-2adf328d` rejected sampled RGBA8Unorm
type9/mode27/1x1 at `8011520000`, while an initialized GPU-written image version2
existed at that address. Its native type/depth/layers and original producer raw
descriptor were omitted. `current_selected=absent` proves the existing matcher
declined, but does not establish an array-layer or volume-slice producer.

Current integrated original storage/volume/display capture does not itself
authorize this sampled alias. The graph-disabled materializer still requires its
native shape matcher; the general immutable texture-source graph remains separate
and off. This older failure is not proven superseded by source or gameplay.

Enable `SHARPEMU_METAL_SAMPLED_ALIAS_DIAGNOSTICS=1` for at most64 first-candidate
refusal snapshots. Also enable the existing `SHARPEMU_METAL_IMAGE_PROVENANCE=1`
before original creation to retain its cached raw/context fields. Capture occurs
at the original matcher under its existing gate, in the unchanged ordered type,
depth, layer and extent checks. The existing failure appends that value snapshot
after releasing the gate; it does not rerun matching for the new diagnostic.
Raw/creation/mutation queue context and original storage mapping/session fields
are cached observations, explicitly not currentness or authority. Absent original
provenance remains absent. The existing legacy failure inspection is unchanged.

Default off exits before optional metadata/counters/formatting. Optional formatting
failure preserves the original message. No provider/tracker/native query, byte
read, dirty consumption, matching score, native image allocation or acceptance
policy changes. The frozen integration is untouched.

Eight prepared managed rows drive actual `CreateDrawTexture` failure with scalar
images lacking native handles: ordered type/depth/layer refusal, default-off
stability, tiled width/height refusal, cached creation context from actual ordered
Submit/Drain, and64-row saturation. The byte provider throws on reads; zero reads
are asserted. These are failure metadata controls, not GPU ownership positives.
No builds, tests or game runs have occurred.

A later native-only sampled view/copy needs exact producer/native retain, version,
session, descriptor/subresource compatibility, ordered producing/consuming native
command buffers and exclusion of replacement while its ownership transfers. It
does not publish guest CPU bytes and must not inherit the full CPU canonical
publication inventory merely because that inventory exists. Original physical
provenance may establish which guest resource the producer belongs to; it cannot
alone certify GPU completion, choose a new view shape, or substitute current
address lookup for retained producer identity. The actual new row must identify
the producer before a finite view/copy conversion is implemented. No new CPU read
over that native producer is part of this diagnostic.
