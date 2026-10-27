<!-- Copyright (C) 2026 SharpEmu Emulator Project
SPDX-License-Identifier: GPL-2.0-or-later -->

# Native occlusion and Silent Hill room measurements

**Current default policy:** [performance-defaults.md](performance-defaults.md)
supersedes the historical opt-in decisions below. Per-run flags and results
remain unchanged; default promotion is not a new FPS measurement.

Native Metal visibility counters replace the conservative always-visible query
results when `SHARPEMU_METAL_V2_NATIVE_OCCLUSION=1`. In a paired, same-binary
Silent Hill first-room comparison, median guest FPS increased from 4.485 to
13.43. This does not establish 30 FPS, full-game performance, or performance in
the user's original 2-FPS scene.

## Query behavior

Counting draws accumulate samples in distinct visibility intervals. A GPU prefix
sum preserves the counter value at each dump; ordered GPU copies publish these
snapshots with the guest ready bit. Empty intervals, sparse instance masks,
resets, multiple queues, pending predication, and query-memory reuse retain
stream ordering. GPU-produced values are never prematurely read into the CPU
label replay cache.

Each batch has 4,096 intervals. At most 256 batches are retained (16 MiB for
counts and snapshots). Between passes, exhaustion waits for retired storage or
commits and waits for the current buffer. Unfinished storage is not reused.
The initial experiment aborted on budget exhaustion; that failed run is excluded
from performance results and has a bounded-backpressure regression test.

The latest implementation initializes only exposed slots, caches visibility mode
and offset within each encoder, and skips a reduction only when no counting draw
or referenced snapshot requires it. A disabled pass preserves an existing
nonzero counter. Sparse dumps excluding block zero still publish ready-only
values without requiring a batch snapshot.

## Measured comparison

Apple M4; x64/Rosetta emulator with native arm64 GPU host; Release, self-contained
.NET 10; 1920×1080, scale 1, VSync enabled and existing rendering settings retained.
Each run uses a private copy of the same warm user/cache seed. Other applications
remain open. Host memory pressure and background load are not eliminated.
Only Silent Hill: The Short Message (PPSA10112) was launched.

The phone was closed after visual confirmation. The stationary view faces the
same first-room shelf and lamp. Samples are two-second guest-presentation reports
in the 375–410 second interval after launch. Host presentation Hz is a separate
counter and must not be substituted for guest FPS.

| Build/arm | Reports | Median guest FPS | Report range | Interpretation |
| --- | ---: | ---: | ---: | --- |
| v2, native queries disabled | 18 | 4.485 | 3.50–5.00 | Same-binary control |
| v2, native queries enabled, serial resolution | 18 | 13.43 | 10.45–14.44 | 2.994× paired median ratio |
| v3, command thread and parallel resolution | 18 | 11.455 | 9.46–11.95 | Slower than serial; different code and mode |
| v4, also queue CP writes | 18 | 11.725 | 9.46–13.00 | No established improvement |
| v5, also request native command-thread QoS | 17 | 11.98 | 10.95–13.00 | QoS applied; no established improvement |

These are observational comparisons, not confidence intervals. Only the first
two rows change one switch in the same executable. Their independently reported
draw and frame windows suggest roughly 9,492 versus 3,366 recorded draws per
frame; those ratios are approximate, not exact per-frame accounting. The five
successful runs completed their 480-second limit and controlled shutdown with
exit 0. A post-measurement camera turn in v5 changed the view and rendered new
frames; its approximately 23-FPS wall view is not the stationary benchmark.

## Experimental switches and correctness fixes

The following performance switches remain opt-in:

- `SHARPEMU_METAL_V2_NATIVE_OCCLUSION=1`: native sample counts and ordered snapshots.
- `SHARPEMU_METAL_V2_QUEUE_CP_WRITES=1`: asynchronously enqueue byte and eligible qword CP writes when the
  existing command thread is active; pending-range reads and prefetch gates keep
  consumers behind those writes.
- `SHARPEMU_METAL_V2_COMMAND_THREAD_QOS=1`: create the command producer using the
  existing native helper-thread QoS policy, with managed fallback.
- `SHARPEMU_METAL_V2_REUSE_PRESENTED_FRAME=1`: suppress repeated presentation of an
  unchanged frame while preserving movie/CPU-frame updates, output changes,
  restore handling and animated overlays. Gameplay acceptance remains incomplete.

Overlapping pending-label writes previously left stale cached qword answers.
The corrected shared queue invalidates conflicting cached widths and falls back
to ordered backend reads when a complete answer is unavailable. The same device
predication test failed in both synchronous and queued modes before the fix.

The command-thread coalescing path now waits for ordered producer-idle work or
normal budgets instead of committing merely because the producer's queue has
become empty while a removed slice is still executing. Test drains use a trailing
producer barrier, rather than mistaking queue removal for completion.

APR session teardown now stops admission, cancels queued commands and joins
executing workers before archive unmount. Admitted submission and result writes
hold lifetime leases without holding the global queue lock during guest access.
Stopped root-memory ownership also rejects newly created wrappers. Cached host
files remain alive while readers hold leases, including during cache eviction.
Address waits and chunked reads cooperate with cancellation.

## Validation and unresolved evidence

Latest source validation, before the v6 runtime publication:

- Full Libs: 8,971 passed, 0 failed, 5 existing Opus skips, 1m18s.
- Full Metal, default mode with required device: 1,606 passed, 0 failed,
  7 existing skips, 1m24s.
- Native-query device cases with command thread, QoS, parallel resolution and
  queued CP writes: 13 passed, 0 failed, 1s.
- APR suite: 57 passed, 0 failed, 753ms, including five new lifetime cases.
- Command-thread lifecycle tests: four passed with QoS requested and four with
  native QoS disabled. v5 full threaded Metal: 1,604 passed, 0 failed, 7 skips.

The first new admission test waited for a guest read that cached capture never
performed. It failed while 56 APR tests passed. The corrected test parks the
actual admitted submission-ID write; it does not weaken the shutdown assertion.

A presentation-reuse run aborted during startup, before any room measurement:
pixel shader `17174605ABA538FD` loaded a descriptor 72 bytes beyond a mapped guest
region. An earlier unrelated experimental run failed at the same shader offset
and a different mapping boundary. The mapping failure remains unresolved; no
causal attribution to presentation reuse is established. Failure-time user-data
logging now preserves the actual failing draw's inputs. The later archive-close
exception exposed the separately repaired APR ownership race.

An artifact-only single-thread compute copy experiment reduced CPU encoding time
but increased GPU time. At 128 tiny copies, CPU encoding changed from
62.5–65.2 µs to 9–10.8 µs while GPU time rose from 10.6–11.1 µs to 26.6–27.1 µs.
It is not integrated and is not an FPS improvement.

## CPU lookup follow-up

A second v7 diagnostic run used CPU phase timing only, disabled native automatic
sampling, and completed its 600-second bound cleanly. In the fixed 475–510-second
first-room interval, 17 presentation reports had a median of 12.0 FPS (9.96–13.5).
This is an instrumented diagnostic, not a new uninstrumented performance record.
Across 422 reported frames, deferred-label ordering consumed 5.38 ms per frame;
resource materialization consumed 6.79 ms and draw-resource preparation 5.79 ms.

The pending-copy list now has two bounded negative page filters: one for guest
addresses and one for native buffer/offset pairs. A missing page avoids scanning
the list; possible matches retain the exact overlap check. Large or wrapping
ranges force fallback, and draining the list clears both filters. The switch
`SHARPEMU_METAL_V2_LABEL_PAGE_FILTER=0` restores the original lookup. CPU profiling
reports query, rejection, scan, inspected-entry and peak-held-copy counts.

A synthetic ARM64 Release benchmark measured a binding in a gap between two
clusters of held copies. Four alternating trials, 100,000 lookups per trial,
tiering disabled; all measured lookups allocated zero bytes:

| Held copies | Original median | Filter median |
| --- | ---: | ---: |
| 16 | 8.27 ns | 3.81 ns |
| 256 | 142.30 ns | 3.84 ns |
| 4,096 | 1,944.69 ns | 3.78 ns |

A first-entry match costs about 2.2–2.4 ns more with the filter. Same-page gaps
still require the exact scan. Filter insertion and reset cost about 6 ns per
copy in the 4,096-copy case. These are synthetic lookup measurements, not FPS.

Sampler numeric-class preparation also changes from a per-sampler scan of all
sampled-image pairs to one pass over the pairs. Classification is recomputed
from the current resolved image formats on every preparation; mixed, invalid,
and unpaired cases retain their behavior.

Validation: 78 focused device/unit cases passed with the filter enabled, and
78 passed with it disabled and CPU profiling enabled. The full Metal suite
passed 1,617 cases with 7 existing skips. Five render-phase tests passed.
Subsequent opt-in materialization diagnostics passed 66 cache/profile cases,
including moved descriptors and in-place comparison. The v8 CPU-profiled run completed its 600-second bound cleanly. In the same
475–510-second room interval, 17 reports had a median of 13.44 FPS (12.49–13.5).
Deferred-label ordering fell from 5.38 to 0.89 ms per reported frame. The filter
rejected 5,347,658 queries beyond the coarse address-hull check; 625,835 exact
scans remained, with a peak of 16,620 held copies. This is a profiled combined
filter/sampler comparison; an uninstrumented v8 acceptance run remains pending.

## Reproducibility

All runtime publications retain their source snapshot, tracked binary diff,
untracked-source archive and payload hashes in the local diagnostic artifacts.
They share base revision `52f07c7b37570adaad7317b8196a86f9bb4d6548`; they are dirty
source snapshots, not separate clean commits. Tracked source-diff SHA-256:

| Publication | SHA-256 |
| --- | --- |
| v2 | `c436a870becdc70703e5b1c29c399cdf146ab0be11119daebdd96dd9c0b39587` |
| v3 | `138d695e9425b452c1b67625a6a377b8d7989a1bfa8e217d6d276d9047a8db54` |
| v4 | `f24dcf90c9d0d797fd7e0346573d7d685b0013f22c97d4d17cd829eb9104f24a` |
| v5 | `1d7372c56af1bb810d4fd75b2bdee2c6243f9bed3c87c9c2b4f974190b707656` |
| v6 | `6cc88fe55291141199cbec17e62ef80dcbf288fbf1b226049a65db5f187acc65` |
| v7 | `efdf79607bd5046d1ef71b74f0e04f9b39fc934fc964f3d8bb4916f9ea9ce0f4` |
| v8 | `2325d12d7defa5ab7fafcd05c425871163a2062e2eb3123ec6ea563217a30f3a` |

The v6 publication adds the query-work reductions and APR lifetime fix. Its
profiled run stopped cleanly at 497 seconds. Automatic native process sampling
and memory inspection started near 487 seconds, contaminating the planned
475–510-second interval; that interval is excluded from FPS comparisons. A
post-hoc diagnostic interval at 440–475 seconds attributes 12.23 ms per frame
to storage-buffer resolution, the largest exclusive CPU phase. Its sampled
GPU encoder durations sum to 16.98 ms per frame, with 17.01 ms of gaps; these
are not exclusive wall times and cannot be added to CPU phases. This identifies
the next investigation, not an uninstrumented performance improvement. Commercial assets, shader
captures, screenshots and raw game logs are not included in this repository.

### Unprofiled v8 retry limitation

The next unprofiled v8 run failed before gameplay at 59.977 seconds, so it adds
no FPS result. The shader's captured pointer again lay 232 bytes before the
end of the mapped region, with the requested descriptor read 72 bytes beyond
it. The complete mapping snapshot and actual user-data words were retained.
This recurring failure predates the label filter and native-query changes; its
cause remains unresolved. The best qualified unprofiled stationary-room result
remains the v2 measurement above.

### v10 diagnostic and register-map follow-up

The v10 CPU/materialization diagnostic completed its 600-second limit and
controlled shutdown with exit 0. No descriptor failure recurred; its cause is
still unresolved. The matching stationary room at 475–510 seconds produced
17 reports with median 12.95 FPS (10.46–13.0). The phone was closed at 401.6
seconds, later than the prior runs' approximately 380 seconds. Detailed profiling
and bounded pointer-origin history were enabled, so this is not an unprofiled
FPS acceptance result.

Across 458 reported frames, resource materialization cost 7.455 ms/frame.
Sampled snapshot preparation estimates approximately 1.559 ms/frame, versus
0.417 ms/frame for hit validation. The cumulative counter interval contained
1,744,075 normal hits, 197 recalled hits, 1,724,144 rebased hits, and 150,881
recorded ranges. Many hits therefore have no ranges to compare. Sampled and
nested timings are diagnostic estimates and must not be summed as exact
exclusive frame accounting.

The dense shader register map preserves written-zero presence, arbitrary
capture keys, reset behavior and AGC's shared map identity. Full Release Libs
validation passed 8,986 tests with zero failures and five existing Opus skips.
A native ARM64 interpreter ABBA comparison changed only `SharpEmu.Libs.dll`:

| PM4 case | Control ns/draw, two trials | Candidate ns/draw, two trials | Mean change |
| --- | --- | --- | ---: |
| 20 context registers, automatic draw | 615.0 / 639.4 | 592.7 / 615.1 | -3.71% |
| Same stream, individual packet reads | 616.3 / 658.6 | 642.4 / 627.0 | -0.43% |
| Indexed draw | 631.9 / 657.1 | 656.7 / 632.9 | +0.05% |
| Five context registers | 248.0 / 262.1 | 246.3 / 249.7 | -2.76% |
| 25 context registers | 723.7 / 763.7 | 748.3 / 742.3 | +0.22% |

All cases reported 0.1 B/draw. Each case used five seconds of warmup and five
150-ms samples, with tiering disabled. Host drift and two trials per arm limit
inference; these numbers establish no game-FPS gain. A separate register-batch
experiment showed no improvement and was reverted before this comparison.

The subsequent v10 command-thread/serial-resolution run stopped at 238 seconds
because the harness's free-space guard tripped. It produced no stationary-room
result and cannot establish a threading performance regression or deadlock.

### Additional candidate validation

Exact resident comparison now covers recalled and rebased ranges using each
range's existing address calculation. Refusal retains the original reader and
base-word probe path. Empty-range hits avoid the comparer. Successful head
lookup also avoids repeating the identical user-data check; other variants
retain their checks. The materialization suites passed 107 tests both normally
and with in-place verification enabled. A first failing test was corrected to
honor the existing immutable-user-data input contract; its probe assertion was
retained. The synthetic comparisons below do not establish an FPS improvement.

Queued qword writes use a conservative pending destination hull only when its
arithmetic is valid. Disabled and wrapping cases retain synchronous behavior.
Twenty-two focused device tests passed, including actual producer-thread
ordering, sparse destinations, indirect packets and predication.

`SHARPEMU_GPU_RESOURCE_ORIGIN=1` retains at most 128 historical PS s28/s29 writes
and reports their packet/table origins on materialization failure. Correlation
with captured inputs and a source-word reread are diagnostics, not proof of
current ownership or permission to bypass a failed descriptor read. Forty-five
focused provenance/interpreter tests passed with the recorder enabled.

### Resident-comparison benchmark follow-up

Native ARM64 ABBA runs changed only `SharpEmu.ShaderCompiler.dll`; the tool and
all other libraries were byte-identical between arms. Tiering was disabled,
with five seconds of warmup and five 150-ms samples per case. The first run
used a harness that converted a capturing reader delegate on every call:

| Case | Control ns/op (two trials) | Candidate ns/op (two trials) |
| --- | --- | --- |
| Full walk | 1503.8 / 1522.0 | 1490.4 / 1535.6 |
| Warm hit | 130.5 / 130.2 | 122.6 / 122.4 |
| Cold miss | 4066.4 / 4064.7 | 4209.8 / 4046.9 |
| Changed descriptors | 4841.3 / 4846.0 | 4903.3 / 4851.4 |
| Eight returning tables | 231.0 / 242.2 | 156.8 / 159.0 |
| Moving descriptor ring | 389.9 / 402.5 | 313.5 / 317.8 |
| Table-write control | 63.4 / 64.0 | 63.3 / 63.7 |

The tool now retains that delegate, matching the renderer. A repeat confirms
zero allocated bytes per warm hit and returning-table hit in **both** arms;
the earlier 64 B/op was harness overhead, not an emulator allocation saving.
Cold misses report approximately 6315–6326 B/op after this harness correction.
The corrected run's timing was unstable: unchanged full-walk and table-write
controls shifted by approximately 25% and 36% between arm means. Warm hits were
140.2/139.7 ns for control and 161.6/143.6 ns for candidate; returning-table hits
were 379.1/241.0 versus 253.5/202.7 ns. These repeats do not support a reliable
speedup estimate. The comparer uses fixed synthetic native memory, without
Metal ownership checks. Neither run establishes a game-FPS improvement.

### Graphics binding candidate and runtime exclusions

`SHARPEMU_METAL_V2_GRAPHICS_BINDING_REUSE=1` opts into skipping an identical
native argument/push buffer binding within a graphics encoder. Matching includes
physical vertex/mesh/fragment stage, selector, slot, buffer and offset. Encoder
reset clears the cache even if a native pointer is reused. Compute bindings,
resource acquisition, uploads and residency declarations retain their behavior.
Cumulative attempted/skipped counters are printed per physical stage.

Six focused tests passed with zero failures or skips: three memo tests, the
existing binding-reuse scene, and control/candidate device scenes with repeated
and changed textures on one target plus an encoder restart. No real MTL mesh
stage-switch device scene was added; physical-stage isolation has pure-test
coverage. Combined Release validation passed: Metal 1,632/0/7 with binding reuse enabled; Libs 8,992/0/5; ShaderCompiler 2,192/0/1; ShaderCompiler.Metal 3,588/0/0 (passed/failed/skipped). Device suites required a device, and Libs used serial run settings. These four suites total 16,404 passed, zero failed, 13 existing skips. This run preceded the shutdown-import fix described below. The v12 runtime measurement below enables binding reuse; a same-binary disabled control remains pending.

The first v11 presentation-reuse run completed 600 seconds and controlled
shutdown, but missed phone dismissal. Its 9.975 FPS median at 475–510 seconds
is from a ceiling/phone view and is excluded from stationary-room comparisons.
Host presentation tracked guest FPS rather than repeatedly presenting at 120 Hz.
A second run stopped near 190 seconds when the disk guard detected less than
1 GiB free. A null-destination memcpy fault followed the shutdown request on the
guest RHI thread; it is a separate shutdown defect, not evidence of a gameplay
crash or a presentation-reuse regression. Neither run changes the best qualified
unprofiled room result of 13.43 FPS. The 30 FPS target remains unmet.

### Managed import shutdown safe points

The managed import dispatcher checked for shutdown after several leaf paths had
already returned. A stop request could therefore leave a guest thread executing
imports and returning to guest code during teardown. The dispatcher now checks
before selecting a leaf, and checks again after a leaf body for a stop arriving
during that call. The late check preserves pending blocks, entry exits, yields
and context transfers. Existing return-slot validation and executor lifetime
fences remain in place.

Sixteen tests covering four leaf paths, main and worker contexts, and stop
requests before or during an import all failed against the unfixed dispatcher.
With the fix, 95 focused shutdown/import tests passed with no failures or skips.
The full serial Libs Release rerun passed 9,009 tests with zero failures and five
existing Opus skips (1 minute 14 seconds). Leaf dispatch was explicitly enabled
for these runs. The fix is included in the immutable v12 runtime, whose x64 CLI
and native arm64 GPU host both published successfully; 471 payload hashes and
the source snapshot were retained.

This is a bounded managed-import fix: native constant thunks and failed return
redirects are outside its scope. It does not establish the cause of the separate
Demon's Souls read-only-destination copy fault after GPU-host termination, and
it makes no FPS claim.

### v12 observed room performance

The 600-second v12 run completed its planned stop with process exit 0 after four
seconds. Phone dismissal at 380.43 seconds was visually gated; screenshots at
407 and 497 seconds show the same shelf/lamp view used for the earlier room
measurements. Resolution remained 1920x1080. Native occlusion, label filtering,
presentation reuse and graphics binding reuse were enabled; command-thread and
parallel resolution were disabled, as were heavy CPU/GPU/memory profiles.

| Measurement, 475–510 seconds | Observed result |
| --- | --- |
| Guest FPS, 17 reports | Median **15.43**, range **11.97–15.99** |
| Host presentation rate | Median 15.43 Hz |
| Eligible graphics bindings, first-to-last PERF report | 5,401,490 attempts; 1,710,231 skipped (**31.66%**) over 32.616 seconds |
| Native binding skips by physical stage | Vertex 1,281,050; fragment 429,181; mesh 0 |
| Host memory pressure | Warn throughout all seven samples; compression and swapping active |

The earlier unprofiled v2 room median was 13.43 FPS. Different source versions,
measurement times and host conditions prevent attributing the difference to
binding reuse. This is an observed combined-build result, not an isolated cache
speedup. The higher FPS after the camera turn at 555 seconds belongs to a simpler
wall view and is excluded. **The 30 FPS target is not met.**

A separate libproc sample at 530–540 seconds recorded approximately 8.00 CPU
seconds on the Metal render worker, 9.02 on the guest audio event thread, and
5.71 on the guest RHI thread. These are thread CPU-time deltas, not stack-profile
attribution or an explanation of every lost frame. GPU interval durations are
not additive to them. The next comparison must use the same v12 binary with
binding reuse disabled before assigning a performance gain to that cache.

No resource-materialization fatal or unsolicited CPU trap appeared. The planned
shutdown logged its expected host-stop redirect/CPU_TRAP result; this does not
certify complete guest teardown. Peak reported footprint was 9,777 MiB and RSS
2,679 MiB; alias-accounted footprint is not unique physical memory. REUSE lint
also passed for all 2,856 files after the source and documentation changes.


### Audio queue query dispatch candidate

The binding-disabled v12 control exited normally after its 600-second bound,
but screenshots through 598 seconds show the phone overlay still open. It is
excluded from the shelf/lamp gameplay comparison. No isolated binding-reuse
speedup is established by that run.

A guest audio event-thread sample led to the queue-level query import. Both
normal and low-latency queue-level exports read register arguments and context
counters, write optional 32-bit outputs, and do not park the guest or invoke
guest callbacks. `SHARPEMU_AUDIO_OUT2_QUEUE_LEAF=1` routes these two queries
through the existing trivial dispatcher. It remains **off by default**. The
queue implementation, clock, device buffering, and push/advance behavior are
unchanged; this change does not insert sleeps into audio polling.

Fourteen real-dispatch comparisons exercise both exports with empty/full queues,
optional and aliased outputs, invalid pointers and missing contexts. The enabled
and disabled focused runs each passed 74 tests with no failures or skips. The
fixed-tier enabled queue-only run passed 21 tests with no failures or skips.

A synthetic measurement invokes the real managed dispatcher and queue export
through a cached delegate, with 20,000 warm-up calls per path followed by eight
alternating 200,000-call batches. `DOTNET_TieredCompilation=0` holds compilation
mode constant for this measurement; these figures are not game frame timings.

| Mode | Full gateway median | Classified query median | Allocations per query |
| --- | --- | --- | --- |
| Flag off | 365.04 ns | 368.20 ns | 40 B on both paths |
| Flag on | 369.41 ns | 84.50 ns | Full: approximately 40 B; fast: 0 B |

The within-process enabled ratio is about 4.37x. Earlier tiered timings varied
while code was recompiling and are not used for this conclusion. A cheaper poll
can allow more polling rather than reduce total thread CPU time; an FPS gain
requires a visually matched runtime comparison. Raw logs and benchmark JSON
are retained under the private runtime-diagnosis artifact directory.

The enabled full serial Release Libs rerun passed **9,024 tests, zero failures,
and five existing Opus skips** in 1 minute 14 seconds. The immutable v13 x64 CLI
and native arm64 GPU host both published successfully; 471 payload hashes and
the exact dirty-source snapshot were retained. REUSE passed all 2,857 files.


### v13 audio candidate runtime result

The enabled v13 run reached the same shelf/lamp view, with the phone overlay
visually confirmed and dismissed at 380.6 seconds. The 475–510 second window
contained 18 guest-presentation reports: **13.955 FPS median**, range
**11.99–14.95**. It completed its 600-second limit and planned shutdown with
process exit 0 after four seconds. **30 FPS remains unachieved.**

The previous v12 observation was 15.43 FPS. The only production source change
between these snapshots is the audio dispatch switch, but this is not a paired
same-binary control: host conditions and menu timing differ. All seven memory
samples again reported warning pressure; system-wide swap activity was higher
than in the v12 window. This result establishes neither a gain nor an isolated
regression. Keep the audio switch off by default.

During the separate 530–540 second sample, the audio event thread used 9.61 CPU
seconds, the RHI thread 6.35 and the Metal render worker 9.52. The audio thread
therefore remained busy despite the cheaper individual query. The sample does
not count polling frequency or prove where all that CPU time was spent.

A ten-second EventPipe sampled-thread-time trace was collected afterward,
outside both measurement windows. It repeatedly attributes stacks to shader
program/resource preparation, argument-buffer writes and completion-serial
reads. Its inclusive stack durations overlap, and its leaf attribution is
predominantly `UNMANAGED_CODE_TIME`; they must not be presented as exclusive CPU
percentages. Together with the busy render worker, this supports testing the
existing command-thread/parallel-resolution path with the newer queued qword
writes. Earlier v3–v5 negative results predate those queued-write fixes.


### v13 command-overlap failure

The v13 run with command-thread execution, parallel resolution and queued CP
writes enabled terminated after 103 seconds, before gameplay. Pixel-shader
resource materialization attempted a 16-byte read 72 bytes beyond the end of a
complete mapped range. The descriptor base was 232 bytes before that boundary;
the shader load added 304 bytes. The bounded provenance log shows that both
pointer words in the submitted direct-register packet matched the captured
shader inputs and were unchanged when reread at failure. This narrows the
investigation to the supplied pointer, its lifetime and the associated shader
state; it does not establish ownership or correct shader selection.

The same boundary signature occurred in earlier serial runs. It is therefore
not established as an overlap regression, but the overlap candidate still
fails runtime acceptance. No gameplay or FPS comparison can be claimed from
this run. The failure remains explicit; unmapped reads are not replaced with
zeroes or fabricated mappings.


### Pair-lock table publication

The sampled v13 stacks frequently contained monitor entry under argument-buffer
writes. The pair-lock table getter acquired a monitor on every read even after
its sole initialization. It now reads the published GPU address atomically;
creation still takes the existing lock and publishes only after zeroing the
table and committing residency. Its lifetime still ends at quiescent disposal.

A synthetic device test starts eight simultaneous first readers, verifies one
zeroed table with one resident allocation, and confirms later reads and a new
submission preserve its sequence words. It passes on x64 and arm64, including
both explicit-resource and residency-set modes. The final arm64 residency-mode
runs each passed one test. The full x64 Metal suite passed **1,634 tests, zero
failures, seven existing skips** before the test-only residency assertion was
expanded. An initial benchmark invocation used a relative output path and
failed to write its result; rerunning with an absolute path passed. An initial
residency-set assertion incorrectly inspected the explicit-resource list;
the corrected test checks the Metal residency set itself.

Nine one-million-read batches with tiering disabled measured these medians:

| Architecture | Original monitor getter | Published-address getter | Allocations |
| --- | --- | --- | --- |
| Native arm64 | 4.49 ns | 1.71 ns | 0 B/read |
| x64 through Rosetta | 9.66 ns | 1.92 ns | 0 B/read |

This is a small lookup improvement, not evidence that monitor contention
explains the slow frames. The uncontrolled inclusive sampled-stack durations
cannot be equated to these timings. A new immutable v14 runtime will measure
the same room and capture actual contention events outside the FPS window.
No FPS gain from this change or achievement of 30 FPS is established yet.


### v14 matched-room result and profiler limitation

The immutable v14 run reached the shelf/lamp room and completed its 600-second
limit, followed by planned termination after four seconds with process exit 0.
The phone overlay was visibly confirmed and dismissed at 431.0 seconds. The
475–510 second window contains 18 reports: **11.96 FPS median**, range
**6.97–14.50**. **30 FPS is not achieved.** The v13 observation was 13.955 FPS;
these are separate runs, not a controlled isolated performance regression.

All seven system-memory samples reported warning pressure. Their interval
totals included 26,795 swap-in pages, 3,824 swap-out pages, 1,286,241 compressed
pages and 1,238,361 decompressed pages. These are system-wide counts with
window-edge overlap, not emulator-specific paging attribution. Median reported
GPU busy time was 956.5 ms per roughly 2.03-second interval, host wait was zero,
and worker idle time was 385 ms. No resource-materialization fatal or
unsolicited CPU trap occurred; the post-stop CPU_TRAP is not a certification
of complete guest teardown. Peak reported footprint was 9,794 MiB and RSS
3,038 MiB; shared-alias footprint is not unique physical memory.

The separate 530–540 second thread measurement recorded 7.32 CPU seconds on
the GPU worker, 8.39 on the guest audio event thread and 5.43 on the RHI thread.
The subsequent CLR trace captured 353 contention starts and 353 stops; the GPU
worker accumulated only **13.96 ms** of reported monitor contention. Its sampled
stacks nevertheless attributed **1,680 ms** to monitor entry. Those sampled
durations are not evidence of equivalent lock wait or exclusive CPU cost.

The trace also recorded 4,537 paired `SuspendOther` intervals totaling 3,946 ms,
72 GC suspension intervals totaling 338.8 ms, and one GC-preparation interval
of 1.89 ms. All `SuspendOther` events came from the same thread. Sampling-related runtime
suspension is a likely explanation, but a no-sampler control is needed to
establish that attribution.
The later native sample coincided with a separate slowdown to 0–1.49 FPS and
a worker waiting for work, so it is not representative of the earlier FPS
window. Further measurement must avoid treating this heavily perturbed sample
as the steady bottleneck.

The only production source change from v13 is the pair-lock table getter. Its
small synthetic improvement remains valid; no game FPS improvement is
established. Both runtime publishes passed, all 471 payload hashes verified,
and REUSE passed all 2,858 files. The build's tracked-source diff SHA-256 is
`4260b73e0534a1c51f37388fef54cdc7cd3175ce2043fa68fb2915df4c845adc`.
The descriptor boundary failure remains unresolved. All prior commits and WIP
are preserved; this continuation made no commit or push.


### v14 overlap run without CLR stack sampling

The same v14 payload completed a second 600-second run with command threading,
parallel resource resolution, and queued command-processor writes enabled.
The phone was closed at 379.7 seconds; the shelf/lamp gameplay view was confirmed
in `shot-0427.png`, with no movement input. The 475–510 second window contains
17 reports: **13.46 FPS median, 11.97–15.45 range**. This remains below 30 FPS.
Different host conditions prevent attributing the difference from serial v14
to overlap alone. No fatal or unsolicited CPU trap appeared before the planned
stop; the process exited 0 four seconds later. The known intermittent descriptor
boundary failure is still unresolved.

Median counters per two-second interval were 92,300 draws, 3,323 passes, 778.6 ms
GPU busy, zero host wait, 277 ms worker idle, 535.2 ms command-thread synchronous
wait, and 759.5 ms queue-space wait. These include overlapping threads and must
not be summed into a frame duration. Seven system-memory samples included two
warning-pressure readings and five normal readings, 21,902 swap-in pages and
zero swap-out pages. Compression/decompression totals were 558,265/437,067 pages;
these are system-wide intervals with window-edge overlap. Peak reported
footprint/RSS was 9,864/6,146 MiB; aliases prevent interpreting footprint as
unique physical memory.

A separate ten-second trace beginning at 550 seconds enabled runtime GC and
contention events, with no CLR stack-sampling provider. It recorded 20,568
allocation ticks totaling 2,220,640,488 bytes, with the largest sampled type
attribution being `UserScalarRegisters` (291,706,192 bytes), followed by
`VertexInputInfo` (165,494,368) and `ResourceSnapshot` (155,113,072). These type
figures are sampled attribution, not exact per-type totals. There were 262 GC
suspensions totaling 924.1 ms and one preparation suspension of 1.7 ms; no
`SuspendOther` interval appeared. This strengthens the concern that the prior
stack-sampled capture was intrusive, but it is not a controlled same-workload
comparison because overlap also changed.

The next experiment targets unchanged scalar packets before copy-on-write.
It compares values, kind markers and initialized extent, preserves validation
and marker consumption, and remains opt-in until runtime measurement.


### Exact scalar-packet reuse candidate (v15)

`SHARPEMU_REGISTER_IDENTICAL_SCALARS=1` enables a pre-copy comparison for direct
pixel, geometry, hull, and compute user-scalar packets. Identical values alone
are insufficient: every kind marker must match and the stored count must cover
the packet. A successful match consumes the packet marker and preserves the
snapshot. Other shader writes, legacy per-entry marker behavior, and invalid
packets retain their original writers. This is default-off. Optional
`SHARPEMU_PROFILE_IDENTICAL_SCALARS=1` reports cumulative checked/reused packet
counts per writer thread every 262,144 eligible packets; reuse counts do not
measure bytes saved when a bank was already writable.

The focused register suites passed 78/0/0 on both x64 and native arm64 with the
feature enabled and disabled. The full x64 Libs suite with reuse enabled passed
**9,039 tests, zero failures, five existing Opus skips**. An initial test-source
build failed because `Random` resolved to a test namespace; qualifying
`System.Random` fixed it. Tests cover retained snapshots, independent mixed-write
state, changed kinds, zero writes that extend the count, packet boundaries,
empty-packet failure semantics, and legacy markers.

Seven 100,000-operation batches per case, with tiering disabled, measured a
packet write followed by a snapshot. Both arms had matching checksums. Medians:

| Host | 32-word pattern | Baseline ns/op | Reuse ns/op | Baseline B/op | Reuse B/op |
| --- | --- | ---: | ---: | ---: | ---: |
| Native arm64 | Identical | 100.55 | 18.61 | 400 | 0 |
| Native arm64 | Always changing | 101.40 | 103.08 | 400 | 400 |
| Native arm64 | Seven of eight change | 102.08 | 93.80 | 400 | 350 |
| x64 / Rosetta | Identical | 164.92 | 26.54 | 400 | 0 |
| x64 / Rosetta | Always changing | 164.85 | 178.94 | 400 | 400 |
| x64 / Rosetta | Seven of eight change | 167.57 | 160.03 | 400 | 350 |

The comparison adds cost when values change, particularly under Rosetta. These
synthetic savings do not establish a gameplay improvement. The next room run
uses native arm64 GPU processing and the same overlap settings as v14.
The immutable v15 tracked-source diff SHA-256 is
`3e93b3efa1114171d16c88fd03ffb9ad4770fd70f855d33c8138a6a7afe8531b`;
both Release publishes passed and all 471 payload hashes verified.

The full x64 Metal device suite in the same overlap mode, with scalar reuse
enabled, passed **1,634 tests, zero failures, seven existing skips**. REUSE
passed all 2,860 files. Provenance comparison confirms all earlier production
patches and untracked source hashes are preserved; the v15 production delta
is limited to `RegisterWriteTable.cs` and its new scalar-reuse partial file.
No commit was rewritten, created, or pushed in this continuation.


### v15 room verdict: reject enabling scalar reuse by default

Run `20261027-145800-silent hill short message.zar-room-scalar-reuse-v15-on-1`
reached the matching shelf/lamp view (`shot-0471.png`). The phone was closed at
432.75 seconds. In the 475–510 second window, 18 reports measured **12.995 FPS
median, range 9.48–15.99**. The preceding v14 overlap observation was 13.46 FPS;
these are not sufficient for an isolated regression claim. **30 FPS remains
unachieved.**

The candidate reused only **18,994 of 2,359,296 eligible scalar packets
(0.805%)** between its 475.352 and 509.634 second reports. Its large synthetic
best-case saving does not apply to most writes in this scene. Keep the feature
**disabled by default**; further scalar work must reduce the cost of writes
that actually change state, rather than relying on identical values.

All seven memory samples in the FPS window showed warning pressure. System-wide
interval totals were 9,551 swap-in pages, 2,992 swap-out pages, 760,250 compressed
pages, and 636,199 decompressed pages. These include window-edge overlap and
cannot be attributed solely to the emulator. Median counters per two-second
interval were 91,157 draws, 3,279 passes, 750.2 ms GPU busy, zero host wait,
357 ms worker idle, 519.0 ms command-thread synchronous wait, and 721.1 ms
queue-space wait. These overlapping counters are not additive frame times.

The 530–540 second CPU measurement recorded 9.73 CPU seconds on the guest audio
event thread, 6.76 on the RHI thread, 7.46 on the GPU worker, 4.22 on program
resolution, and 3.43 on the command stream. The separate trace beginning at
550.02 seconds used the same GC/contention providers as the v14 overlap trace,
with no CLR stack sampler. It recorded 20,507 allocation ticks totaling
**2,194,898,680 bytes**. Sampled type attribution remained led by
`UserScalarRegisters` (316,529,552 bytes), `ResourceSnapshot` (165,137,720), and
`VertexInputInfo` (162,523,712). These are statistical type attribution, not exact
per-type totals. The near-unchanged overall allocation volume supports the
low-reuse-rate verdict; it does not establish a small allocation improvement.

The trace contained 262 GC suspensions totaling 1,127.15 ms, median 2.88 ms,
maximum 226.83 ms, plus one 2.32 ms preparation suspension. There were no
`SuspendOther` intervals. The long outlier and different memory pressure
prevent assigning this timing difference to scalar reuse. Runtime contention
starts/stops were paired at 4,194 each.

A late native game sample at 575.21–580.04 seconds captured only three stack
observations per named thread and coincided with two zero-FPS reports. The
subsequent GPU sample overlapped recovery. Neither sample is accepted as a
representative steady-state CPU profile; both are outside the FPS window.
The game reached the 600-second limit, then exited 0 after the planned stop.
No fatal or unsolicited CPU trap occurred. The first shutdown CPU_TRAP was at
600.727 seconds. Peak reported footprint/RSS was 9,806/6,283 MiB. Both game
processes and all capture helpers are gone.

The next allocation investigation should address objects created for changing
draws: snapshot state, resource snapshots and deferred work lifetimes. Queue
items cannot simply be recycled when `Done` is set: synchronous producers may
still read their result or failure, and pending writes/ranges may retain item
references. Those ownership constraints must be proved before pooling.


### v16: bounded reuse of asynchronous command items

The v15 GC trace attributed about 139 MB of its sampled allocation volume to
`DeferredCommandThread.Item`. The v16 candidate adds an opt-in, 64-entry return
ring (`SHARPEMU_COMMAND_ITEM_REUSE=1`, off by default). The render consumer returns
only asynchronous items after execution, pending-write/range removal and its last
item read. The interpreter producer acquires them for subsequent work. Synchronous
callbacks keep their original allocation and result ownership; failures are not
returned to the ring. Cached items clear retained payload references.

The two writers use separate cursor cache lines and cache the opposite cursor
until their current batch is exhausted. An initial version that read the opposite
cursor for every item increased the native two-thread median from about 540 to
591 ns/item; it was superseded before runtime testing. This is why a same-thread
allocation benchmark alone is insufficient for this queue.

The final synthetic comparison disables tiering, alternates baseline/candidate
order, uses eight 200,000-item trials per same-thread batch size, and six
100,000-item trials with real producer/consumer threads. Median results:

| Architecture | Queue mode | Baseline ns/item | Reuse ns/item | Baseline B/item | Reuse B/item |
|---|---|---:|---:|---:|---:|
| Native arm64 | Same thread, batch 1 | 30.28 | 26.08 | 80 | 0 |
| Native arm64 | Same thread, batch 16 | 19.54 | 16.04 | 80 | 0 |
| Native arm64 | Same thread, batch 64 | 18.79 | 15.44 | 80 | 0 |
| Native arm64 | Two threads, batch 64 | 389.97 | 354.52 | 80.0008 | 0.0260 |
| Rosetta x64 | Same thread, batch 1 | 58.92 | 52.86 | 80 | 0 |
| Rosetta x64 | Same thread, batch 16 | 38.63 | 33.61 | 80 | 0 |
| Rosetta x64 | Same thread, batch 64 | 41.47 | 32.74 | 80 | 0 |
| Rosetta x64 | Two threads, batch 64 | 506.83 | 437.00 | 80.0008 | 0.0136 |

The focused deferred-command selection passed **12 tests, zero failures, zero
skips**, including mixed work ordering, pending write overlays, copied write
payloads, ring wrapping on two threads, synchronous exception identity,
asynchronous failure handling and bounded retention. Native arm64 and Rosetta
benchmark runs passed. These synthetic results establish reduced queue-item
allocation; they do not establish a gameplay FPS improvement.

Full integration with item reuse enabled passed **9,047 Libs tests, zero
failures, five existing skips**, then **1,634 Metal device tests, zero failures,
seven existing skips**. Metal used the command-thread, parallel-resolution and
queued-command-write settings intended for the room run. The commands and
complete output are preserved in `queue-recycle-v16-libs-on.log` and
`queue-recycle-v16-metal-on.log` under the private diagnosis artifact directory.
The v16 tracked-source diff SHA-256 is
`57374ddf9e0258002e70130c91acd5c873bda2bb054dd6069b3e10ebebeae77b`.
Comparison against v15 confirms all earlier production patches and untracked
source hashes are preserved; only `DeferredCommandThread.cs` and its new
recycling partial changed in production code. HEAD remains `52f07c7b3`.


The first v16 run, `20261027-154622-silent hill short message.zar-room-queue-recycle-v16-on-1`,
reached the shelf/lamp view at 468 seconds, but its camera moved away during the
475–510 second measurement window. That window is **rejected for matched FPS
comparison**. Its 23.45 median and 34.39 maximum do not establish the 30 FPS
goal or an improvement over the prior room results. The later GC/CPU capture
also covers a different view and is not a matched allocation comparison.
The trace recorded 875,663,072 allocation-tick bytes and 98 GC suspensions
(457.40 ms total), but workload differences prevent attributing the reduction
to item reuse. The run reached its 600-second limit and exited 0 after the
planned stop; peak footprint/RSS was 9,837/5,490 MiB.

### v17: isolate scripted input for repeatable runtime measurements

Inspection showed `WindowHostInput` merges scripted keys with physical keyboard
and controller input. `SHARPEMU_INPUT_SCRIPTED_ONLY=1`, together with
`SHARPEMU_INPUT_FILE`, now permits controlled runs to consume only scripted keys.
The mode also suppresses physical controller discovery and outputs. Without both
settings, interactive input follows the existing path. This is a measurement
reliability change, not an FPS optimization. The camera movement's exact source
was not recorded; the first v16 run remains rejected regardless of that source.

The input selection passed all **82 pad/input tests, zero failures, zero skips**,
including existing physical-input forwarding and the new exclusive-mode check.
Both v17 Release publishes passed; all 471 payload hashes verified. Its
tracked-source diff SHA-256 is
`6937a2a83673811c1e9b58a2dedaabdc68fd6eb50e6839d48925170b7924e468`.
The production delta from v16 is solely `WindowHostInput.cs`; the renderer and
queue candidate are unchanged. Existing commits and earlier production patches
remain preserved.


The isolated-input run
`20261027-160143-silent hill short message.zar-room-input-isolated-v17-on-1`
reached the phone later, which was closed at 485.05 seconds. Therefore the
original 475–510 second window is rejected: it includes the phone and transition.
Screenshots 0499, 0510 and 0520 confirm the stationary shelf/lamp view. A later
**500–525 second observation measured 4.5 FPS median, range 0–10.99, 13 reports**.
It is not an identical elapsed-time comparison with v14/v15. **30 FPS remains
unachieved; queue reuse is not accepted as a gameplay improvement and remains
disabled by default.**

All five memory samples in that later window reported warning pressure.
System-wide interval totals were 18,520 swap-in pages, 31,740 swap-out pages,
2,045,519 compressions and 1,905,837 decompressions. These overlapping intervals
cannot identify emulator-only traffic. Median two-second counters were 20,916
draws, 768 passes, 404.4 ms GPU busy, zero host wait, 701 ms worker idle,
492.9 ms command-thread synchronous wait and 651.6 ms queue-space wait. These
are overlapping intervals, not additive frame times.

The later 530–540 second CPU sample recorded 7.10 CPU seconds on the audio event
thread, 4.03 on RHI, 4.61 on the GPU worker, 2.36 on program resolution and 2.05
on the command stream. The separate GC/contention capture at 550.02–560.93
seconds recorded 11,503 allocation ticks totaling 1,232,101,880 bytes.
`Item` accounted for only 12 ticks / 1,291,376 sampled bytes, consistent with
queue-item reuse working, but total allocation and GC figures must be normalized
to comparable work before inferring performance. There were 147 GC suspensions
totaling 659.68 ms (median 4.08, maximum 12.23), one 2.53 ms preparation
suspension, no unpaired starts, and 4,247 paired contention starts/stops.

The run reached its 600-second limit, then exited 0 after the planned stop.
The first CPU_TRAP was at 600.338 seconds during shutdown; no earlier fatal or
resource-materialization failure appeared. Peak footprint/RSS was 9,888/4,298
MiB. The next required comparison is the **same immutable v17 build** with
scripted-only input retained and `SHARPEMU_COMMAND_ITEM_REUSE=0`. This must
precede attributing the slowdown to either item reuse or host pressure.


### 2026-10-27: reject unmatched v17 controls; host scheduling confounder

The queue-reuse-off v17 control at `20261027-161900...room-input-isolated-v17-off-1`
ended normally at 600 seconds but remained on the Continue screen (`shot-0597.png`).
Its CPU/GC captures are not gameplay comparison evidence. The repeat at
`20261027-163109...room-phase-v17-off-2` used the same 471 verified v17 payloads,
scripted-only input, queue reuse off, unchanged 1080p settings, and render phase
profiling. It ended normally after its 900-second bound but remained in the
opening cinematic (`shot-final.png`). Neither establishes a queue-reuse FPS
verdict or progress to 30 FPS.

A read-only host census found an unrelated lint process using approximately
4795 MiB of per-process footprint and 237.9% CPU during startup. Later kernel
thread snapshots at elapsed 842.79–852.97 seconds found maximum scheduler priority
4 for all 92 observed emulator and all 50 GPU-host threads; all 139 observed
GeForce NOW threads had maximum priority 63. `ps` reported process priority 97
for GeForce NOW and 4 for both emulator processes. Both emulator processes had
successfully acquired their macOS activity assertions, so missing activity
assertions are not supported as the explanation.

The system's `gamepolicyd` log recorded Game Mode enabled/status on at local
16:36:37, alongside the full-screen gaming session. Apple documents that Game Mode
prioritizes the selected game's CPU/GPU access and reduces background activity:
https://support.apple.com/en-ie/105118 . This is a strong scheduling confounder,
not yet a measured causal FPS improvement. No other application was stopped or
its settings changed. The user explicitly declined a temporary Game Mode-off comparison; GeForce
NOW and its settings must remain unchanged. Continue independent code and
synthetic work, and qualify runtime results under the observed scheduling limits.

Raw evidence remains in ignored run artifacts: `scheduler-priorities.txt`,
`thread-cpu-deltas-0842.json`, `host-footprints-0172.json`,
`host-footprints-0469.json`, `unqualified-room-result.json`, and
`runtime-diagnosis-20261026/gamepolicy-live-20261027.log`. Per-process footprints
can double-count shared mappings and must not be summed as unique physical RAM.


### Depth-target memo collision recovery

`ImageRequestBuilders.DepthTarget` hashed `ZReadBase ^ ZWriteBase` at the same
shift. Normal equal read/write bases therefore removed all address information;
alternating surfaces with equal depth control replaced the same cache entry.
The new regression reproduced 516 format queries for four warmed targets plus
512 lookups, where only the initial four queries were necessary.

The cache now mixes different address bits and keeps four exact states in each
of eight buckets. Full `DepthTargetWords` equality and device identity still
guard every hit. The uncached trace path, the `WorkerScratch` fallback, copy-mode
requests, and copy-on-write mip layouts retain their existing behavior. This is
the only new production delta in this continuation; all earlier source changes
and HEAD `52f07c7b37570adaad7317b8196a86f9bb4d6548` remain preserved.

The isolated synthetic comparison used identical test files and dependencies,
swapping only `SharpEmu.Libs.dll` between the verified immutable v17 publication
and the candidate. Seven batches of 100,000 calls per case ran with tiering off.

| Runtime / targets | Before wall ns | After wall ns | Before bytes | After bytes |
| --- | ---: | ---: | ---: | ---: |
| Native ARM64 / one repeated | 55.15 | 37.06 | 0 | 0 |
| Native ARM64 / four alternating | 174.88 | 51.93 | 136 | 0 |
| Rosetta x64 / one repeated | 47.14 | 50.91 | 0 | 0 |
| Rosetta x64 / four alternating | 351.10 | 63.78 | 136 | 0 |

Native per-thread CPU medians for four alternating targets were 171.11 to
47.47 ns/op. The raw Rosetta CPU-clock readings did not qualify as nanoseconds
and are excluded; subsequent benchmark code emits CPU time only on native ARM64.
The x64 repeated-target wall case increased by 3.77 ns, approximately 8%.
Scheduling and frequency variation remain limitations. These are cache lookup
measurements, not gameplay FPS or proof that depth requests explain the previous
2 FPS report. Exact allocations and format-query counts confirm the eliminated
rebuilds independently of timing.

Validation: 78 focused image/request/memo tests passed. The original regression
failed before the fix and passed afterward; it covers different target addresses,
four states sharing one address, device identity, changed dimensions and clear
state, inactive targets, and independent copied layouts. Pipeline-lookahead
rechecks passed 4/4 on both reference and candidate. REUSE passed 2863/2863.

Full Libs validation did **not** complete: the 600-second bounded run recorded
one pipeline-lookahead failure and four isolated native-return worker timeouts
(30 seconds each) before its outer timeout. There is no full-suite total or green
integration claim. All descendant test processes were verified gone afterward.
The four native-return cases have not been compared against the reference in
this continuation; the passing isolated lookahead reruns do not erase its full-run
failure. The candidate has not been republished into a game bundle or measured
in the stationary room. The 30 FPS goal remains unachieved.

Private evidence: `depth-memo-preservation.json`, `depth-memo-before-test.log`,
`depth-memo-after-test.log`, `depth-memo-cpu-comparison-provenance.json`,
`depth-memo-cpu-summary.json`, `depth-memo-full-libs-result.json`, and
`depth-memo-reuse.log` under the runtime-diagnosis artifact directory.

### Native-worker follow-up with GeForce NOW unchanged

The four native-worker cases that timed out in the full Libs attempt were rerun
serially on x64, using the same isolated test payload and swapping only the Libs
assembly. The immutable v17 reference passed **4/4**, and the depth-cache candidate
passed **4/4**, with no failures or skips. Whole invocations took 94.87 and 90.81
seconds respectively; individual parent tests took 15–20 seconds against their
unchanged 30-second child-process deadline. `DOTNET_HOST_PATH` and `DOTNET_ROOT`
explicitly selected the x64 runtime for both arms.

A direct candidate continuation worker then passed **1/1**. Its adapter reported
about 3 seconds in the test body and 15.80 seconds for the whole test invocation;
discovery completed at adapter timestamp 7.32 seconds. This demonstrates material
runner/startup overhead. It does not establish that startup alone caused the
earlier timeouts, nor exclude a full-suite order or resource interaction. The
earlier incomplete full run and its five observed failures remain recorded.

No emulator code, timeout, rendering option, GeForce NOW setting, commit, or game
bundle changed during this follow-up. No game was launched. The 30 FPS goal is
still unachieved. Evidence: `depth-memo-native-recheck.json`,
`depth-memo-native-reference.log`, `depth-memo-native-candidate.log`, and
`depth-memo-native-direct-worker.log` in the runtime-diagnosis artifact directory.


### Current combined source: full Libs recovery and v18 diagnostic

The subsequent full x64 Libs rerun completed: **9,052 passed, zero failed,
five existing Opus skips** (9,057 total; 269.61 seconds for the invocation).
It used the unchanged depth-cache candidate, explicit x64 `DOTNET_HOST_PATH`
and `DOTNET_ROOT`, serial runsettings and a 120-second hang detector. The
previous timed-out invocation and its five failures remain historical evidence;
the successful rerun does not establish their cause.

The tested Libs SHA-256 is
`552394e3993e9ff7572c3d0060a67676fa9938f89ef0e1ca072c972d4e6a071f`;
the test assembly is
`3ca3a592afc07d5673ac329122252172f2e8edf3037d41144939820f4d4b9b36`.
Release self-contained x64 CLI and native arm64 GPU-host publications both
succeeded. All 471 v18 payload hashes were checked before launch. This build
includes the depth-cache change and uses the same source snapshot as the tests.

Run `20261027-173845-silent hill short message.zar-room-phase-v18-1`
reached the first room. Screenshots at 478 and 508 seconds confirm the stationary
shelf/lamp view with the phone closed. Rendering remained 1920×1080, scale 1,
VSync on. Native occlusion, command threading, parallel resolution and queued CP
writes were enabled; command-item reuse and identical-scalar reuse were disabled.
Scripted-only input prevented external controller movement. GeForce NOW and all
its settings remained unchanged.

In the 475–510-second window, **17 guest-presentation reports had a median of
6.98 FPS, range 0.0–12.48**. This instrumented observation is slower than the
roughly 14-FPS observations above; it is not an isolated depth-cache regression
or an acceptance result. Six of seven nearby memory samples reported warning
pressure. System-wide interval deltas included 30,245 swapped-in and 62,172
swapped-out pages, plus 1,763,427 compressions and 1,613,994 decompressions.
Those counters include other processes and interval-boundary overlap.

Fifteen complete render-profile intervals contained 229 frames over 31.8
reported seconds. A report ending inside the window but beginning before it
was excluded rather than attributed to the whole window. Exclusive render-thread
wall time per reported frame was:

| Phase | ms/frame |
| --- | ---: |
| Program-prefetch wait | 36.13 |
| Idle | 33.12 |
| Draw-resource preparation | 8.84 |
| Command stream | 6.07 |
| Graphics-state application | 5.35 |

These include instrumentation and scheduling delay. They are not CPU utilization;
background program-resolution time overlaps prefetch wait and must not be added.
A separate thread CPU sample at 580.94–590.94 seconds observed 7.95 CPU-seconds
in the guest audio-event thread, 4.71 in RHI, 5.98 in the GPU worker, 2.81 in
program resolution and 2.50 in the command thread. These later samples are not
aligned with the FPS window. All sampled emulator/GPU-host threads had maximum
scheduler priority 63, so the earlier priority-4 observation did not persist.

The run stopped at its requested marker around 717 seconds; process and harness
exit codes were zero. The CPU_TRAP result occurred after the logged host-stop
redirect, not before the measurement. Guest teardown remains unverified. Peak
main-process footprint/RSS was 9,856/3,780 MiB. Shared mapping charges mean that
main-process and GPU-host footprints must not be summed as unique physical RAM.
No 30-FPS result is established. Next work should separate memory residency and
background program-resolution costs while keeping other applications unchanged.

Private evidence: `depth-memo-full-libs-recheck.json` and its TRX, v18 build
logs/source snapshot/payload manifest, plus `room-fps-475-510.json`,
`render-phases-475-510.json`, `room-memory-window.json` and the thread-CPU
capture in the run directory. Raw logs, runtime bundles and game-derived assets
are not part of this PR.


### PR publication validation

The accumulated source package was rechecked before committing it to PR #23:

| Suite | Passed | Failed | Existing skips |
| --- | ---: | ---: | ---: |
| Libs, x64, serial | 9,052 | 0 | 5 |
| Gpu.Metal, required device, command overlap and queued CP writes | 1,634 | 0 | 7 |
| ShaderCompiler, x64, serial | 2,192 | 0 | 1 |
| ShaderCompiler.Metal, required device, serial | 3,588 | 0 | 0 |
| Total for these four suites | 16,466 | 0 | 13 |

The last three invocations rebuilt their Release dependencies. All used
`docs/performance/shader-serial.runsettings` and `--blame-hang-timeout 120s`;
`SHARPEMU_TEST_REQUIRE_DEVICE=1` was scoped to the two Metal suites. Commands,
TRX results and elapsed times are retained in `pr23-current-validation.json`
and `pr23-current-results` under the private diagnostic directory. The Libs
rerun provenance is recorded above. This total does not reclassify older failed
or incomplete attempts, and is not a rerun of every project in the solution.
REUSE passed **2,863/2,863** files. The staged whitespace check found and removed
one trailing blank line from the new scalar-constant MSL template; that final
formatting-only difference was made after the suite build. Main remained clean.
The publication contains source, tests, benchmark tools and this report, without
raw commercial captures, game binaries or local runtime bundles.


### v19 defaults diagnostic: cold introduction, no matched room result

Clean source `fb717072ad11ada9a53e27ac34219fdc91f1f5bb`, self-contained
x64 CLI/native ARM64 GPU host, with 471 verified runtime payload hashes.
Run `20261027-183237-silent hill short message.zar-room-defaults-v19-1`
used a fresh clone of `runtime-data-world-unprofiled-1`, all nine newly promoted
switches unset, and the existing default portable-buffer/LDS/library-deferral
policies. Prewarming was disabled for measurement; resolution, scale, VSync and
GeForce NOW were unchanged. Initial host load average was 13.16.

The title menu was visually verified at 350 seconds. New Game was selected at
367 seconds; a long warning/loading phase followed, with zero-frame intervals
and Metal pipeline/library compilation visible in the GPU-host native sample.
Short confirmation pulses during stalled frames did not immediately advance
the warning. After a later confirmation the introduction advanced, and the
opening cutscene was visually verified at 832 and 872 seconds. The 900-second
cap ended the run before the stationary shelf/lamp room. Thus no FPS comparison
against the earlier room windows is valid. Process exit was 0 after a controlled
four-second termination; this does not certify guest teardown or full-game
compatibility. Main-process peak footprint was 9,460 MiB and peak RSS 2,095 MiB.

The main-process native sample had only ten snapshots and is insufficient for
precise CPU attribution. Separate per-process unique-memory captures at
514.53–515.33 seconds recorded main task footprint 5,050,704,280 bytes and
compressed bytes 3,900,620,800. The helper attributed 4,155,510,784 logical
swapped/compressed bytes to stack/guard objects. This is a diagnostic lead,
not proof that reducing stack reservations is safe or would improve FPS.
The guest shared objects appear in both process maps and must not be added
together. Native samples and VM-object walks were outside any accepted FPS
window. Raw logs, samples and captures remain private diagnostic artifacts.


A follow-up synthetic check rejects treating that stack-region number as direct
physical footprint. Eight idle managed threads with 64-MiB reservations used
only 0.406 MiB of stack-resident pages on ARM64 and 0.516 MiB under Rosetta x64;
zero-thread and 4-MiB controls were also captured. After a one-second native
`sample`, the x64 VM-object walk reported **523.211 MiB** of stack-resident pages,
while task footprint changed only **13.233 → 13.339 MiB** (RSS 27.590 → 27.613
MiB). ARM64's stack count did not change. All six probe processes exited 0.
Thus native sampling can strongly alter this Rosetta VM-object accounting;
the stack-category count cannot establish equivalent process-owned physical
memory or explain the game's FPS. The 64-MiB reservation remains unchanged.
Private source/results: `stack-reservation-probe/Program.cs`,
`run-stack-reservation-probe.py`, `stack-reservation-probe-results.json`.

### Avoid redundant user-config and ordered-append copies

Indexed triangle-list mesh draws already have the topology the resolver needs.
They now reuse the read-only user-config snapshot instead of deep-copying it.
Indexed triangle strips still get a private topology-conversion copy; tests
check source topology and independent ordered-append storage. Other topologies
and nonindexed paths keep their existing behavior.

`OrderedAppendRegisters.Copy` now constructs directly with its cloned counter
array. The former public-constructor/object-initializer sequence allocated an
empty eight-counter array and immediately replaced it with the clone. The new
path still owns every counter and preserves all register values.

Nine alternating-order rounds, 100,000 copies per arm after 10,000 warmup copies,
with matching checksums and allocation measured on the executing thread:

| Runtime / configuration | Reference ns/copy | New ns/copy | Reference B/copy | New B/copy |
| --- | ---: | ---: | ---: | ---: |
| ARM64, tiering disabled | 33.331 | 28.991 | 208 | 120 |
| Rosetta x64, tiering disabled | 58.851 | 51.310 | 208 (one round +0.00056) | 120 |
| Rosetta x64, default tiering, full-suite run | 61.755 | 46.973 | 208 | 120 |

These are component medians: about 13% less time with tiering disabled and 88
fewer allocated bytes per counter-bank copy. They are not a game-FPS result.
The complete Release Libs run passed **9,060 / 0 / 5 existing skips**. Both
architecture-specific benchmark executions passed separately, using the same
compiled test/Libs assemblies. No Metal/shader source changed in this follow-up;
the earlier default-configuration device suites retain their own attribution.

Reproduce the opt-in benchmark after building Release Libs tests:

```sh
SHARPEMU_ORDERED_APPEND_BENCH_OUTPUT=/tmp/ordered-append-copy.json \
DOTNET_TieredCompilation=0 dotnet test \
  tests/SharpEmu.Libs.Tests/SharpEmu.Libs.Tests.csproj \
  -c Release --no-build --no-restore \
  --settings docs/performance/shader-serial.runsettings \
  --filter FullyQualifiedName~MeasureOrderedAppendCopiesWhenRequested
```

Use the matching ARM64 or x64 `dotnet` host. JSON includes the actual process
architecture, each round's checksum, time and allocation. Raw records remain in
`ordered-append-copy-{arm64,x64}-notier.json` and `ordered-append-copy-x64.json`.


### v20 warmed defaults: stationary room and CPU attribution

Clean source `eabcd9f659fcb218dd00bf8f8112f2628b4c48e8` passed both
self-contained publishes (x64 CLI, native ARM64 GPU host) and verification of
471 runtime payload hashes. Run
`20261027-190045-silent hill short message.zar-room-warm-defaults-v20-1`
used a fresh clone of v19's warmed user data. All nine promoted switches were
unset; background program resolution retained its existing default on, and
command-stream threading retained its default off. Portable buffers, LDS
threshold and deferred-library policy stayed at defaults. Detailed render-phase
and GPU-pass profiling were on; prewarming was off. Resolution stayed
1920×1080, scale 1, VSync on, and GeForce NOW was unchanged.

The shelf/lamp room was visually verified after closing the phone; no movement
was sent during the accepted 375–410-second window. Seventeen guest-FPS reports
had median **11.95**, range **7.99–14.96**. This does not isolate the copy fix,
the default promotions, profiling overhead or host-load differences against
the earlier approximately 14-FPS run. **30 FPS remains unachieved.**

Sixteen complete render-profile intervals covered 379 reported frames in
32 seconds. Leading GPU-worker self wall times per frame were:

| Phase | ms/frame |
| --- | ---: |
| Program-prefetch wait | 12.311 |
| Command register writes | 7.338 |
| Command-stream interpretation | 7.332 |
| Draw-resource preparation | 7.273 |
| Label-copy encoding | 4.741 |
| Graphics-state application | 3.737 |

These are instrumented wall times, including scheduling and wait costs. They
are not hardware CPU utilization. The window contained 7,123,581 register-write
phase entries, so per-scope instrumentation also warrants an unprofiled control.

GPU-pass records contained 55,906 passes across 408 frame groups. Excluding
first and last groups, median summed encoder duration was **19.251 ms/frame**
(mean 19.508). Encoder durations can overlap and are not exclusive frame wall
time; they must not be added to CPU phase durations. The largest recurring
compute groups averaged 1.566 and 1.546 ms, and the leading fullscreen render
encoder averaged 1.115 ms. This room does not show the original large
shadow/volume encoder as its dominant single cost.

A separate ten-second thread-CPU capture at 469.759–479.765 seconds, outside
the accepted FPS window, recorded **8.943 CPU-seconds** on the native GPU
worker and **2.897 CPU-seconds** on its program resolver. The guest audio event
thread used 9.686 CPU-seconds and RHI thread 6.067. Combined with the phase
profile, this supports investigating CPU draw processing and resolver handoffs;
it does not prove a particular optimization's gain. No native stack sampling
was used during this run.

The run stopped at its own marker after 711 seconds, followed by controlled
four-second termination with process and wrapper exit 0. Peak main-process
footprint was 9,807 MiB and RSS 3,314 MiB; these are not combined physical usage
for both emulator processes. No full-game or teardown-correctness claim follows.
Private evidence includes `room-stationary-fps.json`, `render-phases-375-410.json`,
`pass-analysis-after-375s-before-410s.json` and `thread-cpu-deltas-0469.json`.


The same immutable v20 payload was then run with render-phase and GPU-pass
profiling disabled, using a fresh clone of the preceding run's warmed user data:
`20261027-191250-silent hill short message.zar-room-unprofiled-defaults-v20-2`.
The repeated title confirmations stopped at 132 seconds; cutscene skip finished
at 139 seconds and phone-close input at 162 seconds. The same stationary
shelf/lamp view was verified at 176 and 246 seconds, without movement.

The preselected 205–240-second window contained **17 reports, median 15.48 FPS,
range 13.96–16.97**. The render/pass profiles were disabled; ordinary presentation
counters and harness screenshots remained enabled. All nine promoted switches
remained unset and all rendering/application constraints were unchanged. This
is a useful normal-settings control, but sequential cache warmth and changing
host load prevent attributing the full difference from 11.95 FPS to profiling.
It is also not an isolated before/after test of the copy fix or default changes.

A separate thread-CPU capture at 259.940–269.945 seconds recorded 6.533 CPU-seconds
on the GPU worker, 2.523 on program resolution, 6.051 on guest RHI and 9.122 on
the audio event thread. The worker remains a substantial CPU cost even without
detailed phase instrumentation, while guest-side command production also needs
investigation. The result remains below 30 FPS. The profiler-off capture and
`thread-cpu-deltas-0259.json` are retained alongside the raw run records.

The control stopped at its own marker after 274 seconds; process and wrapper
exited 0 after four seconds of controlled termination. Peak main-process
footprint was 8,901 MiB and RSS 4,395 MiB. Both emulator processes were gone
afterward; this is bounded-run cleanup, not full-game compatibility evidence.

The only CPU-trap lines in the profiler-off capture occur at 275.367 seconds,
after the 274-second stop marker. They remain recorded as host-stop behavior;
process exit 0 does not establish a clean guest teardown.


### v20 serial program-resolution control

The same `eabcd9f65` payload and the same warmed seed directory as the unprofiled
default run were used for
`20261027-192111-silent hill short message.zar-room-unprofiled-serial-resolve-v20-3`.
The captured environment differed only in the clone's destination path and
`SHARPEMU_METAL_V2_PARALLEL_RESOLVE=0`. Rendering, prewarm, profiling and other
application settings were unchanged. The stationary shelf/lamp view was verified
at 196 and 246 seconds.

The same 205–240-second window had **17 reports, median 10.99 FPS, range
6.49–14.47**, versus 15.48 (13.96–16.97) with background resolution enabled.
Sequential host conditions still limit causal precision, but this control gives
no reason to disable the existing default. Thirty FPS remains unachieved.
A separate 271.936–281.946-second CPU capture recorded 7.253 CPU-seconds on the
GPU worker, 5.360 on guest RHI and 8.565 on the audio event thread. There was no
background program-resolver thread in this arm.

Ten-second managed stack traces followed, outside the FPS/CPU windows, for the
GPU host and guest process. They completed and converted successfully. Their
leaf samples were dominated by `UNMANAGED_CODE_TIME`, with managed anchors at
monitor entry, GC polling, completion-event reads and resource rebasing. These
are sampled wall-time anchors, not exclusive CPU costs or proof of contention.
Earlier v14 event traces already showed why this distinction matters: actual
worker contention was 13.96 ms while sampled monitor-entry attribution was
1,680 ms. No lock or GC-policy change is justified by these stack totals.

This control stopped at its own marker after 390 seconds; the CPU-trap lines
appeared at 391.201 seconds, after that stop. Process/wrapper exit 0 followed
four-second controlled termination. Peak main-process footprint was 8,877 MiB
and RSS 3,818 MiB. Both emulator processes exited; no full-game or clean guest
teardown claim follows.

### Completion-event reads after all submitted work finishes

The host already keeps the greatest completion-event value it has observed.
It now also publishes an upper bound on submitted serials immediately before
each native commit. When the observed completion reaches that bound, the lower
and upper bounds agree: an additional native event read cannot reveal more
completed work. A recorded but unsubmitted command buffer cannot advance the
event. If submitted work remains outstanding, the getter still reads the live
event and updates the monotonic hint. Concurrent readers can observe a valid
earlier completion snapshot, just as a native read racing submission can.
`SHARPEMU_METAL_V2_SERIAL_HINT=0` retains the native-query reference path.

A device regression covers three successive externally gated submissions,
checks that recording and committing do not publish completion early, releases
each gate, then verifies the completed serial and GPU-written word in both
reading modes. The benchmark measures the actual getter on the same host after
its work finishes: 10,000 warmup pairs, nine alternating-order rounds of
1,000,000 reads per arm, and equal serial checksums. A second condition holds
a real GPU submission behind an external event, exercising the live-query
fallback while the completion hint is stale.

| Completion condition | Native query, median ns/read | Guarded getter, median ns/read | B/read, both |
| --- | ---: | ---: | ---: |
| ARM64, completed | 3.020 | 1.319 | 0 |
| ARM64, pending | 3.241 | 2.981 | 0 |
| Rosetta x64, completed | 10.002 | 2.297 | 0 |
| Rosetta x64, pending | 5.864 | 6.209 | 0 |

The x64 pending-work arm costs about 0.346 ns/read (5.9%) more; it does not take
the shortcut. The completed-work arm saves 7.705 ns/read in that harness. These
component medians do not establish the runtime hit ratio or an FPS gain. The
preliminary idle-only harness used 200,000 reads and a different loop shape;
its raw results are preserved separately rather than mixed with this table. The native getter's
measured nanosecond cost also cautions against interpreting its sampled wall
time as a large exclusive CPU cost. Fresh Release compilation and the focused
ARM64 device run passed **52/0/0**. The full x64 Metal suite using those rebuilt
assemblies passed **1,637/0/7 existing skips**. Shader/compiler/Libs production
code did not change in this follow-up, so their earlier suites were not repeated.

Reproduce the optional benchmark with
`SHARPEMU_COMPLETION_SNAPSHOT_BENCH_OUTPUT=/absolute/path/results.json` and
`SHARPEMU_TEST_REQUIRE_DEVICE=1`, filtering Release Metal tests to
`MeasureCompletedSnapshotWhenRequested`. JSON records architecture, iterations,
every timing/allocation round and checksum. Command/TRX artifacts are retained
in `completion-snapshot-validation.json` and `completion-snapshot-results`;
timing files are `completion-snapshot-focused-arm64.json` and
`completion-snapshot-full-x64.json`. The runtime comparisons above predate this
getter optimization; the v21 captures below exercise it without establishing an
isolated FPS effect.

Additional focused controls passed **52/0/0 each** with serial hints disabled
and with polling completions instead of native completion callbacks. These are
configuration executions of existing tests, not 104 distinct new tests. REUSE
passed **2,865/2,865** files, and whitespace checks passed.

### v21 runtime and GC-budget control

Both Release publishes from clean `b4cc8292e6f0590794c41090836b331e78154f28`
completed successfully; all 471 immutable payload hashes were verified before
launch. These sequential captures used fresh clones of the same warmed seed as
the v20 controls, all nine promoted switches unset, background resolution on,
command threading off, 1920×1080, scale 1 and VSync on. Detailed render/pass and
memory profiling, prewarm and automatic stack sampling were disabled. GeForce
NOW and its settings were left unchanged.

The private diagnostic launcher omitted `DOTNET_GCgen0size`, whereas the existing
app-bundle `LSEnvironment` and standard `scripts/macos/run-game.sh` already set it
to `0x4000000` (64 MiB). The first arm retained the earlier diagnostic environment.
The second explicitly supplied the existing application setting; process readback
confirmed inheritance by both the guest process and ARM64 GPU host. This is a
launcher-parity control, not a newly enabled production optimization.

| Same v21 payload and cache seed | Stationary window | Reports | Median FPS | Range |
| --- | --- | ---: | ---: | ---: |
| Diagnostic launcher, GC budget unset | 250–285 s | 17 | 8.97 | 7.48–10.99 |
| Existing application GC budget, 64 MiB | 250–285 s | 17 | 7.97 | 5.98–9.50 |

Both windows were selected before their FPS values were read and after closing
the phone; the shelf/lamp room was visually checked. Arrival was later in the
first arm, requiring a later window than the earlier v20 measurements. These
results establish neither a completion-read FPS gain nor a GC-budget FPS gain.
They also do not isolate a regression from v20's earlier 15.48 FPS result.
Host memory pressure was present in both that earlier run and these captures.
Seven system-memory samples covering the v20 and first v21 windows recorded,
respectively, 7,913 versus 12,638 swap-in pages and 7,148 versus 16,212 swap-out
pages. These are system-wide interval totals, not emulator-attributed I/O or a
proof of the slowdown's cause. Thirty FPS remains unachieved.

Ten-second EventPipe GC/allocation/contention captures followed the FPS and
thread-CPU windows, without the managed sample profiler:

| Post-window GPU-host events | Budget unset | 64 MiB budget |
| --- | ---: | ---: |
| Allocation ticks | 10,728 | 10,941 |
| Sampled weighted allocation bytes | 1,147,942,904 | 1,169,498,312 |
| Fully paired GC pauses | 137 | 20 |
| Total GC pause time | 477.127 ms | 233.208 ms |
| Median / maximum GC pause | 3.205 / 6.969 ms | 11.073 / 25.998 ms |

Neither capture had unmatched suspension/restart pairs or `SuspendOther` pauses.
The larger budget reduced pause count and total traced pause time, with longer
individual pauses; allocation volume did not decrease. Tick weights estimate
allocation contributions and are not exact per-type allocation counts. The
largest sampled types include vertex-input records, vertex-stage registers,
user-scalar registers and resource snapshots. Frame-time conclusions cannot be
derived from these separately instrumented windows. The first capture's stacks
have frame records but unresolved method names; they are not useful allocation
call-site evidence.

The budget-unset run's 293.069–303.081-second thread-CPU capture recorded 5.163
CPU-seconds on the GPU worker, 1.822 on program resolution, 4.365 on guest RHI and
8.402 on the audio event thread. The 64 MiB arm's 292.258–302.269-second capture
recorded 5.433, 1.918, 4.651 and 8.221 CPU-seconds, respectively. These samples are
outside the FPS window and do not measure exclusive frame costs.

Private run directories are
`20261027-195142-silent hill short message.zar-room-unprofiled-defaults-v21-1`
and `20261027-195726-silent hill short message.zar-room-unprofiled-gen0-64m-v21-2`.
They retain selected windows, runtime environments, CPU deltas, event traces,
allocation/GC summaries and system telemetry. They stopped at their own markers
at 315 and 313 seconds, followed by four-second controlled termination and
process/wrapper exit 0. Peak main-process footprint/RSS was 9,039/3,958 MiB and
8,894/4,098 MiB, respectively; shared mappings prevent adding process footprints
to infer combined usage. Post-stop CPU traps remain recorded as teardown behavior.

A return control then launched the unchanged, hash-verified v20 `eabcd9f65`
payload with the original no-GC-override environment and another fresh clone of
the same seed. Run
`20261027-200336-silent hill short message.zar-room-unprofiled-v20-return-4`
measured **8.47 FPS median (7.47–9.50, 17 reports)** in the preselected
250–285-second window, with the same shelf/lamp view verified. Thus the lower
performance is not unique to v21. This does not prove the completion-read change
has no effect, or establish the exact cause of host/session variability. The
return run stopped at 303 seconds, exited 0 after four seconds of termination,
and peaked at 8,903 MiB main-process footprint / 4,838 MiB RSS.

### Synthetic protection-fixture follow-up

One of the two macOS CI executions for `b4cc8292e` failed the 64- and 65-view
cases in `AffectedViewBoundIsExactAndNeverPartiallyReopensOverflow`; the other
execution passed. The failing runner reported ARM64 / .NET 10.0.14 and an
`Assert.True` failure, without an address or source-line diagnostic. Inspection
found that the fixture's fixed guest range could overlap its pinned backing
array under ASLR. The ledger deliberately refuses effects on canonical aliases.
The fixture now places its synthetic guest range beyond its backing range and
logs both addresses. The exact overlap in the original CI process cannot be
reconstructed from that log; this change removes the collision possibility
without weakening the 64-view acceptance or 65-view whole-range rejection.
Production protection behavior is unchanged.

Fresh serialized Release `SharpEmu.Host.Memory.Tests` runs passed **486/0/6
existing skips each** on ARM64 and Rosetta x64. Launcher times were 13.794 and
51.073 seconds, including build/test startup. Commands and TRX files are retained
under `pr23-alias-fixture-{arm64,x64}-validation.json` and corresponding result
directories. No new production-code test claim is implied by this fixture fix.
