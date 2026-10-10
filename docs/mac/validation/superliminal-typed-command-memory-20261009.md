# Closed command-memory writes beside native color consumers

Source-only candidate based on `a3cada8b8b4bc81dfbf865d4e9ed7a4118cfa2fc`.
No build, managed test, device worker or game replay has run for this candidate.

The actual Superliminal first-refusal diagnostic identified an accepted
`OrderedGuestAction` named `command_stream memory operation`, sequence 2613,
behind taken/executing sampled draw 2612 on `dcb.graphics` submission 88. The
selected native image was `0x7068C80000`, 2 MiB, session 0. This identifies the
ordinary-operation construction route; the name confers no effect authority.

## Production boundary

Actual byte writes, fill/copy packets, decoded atomics and semaphores now mint a
closed operation with owned packet bytes/scalars. Existing immediate hosts keep
their immediate operation boundary and interpreter result handling. Metal keeps
the existing accepted queue position, real previous GPU completion wait, one
operation and only then the completion receipt. Self-wait and first failure are
unchanged. Public arbitrary callbacks remain unknown, including identical names.

Only a closed byte write can currently participate in the native-color sibling
inventory. With the existing native-color opt-in enabled, an optional original
mapped copy resolves the **entire** writable destination, at most 4 MiB. All
copied destination bytes are discarded. They supply no CPU/native image content
authority. The first mapping/session/queue is retained without recapture.

Inventory requires complete original physical disjointness from the selected
color span. At execution, after the real GPU wait, the same original observation
and complete detached write runs prepare the existing provider publication
transaction. It owns the backing access pin, original mapping exclusion and
participating-writer exclusion through its one commit. Replacement, revoked
rights, unavailable capabilities, intervening overlapping writer history and
unknown effects refuse. No ordinary `TryWrite` fallback follows an attached
original refusal, and no successful continuation observation is adopted.
The first uncertain commit/cancellation retains its complete original scope for
the process lifetime. Further certified original writes refuse even after a
presenter/session reset; shutdown never clears or replaces that owner. Recovery
requires a fresh process. This is conservative retained-ownership bookkeeping,
not proof that an uncertain operation completed.

Pre-write `NotifyManagedWrite` and `GuestGpuMemoryHook.MarkCpuWrite` calls remain
before bytes. Actual publication commit rechecks the original provider records;
only `Applied` permits a receipt. Optional `GuestWriteWatch` reporting follows
successful dirty-completion reporting. The protected-image tracker uses its
existing native `mprotect` path; other derived protection changes may refuse
while a publication is held. Such refusal does not authorize original refresh
or transaction retry. The tracked-destination device row must establish the
actual supported Metal behavior; no broader notification compatibility is claimed.

Other closed operation kinds remain **unknown** in color inventory until their
execution can be bound to the original effect scope. Pure flush/prefix callbacks,
EOP callbacks, arbitrary actions and unbounded/missing mappings stay strict.

## Prepared controls

23 new managed rows:

- `CommandMemoryOperationTests`: 11 rows covering owned bytes, one execution
  through success/failure, atomic/semaphore results and bounded selectors.
- `MetalStorageCpuSyncTests.NativeColorTypedMemoryInventoryUsesOnlyItsFirstCompleteOriginal`:
  10 synthetic metadata rows through public producer/write admission: disjoint,
  different-VA physical alias, tail-only two-page alias, missing mapping, foreign
  queue, original replacement/rights/session after admission, and read-only map.
  A saved callback invoked without its exact taken/executing receipt also refuses.
  They never encode GPU work or execute a scalar writer.
- `ArbitraryCallbackCannotClaimClosedMemoryEffectsFromItsDebugName`: same-name
  callback remains unknown and uninvoked.
- `RetainedUncertainOriginalWriteRefusesPublicReadmissionAfterPresenterReset`:
  a real public queued DTO supplies only a fake retained bookkeeping scope;
  shutdown/reset and a new session retain it, and new public admission refuses
  before a transaction, bytes or receipt. No unknown native outcome is fabricated.

Six new fresh-device Facts in the existing native-color fixture:

- `ActualNativeColorConsumerAllowsDisjointClosedMemoryWriteOnlyAfterRealGpuCompletion`:
  actual producer and sampled fragment consumer, status 4, all 2 MiB native pixels,
  one detached write, all 64 KiB destination/neighbors and full original chain.
- `ActualNativeColorConsumerRejectsClosedMemoryWriteThroughAnotherVirtualPhysicalAlias`:
  a real different-VA map of the selected backing refuses before consumer encoding
  or writer receipt; initial producer status 4 and original guest bytes retained.
- `ActualClosedMemoryWriteCannotAdoptReplacementMappingAfterItsCohortCheck`:
  real remap at the exact currentness-to-prepare gap refuses the old observation;
  the already completed consumer has full native pixels, the write has no receipt.
- `ActualOriginalClosedMemoryWritePreservesTrackedDestinationNotification`:
  actual armed destination before capture, successful exact transaction, changed
  tracker token/dirty state plus the same full native/guest oracles.
- `ActualClosedMemoryWriteRefusesOriginalRightsChangedAfterItsCohortCheck` and
  `ActualClosedMemoryWriteCancelsOriginalTicketOnChangedSessionWithoutBytesOrAck`:
  rights/session changes at the same execution gap preserve the original proof,
  refuse bytes/receipt and retain the completed consumer's full native oracle.

Device rows use real PVM/Tracked forwarding, actual native waits and no positive
status/counter override. Their writer joins before teardown; uncertain ownership
retains provider/native/event scopes. The positive EOP is deliberately admitted
after consumer/write drain because EOP remains an opaque callback. This does not
qualify an earlier accepted game EOP or all command-memory operation kinds.

Existing memory-order, failure, self-owner, atomic and immediate-host regressions
must also run. Source review and actual per-runtime qualification remain pending;
this candidate makes no game-entry claim.
