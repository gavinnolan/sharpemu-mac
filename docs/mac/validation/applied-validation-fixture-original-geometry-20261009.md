# Applied validation fixture original geometry

Fixture-only successor over frozen `91ce5d5e`; the failed `47174a43` source,
results and new diagnostic candidate remain unchanged. No builds, tests, native
workers or games were executed for this successor.

The shared helper places the singleton's full 65536-byte original at independent
volume allocation base + 65536, and the linear 16-cubed original at base +
131072. The earlier acquired-disjoint writer was mistakenly placed at base +
131072 + 32768, inside the second original's physical footprint. The earlier
stale-first/token/later-overlap points used the allocation's unused prefix.
This is a source geometry finding; the failed runtime's winning predicate is
not inferred without its same-query first-refusal evidence.

All fourteen Applied cases retain their existing effect, status4, one-encoder,
one-Applied, whole native/canonical/neighbor byte, callback/EOP, and quarantine
assertions. Preparation-only geometry stays unchanged. Acquired disjoint
writers now use unused prefix + 32768. Stale-first, token, and later-overlap
negatives use the SAME cached singleton original Mapping.SourceAddress.
Expected independent-backing byte changes use that exact original offset.

Before control hooks or writer effects, discarded one-byte actual-provider
copies capture only setup mapping metadata. Complete original covered spans,
backing identity/incarnation and bounded canonical offsets are checked against
both full 65536-byte volume originals and the full image original. Disjoint
writers must overlap neither volume; the stale-first negative must overlap
exactly volume zero. The separately captured negative point must physically
overlap volume zero and neither volume one nor the image. No CPU copied byte
is used as native content, no runtime original/receipt is replaced, and no
provider/native/currentness policy is changed. Actual winning-guard diagnostic
and fresh serialized qualification remain required.
