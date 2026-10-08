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

## Shader preparation candidate runtime

Clean isolated candidate `c5f5d80ef8bc683677a659afc4a9c2351280193a`
prepares actual shader libraries on the command worker before renderer DTO
admission, using the same retained Metal device as rendering. Accepted
submission receipts wait outside both AGC gates. A blocked parsed prefix may
return DeferredDependency; that result certifies neither later preparation nor
GPU completion. No labels or acknowledgements are applied early.

The build succeeds with127 warnings/zero errors in27.97s. One owned residual
compiler child receives TERM after leader exit0; no KILL or remaining workers.
The same158 focused managed cases pass on the actual ARM64 and x64 host
runtimes, zero skips/failures and stable DLL hashes. Those cases do not qualify
native cache/device teardown. A separate signed native-only239-file app
publishes in31.29s without signals or remaining workers.

Its matching25s Quake probe still calls guest abort at8002C9F65, through the
same frame-start context-label timeout. The window remains alive, peak1197MiB,
and stops by owned TERM without KILL. Only three guest frames are reported,
followed by zero. The shader libraries do compile before draw2 admission:
vertex36.068ms and fragment88.941ms, with cache hits on subsequent submissions.
First draw2 now takes47.047ms wall time: textures14.632ms, bindings10.978ms,
pipeline enclosing8.133ms (native pipeline1.284ms), color attachments4.954ms,
and wrapper-to-core5.692ms. EOP3 waits58.667ms from admission to action start.
The guest abort is logged before that action starts.

The previous diagnostic draw was206.564ms; these separate startup runs are not
a quiet-host performance A/B and compiler cache state is uncontrolled. The
candidate moves library work to the preparation boundary but does not repair
Quake progression. Next work captures an exact runnable prefix and applies
caller backpressure until its real native completion and ordered writes,
outside AGC gates. Deferred CPU/cross-queue dependencies must keep progressing;
no later global queue tail can substitute for the exact accepted prefix.

Evidence: `artifacts/validation/all-games-20261008/cold-library-readiness-c5f5d80e`,
42 retained files, transfer manifest
`4d6b8c5d46d534ad2814edfbb3afaebca18e561e756f4dfd053a95736d6270d9`.
Original signed run: `artifacts/runs/20261008-162520-quake-cold-library-readiness-c5f`.
The full signed bundle matches before/after. This candidate remains isolated.

Apple documents that [waitUntilCompleted](https://developer.apple.com/documentation/metal/mtlcommandbuffer/waituntilcompleted())
waits for GPU execution and completion handlers. The implementation must still
check successful status and preserve writeback/label ordering. [Metal binary
archives](https://developer.apple.com/documentation/metal/metal-binary-archives)
can avoid repeated compilation; they do not establish completion ordering.
Read-only reference inspection confirms REmu persists offline metallibs with
truncated-cache checks and Kairo attaches complete native pipeline descriptors
to archives with atomic temporary-file publication. Neither reference was edited.
