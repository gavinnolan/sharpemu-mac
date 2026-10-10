# Guest buffer writeback and completion prerequisite

Source base: `6a41f69d69911a12ece576d2c78fc2ae7293c9cd`. Source-only checkpoint;
the original 26 managed controls and one subsequent zero-writeback-handle control
have not been built or executed. No device/game run.

Compute, offscreen draw, presentation draw and GDS copy-out already explicitly
flush, wait and then call `WriteBuffersBackToGuest`. Their wait now accepts only
MTLCommandBufferStatusCompleted (4), checks again after an actual blocking wait,
and rejects Error (5) and unexpected/nonterminal post-wait states. Native error
description is diagnostic only. A zero handle remains a legitimate no-preceding-
commands case for empty synchronization/EOP. The four actual GPU buffer-copy
paths require a nonzero committed command buffer before any CPU copy; zero
rejects before status/wait/native access. Nonzero handle/status controls do not
claim a mapping or guest-content lease.

Nonempty writeback without guest memory and rejected `TryWrite` now fail. Writer
exceptions propagate unchanged. Invalid pointer/length/address-wrap combinations
reject before constructing a span. Writable upload snapshots are checked before
any upload/native access, because clamping the copy while later reading the
original length could exceed the allocated upload slice. GDS's independently
allocated exact-length slices remain valid despite their empty `Guest.Data`.

The existing real Drain failure path latches the cause, does not acknowledge the
failed work, and prevents following actions and admission. Partial CPU writeback
cannot be rolled back: earlier successful writes remain, but the remaining writes
are not retried and completion is not acknowledged. Queue controls explicitly
exercise that boundary. Default-null status/wait controls are managed test seams;
they do not establish actual native completion or native resource lifetime.

This is **not a complete end-of-pipe label ordering fix**. The interpreter writes
32/64-bit/clock/flip labels directly through `_host.Memory.TryWrite` before
`RecordEndOfPipe`; that host hook currently defers only interrupts/flip completion.
Waiting in that hook alone cannot undo the earlier label publication. A separate
host-neutral deferred payload+record seam and actual interpreter/Metal-host tests
are required. Normal command memory writes must retain their existing behavior.

Primary status contract: [Apple MTLCommandBufferStatus](https://developer.apple.com/documentation/metal/mtlcommandbufferstatus).
Successful managed controls, eventual native command-buffer qualification and
real guest label/interrupt ordering are separate evidence levels.
