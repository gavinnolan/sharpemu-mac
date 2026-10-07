# Metal image preparation ownership

An image source reservation belongs to one `MetalCommandStreamHost`
preparation. The host records every returned snapshot array before preparing
samplers, shader data, bindings, or pipelines. Scope disposal relinquishes each
preparation reservation once, including canceled and failed preparation. A
failed ownership transfer relinquishes the array's reservations immediately.

Prepared stages can serve multiple draws within their original live scope.
Binding or submitting them through another scope or host is rejected. Accepted
queued work must acquire its own reservation before the preparation releases
its ownership; a previously accepted work reservation does not authorize a new
submission through a disposed preparation.

Snapshot creation owns reservations until it returns an array. If creating a
later image or a metadata copy fails, snapshot creation must release every
earlier and temporary reservation before propagating the original error. The
frontend cannot recover reservations from an array it never receives. Failed
queue admission must likewise release only the work ownership it acquired.
Encoding is not GPU completion: queued/native ownership must continue through
actual completion or safe cancellation.

The current no-device frontend tests use synthetic admitted producer metadata
and explicit source transfers and queue retains against the real registry.
They exercise cancellation, idempotent disposal, independent submitted owners,
scope rejection, and exception unwinding. They do not qualify production
snapshot capture, queue admission rollback, native version preservation, GPU
completion, or game behavior. Those paths require the alias integration and
its separate acceptance tests.

Private registry refinement (inactive source graph): each explicitly retained work now owns a materialized immutable lease array and releases that owner once. Duplicate work release cannot decrement a different work's reference; failed multi-lease acquisition changes no counters. Captured record copies must preserve view/shape/access and live preparation ownership; undefined intent or an integer native format cannot authorize a float source. ARM64 managed46/46 passed (10 registry cases +36 host/preparation cases), log `/tmp/sharpemu-metal-source-owner-atomic-arm.log`. Admission/capture integration and native command completion remain unqualified and inactive.
