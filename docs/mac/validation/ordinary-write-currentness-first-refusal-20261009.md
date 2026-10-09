# Ordinary admitted-write original currentness attribution

Source-only successor over a3cada8b. No compiler, test, native worker or game was
run for this candidate. No permission/currentness exception or write retry is
added. Passive Poppy ingress work remains paused and uninstalled.

The a3 Silent Hill run `20261009-071349-sh-game-entry-provider-and-pad-a3cada8b`
records `original-mapping-changed-after-admission` for eight bytes at
`0x7260EFCCD8`, with actual admission `Admitted` and original capture, pin and
snapshot checks all true at receipt creation. This proves neither the particular
post-admission predicate nor a physical remap. Nearby loader fallback is separate
from APR; it must not be called an APR header failure.

`TryAcquire` validates the saved original before and immediately after its own
outer writer admission. `BeginOperation` changes only ordinary admission/slot
bookkeeping. `OrdinaryWriteIdentity.IsCurrent` contains no writer-admission or
history predicate. Own admission therefore cannot directly invalidate the saved
identity in this implementation.

The remaining alternatives include a replacement of the entire shared immutable
view-list object (even if an unrelated view changed), shared owner shutdown, or
original provider unknown/revoked/copy-unavailable/guest-right/incarnation state.
Derived protection deliberately makes old copies unavailable and advances the
copy incarnation before its native effect. An ordinary operation does not by
itself exclude that provider metadata effect. None of these possibilities is
identified by the existing aggregate game row, and none authorizes ignoring the
saved snapshot or incarnation.

With the existing cached `SHARPEMU_LOG_ORDINARY_WRITE_FAILURES=1` flag, the PVM
post-admission guard uses a diagnostic alternative with the identical ordered
predicates and one read per predicate reached. It records the first reason,
original view address/index and saved incarnation. A compared incarnation is
present only if that final predicate was actually reached; earlier failures say
`not-classified`. The detached row is formatted through the existing 64-row cap
only after all required PVM/provider output gates unwind. There is no later
authority/currentness query to attribute the failure and no diagnostic callback.
The default-off original property, try-acquire path, bytes, cleanup and fallback
decisions are unchanged.

Six prepared real-ledger rows cover own admitted writer remaining current,
unrelated shared snapshot replacement with original physical mapping still
current, raw original provider revocation with unchanged shared snapshot,
in-flight derived copy unavailability, completed derived incarnation change,
and unknown provider winning before a revoked-view predicate. All refuse stale
write attempts and preserve untouched full bytes. Derived native completion is
explicitly synthetic; these controls do not establish actual mprotect or guest
write/runtime authority. Actual case execution and the next exact game row are
still required to select a general repair.
