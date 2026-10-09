# Quake pad delivery diagnostic

The qualified `188b9c28` five-minute replay observes `Quake Initialized` at
52.852 seconds and emits every planned key after that marker. The game starts
`demo1.dem` and never demonstrates player control. Its late captures are
corrupted, and guest flips are sparse. The final command-stream read fault
occurs after host interruption during shutdown; it is not the entry cause.

The input owner's writes do not prove that the guest polls during those holds.
`SHARPEMU_LOG_PAD_READS=1` records the existing pad-write attempt after the single
`Memory.TryWrite` returns. It reports the same buttons, sticks and triggers
already encoded in the pad data, destination address, write result, current
managed thread, read ordinal and monotonic seconds since diagnostic activation.
It performs no second input poll or guest-memory read and changes no return value.

Output is capped at 16 initial, 64 active and 64 periodic neutral rows per process;
the categories can overlap in a single line. Neutral periodic selection has a
five-second minimum interval. An exhausted output budget does not prove the
absence of further pad reads. Invalid-handle/argument reads do not enter the
existing write path and are outside this trace. The cached flag defaults off;
formatting and the best-effort sink run only for selected diagnostic rows.

Next replay should hold Options/Cross and movement long enough to span sparse
guest frames. Correlate the input-owner log with pad bytes successfully written
and visible scene changes. This source has not yet been built or run and supplies
no Quake entry, render-correctness or performance result.
