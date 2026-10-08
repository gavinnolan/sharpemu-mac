# Command-memory ordering and Quake replay

Isolated clean source `49e424a2fd5c03a1b90d2f3f5f949dcc70bc2504` adds an owned,
ordered route for ordinary command-packet writes, fills, copies, atomics and
semaphores. Atomic results become available after their admitted operation
finishes. Borrowed payload spans are copied into queue-owned storage. Existing
portable hosts retain immediate behavior. The initial implementation waits per
ordinary operation; its performance has not been qualified.

The focused selection passes 104/104 on ARM64 and Rosetta x64 configurations:
11 new ordering rows, 59 existing host completion rows and 34 generic interpreter
memory/wait rows. There are no skips or failures; exact case identities and
source/DLL hashes match. These are managed controls, not actual GPU fixtures.
The compiler leader succeeds with 128 warnings and zero errors; a residual
owned compiler child requires TERM, which the build receipt preserves.

A separate self-contained osx-x64 app publish succeeds in 40.30 seconds without
cleanup signals or remaining owned workers. Its 239-file native-only bundle
passes deep strict signature verification, exact clean source provenance and
before/after payload checks. These results do not qualify startup or gameplay.

The signed Quake replay runs for 100 seconds with Enter at 20/40/60 seconds,
bounded command-memory tracing and the existing abort callsite probe. It
remains black after three presentations, peaks at 1083 MiB and stops by owned
TERM with no KILL. The same guest context-label timeout still aborts through
`80034EF6C`, `80034F011` and `8002C9F65`. Host survival is not guest progression.

The trace supplies new scheduling evidence: EOP writes are admitted before the
abort; the first traced execution (sequence 3) appears after the abort in the
same log. Labels at `7095000000` (sequence 17) and `7095000080` (sequence 35) are
eventually applied and acknowledged. All 128 recorded command-memory events
are EOP Write64 of value 1; no ordinary overwrite is observed in this window.
The trace has no timestamps and its cap prevents a complete command timeline;
neither exact context-label identity nor the cause of delayed drain is proven.
The ordinary-operation fix alone does not repair Quake. Next work checks
presenter startup, early queue draining and readiness before the guest's
frame-start wait, preserving real native completion and label order.

Evidence is retained in
`artifacts/validation/all-games-20261008/command-memory-order-49e424a2`
(managed/build/source manifest) and
`artifacts/validation/command-memory-order-49e` (publish, complete game run,
bundle manifest and native process sample). The late sample occurs after guest
abort, so it is not a profile of the failing frame-start wait. The attempted
managed stack tool fails to load an incompatible host runtime and contributes
no managed stack evidence. This candidate stays isolated from main.
