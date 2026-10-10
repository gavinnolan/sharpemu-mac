# Original read-watch busy retry: source checkpoint

Base: `73e0ee8d2dfd29dbdb729444d8f06e1bfac9a104`. No build, tests,
native worker, publication, or game run has executed for this checkpoint.

The actual integrated Silent Hill refusal reported primary admission 2 with
released/revoked/coverage flags zero, the original volume owner/version matching,
and no unknown producer. That is a transient refusal observation, not proof of
current content. The existing boolean currentness remains unchanged.

The optional provider status is Stale by default. Current uses the existing
predicate. Busy requires an otherwise pristine original read watch and a
positive ordinary admission below PublicationBit; mapping currentness is checked
before and after classification. Publication, unknown coverage, revoked or
released watches, mapping replacement and unsupported status are Stale. An
active ordinary operation envelope may be Busy, but grants no permission to read,
write, borrow a native resource, or acknowledge guest work.

Only canonical CPU-dirty acknowledgment classifies Busy. All ordinary native
cohort consumers remain Current-only. A retry retains exact image/debt/origin,
native handle/version, memory/session and original protected token. It neither
creates a new origin nor retains a disposed publication validation. After a
cancel-only validation is successfully released, Busy restores dirty at the
same protected owner, leaves canonical debt healthy and installs one immutable
retry receipt. Other cleanup errors preserve existing failure behavior.

Before retry effects, the receipt must still be the registered image/debt/origin
with its original native version and memory/session. A replacement at the same
address is refused before provider preparation or token consumption. The receipt
allows its own retry while later work is pending, without permitting a first
acknowledgment in an already-busy work queue. Taken/executing work and open native
batches remain refusals. Acknowledged cannot return with the old receipt live.

Busy returns false from the complete drain before future Take/encoding/flush or
completion advancement. RenderFrame returns before materialization, drawable
acquisition, encoding and presentation. TryTake also refuses a retained retry.
The next frame retries the same receipt and requires all original provider,
protected token, mapping, native owner, physical disjointness and session checks.
No status refresh or new observation replaces the original.

Run drops retry bookkeeping only after existing native-depth, volume-watch and
selected-owner retirement attempts succeed, on the closed actual render owner
and after the selected session advances. Active work/batch, incomplete depth
batches, unhealthy canonical debt or sticky cleanup failure retain the receipt.
This releases no native image and advances no completion; existing owner and
quarantine records remain. The graph-disabled presenter has no general production
reopen/reset API, and this change introduces none.

Prepared controls, 16 rows, all unexecuted:

- Host `MappedCopyReadWatchTests.Status.cs`: two ordinary busy/end cases; five
  overlap/failure/release/ABA/publication negatives; one unsupported-status case.
  These use pinned managed bytes and the real ledger model, not Mach permissions
  or guest/native writer certification.
- Libs `MetalCanonicalDirtyRetryTests`: four actual queue/drain refusal-boundary
  controls and two exact retry replacement/version negatives with zero pending
  work. The default-null refusal hook supplies no content, native handle,
  provider reservation or positive publication authority. The frame control
  verifies the explicit caller result boundary, not a real window/native frame.
- Two cleanup-bookkeeping controls use actual closed/shutdown and selected-session
  retirement methods with asserted-empty native inventories. A clean retirement
  drops only the retry; a cleanup failure retains it. Both preserve old labels
  and completed sequence. The explicit new managed scheduling lifetime checks
  old retry metadata cannot block/adopt a new session. Its synthetic canonical
  debt has no provider/content/native authority and proves no native teardown.

Required next native phase (not implemented or executed by this checkpoint):
create a real PVM/Tracked original watched volume and qualified canonical image;
complete actual native publication/status4; hold a participating ordinary writer
at a bounded test-only post-validation-release boundary. Assert restored dirty,
same receipt, healthy debt, unchanged queued label/completed sequence and no frame
encoding. End the disjoint writer, retry through production Drain and verify
full native/canonical bytes and ordered label. Separate overlap, failed envelope,
mapping ABA, session reset and owner-version cases must refuse with no label.
The pause seam itself and cleanup/unknown-outcome quarantine require source review
before any build/native grant. These native assertions are not inferred from the
16 managed/model rows.

Raw/native/JIT writers outside the participating ordinary provider protocol
remain unqualified. This checkpoint does not change that policy or the separate
Superliminal native-depth global-mapping-epoch refusal.
