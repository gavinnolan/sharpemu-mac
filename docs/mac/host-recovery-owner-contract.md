# Host recovery owner contract scaffold

Base `131f674`, updated 2026-10-08. `HLE.Host.HostRecoveryOwner` is an unused
neutral contract scaffold. No provider, tracker or callback calls it. Native
fault acceptance, including Arming/Invalidating behavior, is unchanged.

## Current mutation graph

| Caller/owner | Paths reaching native mutation | Existing serialization and gap |
| --- | --- | --- |
| Core `PhysicalVirtualMemory` | Injected `IHostMemory` allocate/reserve/commit/free/protect/raw-protect; default `CrossPlatformHostMemory` forwards to legacy HLE `HostMemory`; `GuestSpaceOwner` backed-view path | Ordinary region locks and post-kernel mapping generation. Fault-entered `EnsureRangeCommitted`, transient access and instruction/write protection can recurse into these paths. |
| HLE `HostMemory` | Win32 VirtualAlloc/Free/Protect; POSIX mmap, commit mprotect, munmap, protection changes and relocated-map cleanup | POSIX `Gate`/shadow region table; neither excludes native tracker work. |
| Per-OS `IHostMemory` implementations | Mac/Linux allocate/reserve/commit/free/protect/raw-protect, exact placement and cleanup; Windows VirtualAlloc/Free/Protect | Provider-local bookkeeping. Same pages may also be owned by views or accessed through legacy calls. |
| `GuestSpaceOwner` | Reserve, `MapShared`/`UnmapShared`, `AllocatePrivate`/`FreePrivate`, `SetAccess`/`SetTransientAccess`, release/dispose | `MappingLockScope` takes mapping lock and all striped protection locks; transient access takes `ProtectionRangeScope`. Never drain while holding either scope. |
| `SharedBackingViews` | TryMapReservedRange, whole/partial Unmap, rollback `RestoreViewMapping`/`RestoreMappingsInReverseOrder`, Dispose | View lock, published alias snapshots and active-copy counters; these counters are not native tracker restoration pins. |
| Per-OS `IHostViewMemory` implementations | ReserveHole, Split/Join/FreeHole, TryMapView, UnmapView, Commit/ReleasePrivate, ChangeAccess, FreeOwnedRange | Mac/Linux MAP_FIXED replacement and `RestoreHole`; Windows placeholder/section replacement and rollback. Backing Gate serializes mapping with explicit Dispose only. |
| `HostBackingObject.Dispose` and OS release callbacks | Mac/Linux alias munmap and close descriptor; Windows UnmapViewOfFile/CloseHandle | Gate plus disposed flag; a retained CLR reference cannot prevent this explicit release. |
| `GuestGpuMemory`/`PageGuard` | Base-protection changes and derived watch protection through guest-memory hook into PVM | Guest-page ledger and recovery paths need actual host-page coverage and recursion analysis. |
| `GuestImageWriteTracker` | Track replacement, Arm/Rearm, Disarm/Untrack/UntrackProtected, Add/RemovePageWatchers, rollback, `TrySetProtection` direct libc mprotect | Tracker Gate, page CAS and snapshot state. Direct native mutations bypass host mapping generation/provider gates. |
| CPU native packages and shared Core recovery | Stub/trampoline allocate/protect/free; lazy-commit, patch and GPU recovery | HostMemory or IHostMemory plus raw/shared recovery callbacks. Stub allocations need mapping ownership; handler warming must happen before installation. |

Concrete source surfaces: [HostMemory](../../src/SharpEmu.HLE/HostMemory.cs),
[PhysicalVirtualMemory](../../src/SharpEmu.Core/Memory/PhysicalVirtualMemory.cs),
[GuestSpaceOwner](../../src/SharpEmu.HLE/GuestMemory/GuestSpaceOwner.cs),
[SharedBackingViews](../../src/SharpEmu.HLE/GuestMemory/SharedBackingViews.cs),
[HostBackingObject](../../src/SharpEmu.HLE/Host/HostBackingObject.cs),
[MacHostViews](../../src/platforms/macos/SharpEmu.Host.MacOS/Memory/MacHostViews.cs),
[tracker](../../src/SharpEmu.HLE/GuestImageWriteTracker.cs). Linux and Windows
providers expose the same neutral interfaces but require separate native audits.
This inventory identifies integration obligations; it is not a claim that any
surface is already protected by the scaffold.

## Narrow seam implemented

Safe-side construction preallocates a bounded pin-slot array, validates a
nonwrapping range of complete native pages and fixes its arm epoch. A domain has
a monotonic distinct identity, an immutable retained owner reference, and one
atomic word containing closed state and active pin count. Domains never reopen;
rearm/remap requires a new identity and safe publication after the old drain.

The caller supplies a normalized address, write classification, positive entry
invocation and expected arm epoch. The scalar method checks these values and
whole-native-page coverage, reserves one slot by generation/state CAS and makes
one count CAS. It cannot establish that the supplied scalars describe the real
frame, that the mapping is current, or that backing still exists. No syscall,
mapping query, TLS, logging, allocation, event wake or waiting enters this path.
One slot is attempted; contention declines even if another slot is free.

The count CAS linearizes against `Close` on that same word. A preempted reserved
but uncounted slot cannot authorize native work: close may drain, and its later
count CAS must lose. A counted pin blocks drain even if active-token publication
is preempted. Exact generation/state release CAS prevents copied or stale pins
from decrementing a later operation. Slot reuse occurs after the count decrement;
no native/backing access follows decrement. Slot counter wrapping stays bounded,
and exhausted slot generations decline rather than wrapping.

`Close` rejects new acquisitions before native work. `TryDrain` is an explicitly
ordinary-thread bounded sleep/poll operation. The safe caller must release every
gate needed by recovery before draining and must not drain while holding its own
pin. Timeout keeps the domain closed and the owner reference retained; it never
authorizes mutation or disposal. The scaffold intentionally has no disposal or
native mutation delegate. Provider authority and explicit backing-disposal
exclusion remain unimplemented.

## Controls and limits

The final selection passed **17/17 on x64/Rosetta and 17/17 on native ARM64**,
with zero declared skips. This is the same managed selection on each process
architecture. The 4 KiB/16 KiB cases validate scalar coverage, not kernel pages.
No native protected-store, callback, device or game test was run. An initial
mixed build compiled the phase tests against an older HLE assembly and failed
two reflection lookups; that result is preserved and excluded from acceptance.
Both final runs used freshly rebuilt unchanged production/test source.

Ordinary-thread tests cover deterministic preemption on each side of the count
CAS, gate release before drain, timeout/closure retention, copied/stale/foreign
pins, same-address replacement identity, contention/close races, 4 KiB and 16 KiB
coverage, wrong access/frame scalars, bounded cursor wrap/generation exhaustion,
and warmed success plus decline zero-allocation measurement. Private scalar
phases are invoked through reflection in deterministic fixtures; no scheduling
callbacks or new friend assemblies are added to production.

These are managed interleaving controls. Neither native callback safety nor a
mapped lifetime is qualified by them. The signal-facing path must be explicitly
warmed before handler installation when it eventually gains real callers. Full
provider integration, real protected-page retirement/remap/rearm controls and
native callback workers remain required. Retry and kernel permission receipts
must stay disabled until every mutation participates. See the
[reservation design](write-fault-reservation-design.md) and
[test-only restoration ledger](page-restoration-bookkeeping-prototype.md).

## Separate shutdown finding

Parent's read-only finding: Core Imports.cs checks
`_forcedGuestExit && !ActiveForcedGuestExit`, while the `ActiveForcedGuestExit`
getter includes `_forcedGuestExit`. That redirect condition is unreachable and
may explain teardown/import-loop labeling. Parent owns its separate shutdown
investigation; this owner scaffold changes neither shutdown nor that condition.
