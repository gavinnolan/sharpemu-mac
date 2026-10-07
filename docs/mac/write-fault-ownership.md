# Guest image write-fault ownership

Updated 2026-10-07. Native ARM64 CPU execution is still a later milestone;
these results use macOS x64/Rosetta and native 4 KiB page protection.

## Shipped bounded change

The early POSIX guest-image tracker path now admits only SIGBUS/SIGSEGV
frames classified as writes by the existing x64 error-code decoder. Read,
instruction-fetch, unclassified and non-memory signals continue to the normal
fault machinery. The tracker receives the same normalized address as the later
bridge: nonzero `si_addr` wins; otherwise Darwin's mcontext address is used.
Previously the early path used raw `si_addr` without access classification.
No native callback ABI, tracker state machine or disarmed-range acceptance
policy changes are included.

`GuestImageWriteFaultTests` has Darwin frame fixtures for raw/fallback address
selection, write/read/fetch/error-code precedence, unrelated signals and missing
context. They inspect real kernel page protections and dirty flags rather than
only calling a decoder. The separate two-thread ordering fixture deliberately
asserts the current limitation: both pending address-only handlers decline after
a third writer restores and disarms their shared range.

The final focused Rosetta run passed **40/40**: 13 new fault-boundary and
ordering cases, 17 existing tracker cases and 10 existing POSIX frame cases.
The signal fixtures use fabricated native-layout frames; page protection is
queried from the kernel. This is not an actual game/signal delivery test.

## What the Poppy trace establishes

Base `b557990`, run `20261007-204307-poppy-followup-b557990`, records a sampled
texture armed at sequence 15 over `0x900D7A0000 + 0x10E000`. Two unrecovered
normalized SIGBUS writes target `0x900D86A800` and `0x900D7A0000`; a first-write
record for `0x900D7E3800` disarms that same range at sequence 18.
The first-write log is deferred ordinary managed logging, not a native-entry
timestamp. The game log does not record raw `si_addr` or `si_code`. Consequently
it supports a pending-fault race and a raw-versus-normalized address lookup miss,
but does not distinguish them. A controlled native VMOVNTDQ probe qualifies
that probe's fault frame; it cannot establish the actual game's raw fields.
See `artifacts/validation/integrated-20261007/game-followup-b557990/POPPY-TRACE.md`
and `poppy-trace-excerpts.json` for the original evidence.

## Retry prototype withheld

A preserved prototype captures an opaque fault-specific claim while the range
is observed armed or invalidating, tracks range/arm/mapping/page epochs, publishes successful
kernel restoration receipts, and uses a preallocated CAS ticket pool to reject
copied claims after one acknowledgement. It moves dirty/generation publication
before opening the page for writes and rejects restoration failure, pending
page owners, retirement/replacement, rearm and mapping changes.

Its 35 focused Rosetta tracker cases and 10 existing POSIX preflight cases pass.
The focused cases include two delayed captured faults behind a third restore,
one-use/copy rejection, kernel page checks, overlapping owners, zero allocations
in the warmed signal-facing methods, and an actual readonly file-backed page
that makes a later mprotect run fail after an earlier run succeeds. These tests
qualify those orderings only. The prototype is **not shipped**.

Independent review found an ownership gap: checking a claim and then calling
an address-only handler does not atomically reserve the expected live mapping
and protection owner. Untrack can retire an invalidating range without waiting
for its native restoration; that restoration can later act against rearmed or
reused backing. Epoch checks after the syscall can reject acknowledgement, but
cannot undo that kernel mutation. Host mapping generations are published after
some kernel operations, and HostMemory.Query describes shadow metadata, so
neither proves the needed lifetime reservation. A future token consumption
must also verify the current normalized frame's address and access explicitly.

Before enabling retries, implement a shared owner-lifetime contract spanning
tracker protection operations and every host mapping/protection mutation. It
must revoke permission before mutation, keep backing alive through outstanding
native operations, bind claims to one normalized invocation, reserve an expected
arm/page epoch without waiting or allocating in signal context, and publish a
restoration receipt only after successful kernel work. Test mutation-in-progress,
retirement during restoration, same-address remap, rearm ABA, partial restoration
failure, overlapping owners and forged/wrong-frame claims. First lookup after
restoration remains unresolved; accepting every dirty disarmed address would
also hide legitimate readonly/unmapped faults.

Prototype patch, hash and logs are preserved in
`artifacts/validation/integrated-20261007/write-fault-claims/`. They are diagnostic
evidence, not an integration patch. No additional game or GPU run was performed
for this change, and this work does not establish that Poppy progresses.
