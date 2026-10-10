# Integrated Mac mapped-copy qualification

Clean source `115316f03c9f62f7a1ded6d1535259a632cedc6e` integrates the optional
mapping-bound detached-copy contract and Mac producer. Guest execution remains
x64/Rosetta; ARM64 CLR controls exercise host code only. This does not establish
all-nine rendering, sustained 60 FPS, complete gameplay or fast loading.

## Integrated behavior

The Mac producer records successful full-kernel-page shared mappings with an
immutable incarnation, backing offset and read access. Participating map,
protect, restore, unmap and release routes revoke overlapping observations
before the native effect; failure does not reopen them. Same-address replacement
cannot make an old observation current again. Canonical backing aliases are
excluded from unrelated raw mapping operations, and unknown alias outcomes
retain quarantine.

An owned copy resolves at most 64 segments and 64 MiB against the original
mapping footprint. The provider captures mapping metadata under its gate,
copies through a pinned canonical backing alias outside that gate, then checks
the original observation. Rejected destinations must be discarded. PVM, guest
space, shared views and tracked memory forward the optional interface explicitly.
Unsupported logical mappings retain existing behavior and decline the optional
capability.

This establishes detached bytes and a conservative mapping-currentness veto.
It does not freeze content, exclude native/JIT writers, prove native permissions,
authorize signal recovery, certify GPU writeback or enable Metal consumer reuse.
The initial Metal seed consumer remains separate pending qualification.

## Actual checks

SDK 10.0.103, runtime .NET 10.0.3, Apple M4, macOS 27.2. Builds completed with
zero errors: library/Core reference build 23.71 s (128 warnings), host tests
2.00 s (zero warnings). On each actual ARM64 and x64 CLR:

| Suite | Passed | Scope |
| --- | ---: | --- |
| Existing library regression union | 376 | Prior selected-mip, vertex and memory controls |
| New observation controls | 27 | Shape, bounds and immutable metadata |
| Host-memory managed union | 60 | Authority, retirement, copy races and alias outcomes |
| Fresh-process native mapped-copy workers | 4 | Actual Mac shared copy, ABA, alias exclusion and protection race |

All 467 rows per CLR passed with zero skips, failures, timeouts or forced
signals. Native workers assert process architecture, exact framework and
measured 16 KiB kernel pages; provider page geometry differs between CLRs.
Original DLL hashes match after execution. The 117 managed Metal and 55 native
Metal checks from the earlier R16 integration were **not rerun at this source**.
Actual PVM/Tracked native forwarding workers were subsequently [qualified
separately](pvm-mapped-copy-forwarding-workers.md), four fresh scenarios per CLR
at754a7637, then imported locally as test-onlyef8d4f20. The four native rows above
remain the earlier shared views/provider composition checks.

Evidence is retained at
`artifacts/validation/all-games-20261008/mapping-copy-main-115316f0/`, including
exact commands, case identities, TRXs, source patch and 18 frozen source files,
34 frozen DLLs and their hashes, runtime file manifests and screenshots.

## Signed application and Sarah input replay

`artifacts/macos/compat-mapped-copy-115316f0/SharpEmu.app` passed deep/strict
ad-hoc signature and 239-file native closure checks. Publish took 26.14 s; this
is a build duration, not game loading time. Bundle manifest SHA256:
`28f87e007cb17d0801c1b6e56a0b0e4fbf652cc79a36989df4ac8d9d7946c788`.

Dreaming Sarah remained alive for 150 s, peak RSS 890 MiB, and showed its outdoor
scene. The first input plan used D-pad arrows and Space; Space is not a mapped
pad button. A corrected 90 s replay used A/D for the left stick and Z/Enter for
Cross, peak RSS 879 MiB. Screenshots 39, 43 and 77 verify rightward travel and
leftward return with terrain/camera movement. A combined D+Z request was sent,
but retained one-second snapshots do not conclusively prove an airborne jump.
Late reported guest-frame windows were around 100 Hz. Dense screenshots,
simulation cadence and window reporting do not establish sustained scanout FPS,
full scene fidelity or a causal performance improvement from the mapped-copy
change, which has no Metal consumer yet.

Both runs used the shared single-game harness with no concurrent owned
compiler/native/game workers and stable bundle hashes. Owned TERM returned
without KILL. A guest `pthread_join` error was logged during stop; host shutdown
does not certify clean guest teardown. Game files and save/config data were
preserved.

## Octopath diagnostic replays

The earlier clean diagnostic source `073d7bb9` adds exact texture shape and
provenance details when CPU dirty synchronization rejects a typed image. It
preserves the existing shape veto. Its separate signed app passed signature and
closure checks; bundle manifest SHA256:
`183542ff2270adeecd15c01b3bcd1ada0ec7899d72499bd5a9b1efaa0c23b436`.

Two 150 s Octopath replays, with and without additional lifetime trace filtering,
remained alive after rendering stopped. Peak RSS was 3061/3268 MiB. Both final
screenshots are black. Neither reached the new typed-array diagnostic because
selected-mip admission first rejected global mapping epoch drift:

- `0x8073D60000`, mip 0, epoch 993288 to 993579.
- `0x8077D60000`, mip 0, epoch 1059273 to 1059306.

Survival is not gameplay progress. Evidence is retained at
`artifacts/validation/all-games-20261008/typed-cpu-sync-073d7bb9/`.

## Reference findings and next work

The callsite audit distinguishes aggregate process mapping churn from
participating overlapping mapping retirement. Direct tracker libc protection
changes bypass both the aggregate epoch and optional ledger; unchanged mapping
metadata therefore cannot replace content/trap checks. The next consumer must
use the original detached CPU seed observation and complete content token;
later GPU-written owner reuse requires separate ordering and publication proof.

Kairo's resident-image and selective-settlement source and REmu's physical
texture design inform the separation of backing identity, view layout and GPU
write debt. Reference HEADs were `755e7657820161dd90ac93a93eb80d833eecc394` and
`8460648beb2e3ffae2610bff87228340373f2c57`; dirty source/status hashes are frozen
with the diagnostic evidence. Neither reference repository was edited, and
proposed design is not treated as qualified implementation.

Apple's [CPU/GPU synchronization guidance](https://developer.apple.com/documentation/metal/synchronizing-cpu-and-gpu-work)
requires ordering resource reuse around completion. Its
[shared-event guidance](https://developer.apple.com/documentation/metal/synchronizing-events-between-a-gpu-and-the-cpu)
and [resource synchronization documentation](https://developer.apple.com/documentation/metal/resource-synchronization)
inform the separate Metal completion work. Shared memory alone does not certify
CPU/GPU ordering. Downloaded official documentation and hashes are retained.

No remote push, PR or merge was performed. All nine titles remain in scope.
