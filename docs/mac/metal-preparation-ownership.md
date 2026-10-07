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
