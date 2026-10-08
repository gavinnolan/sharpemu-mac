# Native host-stop result classification

A host request is a separate retained cause, not a guest execution error. Callback scopes intentionally clear and restore `LastError`; they must not discard the first host-request reason or overwrite an existing guest/native failure merely because Stop was requested.

The backend classifies a confirmed forced host stop only when the original main entry context accepted its return-sentinel redirect while that exact native call was in flight, the native entry actually returned, and no caught execution failure or existing backend error remains. Worker/nested callback redirects alone are insufficient. A confirmed stop still returns `false` with `ORBIS_GEN2_ERROR_CPU_TRAP`; the dispatcher labels its session `HostShutdownRequested` rather than inventing a missing implementation. Normal zero returns remain `ReturnedToHost`; nonzero returns, explicit import-loop errors, and native failures retain their existing outcome. An outer execution/cleanup exception invalidates the confirmed-stop classification.

This records the observed native return after a host redirect. It does not establish clean guest shutdown, joined workers, released game resources, successful guest exit, or a real game's termination latency. Request without redirect/return remains unconfirmed. Unknown forced unwind no longer fabricates an import-loop cause.

No signal-handler ABI, recovery acceptance, retry, native stub, scheduling, teardown policy, mapping lifetime, or backend fallback changes are included. The first request cause is allocated/formatted on the ordinary host request path; import-boundary publication is one scalar atomic operation.

## Focused validation

The isolated x64/Rosetta controls pass 15/15 with zero skips: eight actual import-boundary redirect/negative controls, plus seven result/cause controls. They include a worker/nested context and context-matching later continuation unable to certify the main entry, first-request retention, request without redirect, missing native return, existing native/import-loop errors, normal zero/nonzero returns, and request not overwriting a prior error.

The existing actual normal-return worker was directly invoked with its worker flag enabled, passes 1/1 with zero skips, and retains `Guest returned: 0` / `Execute END (LastError: null)` plus dispatcher `ReturnedToHost` assertions. This is actual x64 execution, not an ARM early return. No ARM native-execution test, native stop/game/GUI/device run, or Windows/Linux runtime was attempted. The redirected stop completion controls compose the actual sentinel-writing helper with the safe-side result classifier; they do not claim a full real-game stop fixture.
