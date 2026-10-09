# Native fixture attachment successor

This source-only test successor starts at frozen
`b5fe2c0b5b6db3d8795277fe35b82e366403f28d`. Its failed native qualification
archive remains immutable. No compiler, tests, native worker or game were run
for this successor, and no runtime production source or authority guard changed.

The common finite storage native fixture previously wrote a `TrackedCpuMemory`
directly into private presenter `_guestMemory`. Production preparation selects
the tracker's explicit GPU reader before constructing host context or snapshot
originals. Consequently the cold callback's strict identity veto compared a
raw tracker against the selected `ColdNativeMappedSource` (or physical/routed
provider). That setup bypass is sufficient to make identity false. It does not
establish an environment-flag cause or justify weakening the production guard.

Setup now calls the actual `AttachGuestMemory(memory)` API. Assertions retain
the CPU tracker as owner and check the exact selected source at resolution,
presenter attachment, interpreter host memory and original receipt Memory/Source.
No receipt is rewritten or recaptured. All native row identities, original
mapping/watch ownership, full-byte payloads, status4, once-only handling, EOP
ordering and cleanup/quarantine remain unchanged; the only native fixture
change is attachment plus these additional identity assertions.

The audit found no other tracked setup bypass among native immutable-version
fixtures. `Consumer.ExecuteConsumer` installs opaque `FakeCpuMemory` solely for
buffer writeback; `Color` installs opaque `ColorWritebackMemory` as an ordering
sink. `Drain.NoGuestReadScope` deliberately installs `NeverReadMemory` to reject
CPU source reads. Those identities remain opaque, and those scopes are unchanged.
The remaining raw `_guestMemory` writes in storage, color, consumer, color-borrow
and depth fixtures restore the exact prior object and must not normalize it.

One new ordinary Libs row,
`MetalStorageCpuSyncTests.ProductionAttachmentAndPreparationContextKeepSameOpaqueColdOwnerIdentity`,
uses actual nested trackers, an opaque reader, the production host constructor,
production attachment and the existing cold owner veto. The prior direct-field
bypass refuses; correct attachment accepts the same selected context identity;
passing the tracker itself and replacing the opaque root still refuse. It
asserts zero source/inner reads and no backend worker. This is identity metadata
coverage, not a mapping/copy/watch/native publication result, and requires no
cached-flag activation.

Expected ordinary inventory grows by one from 1244 (279 Host / 860 Libs / 105 GPU)
to 1245 (279 / 861 / 105). The inherited 238 native/cold workers and eight fresh
GPU-original metadata activation workers keep every identity and oracle; 246
separate workers remain planned. Fresh complete qualification is required.
