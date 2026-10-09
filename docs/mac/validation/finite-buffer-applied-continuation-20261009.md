# Finite buffer own-publication continuation — source only

The frozen 592 mixed native phase failed after the buffer publication and one
image Applied effect, before image canonical finalization. `PublishFiniteStorageBuffers`
had correctly removed the consumed input's `_finiteStorageBufferOrigins` entry.
`ValidateFiniteStorageAppliedOriginals` then called the pre-effect input lookup
again. A consumed input is not an unconsumed producer copy.

This successor allocates bounded per-buffer slots with the exact successful
pre-encoder phase. Each retains its original DTO, Data reference, geometry,
memory/session and original copy. The exact prepared transaction is stored
before commit, and commit starts once. On Applied, this transaction's
SuccessfulObservation is saved before any throwing continuity checks. The
original CWT input is consumed exactly once. Continuity, completed notification
and successful transaction disposal must all finish before the slot is complete.

The slot capture runs beside the original buffer validation BEFORE entering the
presenter gate. The phase constructor only stores those already captured slots;
it performs no provider currentness query under the presenter gate. This narrow
successor preserves the unexecuted b9 source and its gate-order review finding.

Image post-Applied validation now checks the complete slots, their same original
input bytes/identity/session, both original and exact successor mapping identity,
and full physical disjointness against the image and other buffers. It performs
no guest copy, re-enrollment or writer revision recapture. Initial buffer provider
history admission and later image cancel-only history validation remain unchanged.
The buffer successor is mapping/own-effect continuity, not an external writer
or new CPU content certificate. All original volume/token/native cohort checks
and the strict finalization callback race remain unchanged.

A throwing/PartialUnknown commit, failed continuity/notification or uncertain release
quarantines the pre-existing image receipt root, original phase and exact buffer
transaction. Cancellation is attempted at most once; it cannot create a completed
slot. Known pre-effect cancellation does not invent Applied evidence. No dispatch,
staging extraction, buffer/image commit or finalization callback is retried.

Eight new ordinary real-ledger controls cover consumed-input success, actual
same-address remapping, changed session/input, unknown/incomplete state,
duplicate enrollment/completion and a cancellation error injected after real
provider release. The existing actual status4 mixed-buffer native case retains
its full image/buffer/native-volume/neighbors/EOP oracle and now also checks the
exact completed slot and consumed input. The simulated cleanup failure is
explicitly distinct from a real provider release failure.

Qualified source `1e17bc8d` passes three builds, 912 managed cases (188 Host,
644 Libs, 80 GPU) and 58 actual native workers per CLR. The mixed buffer/image
regression passes first on both actual ARM64 and x64 runtimes. ROOT independently
verifies all 830 native qualification artifact hashes and closure of 117 owned
process roots; source and compiled files remain unchanged. Native qualification
manifest SHA-256: `5d1798217e3c6b63f1efc3faa7febe6e4e1952e909b7ee3baceba8efd5ae7bb3`.
Publication and game replay remain separate validation steps. The failed 592
source, log and closure receipts remain immutable; fixtures establish no gameplay.
