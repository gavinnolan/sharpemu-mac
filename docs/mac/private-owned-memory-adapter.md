# Dormant macOS owned-operation control

`MacHostMemory.OwnedOperations.cs` adds a private control factory for the canonical
process provider. Startup, CLI, Core, image tracking, PageGuard, HostViews and the
shared mapping registry do not call it. No public provider contract or friend
assembly was added. This is not fault recovery, a permission lease or an enabled
production ownership registry.

The factory owns only kernel-selected anonymous allocations of 1–64 complete
actual native pages (4KiB Rosetta, 16KiB native ARM controls). Supported raw
protection is NoAccess, ReadOnly or ReadWrite. Darwin reserve intent remains an
actually backed mapping whose logical query state is MEM_COMMIT. Executable
allocations, aliases, fixed mappings, files, backing retirement and injected
noncanonical providers are excluded.

An exact immutable allocation cookie is retained independently of the logical
Region table. Every legacy desired-address allocation, commit, protect and free
checks its complete native-page effect envelope against cookies before native
work. Interior, cross-range and padding effects are covered. While a retained
cookie contains released or unknown pages, all new anonymous placements are
declined before mmap: a null address or relocatable hint could otherwise select
its hole. This conservative restriction affects only explicitly created control
allocations. Normal composition never creates one. Unknown cookies also veto a
logical query even when setup failed before Region insertion.

Preparation allocates result slots and a replacement domain, reserves one exact
operation, then closes the previous domain under the provider gate. Drain occurs
outside that gate; timeout retains a closed pending owner and invokes no syscall.
Execute revalidates canonical provider, cookie, Region reference, exact pending
operation and drained closed domain, then runs once. The provider captures each
full-page mprotect/munmap return and immediate errno; Mach maximum restriction
records kern_return_t separately. No caller records or supplies native success.
Commit on Darwin is protection work, not physical backing allocation.

Successful protection work publishes its preallocated new mapping-retention
domain only after all requested pages succeed. Failed, partial, unknown or
post-syscall bookkeeping failure stays quarantined; confirmed page results remain
inspectable. A domain never authorizes writable recovery. A fixture-only scalar
exception seam can test bookkeeping failure after a real recorded syscall; it
is not an allocation failure or a signal callback.

Page release records removed pages before metadata work. Partial release leaves
the whole allocation quarantined and never publishes a domain across a hole.
Cleanup can release only positively owned surviving pages. Unknown release
receipts decline further native mutation of the uncertain page. No finalizer or
address-only whole-extent cleanup can unmap an acquired hole. A bookkeeping
failure after the last confirmed native release still keeps the cookie
quarantined rather than certifying operation completion; the isolated worker can
exit without releasing any newly acquired address. No automatic repair/retry is
provided.

The managed contract selection tests native geometry, overflow envelopes,
interior/cross-range cookie exclusion, hole admission and unknown unregistered
logical query using explicitly phantom metadata. It does not initialize the
process authority, allocate/protect/free host pages, issue Mach operations, or
establish physical ownership. Actual native page, close/drain interleaving,
permission-error, partial-unmap and bookkeeping-failure controls require a
separate bounded worker grant. Native ARM ordinary memory controls would not
qualify ARM guest CPU execution. Callback, alias, view and tracker participation
remain prerequisites before any recovery acceptance change.

## Source-only native worker qualification plan

`MacOwnedNativeOperationTests` is a separately selected, explicit opt-in suite
(`SHARPEMU_TEST_REQUIRE_OWNED_NATIVE=1`). Each case starts a fresh process with
three full native pages and a 45-second harness deadline. The worker does not
install signal handlers, execute guest code or initialize the shared registry.
It exercises real provider-owned allocation, reserve/commit, RO/RW transitions,
canonical raw/neutral/legacy exclusions, held-pin close/drain timeout, and
per-page release. Mach maximum-protection reduction goes through the same
owned close/drain operation before producing real first/middle/last mprotect
failures. Kernel protection readback is separate from logical provider query.

Partial release cleanup invokes only positively retained surviving pages; it
never calls full-extent unmap over a released hole. The reflection-only
bookkeeping failure control fires after a recorded real syscall result and
checks quarantine, without forging native receipts. These cases do not force
an mmap allocation failure, native munmap failure, setup rollback failure or
unknown native-call outcome. They cannot establish alias/view participation,
trap ordering, write-permission authorization or callback recovery safety.

The initial fixture checkpoint is build-only. Native outcomes remain
unqualified until the frozen source is reviewed and separately executed on
the 4KiB Rosetta and 16KiB native ARM CLRs. The dormant factory and production
retry/fault acceptance remain unchanged.
