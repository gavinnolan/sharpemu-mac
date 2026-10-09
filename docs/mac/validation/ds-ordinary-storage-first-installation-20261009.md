# DS first ordinary-storage installation refusal

Unqualified source successor of the DS/Void/Fill/canonical union `a13a75e7`.
No build, test, native worker, publication or replay has run on this successor.

The first actual `5016bc82` DS replay ends with exit 134 after 51 seconds,
peak RSS 2,905 MiB, launch artwork only and zero observed guest frames. The
first command-stream error at lines 633–634 is Compute `3E9247D2A2F42DBB`:
“Ordinary storage original installation changed or exceeded its bound.” The
read and storage-write bindings both name `0x715FF00000`, a 64×64 float 2D
image. The later fatal at line 694 is consequential. This log does not tell
which installation predicate won; neither capacity nor a registry race is
established by the combined exception.

The immutable DS manifest is
`ds-void-placement-dependency-5016bc82-game-entry-prepared/ds-entry-manifest.json`,
SHA-256 `9eaa583f46f2145688abbe43a4cd9a19ed497e635dde0ed75f5bbe36c83586ba`.
Root independently verifies 37 new artifacts, 2,003 source files, 104 compiled
files, the 239-file signed payload and owned process closure. No game retry on
the failed source is performed.

This source saves the first installation guard under the existing presenter
lock, preserving its exact evaluation order and exception type. The exception
includes that predicate, the address and the same-lock registry count/capacity.
It changes no installation entitlement, bound, mapping capture, watch, native
owner, retry or release behavior. A later sample cannot replace the saved
refusal. Existing DS full-byte/native ownership controls and the two separate
Fill/canonical fixes still require qualification on this combined successor.
