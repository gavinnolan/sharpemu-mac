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
separate allocation after each read. The builder rejects all three attempts even
though the original backing remains kernel READONLY, its observed ContentToken
is identical across attempts, and its write generation remains 0. Thus an
unrelated protection mutation can reproduce this global mapping veto without
any snapshot-backing write. This is a deterministic mechanism proof, not yet
attribution of the actual game failure.

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
