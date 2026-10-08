# Canonical short publication: source checkpoint

This checkpoint is unbuilt and unexecuted. It implements an ordinary-thread
canonical alias transaction; it does not establish native guest writer coverage,
GPU completion, signal recovery, base permission ownership or current content.
Metal's separate native owner, original content-token and completion checks remain
necessary. No Metal feature or fallback is activated by this provider slice.

The actual Mac provider resolves only its original opaque mapped-copy observation.
The complete source mapping footprint remains excluded from participating native
mutations while a ticket is held. The same provider gate checks this exclusion
before revocation, kernel effects or preparation of a replacement view. Original
view incarnations and guest write entitlement remain necessary. Alias lifetime
is pinned across preparation, caller checks, byte copies and the terminal result.
PVM and TrackedCpuMemory explicitly forward without taking outer gates or making
a new copy/observation.

Preparation detaches at most eight runs and eight bytes in total before admission;
each run must fit its original copied source and one recorded writable view.
Overlapping physical runs reject. Only those exact bytes change; no padded image
seed is published. There are at most 128 retained tickets. Commit and cancellation
consume an exact ticket once. Bytes run without provider or PVM gates. An unknown
outcome after touching bytes retains the ticket, original mapping exclusion,
participating-writer exclusion and alias pin until process exit. Dispose never
turns a committing or uncertain operation into a retry.

SharedBackingViews enrolls before publishing a Mac publication-capable wrapper.
Clear, TryWriteBacking and TryCopyBacking now retain a bounded operation admission
across all chunks, staged source reads and memmove, alongside their original exact
destination intervals. Destination bookkeeping counters continue counting chunks,
not operation envelopes. Disabled legacy wrappers remain unenrolled. Saturation
or identity overflow keeps legacy ordinary writes available but permanently loses
publication coverage. A held publication rejects participating writes before
bytes. A late losing writer can increment/decrement the admission word, but never
accesses the alias; no transient-count revalidation is used after reservation.

An original copy retains a private writer revision only if participating writers
were quiescent at both boundaries and the revision stayed unchanged. Mapping-only
IsCurrent is unchanged. A completed participating alias write since that original
copy vetoes publication even if the texture tracker did not see it. Publication
increments that revision. The first-use checkpoint rejects a second publication using the old observation.
Its separate continuity successor preallocates a provider-owned observation from
those SAME original view identities. Only an exact Applied ticket exposes it,
with the revision returned by its own write. There is no fresh lookup, writer
recapture, content-token update or restored mapping identity. A subsequent external
participating write still vetoes prepare; cancelled/rejected/unknown tickets expose
no observation. Any Metal origin/content-token transition remains a separately
reviewed caller responsibility, with missing raw/native writers still uncertified.

Raw cached AliasBase/Descriptor access, native/JIT guest stores, tracker direct
mprotect, external syscalls and unsupported providers remain outside this writer
protocol. They cannot be inferred safe from writer quiescence or mapping currentness.
The optional whole-view retirement branch must merge a pre-close overlap guard and
retain its pending-state exclusion in publication preparation; it is not imported
into this checkpoint. No waits or callbacks are introduced under provider/PVM gates.

Prepared managed controls exercise the real neutral transaction and alias bytes
over privately pinned arrays, including detached 1/4/8-byte scatter and every
neighbor, ordinary-writer history, held reservation refusals, cancellation/replay,
concurrent exact commit, old same-address incarnation, foreign owner and bounds.
Their provider is explicitly synthetic and records no native receipt. Four fresh-process Mac workers are now source-prepared for actual owned dual
aliases, detached scatter/full neighbors, exact own continuity versus a completed
alias write, reservation refusal/cancel and same-address ABA. Each uses two whole
32KiB Mach-owned placeholder extents and a small canonical backing. They explicitly
assert process architecture and print measured kernel/provider page sizes. Normal
cleanup consumes whole extents once; uncertain cleanup retains them until the fresh
process exits. Real workers and native staging integration need separate reviewed
source and execution grants. No test or game advancement claim follows from this source checkpoint.

The first-use a57 checkpoint prepared 12 managed rows. The 395 continuity successor
prepared 15. The following source-only worker/control successor prepares 18 managed
rows and four opt-in fresh-process native rows. No row has executed. Managed selection:
`FullyQualifiedName~MappedPublicationTests|FullyQualifiedName~ManagedWriteIntervalTests`.
Native selection: `FullyQualifiedName~MacMappedPublicationNativeTests`, separately
requires `SHARPEMU_TEST_REQUIRE_MAPPED_PUBLICATION_NATIVE=1` and exact
`SHARPEMU_TEST_EXPECT_ARCH=Arm64` or `X64`. Normal suites declare the native theory
skipped; that must not be counted as native qualification. Zero-byte operations keep
original validation and no-op behavior even while publication is held.
