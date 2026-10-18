# Expanded architecture and quality roadmap

Discussion proposal, 2026-10-07. Confirmed scope: Metal first with x64/Rosetta CPU
execution; native ARM64 CPU execution later. Evidence references E01–E11 are in
[the evidence audit](evidence-audit.md). No proposed performance benefit below is
a measured SharpEmu gain unless explicitly identified as an existing observation.

## Direction

Develop a portable emulator with replaceable host implementations, a first-class
Metal backend, explicit lifetime/ordering rules, and repeatable evidence. Keep
the C# core and useful existing interfaces. Extract real responsibilities rather
than mechanically splitting all files into three platform copies. Share guest
semantics; separate the implementation choices the host actually changes.

The largest potential wins are feeding the GPU sooner, reducing coherence waits,
cutting shader/compilation overhead and keeping memory bounded. Platform folders,
a native UI and packaging improve maintainability and experience, but do not by
themselves explain low game FPS.

## Proposed boundaries

These are responsibilities first. Some can begin as namespaces/services inside
existing projects; create a separate assembly only when dependency isolation or
build composition benefits justify it.

### 1. Session and lifecycle

Own one emulation session's configuration, guest process, module registry,
memory, queues, audio, video and cancellation. Make static SysAbi exports thin
adapters into session-owned services. Replace process-wide mutable registries
incrementally; retain the generated export registry and measured fast paths.

Use an explicit lifecycle: prepare, load, initialize, run, request stop, unblock
waiters, retire submitted GPU work, persist caches, release resources, stopped.
Faults must propagate to the session and UI even if the process exits code zero.
Preallocate crash/stop status so failure paths can report without depending on
a failing renderer or unrestricted logging.

Acceptance: repeated start/stop in one process does not leak threads, handles,
memory or prior-game state. Cancellation reaches blocked readbacks/compiler/I/O
requests. Distinguish host shutdown success from normal guest return.

### 2. CPU engine, guest ABI bridge and fault handling

Separate instruction execution, native import gateways, guest thread management,
OS faults and executable patching. Preserve current CPU interfaces where useful.
Host adapters own Windows exceptions, Linux signals and macOS Mach/signal details.
The bridge owns stack switching, red zones, callee-saved registers, TLS and
floating/vector state, including reentrant calls.

Managed runtime calls on guest/native stacks require a verified safe transition.
Eager JIT/type initialization currently mitigates this. Modularization must not
casually remove that warmup. Audit the reachable bridge graph; then experiment
with generated bounded warmup manifests and compatible ReadyToRun builds. Broad
NativeAOT is a separate feasibility study because the current design uses JIT,
reflection and generated executable stubs.

Acceptance: executable ABI fixtures plus actual Rosetta launch/shutdown loops;
no allocation/logging/blocking added to unsafe fault callback contexts. Preserve
native crash evidence, not merely a larger stack or GC knob that hides a symptom.

### 3. Guest scheduling and wait semantics — immediate performance priority

Extract guest runnable/wait state, clocks, mutex/condition/event behavior, host
thread adapters and CPU execution policy. Implement traceable wait causes and
state transitions: runnable, on-core, blocked on guest mutex, guest fence, HLE,
host I/O, GPU data, and runtime suspension.

Correlate guest work completion with AGC enqueue, command-worker dequeue, GPU
submit, GPU completion, flip and presentation. This should explain E01's renderer
starvation rather than treating idle time as shader time. Verify clock units,
timeout rounding and condition rechecks to avoid missed wakes and polling taxes.

Do not change reported guest CPU count, thread priority or wake policy merely
to make a title run faster. Arbitrary direct x86 execution cannot automatically
be turned into a cooperative task scheduler. Make policy changes bounded,
measured and faithful to guest behavior.

Acceptance: explain the dominant idle intervals in a quiet-host capture, show
reduced guest wait/producer latency and improved matched frame-time/progression
without new timeout or starvation regressions.

### 4. Guest memory and shared physical ownership — correctness prerequisite

Separate guest virtual addresses, physical backing identity, allocation leases,
page protection/watch machinery and GPU resource views. Associate version and
producer state with physical ranges so two guest virtual aliases cannot diverge.
OS adapters query actual host page geometry; guest page sizes remain independent.

Make read/write ownership explicit. Every tracked range needs its authoritative
copy, dirty versions, pending writer, consumers and retirement point. Imported,
mirrored, buffer and image paths must obey one ownership contract; otherwise
mixed fallback aliases remain unsafe. Precision should be driven by actual
written bytes when possible rather than transferring ownership of an unrelated
counter's whole page.

Preserve permanent backing until its last submitted GPU use retires, even if
guest VA mappings change. Native Metal shared buffers are a capability-specific
implementation of this contract, not a substitute for it.

Acceptance: alias/remap/overlap/atomics tests on the real device, CPU visibility
after correct guest completion, mirrored fallback equivalence, and controlled
memory/FPS results. Keep direct memory opt-in until these pass (E02/E08).

### 5. Command processor and submission scheduler

Separate packet decoding and guest register semantics from resource preparation,
backend command encoding and presentation. Reuse immutable register snapshots
until their generations change. Represent blocked dependencies explicitly, with
producer indices by physical range and guest queue while preserving each queue's
FIFO and guest visibility points.

Experiment with preparation/encoding pipelining once traces show useful overlap.
Independent compute queues can be parallel only after caches and lifetimes have
safe ownership. Coalesce redundant dirty-state resolution without losing memory
side effects. Batch size and in-flight depth should be chosen by latency evidence;
more work in flight can increase readback drain cost (E05).

Avoid waits for the entire device when an exact producer is sufficient. Avoid
parse-time publication of results whose guest contract requires GPU completion.
Any forwarding prediction is internal and expires/invalidate on submission,
overlap or CPU resets; it must never masquerade as CPU-visible completion.

Acceptance: ordering and liveness fixtures including WRITE_DATA-only producers,
cross-queue waits, wraparound submissions and cancelled shutdown; lower enqueue
to submit latency without changed outputs or increased memory.

### 6. Shader semantics, analysis and backend code generation

Keep decoding and guest arithmetic/control/resource semantics in the shared IR.
Separate immutable program analysis, changing descriptor materialization, legal
optimization passes and SPIR-V/MSL emission. Reuse analysis by content plus all
semantic inputs; reject stale resource snapshots immediately.

Build a scalar/reference evaluator and synthetic programs for arithmetic, NaNs,
half rounding, EXEC/SCC/VCC, cross-lane wave32/wave64, LDS/barriers, buffer/image
access and bounds. Cross-backend agreement alone is insufficient because both
can share the same bug. Add property/fuzz tests around decoder and IR boundaries.

Prioritize conservative register extent/liveness, scalar propagation, redundant
loads and allocations, proven control-flow simplification, and compact wave/LDS
lowering. Dynamic indices require safe bounds/full-file fallback; include all
prologue/system writes. SALU is not EXEC-masked, and derivatives/barriers have
special rules. E10's register gain is an experiment lead; E05/T3 show why one
transform may help compute and regress graphics.

Acceptance: exact fixture outputs on Metal, checked tolerance where specified,
invalid-program rejection, compiler size/time, peak registers/threadgroup usage
and stage-specific GPU time; no whole-game improvement inferred from one pass.

### 7. Compilation service and layered cache storage

Separate decoded-program/analysis cache, translated shader cache, resource-plan
cache, native shader/pipeline cache and on-disk storage. Keys must include compiler
schema, guest content, semantic specialization, device/OS/backend identity and
relevant settings. Content fingerprints can accelerate lookup but full identity
must still prevent returning a different resource plan/pipeline.

Use a bounded compile service with duplicate suppression, cancellation, memory
budgets and priority for imminent work. Predict/prewarm from recent title usage
or a safe scan-ahead snapshot. Cache import and native compilation must not hold
unrelated guest-service locks. Persist atomically and recover from corruption.

Explore Metal binary archives with compatibility validation. Keep translation,
archive initialization and native compiler timing separate. Under strict behavior,
queue required work until its pipeline is ready; do not skip side-effecting draws
or compute to advertise asynchronous compilation.

Acceptance: independently reduce cold/warm time to useful scene and first-use
stutters; no missing work, unbounded compiler fan-out or stale cache reuse.

### 8. Graphics backend and native interop

Keep Metal and Vulkan implementation assemblies independent of shared emulation.
Metal owns ObjC/Metal handles, retention/release, autorelease scopes, command
encoders, pipeline state, resource storage, completion callbacks and capture.
Extend the current Metal implementation rather than creating a second one.
Use typed wrappers and lifetime rules around the current C# bindings; a small
native ABI shim is an option only where it measurably simplifies correctness or
hot interop. Preserve C# host-facing ownership.

For Apple GPUs, measure appropriate shared/private buffer/texture choices,
reduced encoder/pass breaks, batched state changes and GPU detiling. Consider
heaps, argument buffers and indirect command encoding when the measured CPU
front end and resource patterns justify them. These are device-capability choices.
Memoryless targets are valid only for proven pass-local, unobserved contents;
guest aliasing, readback or later sampling excludes them. Intel/different GPU
memory behavior needs a separate capability path.

Acceptance: Metal-only launch with no MoltenVK dependency, explicit capabilities,
real graphics/compute hardware tests and correct resource lifetime under pressure.

### 9. Images, tiling, metadata, formats and resource budgets

Separate guest surface layout/format interpretation from backend allocation and
transfer. Model aliases, mip tails, cube faces, volume views, DCC/HTile, stencil
association and GPU-written metadata. Distinguish logical guest size, native
allocation size, scaled backing, transfer twin, pooled and deferred objects.

Maintain an allocation ledger across guest backing, shaders, resources, transient
uploads, caches and native objects, with byte/cost-aware eviction and hysteresis.
Evict only safe completed content; preserve dirty guest data before disposal.
Track managed heap, process footprint, compression/swap and OS memory pressure
separately from the ledger. Do not present a cache budget as a hard process cap.

Acceptance: bounded repeated-scene memory with no lost GPU contents, no monotonic
pool growth, and matched title checkpoint measurements (E04).

### 10. Presentation, frame pacing, display state and color

Separate guest VideoOut state/flips from host window events and device presentation.
Define submitted/completed/presented frames consistently. Keep window size, guest
display mode and internal rendering scale independent. Backend capabilities must
drive UI visibility: candidate scaling currently applies only to Vulkan (E09).

Own resize/Retina/fullscreen, drawable/swapchain lifecycle, sleep/wake and HDR/SDR
format/color conversion in presentation adapters. Track image color spaces and
transfer functions through the final output; avoid fixing lighting by a blanket
tone or half-value clamp. Minimize repeated SDL polling through one event owner.

Acceptance: frame progress versus repeated static screenshots, correct SDR/HDR
targets, responsive resize/sleep/wake, and resolution changes that preserve guest
memory/layout semantics. Check scaling's pixel effects separately from FPS.

### 11. Queries and advanced GPU features

Give queries, predication, indirect draws, geometry/NGG, mesh and ray-tracing
features explicit guest semantics and backend capability adapters. Trace result
write, availability and guest fence ordering. A visibility fallback needs an
honest compatibility status; synthetic nonzero values are not real sample counts.

Audit features against actual captured workload frequency before expanding them.
Keep experimental native occlusion gated until false culling is resolved. Do not
import the entire broader PR 1061 to obtain independent arithmetic/format fixes.

Acceptance: exact synthetic query results and availability across resets/reuse,
real scene visibility, and no guest read before required publication.

### 12. Audio, codecs and host media

Separate guest AudioOut/AJM job state, codec interfaces, mixing/routing, audio
clock and host device output. Queue level/nonblocking submission must reflect
actual guest consumption semantics; sleeping in a push while a guest audio lock
is held can block the game (E05). Implement bed/port/channel semantics rather
than reporting success with absent sound. Measure underruns and audio latency.

Separate AvPlayer/Bink observation, decoding, YUV conversion, guest timing, audio
sync and final presentation. Host decoded movie completion and guest EOF/skip
progression are distinct. Explore native hardware video decode only for supported
formats, with a deterministic software path and equivalent timing behavior.

Audit AJM AAC and decompression needs using actual import/job traces. The older
survey's labels are not proof of current support; use maintainable compatible
libraries and synthetic inputs, not proprietary SDK/code or game assets in CI.

Acceptance: audible output, correct channel routes, bounded latency, pause/seek/
EOF behavior and no hidden guest synchronization changes.

### 13. Filesystem, asset I/O, decompression and saved state

Separate guest mounts/path rules, host file access, APR command semantics, cached
file lifetime, asynchronous job execution and codecs. E05 exposed an eviction
versus active-reader race. Hold backing handles until readers finish; cancel or
complete outstanding jobs predictably during shutdown.

Index large installations with validated cache identity. Bound parallel I/O and
prefetch by bytes, memory and actual storage behavior. Preserve command dependencies
and guest event/error publication; asynchronous is not automatically faster.
Keep saves and game settings session-safe with atomic writes and versioned paths.

Acceptance: identical output and errors for cold/warm/local/external storage,
concurrent reads/eviction/remount, improved measured loading phases and intact saves.

### 14. Configuration, capability policy and compatibility records

Replace scattered switches with a typed, versioned option registry that generates
CLI parsing/help, GUI metadata, environment adapters and documentation. Retain
existing env names as compatibility aliases. Log effective values and their
sources once; distinguish correctness options, performance experiments and
diagnostics. Reject or clearly report backend-inapplicable settings.

Record compatibility by title/version/checkpoint/backend/build and bounded known
limitations. Per-title records can select tested capabilities/workarounds, but
must not silently patch game instructions or force outputs. Require evidence and
an expiration/retest policy for every workaround.

Acceptance: GUI/CLI/env precedence agrees, migration preserves user settings,
run manifests are reproducible, and unsupported options do not silently do nothing.

### 15. Frontends, app bundle and update channel

Extract a real headless CLI composition path without GUI/native-window dependencies
when the chosen task does not need a window. Keep Avalonia initially; split the
large window's session/settings/library/update logic into services and view models.
Give the GUI reliable progress and failure states, including compiling and waiting.

Later evaluate a separate emulator worker process behind a narrow versioned
control protocol for crash isolation. An ARM64-native launcher could then coexist
with an x64 worker; it does not make guest CPU execution ARM64-native. Benchmark
any IPC/frame transport cost before committing to this split. A SwiftUI/Cocoa
frontend is optional after the engine interface is stable.

Improve .app/DMG packaging, signing/notarization, Finder launch, dylib resolution,
application-support/cache paths and Rosetta checks. Keep fork updates/artifacts
distinct from upstream. A universal release requires working architecture slices,
not merely a successful cross-publish.

Acceptance: CLI/GUI/installed bundle launch, visible failures, recoverable update
and session state, no accidental upstream release/channel publication.

### 16. Diagnostics, capture/replay and conformance — immediate foundation

Promote the useful scattered tools into a supported diagnostics surface: bounded
event rings with submission/thread/resource IDs, periodic incremental flush,
triggered stack/memory capture and backend-specific GPU capture. Keep default
runtime instrumentation cheap and opt-in heavy probes clearly labeled.

Version a backend-neutral capture format for guest registers, shaders, resolution
inputs, resources and observed ordering. Extend Vulkan-only replay to Metal;
include depth/stencil, compute outputs, counters and intermediate images. Reject
incomplete/truncated capture inputs. Keep private real-title cases local; produce
minimal synthetic public fixtures from the behavior they expose.

Make input injection a test adapter with delivery and guest-pad acknowledgement.
Use explicit checkpoint recognition/manual confirmation where needed, not wall-time
or thumbnail brightness alone. This addresses E04's unused input-file runs.

Acceptance: one reproducible case diagnoses a bug without repeated full boot;
results survive forced exit via incremental trace flush; no silent device-test skips.

### 17. Evidence and benchmark infrastructure

Extend the shared machine-wide GPU lock to device tests and replay. Add a separate
benchmark eligibility check for host CPU load, swap/pressure, competing builds,
device support, source/binary SHA and a known scene. Do not interrupt unrelated
work automatically; queue or mark contaminated runs ineligible.

Store a structured run manifest: fork/upstream/source tree and binary hashes,
SDK/native dependency/OS/device versions, resolved config, title/version/source,
input plan/acknowledgement, cache/save state hashes, scene, trace overhead and exit.
Alternate A/B order and repeat matched scene windows. Report uncertainty and
median/p95/p99 frame intervals from per-frame timestamps. Separate presented-frame
cadence from guest simulation, movie playback and synthetic profiler scope totals.

Incrementally consolidate observations into a searchable evidence index with
superseded/retracted status. Preserve failed experiments. Do not call a green
CI job device coverage unless it records the device and exercised case counts.

Acceptance: another developer can reproduce a comparison from its manifest and
verify exactly which phase/scene/commit the claim covers.

### 18. Upstream synchronization and review boundaries

Retain `upstream` as an exact mirror and `main` as our integration branch, with
temporary topic branches. Stage mechanical moves separately from behavior changes.
Continuously validate Windows/Linux to keep common improvements upstreamable.
Enforce dependencies in CI using project references and a small set of namespace
rules: shared guest layers cannot acquire backend/OS APIs; frontends compose them.

Inventory candidates by ancestry plus substantive change, including corrected
reverts. Current #1066 is review-only; experiments #1042/#1060 remain gated. Do not
resurrect them indiscriminately as a bootstrap shortcut. Keep existing consolidated
review work cohesive until the user chooses how it enters sharpemu-mac.

Before broad architecture changes, prepare an RFC and discuss the proposed seam
with upstream when the user authorizes publication. Upstream fixes should be useful
independently of fork branding, packaging policy and experiments.

## Concrete project layout target

The [platform package design](platform-packages.md) refines this initial layout:
domain-owned abstraction assemblies, separate OS/x64 execution integrations,
toolkit/media adapters, thin platform factories and target-specific app roots.
The [implementation plan](implementation-plan.md) maps all 18 areas into ordered
work packages and acceptance gates. Use those documents for package boundaries.

Preserve current names where practical. An eventual dependency layout could be:

```text
SharpEmu.Contracts                 narrow session/host/guest service contracts
SharpEmu.Core                      runtime orchestration and loader
SharpEmu.Cpu                       execution, ABI bridge, guest thread semantics
SharpEmu.Memory                    guest virtual/physical backing and ownership
SharpEmu.HLE / SharpEmu.Libs        guest kernel/library services and export adapters
SharpEmu.Gpu                       guest command semantics and backend contracts
SharpEmu.Gpu.Vulkan                Vulkan resources/encoding/presentation
SharpEmu.Gpu.Metal                 Metal resources/encoding/presentation
SharpEmu.ShaderCompiler*            shared semantics/analysis plus existing emitters
SharpEmu.Host.{MacOS,Linux,Windows} OS services and native fault/window adapters
SharpEmu.Media                     codec/mixing/streaming contracts and core behavior
SharpEmu.CLI / SharpEmu.GUI         separate composition roots
SharpEmu.Diagnostics / Tools.*     capture, replay, profiling and conformance
```

Contracts reference no renderer/frontend. Implementations depend inward; composition
roots select and connect them. Layering must not create a Core↔Libs cycle. This is
not a demand for fourteen new projects at once: start with session/selection and
the narrow contracts required for each demonstrated extraction.

## Priority and delivery

### P0: establish the foundation and explain the critical path

1. Bootstrap upstream history and a trustworthy local source/binary manifest.
2. Make existing Metal start without MoltenVK; qualify actual supported paths.
3. Add the guest→submit→complete→flip correlation and quiet-host measurement gate.
4. Establish current-main and chosen candidate baselines at fixed checkpoints.
5. Design physical ownership/alias contracts and bounded session shutdown.

The first benchmark is the same Demon’s Souls language menu as E01 under a quiet
host, followed by a separately labeled 3D checkpoint. Do not invent a FPS target
before identifying the critical path. Silent Hill correctness needs its own scenes.

### P1: high-value changes with direct evidence leads

Host ABI/fault isolation; scheduling/wait fixes; shader extent and semantic tests;
bounded compilation/cache service; exact readback dependencies; managed allocation
and native memory ledger; audio clock/queue semantics; complete capture/replay.
Each work item needs before/after fixtures and at least one relevant runtime result.

### P2: measured backend and application improvements

Metal resource/pass/binding optimization, robust format/query/presentation coverage,
I/O concurrency, media improvements, Metal internal scaling, polished .app/update
behavior, typed settings, frontend separation and sustained multi-title tests.

### P3: larger experiments after the portable engine is stable

ARM64-native launcher/worker separation, x86-64→ARM64 CPU engine, SSA/control-flow
compiler restructuring, deeper parallel command preparation, indirect command
encoding and optional native UI. These require their own design and acceptance.

## Performance and quality goals

Aim for correct playable progression, stable frame times, bounded memory and
predictable startup, with 60 FPS wherever the workload and hardware permit. Avoid
promising 60 FPS from Metal alone. A measured shader improvement cannot overcome
an unchanged producer bottleneck; lower resolution cannot fix a CPU/compute-bound
frame; fewer readbacks are not a gain if guest CPU writes wait longer.

Promote a change only when the supported scene's correctness, latency, memory and
shutdown are understood. Track regressions across a small diverse title matrix,
including Dreaming Sarah, Superliminal, Demon’s Souls, Silent Hill, Octopath and
other existing harness titles, with actual checkpoints rather than generic boot.

## Metal API references for proposed experiments

- [Resource storage choices](https://developer.apple.com/documentation/metal/choosing-a-resource-storage-mode-for-apple-gpus)
- [Binary archives](https://developer.apple.com/documentation/metal/mtlbinaryarchive)
- [Resource heaps](https://developer.apple.com/documentation/metal/mtlheap)

These establish available mechanisms; they do not establish a SharpEmu speedup.
