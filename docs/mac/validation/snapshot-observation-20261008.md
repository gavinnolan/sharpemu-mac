# Snapshot freshness veto diagnosis

The root 4b5 Poppy run stops before presentation while capturing the mode 27
1x1x1 RGBA8Uint storage resource at 0x9007000000. Sarah 4b5 also stops progressing
at the new snapshot freshness check. Runtime attribution requires the detailed
safe-side failure fields added in 467a6fe; the original generic exception cannot
distinguish generation changes, owner changes, observation coverage, or the
process-wide mapping epoch.

The new `MetalNativeSnapshotObservationTests` exercise actual 64KiB-aligned
host-owned backing and the real production snapshot builder, using the same
non-address raw words as Poppy. The fixture does not launch Metal or a game.
The native kernel permissions are queried through Mach, because the host shadow
mapping table does not reflect the tracker's direct libc mprotect calls.

The unchanged-backing control shows initial tracker arming followed by a real
65,536-byte read produces the complete four-byte singleton seed in one attempt.
The tracker arm does not advance `HostMemory.MappingGeneration`: it calls libc
mprotect directly. The adversarial case calls real `HostMemory.Protect` on a
separate allocation after each read. Before the bounded correction, the builder
rejected all three attempts even
though the original backing remains kernel READONLY, its observed ContentToken
is identical across attempts, and its write generation remains 0. Thus an
unrelated protection mutation can reproduce this global mapping veto without
any snapshot-backing write. This is a deterministic mechanism proof. Later root 618b57f diagnostic runs
showed the same mapping-only veto in both titles: Sarah 85610→85611 with
generation 0 and unchanged protected/watch tokens; Poppy 554266→554287 with
generation 0 and unchanged protected token. The exact mutation address remains
unidentified. Source-linked evidence is in the root artifact folder
`integrated-20261007/snapshot-observation-618b57f`.

The diagnostic helper runs only on safe-side final exceptions. Its mapping,
owner state and token fields are separate observations, not an atomic snapshot
or backing lifetime receipt. This change does not alter freshness acceptance,
retry counts, fault recovery or the disabled source graph. A future correction
must keep graph certification distinct from acceptance of the existing owned
CPU snapshot; it must not treat global post-kernel counters or equality as a
mapping reservation.

Executed on macOS arm64 host with x64/Rosetta dotnet: two fixture cases passed,
zero runner skips, no Metal/device/game work. Per-read Mach READONLY checks and
exact ContentToken equality are assertions in both cases. The fixture explicitly
returns early off macOS; other OS behavior is not qualified by this run. Logs
and TRX are retained in this worktree's `artifacts/validation/snapshot-observation`
and `tests/SharpEmu.Libs.Tests/TestResults`. Startup restored and built fresh
worktree dependencies with one MSBuild worker and shared compilation disabled;
existing project warnings were emitted, with no build errors.

The bounded correction separates content-token stability from full mapping
observation. The graph-enabled builder still requires both, and registration
of GPU-source provenance always uses the strict full observation. With the
graph disabled, the builder admits an otherwise unchanged owned CPU copy
despite unrelated process-wide mapping changes; write-generation and qualified
content identity/counter changes still reject it. The admitted mapping-raced
copy has `CpuSnapshotStable=true` for its existing owned upload, but its distinct
GPU-source provenance record remains `Stable=false`, preventing later promotion
to a certified source. This neither acquires nor claims a backing lease.

The expanded real-host fixture covers both graph settings with no mutation,
unrelated protection mutation, actual overlapping managed-notified stores with
rearm, and owner retirement/replacement at generation 0. Mapping-only mutation
is accepted in one read with graph disabled and rejected after three attempts
with graph enabled. Actual content writes and owner ABA remain rejected under
both settings. The graph remains disabled in production.

The correction passed all eight real-host cases and the 82-case x64/Rosetta
selection covering source lease/admission/metadata, queued snapshots,
graph-disabled snapshots and observed-content-token regressions, with zero
runner skips. Package-contract review independently checked every admission
call and the unchanged strict provenance registration/publication checks. No
new game or device qualification is claimed from these CPU-only checks; root
owns subsequent integrated builds and frozen runtime verification.
