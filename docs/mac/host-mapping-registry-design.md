# Mapping registry and provider integration boundary

Base `ef49e43`, updated 2026-10-08. This increment adds an unused neutral
safe-side registry contract and coherently renames the scaffold's `ArmEpoch` to
`DomainEpoch`. Mapping lifetime is independent of overlapping tracker arm/page
epochs. No native provider, callback or recovery path uses the registry. No
kernel permission receipt or mapping authority is claimed by its metadata.

## Registry behavior

A registered native-page-aligned segment has its own immutable mapping-domain
identity, backing identity by reference, backing size/offset and neutral
protection. Aliases share the canonical backing object, but retain distinct
mapping domains. Registration rejects overlap, wrapping/misalignment, invalid
offset/size/protection and inconsistent sizes for the same backing identity.
All registry methods are ordinary-thread operations; `TryFind` is never a
signal-safe replacement for native or shadow Query.

One process-wide mutation is modeled at a time, conservatively including
address holes. Preparation captures caller replacement specs and allocates all
split/replacement domains before closing anything. New records are private to
the mutation until successful completion. A partial-range change closes the
whole original domain; unchanged prefix/suffix segments get distinct identities
and correct inherited backing offsets on successful publication. Unrelated
mapping/alias identities remain unchanged. Backing retirement closes every
registered alias with the same canonical reference and records every alias scope.

The transaction progresses through:

```text
Prepared (affected domains closed) -> Draining -> Ready
Ready -> KernelInFlight -> Recorded -> Committed or Quarantined
```

Drain drops the registry gate before waiting. Caller integration must also have
dropped every provider, backing, mapping and recovery gate. Timeout returns to
Prepared with identities still closed; it does not authorize a kernel operation.
A caller may abandon before starting kernel work, leaving closed records
quarantined. It cannot abandon or release the global reservation while a kernel
operation is in flight or its result awaits completion.

An immutable operation object binds one recorded outcome to the exact requested
native range list. A scalar ID from another registry is insufficient. Wrong
scopes, incomplete alias scopes, copied results and success with nonzero error
decline without publication. Only `AppliedWholeScope` with zero error publishes
the preallocated plan. Even `FailedUnchanged` quarantines the old domains; no
failure path reopens or republishes an inferred live identity. Partial/unknown
results likewise preserve closed coverage. Repair requires a new operation
covering the entire quarantined domain rather than exposing an unverified prefix.

These are bookkeeping invariants. The caller can report incorrect native
results or backing identities, and a retained CLR object cannot prevent explicit
native disposal. The registry therefore grants no restoration/retry permission.
Its globally serialized model is intentionally conservative; range concurrency
and performance are later work after ownership correctness is demonstrated.

## Canonical identity before provider hooks

The bounded [host-memory routing migration](host-memory-authority-design.md)
replaces the independent HLE anonymous-memory table with the exact provider used
by the process-wide IHostPlatform.Memory. The provider is selected at safe
startup independently of x64 guest execution and cannot be replaced. This closes
one alternate routing seam, but it does not register mapping-domain identities
or hook any registry transaction. View metadata still lives in HostViewRegions
and each SharedBackingViews instance; backing release uses HostBackingObject.Gate.

Exact raw Windows flags/state/protection/query fields remain forwarded to the OS.
POSIX 4KiB logical allocation/protection bookkeeping is preserved, including in
native ARM64 tests with 16KiB kernel pages. Actual physical extent, reserved/committed
representation and raw modifiers must still be reconciled before native domains
can use these records. Native control fixtures operate on full kernel-page sizes;
they do not certify sub-page lifetime or aliases. The registry spec does not yet
represent all raw/state details.

Anonymous allocations need one backing identity per actual allocation. Views
must use the owning `HostBackingObject` identity and exact backing offset; its
native alias window must also be registered. Backing disposal must revoke and
drain every alias/mapping before releasing native storage. A provider-selected
object reference or file descriptor number alone is not proof that the registry
captured every alias or that a descriptor has not been reused.

Unknown-address no-clobber allocations have no prior mapping to pin, but need
safe publication and exact cleanup if metadata setup fails. Fixed replacement
must reserve its complete scope before the syscall. Native protection receipts
must cover actual provider pages, including 4 KiB x64/Rosetta and 16 KiB ARM64,
not just tracker guest-page units. Partial multi-call outcomes need per-page
kernel-success accounting; the aggregate registry refuses them in this stage.

## Explicit unintegrated provider paths

| Surface | Required next integration |
| --- | --- |
| `HostMemory` facade and canonical anonymous provider | Common routing is implemented; commit/native cleanup/free/protect still need expected identity, pre-kernel close/drain and exact native outcomes. No registry hooks are active. |
| `MacHostMemory.Darwin` and `LinuxHostMemory` allocate/commit/free/protect/raw-protect | Capture canonical expected allocation under ordinary gate, release gate, revoke/drain, reacquire and verify exact identity before syscall. Avoid draining inside current provider Gate. |
| `WindowsHostMemory` VirtualAlloc/Free/Protect | Same canonical identity plus exact reserved/committed and raw modifier behavior. |
| Mac/Linux `HostViews` fixed maps and `RestoreHole`, private commit/release, ChangeAccess, FreeOwnedRange | Register holes, views and alias windows; include native rollback/cleanup calls. Do not drain under backing Gate. |
| Windows placeholder split/join/map/unmap/private/free and rollback | Reserve complete replacement scopes before placeholder/section mutation; certify all outcomes. |
| `GuestSpaceOwner` / `SharedBackingViews` | Drop MappingLockScope/ProtectionRangeScope and view/backing gates before drain; revalidate logical ownership after reacquisition. Existing active-copy counters do not cover native fault restoration. |
| PVM lazy commit, transient read/write/instruction protections | Audit signal-entered recursion before pin activation. Closing/draining an owner while the current signal holds its pin can deadlock; this scaffold deliberately supplies no recursive mutation shortcut. |
| `GuestImageWriteTracker` direct mprotect, arm/rearm/untrack/retirement, PageGuard/GPU protection | Join canonical mapping pin lifetime and transactional per-page restoration before changing any fault acceptance. Dirty state/epochs/shadow writable metadata do not authorize retry. |
| CPU stub/trampoline HostMemory calls | Register exact code allocation/protection/free identities with the same service. |

No row is hooked to the registry by either scaffold or routing migration. See the
[owner-contract mutation graph](host-recovery-owner-contract.md),
[reservation design](write-fault-reservation-design.md), and
[test-only per-page ledger](page-restoration-bookkeeping-prototype.md).

## Managed controls

The final combined selection passed **30/30 on x64/Rosetta and 30/30 on native
ARM64**, with zero declared skips. It comprises 17 renamed owner controls and
13 registry controls. Source/test code was unchanged between both green builds.
These counts qualify managed bookkeeping only, not any native provider.

The focused selection combines the renamed owner controls with registry controls
for split backing offsets, alias retirement, unrelated identities, global mutation
serialization, timeout/closure, exact/one-use/foreign outcome binding, in-flight
abandonment rejection, unknown-result quarantine, full-domain repair and drain
outside the registry gate. Provider-page cases test scalar alignment only.
No native mapping/protection, callback, game or device test runs in this selection.
