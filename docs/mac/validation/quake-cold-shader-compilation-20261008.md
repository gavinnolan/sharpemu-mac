# Quake cold shader-library timing

Isolated source `d6461778a99655323fbbcda74d241a27533c0133` adds bounded
default-off work-stage timestamps over46b. It changes no scheduling, waits,
completion acknowledgement or failure policy. The build succeeds with128
inherited warnings; the owned idle compiler child requires TERM after leader
exit0, with no KILL or remaining worker. On each actual .NET10.0.3 ARM64/x64
runtime, the same106 focused controls pass with zero failures/skips and frozen
source/DLL hashes.

The separately signed239-file native-only app publishes in33.24 seconds without
signals. Its25-second Quake probe repeats the guest context-label abort at
8002C9F65; the host remains alive, peak1076MiB, and exits normally on owned TERM.
The signed bundle is byte-identical before/after the game.

| Measured interval | Wall time |
| --- | ---: |
| First compute execution |53.100ms |
| First draw execution, work2 |206.564ms |
| First draw vertex shader library creation |53.968ms |
| First draw fragment shader library creation |106.183ms |
| First draw native render pipeline creation |1.393ms |
| First draw texture resolution |14.548ms |
| First draw bindings |7.693ms |
| First draw wrapper entry to core entry |6.245ms |
| EOP work3 admission to execution |217.623ms |
| Context label work17 admission to execution |217.441ms |
| Context label work35 admission to execution |213.059ms |
| Later cached draw20 / draw38 execution |4.084 /2.467ms |

The two library intervals total160.151ms, about77.5% of this draw's wall time.
They bracket compile-option/source construction and the synchronous native
`newLibraryWithSource:options:error:` call; they do not isolate exclusive
compiler-service CPU or GPU execution. Diagnostic output and scheduling can
affect these intervals. The abort appears between vertex-library begin/end,
before the first draw finishes and before the context labels execute. This is
one cold startup, not a steady-state gameplay or quiet-host A/B result.

The next production candidate prepares shader libraries before allowing the
guest to advance into its timeout-sensitive work. Any readiness wait must occur
outside both AGC submission gates, and preparation must never acknowledge GPU
completion or apply EOP labels early. Persistent native library caching may
improve warm startup, but it cannot replace correct first-run readiness.

Runtime research also checks the exact .NET10.0.3 implementation: quick JIT for
loops defaults on when on-stack replacement is compiled in, and TieredPGO
defaults on. An unset loop setting does not prove disabled quick JIT. See the
[runtime source](https://github.com/dotnet/runtime/blob/v10.0.3/src/coreclr/inc/clrconfigvalues.h).
[ReadyToRun](https://learn.microsoft.com/en-us/dotnet/core/deploying/ready-to-run)
can reduce managed first-use compilation with size/working-set tradeoffs; the
measured native shader-library delay still needs its own fix.

Evidence: `artifacts/validation/all-games-20261008/command-memory-cold-d6461778`
retains32 source/build/test files, manifest
`6b7cae2832d6abb572946fd57f6ebf13aaa14928220b0368c1b6a06aeb5718fb`,
and17 signed runtime files under`runtime`, transfer manifest
`80e64f50fa1343de6d8b72153ed6be14373bd69a7b14533ddd371ad6a70a5dea`.
No new title, rendering, loading or sustained60+ gate is accepted.
