# Original watched volume construction

Source checkpoint only. No build, test, native execution or game result is claimed here.

The actual Octopath 8aca failure was Compute36, dcb.graphics/submission2,
volume8030FB0000/65536: the original physical watch had ordinary admission2
after private upload, before TransferSingletonVolumeOriginalWatchLocked.
The constructor had no exclusion spanning upload and its exact watch transfer.

This change adds a separate optional cancel-only `CpuMappedReadValidation`.
It requires the SAME opaque original watched copy, exact source runs covered by
that watch, original enrollment revision, Read entitlement, current original
views, bounded reservation capacity and actual ordinary-writer exclusion.
Its alias pin and full original virtual-footprint reservation remain owned until
Dispose. It neither copies bytes nor publishes a write, increments a revision,
captures a token, creates an observation or grants native/JIT write authority.
The existing disjoint-publication validation remains strict and separate.

Metal acquires this receipt outside all gates for private State0 watched volume
construction only. Existing registry owners retain the existing live lookup
checks. Private allocation/upload and under-gate ownership transfer use the exact
local receipt. Original memory/session/protected token and read generation remain
mandatory; the token and mapping are never refreshed. The constructor releases
the receipt once, then remains synchronous outside all gates until the SAME
ordinary watch is Current. Busy only permits bounded waiting (500ms), before any
shader encoding. Stale/unknown/timeout/closed/session/replacement refuses and
marks the uncertain producer; it never reuploads or retries an encoded dispatch.
The later cohort and dispatch checks remain ordinary-current checks. This is not
a lease covering arbitrary concurrent native stores through GPU completion.

The real exclusion can temporarily reject ordinary guest writes. Runnable
integration must therefore preserve the separately prepared ordinary-write
admission wait (301/794) alongside this receipt, and preserve the 834 original
watch EnrollmentRevision and 096 enrollment diagnostic fields. The 787/2e dirty
retry is a separate post-canonical-publication path; no constructor failure is
converted into a successful dirty acknowledgement.

Prepared genuine managed rows:

* Host.Memory `MappedCopyReadWatchTests.ReadValidation`: 9 (2 exact Read/RW,
  1 other-watch identity, 6 busy/stale/ABA/unknown/foreign refusals). Actual
  ledger/exclusion over pinned managed bytes and synthetic mapping metadata;
  no kernel/native entitlement claim.
* Libs `MetalStorageCpuSyncTests.ConstructionRead`: 14 (5 device0 constructor
  refusals, 4 exact transfer/release/owner/session controls, 5 final-status/content/
  owner-window Busy versus changed-token/replacement/session controls). Opaque synthetic
  ticket and protected owner metadata; fake123 texture is never dereferenced or
  released by MetalNative. No actual provider/native completion claim.

The final two race windows continue the SAME retained constructor when a new
ordinary Busy is observed after the first Current result or after token validation
before the final owner gate. They do not rerun allocation/upload/transfer. The
original independent token/mapping/session/health and complete native-owner
identity are checked again; a changed original still fails. A secondary provider
failure while classifying a caught content refusal preserves that first refusal.

Native qualification prerequisite (separate source fixture/approval): use the
existing public original singleton+linear-volume Submit/Drain fixture with a
real ordinary Mac/PVM/Tracked original watched copy. Observe that a SAME read
ticket is Current while the ordinary bool guard refuses PublicationBit; retain
that ticket through real private native upload and original-watch transfer.
After ticket release, pause a real disjoint writer owning its envelope, chunk
and alias lifetime without gates. Check unchanged native/owner/token/mapping,
no dispatch/EOP acknowledgement while construction waits, then release the
writer and verify actual dispatch status4 plus all native/canonical bytes and
ordered EOP. A paired overlapping writer must invalidate the ORIGINAL watch
and prevent dispatch/label acknowledgement. Preserve unknown writer/native
completion owners through quarantine; no timeout retry, prearm, recapture,
disposed-ticket borrow or fake counter override.
