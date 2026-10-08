# Quake exact-prefix completion replay — 2026-10-08

Isolated clean `71b05e8cdedc5448c553139a30bbb233f9f13717` captures the actual
accepted runnable prefix's logical queue, submission and original producer
sequence. Its completion barrier follows exact renderer descendants, including
followups admitted before or after parser completion and nested followups. It
never uses a later unrelated queue tail as the prefix receipt. The guest caller
waits outside the queue and AGC gates; the command worker can continue servicing
cross-queue dependencies. Deferred blocked prefixes carry no fabricated native
completion. Actual successful GPU completion and writeback/label actions remain
ordered through the existing renderer path. Unsupported/skipped work in an
enrolled prefix refuses certification.

## Qualification and runtime

The Release build passed with 130 warnings and zero errors, compiler wall time
27.80 seconds. One owned residual compiler process received TERM after leader
success; no KILL, timeout or remaining owned group. All 185 exact managed cases
passed on each ARM64/x64 runtime, zero skips/failures, identical case sets and
unchanged DLL closure. The managed controls use native overrides; these are
not actual GPU tests. Actual CLR drivers, dotnet info and TargetPlatform were
recorded; no selected-case in-process architecture assertion is claimed.

A separately published clean-source native-only signed app contains 239 verified
files. It excludes Vulkan, MoltenVK, SDL, Skia/Avalonia and HarfBuzz artifacts.
All signed bundle hashes remain unchanged before/after the real Quake replay.

The 90-second-bounded replay exits 134 after 63 seconds, peak RSS 2274 MiB.
Unlike c5f5d80e and current-main controls, it passes the initial guest
`8002C9F65` frame-start label timeout. Screenshots show the intro movie and the
photosensitivity warning. Guest logs proceed through Quake engine initialization,
`Starting Game`, demo1 loading and `the Necropolis`. The movie reports roughly
24–30 guest frames per second; these are not gameplay FPS. The final screenshot
remains on the warning at zero FPS; no rendered demo scene or controllable
Quake gameplay is accepted.

The next failure is the known complete 8192x8192 R32Float depth-compare source:
address `0x7095200000`, tile mode 24, Pixel shader hash `A06A7C056FBF7B7F`,
required snapshot 268435456 bytes against 134217728 bytes. Exact descriptor:
`[70952000,C1600000,07FFC7FF,91800204,00000000,00700000,00000000,00000000]`.
The terminal command-worker exception promptly reaches the presenter failure
loop. No artificial depth bytes, partial snapshot or budget increase is used.
Producer/native ownership needs tracing before choosing the next render path.

This candidate is now selectively integrated into local main at 5b54e07f,
preserving newer main worker-failure cleanup. Actual main Quake and healthy
Sarah scene replays are recorded in the [integrated checkpoint](main-prefix-and-wait-profile-20261008.md).
No sustained FPS, loading-time or all-games acceptance is inferred. No remote
publication or upstream merge occurred.

Root archive:
`artifacts/validation/all-games-20261008/completed-prefix-71b05e8c` (83 files).
Transfer manifest SHA256:
`0cf88aab75c7506bbb1701ae9077c00554410e421be83ac02ca70ebde008c8fd`.
Original qualification manifest SHA256:
`49a92980a71b2580f5d28f45eced2d7aed81ccf71336381b2b362d236bfcad9d`.
Original run: `artifacts/runs/20261008-172811-quake-completed-prefix-71b`.
