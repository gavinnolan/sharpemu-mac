# First existing-watch admission owner diagnostics (source only)

Base: frozen `a27c011f2045319bcd2677cea459e7635eb9b613`. This isolated candidate
is UNQUALIFIED: no compilation, discovery, tests, native workers or games were
run for it. The inherited canonical-pending fixture correction remains a
separate successor; it is not mixed into this a27-based source change.

## Motivation and limits

The prior Octopath first query reported original-watch Stale with the actual
PublicationBit guard. That scalar alone cannot distinguish short enrollment,
publication, cancel-only validation, original-read validation, or a lost link.
The identity of the actual Octopath holder remains unproved. This candidate
adds detached first-query attribution only. It does not add a wait route,
change Stale to Busy, rescue an original, recapture bytes/mappings/tokens, or
change any provider entitlement, admission counter or lifetime operation.

## Metadata and publication order

Detailed tracking is explicitly opt-in through the existing startup flag
`SHARPEMU_CANONICAL_PUBLICATION_DIAGNOSTICS=1` (default off), checked before
allocation. Production never changes this cached flag. A private per-ledger
mutable integer override is used only by managed source controls, without
reflecting cached readonly flags. It changes diagnostic allocation only and
cannot supply an identity or receipt. Existing records still detach exactly if
diagnostics are disabled; an unrecorded live owner is never backfilled when
enabled, and continues to report owner-gap / strict Stale.

Each enabled cold exclusive attempt owns a separate diagnostic record with a nonreused
reference, diagnostic identity, kind, acquiring managed thread, phase and
terminal outcome. It contains no transaction, capture, backing or native owner
reference. Transaction records allocate their diagnostic record before provider
admission; enrollment allocates before its actual CAS. The active reference is
published only after successful original PublicationBit CAS. Ordinary writer
intervals and rejected CAS attempts never publish an owner.

On the diagnostic overload only, the active reference and phase/outcome are
sampled around the same original admission read, after the original released,
revoked and coverage guards. A missing/changing reference, changed phase or
identity exhaustion reports unknown attribution. An ordinary query does not
read owner metadata. Guard short circuit order, primary admission value,
Boolean result and Status classification expressions are preserved.

An exact owner compare-exchange clears its reference BEFORE the unchanged
admission subtraction, producing an intentional unknown gap. It cannot clear
a younger owner. Terminal AdmissionReleased is published only after subtraction
and the existing caller cleanup return. This describes known admission cleanup,
not content, native ownership or global writer coverage. Detached first-query
outcome is never refreshed after the call; there is no public owner handle.
Enrollment retains only its released diagnostic record on the exact watch for
source controls; private successor watches have no enrollment record.

All actual reservation families pass the same exact diagnostic record through
prepare/install, commit/cancel, failed-history release and cleanup. Lost
publication, validation and original-read links mark that record Unknown BEFORE
the existing throw. Publication quarantine and cleanup exceptions also mark
Unknown. Existing throws, pin/exclusion retention and cleanup ordering remain.
No additional provider list scan, lock, callback, forced release or retry is
introduced. Unknown metadata never suppresses real cleanup.

The presenter records local ticket kind before its existing short circuits and
prints `local-ticket-no-wait-authority` for any local prepared ticket. No reporting
label is consumed by any authority or continuation. First provider metadata is
passed through the existing wrapper diagnostics unchanged and printed alongside
the first watch guard in bounded opt-in diagnostics; no later query replaces it.

## Source controls, not execution evidence

New Host memory source cases (19): three disabled-path / enable-during-live
reservation cases (no record and no backfill); three actual prepared-family cases asserting
real receipt provenance, exact identity/thread/phase, first Stale and Boolean
refusal, detached outcome, full backing bytes, one-use cancellation and original
pin count; one actual enrollment terminal-record case; five pure negative
bracket inputs (missing-before/missing-after/mismatch/phase-change/ordinary);
one actual admission ABA identity case; three earlier released/revoked/mapping
ABA veto cases; three actual lost-link cases. Lost-link tests root exact backing,
managed pin, watch and consumed receipt for the worker lifetime; they perform no
retry, forced release, restoration or cleanup of unknown ownership. The three
quarantined fixtures retain bounded managed backing allocations (3 x 49152-byte
arrays) and their actual alias access pins. These are real managed ledger
transactions, not native execution or kernel authority tests.

New Libs source cases (4) check the pure local-ticket reporting labels. They
perform no provider operation and claim no receipt authority. No existing case
or native oracle is weakened or removed. Static source inspection confirms all
four exclusive acquisition families and their release paths use diagnostic
records; identity exhaustion remains a source-only conservative unknown path.
Fresh exact-source builds and case discovery are required before execution.

## Overhead and unresolved qualification

With tracking disabled, new owner allocation, identity increment, reference
publication/clearing and phase writes are absent. The cold paths add one bounded
enablement load/branch per attempted acquisition and nullable metadata checks;
ordinary interval accounting is untouched. Existing full diagnostic structs
still grow even with owner tracking off.

With tracking enabled, there is one additional small managed object and diagnostic identity increment
for each publication/validation transaction creation, including later declines.
There is one per cold enrollment attempt, including busy declines; the existing
500ms cold wait can therefore allocate multiple records at its existing 1ms
pace. New allocation occurs before exclusive CAS, never while excluding writers.
It can fail allocation before admission; no exclusion is acquired on that failure.
Actual allocations, GC effects and latency have not been measured. There is one
active-reference publication and exact-reference clearing CAS per successful
exclusive operation, plus phase/outcome scalar writes. Ordinary writer slots,
admission increments/decrements and history accounting are unchanged. Non-
diagnostic watch checks perform no owner-reference read, allocation or atomic
operation; diagnostic checks add bounded scalar reads and detached value copies.
Public diagnostic structs grow; runtime and build compatibility are unqualified.

The scalar snapshot is a diagnostic observation, not a linearizable certificate
that an owner will finish, that it is foreign to a consumer, or that a lost link
can safely wait. A diagnostic trace failure after known subtraction can mark the detached record
Unknown while the bit is already released; it is fail-closed metadata, with no
remaining active owner or altered cleanup.

No game boot, gameplay, correctness or performance claim follows.
