# Singleton volume first refusal diagnostics

Base: fully qualified local main `188b9c2843289cdd4da5b709f49973eef3084f52`.

The fresh Octopath run `20261009-053910-octo-game-entry-readback-color-188b9c28` stopped in `FinalizeFiniteStorageWrites` / `WithFiniteStorageValidation` after two producer completions and two applied publications, with one canonical completion. The Oregon run `20261009-054029-oregon-game-entry-readback-color-188b9c28` stopped in canonical dirty acknowledgment after two canonical completions. Both reported the generic singleton original-content exception. Nearby read-watch admission messages cannot prove which individual original predicate caused that exception.

This slice records the first failing predicate while preserving the existing short-circuit order, ordinary Boolean versus classified status calls, independent typed-Busy classification, and all refusal behavior. Exception data includes the first predicate and the original classified watch result, when actually queried. With the existing `SHARPEMU_CANONICAL_PUBLICATION_DIAGNOSTICS=1` flag, the exception text also identifies the original address/length and explicitly later state/unknown-producer samples. No additional mapping, watch, token, provider, or native queries supply those diagnostics. Later state samples are not authority.

The existing twelve scalar refusal controls now assert the reason/status on untyped refusals, including stale-then-Busy, strict-default, unknown, released, session, and memory. The provider exception remains the original exception and typed healthy Busy remains typed. These are scalar controls, not GPU or gameplay evidence.

No publication, dispatch, staging allocation, metadata callback, dirty consumption, or acknowledgment is replayed. This is diagnostic source only until the exact candidate completes managed and native qualification; no game-entry improvement is claimed.
