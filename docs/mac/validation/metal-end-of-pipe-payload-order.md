# Ordered Metal end-of-pipe payloads

Source-only successor to `e2794f04`; no build/test/device run at this checkpoint.
32 original managed rows are prepared in `MetalRenderHostTests.EndOfPipe.cs` and
27 additional receipt/presentation controls in `MetalRenderHostTests.InitialCompletions.cs`,
separately from the 27 strict writeback controls. Synthetic status/wait overrides prove only
managed control flow. No canonical backing authority or dormant graph activation.

The neutral `ICommandStreamHost.DefersEndOfPipeLabels` capability defaults false.
Managed and Vulkan hosts preserve their immediate interpreter label behavior.
Metal alone defers the existing immutable `EndOfPipeWrite` scalar payload. The
interpreter skips only EOP/clock/flip label copies, clears its immediate own-write
receipt, and leaves ordinary command memory writes alone. Packet-time clock
values are preserved; this does not qualify exact hardware timestamp semantics.

Metal admits one ordered action in the existing logical queue: flush and wait for
successful native completion, write the label through the host's original memory
domain, check `TryWrite`, then deliver the existing interrupt/flip acknowledgement.
A preceding writable-buffer dispatch/draw completes its explicit CPU writeback
before the consumer can take this action. Missing/throwing label writes cannot
reach acknowledgement. Following actions are suppressed by existing sticky Drain
failure handling. Waiting on the serial Metal queue is conservative cross-queue
over-synchronization, not a new global sequence scheduler.

There is one synchronous initial case: under the presenter gate, a never-admitted,
not-closed, failure-free lifetime with no renderer/device/queue/batch/last buffer
and empty queue/accounting proves no preceding native work. Its exact in-flight receipt
is installed under that gate; memory/event callbacks run outside it. Later EOPs,
startup, presentation publication, queue admission/execution and close wait for
that exact callback to finish, releasing the gate while waiting. Every waiter
rechecks the currently live receipt after wakeup; consecutive callbacks cannot
be overtaken. The wait is bounded to five seconds per operation/receipt. Self
reentry or expiry latches a failure without clearing or stealing the callback
owner. Close cannot revoke/free owners while that receipt remains live. Only
the original callback finally removes its exact receipt and wakes waiters.
Callbacks and native teardown never execute under this wait gate. A refused admission
after any accepted/native work, shutdown, or failure never falls back to label or
acknowledgement publication. It records the original cause, wakes waiters and
reports an actionable failure through the existing GPU fatal route.

GDS readback continues to synchronize before reading bytes. Zero synchronization
admission is accepted only for that same proven initial empty case; a refused or
unsuccessful synchronization fails before GDS reads/interrupts. Orderly backend
RequestClose stops/drains the command worker (including its last SynchronizeGpu)
before requesting presenter close; an already-closed/failed UI is not reported as
a successful synchronization.

The isolated candidate uses the existing GPU fatal route for refused closed
admission. An earlier manual UI close while the command worker is still running
can therefore fail fast under the application's default fatal policy. It still
suppresses label/interrupt publication and preserves any original GPU failure.
There is no newly fabricated successful-shutdown or typed normal-cancellation
receipt; broader clean-exit classification remains separate.

Prepared controls cover actual interpreter EOP32/64+paired WAIT_REG_MEM blocking
and resumption, unchanged default behavior, actual Metal scalar widths/neighbors,
clock deferral, initial acceptance/refusal, writer/native failures, and three
actual registered kernel interrupt cases. These remain unexecuted source fixtures.
They do not prove native command-buffer lifetime, mapping/copy serialization,
guest gameplay, all hardware EOP semantics or GPU performance.

The pre-reservation source checkpoint `6a5480ea` is retained as reviewed failure
history: its empty gate predicate did not order the outside-gate payload against
concurrent completions or startup. It is not a qualified ordering implementation.

Translated onscreen presentations execute after guest Drain and currently have
no ordered producer/writeback receipt. The candidate records scalar unresolved
write debt at actual accepted presentation publication for nonempty global
buffers with both `Writable` and `WriteBackToGuest`, and image `Write`/`Atomic`
intent. The aggregate buffer list already includes stage/range buffers; stage
bindings only index it. Read-only buffers, disabled writeback, zero-length buffers,
read-only storage images and the fixed fullscreen shader's transient-only output
do not create this debt. Writable presentation acceptance is also behind the
initial callback barrier. Replacing the current presentation or closing cannot
prove a replaced render borrower completed and cannot clear its scalar debt.
No reset is implemented for this non-restartable presenter lifetime. Only a
future actual ordered producer receipt or genuinely new initialized lifetime
could retire it. EOP checks debt at admission and again before execution/label
acknowledgement, preserving the original failure rather than claiming GPU success.
This is an explicit unsupported scheduling boundary, not presentation activation.
GDS/device-address access beyond explicit aggregate buffer/image write metadata
is not newly qualified by these controls.

The new source controls pause an actual initial callback, observe production
Monitor.Wait waiter accounting, and exercise later EOP, actual admission, common
presentation publication, startup failure, close/retry and self-reentry. The
startup negative control never reaches native startup. Presentation tests use
actual SubmitTranslatedDraw and subsequent actual interpreter-host EOP/Drain,
including replacement debt and an accepted EOP followed by a writable publication.
All 86 prepared rows remain unbuilt/unexecuted at this source checkpoint.

The receipt orders presenter operations only. It is not an ICpuMemory backing
lease or all-writer content authority, and does not stop unrelated guest memory
mutation. A label writer already in progress cannot be rolled back if an external
failure occurs; the subsequent health check suppresses interrupt/ack publication.
A callback that stalls past the bound remains its owner and must finish or be
handled by the existing broader process shutdown policy; no forced memory return
or successful cleanup receipt is invented. Initial callbacks that themselves
attempt startup/admission/close are rejected as self-reentry. Broader ordered
presentation execution, clean UI-close cancellation and native qualification are
separate prerequisites.

Direct presentation callers wait before deriving current presentation sequence,
required queue tail or splash state; the common publication guard also protects
accepted ownership. Otherwise Monitor.Wait could release the gate after a caller
had captured stale metadata. The execution-time health check for an already
queued callback remains a scalar snapshot, not an atomic acknowledgement-versus-
shutdown lifetime pin. Graph-disabled close still does not join an already-taken
queued callback; this candidate makes no general queued callback teardown claim.
That separate accepted-action/close protocol is not fabricated here.
