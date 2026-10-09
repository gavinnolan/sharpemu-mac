# Typed native fixture uses actual render scope

Source-only successor of frozen 28d0b8cc. The original first ARM native failure
and its source, closure and receipts remain unchanged. No build, test, native
worker, publish or game ran for this successor.

The failed typed native callback reached RequireCommandMemoryExecutionLocked
with exact taken/executing work but without the production drain scope. Its
fixture called DrainGuestWorkCore directly through DrainWithControlledBudget.
Only TryDrainGuestWork increments the thread-local drain depth and restores it
in finally. The strict first execution predicate therefore correctly refused
the fixture callback. This is a source-localized composition error, not proof
that real runtime receipts or native original publication are faulty.

The six typed native scenarios now use a fixture helper that calls the actual
TryDrainGuestWork wrapper, retaining the production per-pass work budget. It
repeats bounded render passes only while guest work is pending, with the same
five-second aggregate fixture deadline checked between passes. It never sets
drain depth, changes the readonly work budget, or bypasses the execution guard.
The shared direct-core fixture helper and unrelated native color cases remain
unchanged. Native external worker watchdogs remain necessary for blocking
native calls; this helper is not a preemptive native timeout.

All six full-byte/status4/EOP, physical-alias, mapping replacement, rights,
session, tracked-destination and no-ack oracles remain unchanged. Existing
native caller join/unknown-ownership quarantine is preserved.

Two new metadata rows exercise actual enqueue, Take and callback handling.
The direct core refuses the exact original receipt at drain depth zero without
completion. The actual wrapper validates that same receipt and completes the
metadata callback while restoring drain depth to zero. Neither row prepares
a publication transaction, executes writer bytes, or establishes native pixel
authority. Both retain the first complete mapping and perform no recapture.

Source-derived expected inventory is 972 managed rows per runtime
(199 Host, 693 Libs, 80 GPU), adding two rows to 28d0's 970. The 64 native Facts
per runtime are unchanged. Compiled discovery and runtime qualification are
pending. Package remains the exclusive executor of future qualification.
