# Original watched-copy enrollment revision

Source-only successor to `52172493321e9857469138c042699d3d683e0ae8`.

The actual Octopath original watched capture retained `WriterRevision=-1`: the early ordinary revision sample encountered a participating writer, but the writer completed before exclusive watch enrollment. The later full owned copy and original watch succeeded. Publication correctly refused the negative revision; the capture had omitted its original enrollment baseline.

Each watch now stores an immutable `EnrollmentRevision` read while its writer registry owns the exact `PublicationBit`, before installation and before any copied bytes. After the complete copy, original mapping validation, and original watch validation, a watched capture adopts that pre-copy value. There is no post-copy writer revision sample for watched captures. Writes admitted after enrollment retain existing before-byte overlap invalidation; unknown completion revokes watches, including disjoint ones. Ordinary watch currentness still requires no admitted writer. No younger watch, mapping lookup, content token, or session is substituted.

Unwatched capture retains its original two-sample rule and negative-revision/history-window refusals. The private unpublished watch created during publication records its own enrollment point, but the successful capture still receives only `RecordPublicationWrite`'s exact Applied commit revision. Cancellation exposes no private successor.

The private, default-null `afterWriterRevisionForControl` ordinary-copy seam runs after the first sample and before enrollment/bytes, outside the provider gate. It is not a signal callback or public capability. The existing after-enrollment control remains at its original location.

Ten new prepared cases in `MappedCopyReadWatchTests.EnrollmentRevision.cs` use the real ledger/writer/watch protocol and pinned managed backing: busy first sample with actual writer completion before enrollment; immutable baseline despite a later disjoint completion; unchanged unwatched refusal; three overlap/unknown controls; still-admitted enrollment denial without copying; younger-watch/history nonrepair; separate Applied revision; early callback failure with balanced ownership. Existing watched-history eviction, unwatched history limit, cancellation, saturation, unknown and own-publication controls remain relevant.

No build, managed test, native worker, publication or game replay has run for this source checkpoint. These prepared controls make no kernel receipt, raw/JIT writer completion, signal continuation, game-entry or gameplay claim.
