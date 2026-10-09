# Native fixture selected buffer reader

Source-only successor of `d85fbaa639a581284485f882ad5b990e81278d03`.
The failed d85 native batch remains immutable and is not qualification for this change.

The common storage-publication fixture attached its tracked CPU writer through
the production attachment API but still captured and registered writable buffers
through that raw tracker. Production `MetalCommandStreamHost.Rendering` uses its
already selected `Memory` for both operations. The strict origin guard compares
the captured reader with the presenter's selected reader; a real mapping receipt
does not make the two different object identities interchangeable.

`CaptureBuffer` now uses `host.Memory` for the first copy and origin registration,
asserts the chosen `cpuSource` before capture, and checks exact saved Memory,
Source and Mapping before queue admission. The CPU writer remains tracked.
Production registration, lower identity guards, bytes, native oracles, case
identities, owner retention and cleanup are unchanged. No saved receipt is
normalized after acquisition.

Manual buffer fixture audit: `BufferAppliedContinuation` uses its actual
`DisplayLedgerMemory` directly for both capture and registration;
`MetalCanonicalPublicationRoutingTests` uses its opaque mapped metadata provider
directly. Neither is a tracked-view bypass. `StoragePhaseBusy` validates an
already captured origin rather than acquiring another buffer.

One new ordinary row,
`MetalStorageCpuSyncTests.BufferOriginalRequiresTheHostSelectedReaderAndNeverRecapturesOnRefusal`,
uses genuine pinned managed ledger copies through real nested trackers. It
checks raw-tracker origin refusal, first host-selected copy/origin acceptance,
different-root refusal and unchanged receipts with no recapture. It creates no
native handle, worker, command buffer, read watch or publication transaction.

Expected ordinary inventory: 1,246 (279 Host, 862 Libs, 105 GPU). The inherited
246 separate workers (238 native/cold plus 8 GPU-original metadata activation)
retain their identities and oracles. No build, discovery, test, native worker,
publication or game has run for this successor; qualification remains pending.
