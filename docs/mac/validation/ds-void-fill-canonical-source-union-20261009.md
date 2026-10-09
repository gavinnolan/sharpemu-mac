# DS, Void, Fill and canonical dirty source union

This isolated, unqualified source stack starts at
`5016bc82457a17ac9c25ffb1e1e9b540f9001290`, which adds the missing exact storage
placement helper to the frozen DS and Void source stack. Its inherited BFC
first-build failure remains retained; qualification of the dependency successor
is owned separately and is not evidence for this union.

The complete ten-path Fill delta from `8c4b352f` through
`aadd72488b6e35b681985a6dd80f85341d82593b` and six-path canonical dirty delta
through `a1db7b57916b5f8916e17c6763168dabce7fa604` were imported by explicit
local cherry-picks of `f9954b63`, `aadd7248` and `a1db7b57`. All applied without
conflict or manual resolution. The two donor path sets are disjoint, and each
resulting blob exactly matches its frozen donor. All inherited base blobs
outside those sixteen paths remain exact. This note is the only additional
change. Reversing the sixteen donor paths to the base and removing this note
must reconstruct the complete dependency-base tree.

Both donor implementation and validation notes are retained as provenance.
Their isolated source inventories are not combined execution results. The
union source expectation is **1092 ordinary managed rows per CLR** (208 Host,
779 Libs, 105 GPU) and **103 separate native workers per CLR**: base 1064/89,
Fill +20/+7 and canonical dirty +8/+7. Guarded native Facts are excluded from
the ordinary count. No inherited case was removed. Compiled discovery and all
qualification remain pending for this exact union.

Static dependency closure was reviewed: all sixteen donor blobs are present,
including closed Fill payload/host mirror execution, original retention,
canonical pending guards and retry helpers, and both native fixture partials.
The existing storage placement helper remains byte-identical to the dependency
base and donor. Existing HLE/Libs/Metal project references and SDK compile globs
supply these files and their metadata types; no package/reference or build
configuration changes were introduced. Static review cannot establish a
successful build.

Flags and defaults are unchanged from the exact donor and base blobs: native
color borrowing retains its existing opt-in flag, rejected Void capture remains
default off, and no runtime configuration was activated. Fill ownership remains
bounded to the qualified aligned non-GDS payload and no native mirror extent;
unknown effects retain their exact original scope. Canonical pending work
retains the same original already-Applied owner only after known cancellation,
strict Current lower predicates and absent active encoding. This union does
not add the separate cold-enrollment wait or any existing-watch publication
wait protocol.

No compiler, build, discovery, test, native worker, publication or game ran while
creating this union. Main, frozen donors and previous evidence were untouched;
no remote operation occurred. Game entry, rendering and performance remain
unverified for this exact source.
