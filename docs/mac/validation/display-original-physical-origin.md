# Original display placement for the finite storage cohort

The retained 9e3 Octopath and Silent Hill runs both refuse a display image at
0xEFC0000000: 3840x2160, pitch3840, RGBA8, native2D, initialized/version1,
GpuWritten=false. The image was created by the ordinary display path without
original canonical mapping metadata. Distinct virtual addresses cannot prove
that it is disjoint from the finite storage publication.

This successor captures metadata only at that image's initial complete guest
copy. Under the existing opt-in RGBA publication policy, the ordinary display
reader uses ICpuMappedCopySource for the full pitched native byte extent, with
an original read/content/session observation. Copied must return the exact full
source/footprint and remain current; Rejected never falls back. Unavailable
overwrites all bytes with the existing ordinary read and grants no positive
origin. External pending initial-data arrays likewise grant no mapping origin.

The exact uploaded native owner retains that original mapping, session, shape,
format and texture identity. Same-owner native version advances do not rebase
the physical mapping. Cohort checks compare original canonical backing offsets,
not new queries or virtual addresses. This metadata grants neither display
writeback nor GPU completion, permissions, dirty acknowledgment or arbitrary
native/JIT writer authority. All existing target-token/debt/publication checks
remain in place. Stale mappings, shape/owner/session changes, physical overlap
and missing origins still refuse.

Retired GPU-written display origins remain in the same combined128-entry
physical-debt budget as typed retired origins; unknown/missing/saturation stays
sticky. Construction failure rolls back only its exact registry owner, consumes
its texture before release and preserves the original failure. Defaults retain
the ordinary source read with no optional provider capture.

Eighteen new managed rows are prepared but unbuilt/unrun. They use actual ledger
copies over pinned CLR bytes to check full pitched padding, copy rejection,
mapping ABA, generation/session changes, unavailable overwrite, original
exception propagation, default-off behavior and canonical backing identity.
Owner/shape rows exercise only the scalar validator with inert sentinel values;
they do not invoke Metal or establish native receipts. No native display test or
title advancement is claimed. Exact qualification and affected-title replay
require separate serialized grants.
