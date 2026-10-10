# Protection intent and combined Octopath replay

Clean `7a286abb49686198316028cc19288a24e5561c33` qualifies the production
protection changes from `9085b80a`: guest access remains separate from temporary
host watcher permissions. Successful exact provider effects can restore future
copy availability while old mapping observations stay stale. Physical mapping
facts survive a temporary restriction; no query manufactures a new identity.

Each actual .NET 10.0.3 ARM64 and x64 CLR passes 71 managed rows: 18 new derived
protection/PageGuard controls, nine ordinary protection controls and 44 range
publication controls. All cases pass with zero skips and identical case sets.
The initial 9085 ARM run has nine fixture failures from copying under the
provider gate. The reviewed 7a correction moves those copies outside the gate
while preserving pending receipts; original failures are retained.

Four fresh native cases per CLR also pass. They use a continuously owned
128 KiB Mach allocation with a contained 64 KiB mapping, compare every varied
canonical byte, and check temporary RW-to-RO protection, genuine guest RO,
partial protection and actual native failure. The maximum-protection control
gets real `mach_vm_protect` success followed by `mprotect` failure with errno 13;
it rejects copies until a separate successful read-only protection. Kernel pages
are 16 KiB on both CLRs; provider geometry is 16 KiB ARM and 4 KiB x64. All owned
workers exit normally, source/DLL hashes match, and the shared lock is released.

This qualifies ordinary provider copy availability and original guest rights.
It does not qualify Poppy recovery, signal/JIT safety, direct tracker libc
participation, native guest-writer completion or general GPU writeback.

## Combined source and actual replay

Isolated `c24c327a293986d334cbae9db1326adabdedf500` layers the exact canonical
provider/protection chain over typed-storage candidate398 and current native
depth source878. HLE/Mac/PVM source matches qualified7a exactly. The original
typed-copy mapping epoch is captured after initial read-tracking setup and
before token observation/copied bytes, following reviewed34. No failed
observation is refreshed, and finite GPU writeback/source-graph flags stay off.

Three builds succeed; the library compiler requires residual-child TERM, which
is retained in its receipt. On each runtime configuration the combined source
passes 647 managed rows (22 typed, 489 library, 128 GPU, eight shader) and 78
required native rows (four typed, 68 Metal, six fragment compiler), zero skips
or failures and stable source/DLLs. A separate native-only 239-file signed app
publish completes in29.67 seconds with no signals or remaining workers.

The signed Octopath replay clears the earlier provider mapping-copy rejection
at `8030F00000`, then exits134 after38 seconds, peak2767MiB. It fails at
`8030300000` with `gpu-produced-or-pending-write-debt`. The guard tests an OR of
image GPU-written state, global pending-work count and an open native batch;
this log does not identify the triggering term or prove an actual image writer.
A dirty flag alone cannot establish full CPU replacement or completed writers.
The replay provides
no gameplay, rendering-fidelity, loading-time or sustained-FPS acceptance.
No window screenshot is produced in this run. The combined source stays
isolated; source provenance/dirty ownership is the next runtime investigation.

## Exact dirty-guard diagnostic replay

Diagnostic-only successor `ab9dedeb925d3098b8a9082860f6c3948a638d9b`
passes the same 22 typed controls on both actual CLR architectures. Its signed
239-file native-only bundle publishes in42.70 seconds and remains byte-identical
before/after the run. The diagnostic flag leaves rejection policy unchanged.

The next Octopath run exits134 after35 seconds, peak2359MiB, at a different
resource: `805E490000`. Here the exact guard reports GPU-written=true,
pending-work=false/count0 and batch-open=false. The1024x1024 RGBA8Unorm singleton
storage array (pitch1024, mode27, guest format10/0, native type3) was created and
mutated by storage dispatch41 on `dcb.graphics`, submission3; content version2.
The original/current seed sessions both remain0. This proves the recorded image
has GPU production history; it does not prove CPU dirty causality, completed
native writes or safe canonical publication. The prior c24 resource's triggering
term remains unknown because that run lacked these diagnostic fields.

The captured30-second window is black. Only one guest frame is reported; no
menu/gameplay, visual correctness, loading or sustained-FPS gate is met. The
next change must preserve GPU-produced contents and establish exact dirty/writer
ownership rather than replace them from an unqualified CPU seed.

Source/build/test and full signed runtime evidence is retained under
`artifacts/validation/all-games-20261008/typed-cpu-diagnostics-ab9dedeb`.

Evidence:
`artifacts/validation/all-games-20261008/protection-intent-7a286abb`
(104 files; transfer manifest
`ced978296c13dca4596baccb8af1f697a395625a80e7f52df6f7ae8e5284831c`)
and `artifacts/validation/all-games-20261008/typed-protection-combined-c24c327a`
(combined builds, TRXs, native compiler/device checks, signed closure and run).

## Original dirty-seed diagnostic qualification

Isolated diagnostic `28bb509ecbd117ccb83fa39aaa86aa3428976fd2` adds bounded,
default-off dirty-generation records comparing current tracker state to the
original uploaded seed. It changes neither ConsumeDirty nor image ownership.
An initial restore failed with ENOSPC before compilation; that failure and exact
cache cleanup plans remain in evidence. The retry uses one verified shared
package cache and succeeds in31.40s with128 warnings/zero errors. An owned idle
compiler child receives TERM after exit0; no KILL or remaining workers. The
same22 typed controls pass on actual ARM64/x64 runtimes with no skips/failures,
stable source and DLL hashes.

The separately signed239-file app publishes in28.20s without cleanup signals.
Its90s Octopath replay stays open, peaks3576MiB, and stops on owned TERM without
KILL. The final screenshot is visually verified black, FPS0. One early guest
frame is followed by zero; this is not runtime progression. No dirty diagnostic
record or typed synchronization rejection is reached. Instead the command
worker logs a selected-storage-mip admission failure at8073D60000:
`global-mapping-epoch`,1095792 to1096679, selected mip0/domain1. Stop rethrows
that earlier cause during shutdown. Host redraw continues after worker failure,
so prompt failure propagation is a separate necessary repair.

Whether the prior ab9 dirty event occurred before or after the seed upload
remains unanswered. The new per-resource original mapping/content proof must
first clear this admission failure; no stale-dirty bypass is authorized by this
run. Evidence retains44 files under
`artifacts/validation/all-games-20261008/typed-dirty-original-28bb509e`, transfer
manifest `9ec535914afb81358a273857b009e5edd119961b34f1353d583a411356e85883`.
Original run: `artifacts/runs/20261008-161346-octo-typed-dirty-original-28b`.
The full signed bundle matches before/after. No additional title is accepted.
