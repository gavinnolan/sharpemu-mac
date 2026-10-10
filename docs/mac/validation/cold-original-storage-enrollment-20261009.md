# Cold original storage enrollment

Source work only. Compilation, managed tests, fresh activation workers, native
provider/Metal tests, publication and game replays remain unrun.

The Oregon replay on `8c4b352f` refuses its first watched copy before returning
an observation. The first refusal records publication exclusion, followed by
ordinary writer admission. Existing copy retries acquire another original;
this optional path instead keeps the first provider capture and its backing pin
before any bytes are copied.

## Contract

`TryReadMappedCopyWithWriteWatchAdmissionWait` is an optional CPU/host provider
contract. Unknown implementations decline by default. The known Mac provider,
shared backing, guest-space owner, physical memory and tracked-memory wrappers
forward the same capture while checking closure, identity and inherited gates.
The caller callback is a veto only; it cannot supply content or currentness.

Only a positive admission-busy result from the first watch-enrollment CAS permits
waiting. The provider keeps the same mapping objects, physical runs, backing
access pin and private watch charge. It waits at most 500 ms without inherited
gates, checks the original mapping and caller veto around every wait, and then
enrolls one watch before one logical source copy. Callback capability and exact
lifetime checks run before and after caller code. First refusal diagnostics are
detached and emitted after provider gates and access pins unwind.

Capacity, coverage, rights, remapping, backing closure, lost ownership and caller
veto do not create another original. A retained reservation can time out, but
the waiting path never releases somebody else's ticket. A failure after bytes
were copied returns no observation and does not repeat the copy, refresh its
mapping or enroll another watch.

## Frontend

`SHARPEMU_METAL_STORAGE_ORIGINAL_ENROLLMENT_WAIT=1` is default off. It applies
only to an already-qualified watched storage copy with the actual Metal backend
and explicit provider capability. That route uses one original attempt, retaining
the first read token, epoch metadata, session and backend. Unsupported capability
refuses; it does not switch to another copy route. Registry metadata vetoes a
native or queued image owner appearing during the wait. Existing epoch stability
semantics and every final geometry, content, provenance and registration check
remain in place.

This does not change the already-enrolled Octopath first-watch Stale result.
Publication exclusion alone supplies no owner identity or completion promise.
An existing-watch continuation requires a separate contract and proof.

## Pending validation

The Host test partial has 30 real-ledger/pinned-managed-byte rows, including
known ordinary/read-validation/disjoint-publication release, retained first
mapping and pin, mapping/lifetime loss, callback-acquired gates, post-copy loss,
timeout, capacity/coverage refusal and unknown wrapper defaults. Its deliberately
unlinked validation reservation is restored only by fixture teardown before
normal receipt disposal; no production admission or pin is reset.

The frontend partial adds 14 ordinary owner/gate/registry rows and 13
separately activated metadata rows. Three separately activated actual
provider/Metal scenarios reuse the existing full-byte/status-4/two-publication/
ordered-completion fixture with a foreign read-validation, disjoint publication
or ordinary writer admission. All are source work, with no executed results. Synthetic frontend adapters cannot establish provider exclusion or
native authority. Real provider/Metal controls must retain full original bytes,
completion status 4, one copy/publication and ordered completion-event evidence
before this flag is activated in a game replay. No native/JIT write exclusion,
game entry, FPS or loading-speed claim follows from these source controls.
