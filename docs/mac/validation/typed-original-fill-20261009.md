# Closed original Fill at its actual queue position

Source-only successor of frozen `8c4b352f6a85141c0a1ad54c94c8f2c6adca89fc`.
Actual Superliminal 8c reaches beyond the prior closed Write history refusal;
its next first refusal is pending `command-memory-Fill/FILL`, sequence 2638,
queue dcb.graphics/submission 89, destination 0x70803CDC50, four bytes, value
zero. The taken/executing draw is sequence 2637, sampling selected color at
0x7068B80000 over its complete 2 MiB original range. This is observed progress
and an identified refusal, not game entry or visual correctness acceptance.

## Actual operation and transfer contract

`GpuCommandInterpreter.Memory` validates DMA controls and dispatches immediate
source selector 2 to `FillBuffer`. Non-GDS Metal FillBuffer queues its closed
Fill DTO. The existing ordered completion wrapper first waits the preceding
flushed GPU commands, then `FillBufferAtQueuePosition` calls managed FillBuffer:
aligned repeated little-endian uint bytes are written in 64 KiB chunks, followed
by `MirrorTransferToSnapshotImage`. That helper submits a native image fill only
when a cached image extent at the exact destination fits inside the transfer.
GDS uses its dedicated GPU transfer path and is excluded from this change.

The new original transaction scope is deliberately at most one existing chunk:
nonzero aligned non-GDS fills of 4..65536 bytes own their entire repeated uint
payload before admission. Unsupported/zero/unaligned/overflow/larger fills,
Copy/Atomic/Semaphore and arbitrary callbacks cannot claim this scope.
The existing closed Write ownership and its once-execution guard remain.

The renderer captures the same first complete watched mapping at admission,
without later enrollment, plain fallback or recapture. Certification requires
an actual sealed MetalCommandStreamHost with actual MetalGuestGpuBackend snapshot
registry, no destination extent, and exact memory/session/queue. The queued
clone retains the original host, immutable operation, source, mapping and watch
identities. Every original-owner check repeats the host/backend/memory and
no-extent scalar predicates around the existing gate-free provider query.
The native-color cohort uses the complete original physical runs, including
aliases, and its unchanged accepted/taken inventory and native producer proofs.

Execution is the same exact-original provider transaction as Write: prepare
from the complete first observation; pre-byte NotifyManagedWrite and MarkCpuWrite;
validate owner; one TryCommit; require SAME nonnull Applied successor when
watched; final owner check and dirty/optional GuestWriteWatch diagnostics; consume
successor once and dispose ticket once. Mapping/history/watch/rights/refusal has
no ordinary TryWrite fallback, retry or new capture. Unknown commit/cleanup keeps
its exact scope rooted and refuses further certified effects across reset.

After known Applied the actual existing mirror helper is still called. Its
failure retains the same host/effect/owner DTO, forbids acknowledgement/replay,
and preserves the primary. Existing mirror destinations cannot be certified;
they retain the legacy transfer behavior and remain unknown to native borrowing.

## No concurrent mirror installation premise

All extent installs in this exact runtime are native materialization paths:
CreateDrawColorTexture (`Draws`), storage image construction/publication,
EnsureGuestImage, and MaterializeTextureSourceOutputs. Their native encoding is
performed by the sole render-owner drain. Queue frontend admission and original
snapshot preparation do not install a native extent. The typed execution guard
requires the exact taken/executing DTO in that real drain; subsequent native
materialization cannot overtake its callback. The direct initial path requires
the existing initial-completion owner/barrier, which prevents take/admission
from overtaking that callback. Reset/session changes refuse final ownership.
Thus the repeated no-extent guard does not replace physical mapping exclusion:
the actual write is still bound to the first provider transaction and pin.
There is no new general native image mirroring proof or cache admission policy.

## Prepared qualification controls

Nineteen new ordinary Libs rows: six unsupported shape/payload rows, three real
ledger independent full-pattern Applied/once rows (4/4096/65536 bytes), five
original watch/overlap/unknown/rights/ABA/release refusals, three actual-Applied
missing-successor/accessor/return-loss no-ack/no-replay controls, and two
scalar no-mirror-at-capture/later-registration refusals. Protection outcome in
the managed ledger control is explicitly synthetic; it proves no native syscall.

Seven new actual-native source scenarios reuse the existing real selected color
producer and real sampled consumer: FILL4 zero, 4096-byte unequal-byte pattern,
physical alias, remap after cohort validation, rights, session and tracked dirty
notification. Positives require actual status4, all 2 MiB native sample bytes,
all 64 KiB destination bytes, unchanged original chain and later actual EOP.
The FILL4 row also executes 2022 real disjoint participating writes after the
first watched admission. Its independent guest oracle includes that intentional
churn. Expected fill bytes are calculated independently by byte shifts; no
provider, native status or completion counter is overridden for positive rows.
Join-unknown teardown keeps real caller/native/provider owners quarantined.
EOP is admitted after consumer/effect completion; queued-ahead opaque callbacks
remain unknown, so this fixture does not qualify that different game topology.

Source expectation over 8c: **1039 ordinary managed rows per CLR**
(208 Host, 751 Libs, 80 GPU), **73 separate native workers per CLR**.
All old controls/oracles are retained. Nothing is compiled, discovered, tested,
published, activated or replayed for this candidate. Frozen 8c/DS/Void candidates,
main and prior failed/game evidence remain unchanged.
