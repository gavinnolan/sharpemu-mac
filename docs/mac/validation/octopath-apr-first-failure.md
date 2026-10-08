# Octopath APR first-failure attribution

Source-only diagnostic successor to `43a89254e94d2b0b8e98d460945e82d4e6962032`.

The preserved run is `root-entry-watch-fixes/artifacts/runs/20261009-011457-octo-entry-watch-fixes-43a89254/game.log`. First HLE failure is `sceKernelAprSubmitCommandBufferAndGetResult` (`ASoW5WE-UPo`) returning MEMORY_FAULT with command buffer `0x7061482A48`, result pointer `0x7061482A70`, ID pointer `0x7061482A78`. A later `sceKernelAprWaitCommandBuffer` (`rqwFKI4PAiM`) fails to find ID `0x740073`; the guest then reports APR read failure `74006e` at command-buffer offset `6881356` and aborts. The later GPU original-volume admission refusal is secondary.

Submit MEMORY_FAULT alone does not establish a missing or unreadable host file. Queued read/event/write failures ordinarily enter `executionResult` plus `errorOffset` while `CompleteCommandBuffer` returns OK. Submit itself can refuse command-state lookup, its ID write or its result write. The preserved run cannot identify which branch failed.

Opt-in `SHARPEMU_LOG_APR_FAILURES=1` captures original first failed header read address/count and submit command-state, ID-write or result-write branch. Command-state phase uses bytes zero because it is not a single transfer; individual header-read rows report the actual 4/8-byte request. The header helper preserves the existing read order and short circuit, with no reread. Fields come from the original failed call and its existing execution result/offset. A process-wide 64-row bound precedes formatting; cached default-off returns before formatting/counter, and formatter/sink failure cannot replace the original result. No host file query, read retry, token refresh, memory access fallback or dirty-policy change is introduced. This diagnostic does not yet expose a provider admission reason, so it must not label an unclassified write as PublicationBusy.

Separately, source inspection proves that a whole-backing publication reservation can reject an unrelated `SharedBackingViews.TryWriteBacking` operation before bytes, and PVM currently reports that Boolean as inaccessible memory. This is a possible general concurrency cause, not an established explanation for the actual43 APR error. The coordination successor must retain exact rejection provenance, release outer gates before waiting, reacquire real ordinary writer admission and revalidate original mapping/lifetime before one write attempt. It must not retry partial file operations or bypass publication exclusion.

No build/test/native/game execution for this checkpoint. Existing APR streaming and completion controls remain relevant; no mirror diagnostic tests are added.

## Source-only ordinary publication-busy coordination successor

The new path is limited to `PhysicalVirtualMemory.TryWrite` for shared-backed
spans. `Clear`, `TryCopy`, Vulkan's direct backing write API, raw stores and
signal recovery are unchanged. It is not proof that the actual 43 APR failure
was publication contention; the small `7232e6d8` diagnostic must identify that
original branch on a later authorized replay.

Before the first admission attempt, PVM retains the exact `MemoryRegion`
reference and all its fields, and SharedBackingViews retains its immutable view
snapshot. An enrolled known provider additionally captures at most 64 ORIGINAL
recorded Views, including their backing/physical offsets/logical guest Write
entitlement and copy incarnations. This capture allocates bounded arrays and
acquires the ordinary provider gate before admission; it performs no byte read,
query, content sampling or younger mapping reconstruction. This per-enrolled-write
cost is explicit and has not been measured. Missing, readonly, revoked, partial,
unknown or unsupported original provider records cannot qualify a busy receipt.

Only the exact rejected `BeginOperation` token containing PublicationBit can
mint the reservation, and only before any copy bytes. The reservation pins the
same alias/backing lifetime, not virtual permissions. PVM exits its read gate
before a maximum 500ms ordinary wait. Reentrant PVM/allocation, GSO mapping/
protection/disposal, SharedViews/backing/disposal and Mac provider/native mapping
gates forbid waiting. Unknown wrappers cannot authorize this optional wait.

After a REAL writer envelope is acquired (legacy incomplete/saturation tokens
are insufficient), PVM reacquires its read gate and checks the same original
GSO, exact region and fields, unchanged immutable view snapshot, original private
provider View identities/incarnations and original guest access. It then makes
ONE existing alias write attempt. Prewrite dirty notification is not repeated.
A false/thrown/identity-drifted attempt is recorded unknown and is never retried;
only the actual successful write calls the existing post-byte notification.
No-byte cancellation records an empty successful envelope, not guessed physical
write history. This does not serialize native/JIT/raw writers or confer an
exclusive virtual permission lease; participating provider changes invalidate
the original identity and veto success rather than rebase it.

Thirteen prepared Host.Memory controls use the real ledger/reservations plus
actual pinned-array SharedViews bytes: first rejection/no bytes, peer cancel,
bounded timeout, same-address remap, backing close, provider gate refusal,
thread affinity/replay, whole-backing exclusion, no-byte history, unresolved
completion, incomplete coverage, raw-provider revocation despite unchanged
outer snapshot, readonly/invalid destinations and post-attempt identity drift.
They do not instantiate PVM or fabricate native outcomes. Actual PVM/macOS
forwarding and the APR branch require separately authorized runtime controls.
