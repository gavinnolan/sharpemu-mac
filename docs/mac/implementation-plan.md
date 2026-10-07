# Implementation plan and acceptance gates

Status: proposed backlog, 2026-10-07. All items below are planned, not implemented.
The user asked for deeper platform packages and a complete plan; source import,
runtime experiments, publication and package implementation have not started.

Design: [platform-packages.md](platform-packages.md). Improvement areas and their
evidence: [architecture-roadmap.md](architecture-roadmap.md),
[evidence-audit.md](evidence-audit.md). Decisions: [ADR 0001](decisions/0001-platform-packages.md).

## Working rules

- Bootstrap on full upstream ancestry; preserve uncommitted planning documents.
  Keep upstream mirror clean, integrate into main through ordinary merges.
- Assign one source owner, package destination, dependency justification and
  validation record to each extraction. Temporary facades have removal gates.
- Separate mechanical movement from changed behavior. Prefer an independently
  reviewable boundary per change; avoid wholesale namespace/path churn.
- Keep Windows/Linux build and contract checks running throughout. Changes to
  their native adapters need actual OS runtime checks, not a Mac cross-build.
- Record real-device tests exercised/skipped/unavailable distinctly. Device
  absence cannot produce a "passed GPU qualification" claim.
- Preserve existing review boundaries. Recheck candidate/revert provenance before
  reuse; no automatic import or merge of the earlier review-only #1066 work.
- Each implemented item records source/binary SHA, tests, scene/device evidence,
  limitations, rollback path and any capability/default changes.

## Milestones

| Milestone | Result | Prerequisites | Gate |
| --- | --- | --- | --- |
| M0 | Full-history fork, reproducible source and runtime baseline | W01-W04 | Exact provenance, preserved docs, three-OS baseline; unavailable runtime evidence explicit |
| M1 | Metal startup independent of MoltenVK; enforceable host/adapter boundaries | W05-W09, W11-W12 | Metal startup with no Vulkan dependency; OS host contract checks |
| M2 | Separate OS execution integrations and backend-free shared engine | W10, W13-W15 | ABI/alias/order fixtures plus real launch/stop evidence |
| M3 | Qualified native Metal with measured common-engine improvements | W16-W24 | Defined correctness/progression matrix and controlled performance comparisons |
| M4 | Reliable Mac application and distributable bundle | W25-W28 | Packaged launch, lifecycle, relocation and distribution checks |
| M5 | Upstream-ready boundary changes and ongoing sync | W29 throughout | Small independent changes, cross-platform evidence, tracked accepted fixes |
| Later | Native ARM64 execution and optional native shell | W30 | Separate design/qualification; no ARM64 support claim from publish alone |

Milestones are dependency gates, not calendar promises. M3 optimizations can be
investigated while M2 extraction proceeds, but behavior changes need isolated
comparisons. Packaging prototypes may happen early; release defaults wait for
their qualification gates. Upstream sync starts at M0 and continues throughout.

## Work packages

### W01 — Bootstrap history and safe fork operation

Depends on: none. Priority: P0. Destination: repository/build policy.

Import full upstream history; create the upstream mirror and development main;
preserve planning files; audit inherited workflow publication/notifications;
document remotes and fully qualified refs; embed upstream/fork provenance.
Do not introduce an independent root commit to hold copied upstream source.

Done when: both branches have intended ancestry, mirror tree matches recorded
upstream, docs survive, unrelated work is untouched, fork release identity is
distinct and no inherited release action accidentally publishes upstream assets.

### W02 — Source, dependency and native-import inventory

Depends on: W01. Priority: P0. Destination: tools/architecture + audit artifacts.

Inventory all project/package edges, native imports, OS checks, static owners,
entry points, build targets, callback types, generated exports, native assets and
tests. Classify each file as shared, OS, architecture, graphics, frontend or glue.
Identify Core/Libs/HLE cycles and the exact contracts needed to eliminate them.

Done when: the initial migration map covers all platform-sensitive source and
asset owners, has no unexplained circular dependency, and names temporary facades.
File counts do not establish performance cost.

### W03 — Reproducible build and dependency acquisition

Depends on: W01-W02. Priority: P0. Destination: build/dependencies and profiles.

Pin upstream SDK and managed/native versions. Isolate outputs per configuration,
RID and backend profile. Extract verified native fetch/staging from ordinary
shared-library build. Record checksums, architecture, ABI and licenses. Retain
existing upstream-compatible FFmpeg builds first; prototype a local native package
feed without requiring external package publication.

Done when: a clean build and prepared-cache build produce the intended dependency
manifest; restore/publish uses target RID rather than build-host OS; stale outputs
and unsupported architectures cannot leak into artifacts. Preserve source/license
and REUSE requirements for moved/generated files.

### W04 — Honest baseline and benchmark eligibility

Depends on: W01, W03. Priority: P0. Destination: tools/benchmark + diagnostics.

Use the shared single-game/device lock after adapting paths to the new checkout.
Record host load, memory pressure/swap and competing processes without killing
unrelated work. Manifest source/binary SHA, device/OS/SDK, config, save/cache state,
input acknowledgement, scene and exit. Capture incremental evidence through stop.

First controlled case: Demon’s Souls language menu, then separately recognized
3D progression. Include Silent Hill visual checkpoints independently. Existing
October 6 menu FPS has an unknown exact source SHA; it is historical context,
not an acceptance baseline. Current-main contended loading run is not gameplay.

Done when: matched quiet-host runs are reproducible and eligible; cold/warm startup,
first frame, menu, gameplay, correctness, memory and shutdown are distinct results.
Unavailable checkpoints remain explicitly unverified.

### W05 — Backend selection before native loading

Depends on: W02-W03. Priority: P0. Destination: configuration/composition.

Resolve explicit graphics selection before backend library/device initialization.
Metal startup must not configure/load MoltenVK. Make requested/actual backend,
capability failure and dependency versions visible. Remove silent fallback.

Done when: Metal starts with Vulkan libraries absent, Vulkan still starts in its
comparison profile, and missing/unsupported dependencies produce a useful error.
Early success proves selection isolation, not game-level Metal compatibility.

### W06 — Extract host abstractions and remove toolkit dependencies from HLE

Depends on: W02, W05. Priority: P0. Destination: Host.Abstractions, Adapters.Sdl.

Move neutral IHost* contracts and types without changing semantics. Keep guest
clock behavior in the guest layer. Move SDL audio/window-input implementations;
inject them at composition. Remove HLE's direct SDL dependency. Replace static
host access incrementally with service ownership; leave bounded facades if needed.

Done when: HLE's transitive closure excludes SDL/GUI/OS implementation packages,
existing audio/input defaults behave equivalently, native initialization remains
lazy and source-generator/warmup reachability still resolves.

### W07 — macOS host package

Depends on: W06. Priority: P0. Destination: Host.MacOS.

Extract Mac VM/page protection, threads/TLS, symbols, clocks, paths and diagnostics.
Move CoreAudio implementation but retain qualified default output. Explicitly
separate host page size and guest page geometry; map failures to meaningful results.

Done when: native memory/thread/symbol/clock contracts pass on macOS; repeated
allocate/map/protect/unmap and thread lifecycle checks preserve ownership; x64
Rosetta launch/stop baseline remains equivalent. ARM64 host-only tests do not
establish guest execution support.

### W08 — Windows and Linux host packages

Depends on: W06. Priority: P0. Destination: Host.Windows, Host.Linux.

Extract Windows implementations and split Linux from combined POSIX behavior.
Audit per-OS constants/structs/symbol names rather than copying declarations.
Preserve audio/input defaults and share only proven identical private helpers.

Done when: OS-native contract suites and launch/stop checks pass on the supported
OS/device matrix. Cross-building their managed assemblies is additional evidence,
not a replacement. Unavailable runners block qualification claims, not unrelated
Mac design/implementation work.

### W09 — Dependency rules and platform factories

Depends on: W06-W08. Priority: P0. Destination: Platform.* + tools/architecture.

Introduce thin host/execution composition factories. Enforce project and transitive
asset rules, platform API annotations, native-import ownership, a bounded OS-check
allowlist and useful diagnostics for violations. Prevent arbitrary InternalsVisibleTo
expansion or service-locator workarounds.

Initially factories adapt the existing CPU entry point; complete execution factory
isolation arrives with W10. Enforce new rules for extracted packages immediately
and keep a named shrinking legacy exception list until W14 removes old dependencies.

Done when: intentionally invalid dependency fixtures fail the checker; real graphs
are acyclic; Mac/Windows/Linux closures exclude sibling OS implementations.

### W06-W08 — Progress

Done. The host OS implementations are separately compiled packages:
`src/platforms/{macos,linux,windows}/SharpEmu.Host.*`, each referencing only
`SharpEmu.HLE` (which keeps the neutral `Host/` contracts and is the de facto
`Host.Abstractions` until an extraction is needed to break a real cycle) and
each marked `[assembly: SupportedOSPlatform(...)]`. `SharpEmu.Adapters.Sdl`
owns the SDL audio output; HLE no longer references `ppy.SDL3-CS`.

`HostPlatform`'s `OperatingSystem`-based factory is replaced by explicit
registration (`HostPlatform.Initialize` / `HostViewMemory.Initialize`, with
`Current` throwing a directed `InvalidOperationException` when nothing is
registered). `SharpEmu.CLI` references exactly one host project, selected from
the target RID, and `src/SharpEmu.CLI/HostComposition.cs` — the counterpart to
`GpuComposition.cs`, using the same RID-to-OS convention — is the only place in
the application that names a host package. Enforced by
`tests/SharpEmu.Libs.Tests/ArchitectureRules/HostPackageBoundaryTests.cs`, which
parses the project files and the compiled sources (including linked shared
source) rather than reflecting over loaded assemblies — only one host package
is ever loaded, so a reflection-based check could not see the other two.

Audio is the one host service the application supplies rather than the host
package: `WindowsHost.Create` / `LinuxHost.Create` / `MacOSHost.Create` take an
optional `IHostAudioOutput`, and `HostComposition` hands the Windows and Linux
hosts `SdlHostAudio`. That keeps SDL the qualified default there without any
host package referencing `SharpEmu.Adapters.Sdl`, which also owns the window
and input and therefore references `SharpEmu.Libs` — a dependency that does not
belong in a host closure. Passing null falls back to each package's own native
output (`WindowsWaveOutAudio` / `LinuxAlsaAudio`), still unqualified pending
latency, underrun, clock, device-change and shutdown evidence.
`SharpEmu.Host.MacOS` owns the native CoreAudio output and references SDL
neither by package nor by adapter, so a macOS process never initialises SDL
audio. That retired the last macOS SDL exemption: the `SDL3-CS.dll` /
`libSDL3.dylib` entries in `build/platforms/macos/macos.targets` and the
`SharpEmu.HLE -> ppy.SDL3-CS` `AllowedEdges` entry in
`VulkanFreeSharedGpuTests` are both gone, and an `osx-x64` release publish
reports a clean closure with no SDL string in the artifact at all.

#### Known remaining OS checks in shared code

These were deliberately left in place; each needs its own change.

- `src/SharpEmu.HLE/HostMemory.cs` — a static `public` Win32-semantics shim
  (`Alloc`/`Free`/`Protect`/`Query`) with its own `OperatingSystem.IsWindows`
  split and a second private POSIX shadow-region table, duplicating what the
  host memory backends now own. It is used widely and statically
  (`PhysicalVirtualMemory`, `GuestImageWriteTracker`, the `Kernel*CompatExports`
  families, `StubManager`, `DirectExecutionBackend*`), so routing it through
  `IHostMemory` is a behavioural change to the memory and CPU paths, not a move.
  Retiring it belongs with the `Memory` extraction (W13/W14), not here.
- `src/SharpEmu.Core/Cpu/Native/PosixHostStubs.cs` and the
  `DirectExecutionBackend.{MachExceptions,PosixSignals,PosixSignalStack}`
  partials — a second, independent copy of the POSIX stub/TLS helpers next to
  the fault-handling code, with its own macOS/Linux branches. Splitting these
  is W10 (`Cpu.DirectExecution.<OS>.X64`); process-wide fault hooks and the
  emitted pre-filter thunks are exactly the code the plan says not to move
  without the ownership seam, so this workstream left them untouched.
- The view backends diverge in their reservation primitive, by design and not
  by an OS check: macOS reserves only through `mach_vm_allocate` with
  `VM_FLAGS_FIXED` (Apple Silicon ignores mmap's address hint entirely) and
  answers `IHostViewMemory.FindUnmappedRange` from
  `mach_vm_region_recurse`, while Linux uses `MAP_FIXED_NOREPLACE` and
  inherits the interface's default `FindUnmappedRange`. The Mach declarations
  exist only in `SharpEmu.Host.MacOS`.
- `src/SharpEmu.HLE/Host/HostViewRegions.cs` holds no OS check: it is pure
  managed bookkeeping (no syscalls, no native layouts, nothing per-OS beyond
  `Environment.SystemPageSize`), shared by both POSIX view backends and by
  `HostMemory.Query`. It stays in the neutral host layer rather than becoming
  a `Host.Posix` assembly.

The host packages now use explicit HLE integration contracts for mapping
invalidation, view bookkeeping/validation, window input and backing ownership.
No production host/backend/platform package receives HLE or Libs friend access.
Backing handles are initialized once; mapping and disposal share a documented
synchronization gate. Existing Core and test access remains. See
[package contracts](package-contracts.md) for the API and validation scope.

### W10 — Direct execution and OS/architecture fault adapters

Depends on: W02, W07-W09, W13 ownership seam. Priority: P1.
Destination: Cpu.Abstractions, Cpu.DirectExecution, OS .X64 integrations.

Extract existing execution interface and compatible context types; isolate native
fault context layouts, signals/Mach/Windows hooks, native worker and TLS details.
Retain shared patch/import/stack policy only where actually common. Audit generated
stubs and reachable managed calls; preserve safe stack transition and eager warmup.
Move Mac GC/Rosetta process policy to application build/runtime configuration.

Done when: register/TLS/red-zone/reentrancy/stack-switch/fault fixtures and actual
x64 launch/stop loops pass on each OS; no allocation/blocking is introduced into
unsafe callbacks; memory mappings and fault-hook ownership survive shutdown.
Do not replace the CPU implementation as part of this extraction.

### W11 — Graphics contract and backend package extraction

Depends on: W02, W05-W06. Priority: P0/P1.
Destination: Gpu.Abstractions, Gpu.Metal, Gpu.Vulkan, existing shader emitters.

Promote the existing guest-domain GPU seam and opaque shader handles into a usable
assembly boundary. Move Metal implementation and Vulkan-specific machinery out
of Libs. Keep Vulkan-shaped interfaces private. Extract shared packet/register
code only after proving it backend-neutral. Separate compiler semantics from emitters.

Done when: shared Core/HLE/Libs closure excludes concrete backends and their native
bindings; no Metal↔Vulkan dependency; existing backend fixtures and required scenes
remain equivalent. Device capability gaps are listed, not silently emulated away.

### W12 — Presentation and SDL surface boundary

Depends on: W06, W11. Priority: P1. Destination: Presentation.Abstractions, Adapters.Sdl.

Separate window/input/event pump and surface lifetime from GPU encoding. Design
borrowed native-surface negotiation for SDL Metal/Vulkan creation, render thread
handoff, main-thread calls, resize generations and device loss/close. Add headless
Metal compute fixtures with no window. Respect UI toolkit ownership of its loop.

Done when: neither backend depends on SDL; native bridge ownership is audited;
resize/fullscreen/display/close tests do not race retired surfaces; CLI fixtures
need no GUI framework and do not initialize SDL video.

### W13 — Guest memory and physical ownership seam

Depends on: W06-W08; full GPU implementation also needs W11. Priority: P0/P1.
Destination: Memory.Abstractions, Memory, backend resource stores.

Define guest VA, stable physical backing identity, alias/remap leases, dirty
versions, authoritative writer/reader and GPU retirement. Inject host mappings.
Unify imported/mirrored/image/buffer ownership before enabling broad zero-copy.
Map resources by actual backing ranges, not only guest virtual address.

Done when: alias/overlap/remap/CPU-GPU visibility/atomics cases pass on hardware
with explicit retirement; guest unmap cannot free in-flight physical backing;
mirrored fallback agrees with supported direct paths. Keep unsafe imports gated.

The 2026-10-07 native fault probe adds a concrete retirement requirement:
partially unmapping a read-only watched span can make whole-span `mprotect`
fail while a surviving page stays read-only. The current tracker can discard
watch ownership despite that restoration failure. Retire watchers transactionally
with mapped-backing ownership; retain diagnosable ownership until restoration
succeeds, and verify page-local handling of surviving/unmapped/shared pages.
Do not repair this by resetting only the range's armed flag or refcount.
Include controlled restoration failure, overlapping owners, partial rollback,
remap/reuse and signal-safe retry fixtures. Repeated writes within one dirty epoch
also need explicit snapshot-version semantics. The Poppy runtime fault has not
been attributed to this mechanism; preserve that distinction. Reproducer and
review: `artifacts/validation/integrated-20261007/game-corpus/POPPY-REVIEW.md`.

Sampled GPU images need an explicit producer/source lease, not an address-only
advertisement. Specify CPU snapshot versus ordered GPU producer, typed shape,
format, mip and layer identity, retained initialization data, read/write intent,
queue dependencies and backing retirement before changing alias recovery.
The [GPU image ownership acceptance specification](validation/sampled-gpu-alias-20261007.md)
records the concrete DS failure and the positive/negative/runtime gates.
The [write-fault ownership review](write-fault-ownership.md) records the bounded
normalized write-only guard and the withheld retry prototype. A retry requires
an owner-lifetime reservation spanning tracker restoration and host mutation;
post-syscall epoch checks cannot undo a write-protection change to reused backing.

### W14 — Session lifecycle and shared-core extraction

Depends on: W06, W10-W13. Priority: P1. Destination: Core, Application.

Define process/application/session/submission/surface owners. Remove global
per-game state and callback closures that retain prior sessions. Stop admission,
unblock waits safely, join guest threads, retire GPU use, persist caches and release.
Surface native faults as session failures even if process exit status is zero.

Done when: repeated load/run/stop in one process has no growing threads/handles/
memory or previous-game state; bounded stop/cancel behavior has incremental evidence;
shared Core has no concrete platform/backend/frontend dependency cycle.

### W15 — Critical-path tracing and guest scheduling

Depends on: W04, W10-W14. Priority: P1. Destination: HLE + Diagnostics.

Trace runnable/on-core/waiting transitions with wait reasons. Correlate guest
producer, AGC enqueue/dequeue, host compile/resource preparation, GPU submit/complete,
flip and display. Audit condition/event/mutex timeouts and wake semantics; measure
lock hold time and GC/native wait contributions. Keep tracing bounded and cheap.

Done when: the dominant idle intervals are explained by evidence; any wake/lock
change improves matched producer/frame latency without starvation/timeout regressions.
Do not set arbitrary core counts/priorities from a single title's FPS.

### W16 — Metal conformance and capability qualification

Depends on: W11-W14, W04. Priority: P1. Destination: graphics/metal tests.

Define the qualification matrix for arithmetic/EXEC/waves/LDS, compute, indexed
draws, formats, tiling/mips, render/depth/stencil, aliasing, queries, CPU visibility,
ordering, presentation and stop. Use independently expected synthetic outputs;
cross-backend agreement is useful but cannot prove a shared semantic bug correct.

Done when: exercised case counts and device identities are recorded, unsupported
features fail clearly, chosen title checkpoints/progression are verified and no
unexplained correctness or lifecycle regression blocks the supported release set.
Only this gate can promote Metal to the normal Mac release default.

### W17 — Shader analysis and smaller code generation

Depends on: W11, W16 fixtures. Priority: P1. Destination: shared compiler + emitters.

Add reference/property fixtures; bound register extent/liveness conservatively;
retain dynamic-index fallback and all prologue writes. Evaluate scalar propagation,
control simplification, wave/LDS lowering and repeated loads independently by stage.

Done when: exact/tolerance-defined outputs hold, generated size/compile time and
stage GPU time improve on measured cases, and whole-title gains are measured
separately. Do not transfer Kairo's experimental speedup directly to SharpEmu.

### W18 — Bounded compilation and layered caches

Depends on: W11, W14-W16. Priority: P1. Destination: compiler/cache services.

Separate analysis, translation, resource plans, native compilation/pipelines and
archive I/O. Define complete specialization/device/version keys, duplicate work
suppression, bounded concurrency/memory, imminent-work priority, cancellation and
atomic persistence. Explore Metal archives after native pipeline correctness.

Done when: corrupt/stale caches recover, repeated requests share work, unrelated
guest locks are not held during compilation, and cold/warm startup costs have
separate matched measurements. Existing cumulative timers overlap; never add them.

### W19 — Submission and exact producer dependencies

Depends on: W13-W16. Priority: P1. Destination: Gpu scheduling + backend encoders.

Preserve guest queue FIFO and visibility; index exact physical-range producers;
reuse immutable register snapshots; coalesce redundant dirty resolution safely.
Cover WRITE_DATA-only producers, cross-queue waits and cancellation. Evaluate
preparation/encoding overlap only after caches/lifetimes are safe.

Done when: ordering/liveness fixtures and runtime images match, enqueue-to-submit
latency improves without increased drain cost or unbounded in-flight memory.
Internal signal prediction must never be early CPU-visible completion.

### W20 — Memory accounting and pressure response

Depends on: W13-W14, W16. Priority: P1. Destination: memory/backend stores.

Track managed shader data, compiled programs, buffers/images/heaps, staging twins,
leases, pinned backing and deferred retirement. Apply bounded caches and pressure
hysteresis without freeing live objects. Trace actual bytes in addition to scope counts.

Done when: accounted allocations reconcile with native/process measurements,
matched scene memory stays bounded through repeated transitions, and eviction does
not create correctness failures or unmeasured shader/staging churn.

### W21 — Audio, AJM and media boundaries

Depends on: W06-W08, W14. Priority: P1/P2. Destination: Media, Media.FFmpeg, host audio.

Separate guest pacing/queues/codec state from FFmpeg decode and host device output.
Audit audio lock scope, Wwise-facing progress, missing-port semantics, EOF/seek,
resampling/underruns and timestamp drift. Prototype native Mac output against SDL
using the same playback behavior; preserve portable fallback.

Also isolate guest font rasterization into a narrow shared contract and Fonts.Skia
adapter. Preserve font handle/metric/glyph semantics and audit RID-native Skia
assets. Font rendering is guest behavior and cannot be removed as "GUI cleanup."

Done when: fixture decoding/timing and actual movie/audio progression agree,
locks no longer stall unrelated guest work where measured, device changes/stop
are reliable, font fixtures retain expected metrics/coverage, and any native
default has explicit latency/quality evidence.

### W22 — File, archive, save and I/O behavior

Depends on: W07-W08, W14. Priority: P2. Destination: shared guest services + host files.

Separate guest path/case/access semantics from host filesystem calls. Use bounded
I/O/concurrency, reusable handles/indexes and cancellable reads where valid.
Cover case-sensitive volumes, USB/external volumes, Unicode paths, short/error
reads, save consistency and host paths. Preserve guest semantics on case-insensitive
Mac filesystems; do not inherit host behavior accidentally.

Done when: guest-visible file/save fixtures and load checkpoints work on supported
filesystems and storage; improvements distinguish measured I/O from native compiler
time. All path migrations preserve existing saves/config.

### W23 — Capture/replay and reliable input automation

Depends on: W12-W16. Priority: P1. Destination: Diagnostics + tools/capture/replay.

Version backend-neutral register/shader/resource/order capture. Include compute,
depth/stencil and intermediate outputs; reject missing/truncated ranges. Add Metal
replay, bounded incremental traces, input injection acknowledgement and recognized
checkpoints. Share synthetic minimal cases; keep game assets private.

Done when: a retained case reproduces a bug on the relevant backend without full
game boot, captures survive stop/failure, and scripted input has guest delivery
evidence. Capture overhead is labeled and excluded from performance claims.

### W24 — Measured Metal resource and presentation optimization

Depends on: W16-W20, W23. Priority: P2. Destination: Gpu.Metal.

Evaluate shared/private storage, heap suballocation, pass breaks, binding traffic,
argument buffers, copies and command batching against traces. Separate guest
resolution from internal render size, compute/stencil requirements and final
presentation. Validate Retina, HDR/SDR/color handling and frame pacing.

Done when: independently tested correctness holds and matched frame-time/latency/
memory comparisons support each change. UMA and zero-copy are mechanisms, not
automatic speedups. Advanced query/mesh/ray features get explicit capability gates.

### W25 — Typed configuration and frontend separation

Depends on: W09, W14. Priority: P2. Destination: Application + Frontend.* + apps.

Create a typed settings registry with validation, defaults, provenance and title
profiles; map legacy environment switches explicitly. Reuse command parsing and
view models; make standalone CLI independent of Avalonia and GUI artifacts retain
their intended launch behavior. Report failures and real capabilities clearly.

Done when: CLI/GUI resolve equivalent settings, CLI's dependency closure excludes
Avalonia, invalid combinations fail early, and profiles don't conceal game patches.

### W26 — Platform app profiles and Mac lifecycle

Depends on: W12, W14, W25. Priority: P2. Destination: apps + build/platforms.

Implement thin target-specific entry points and selected backend/native closure.
Preserve Mac Rosetta/GC policy. Handle application paths, portable mode, main-thread
window requirements, open requests, display changes, sleep/wake and orderly exit.
Specify supported versus unimplemented guest suspend/resume behavior.

Done when: CLI/GUI launch from supported contexts with no sibling OS dependencies;
Metal-only profile has no Vulkan/MoltenVK assets; lifecycle matrix has evidence.

### W27 — App bundle, signing and distribution qualification

Depends on: W03, W16, W26. Priority: P2. Destination: build/platforms/macos.

Build directory-based self-contained .app; validate .NET payload/native resolution,
relocation, dylib architecture and nested dependencies; include notices/manifest.
Audit actual executable memory/code generation before deriving entitlements.
Later validate signing, notarization, stapling, Gatekeeper and downloaded-artifact
launch. Keep local debug builds independent of notarization.

Done when: clean-machine Finder/CLI and relocated/quarantined bundles work without
Homebrew paths; the actual signed artifact passes distribution checks. No artifact
exists at planning time, so neither packaging nor notarization readiness is claimed.

### W28 — Update/recovery and sustained compatibility

Depends on: W16, W26-W27. Priority: P2. Destination: Application + distribution.

Define a distinct fork update identity and signed artifact verification. Preserve
saves/settings, handle interrupted updates and recovery. Run a small diverse title
matrix with recognized scenes and sustained transitions/start-stop/memory checks.
Choose budgets from baselines rather than inventing universal 60 FPS thresholds.

Done when: update recovery is demonstrated and supported release capabilities,
known limitations and scene-scoped performance evidence are published accurately.

### W29 — Continuous upstream integration and contribution tracking

Depends on: W01; applies throughout. Priority: P0 ongoing. Destination: repo/CI/docs.

Daily proposed mirror check plus manual sync; fast-forward only; integration branch
and tested sync PR; record fetched versus integrated SHAs. Refresh candidate/revert
status before reuse. Track local→upstream patch identity to avoid duplicate fixes.
Prepare architecture RFC and small independently useful changes; publication occurs
when authorized. Keep branding/distribution policy separate from common fixes.

Done when: sync status/conflicts/CI are visible, supported platforms stay covered,
accepted changes can be removed cleanly from fork-specific maintenance and no
published main history is rewritten. No scheduler is created by this plan alone.

### W30 — Later execution and UI designs

Depends on: stable M2-M4 plus evidence. Priority: P3.

Design x86-64 interpreter/translator for native ARM64 with instruction, memory
ordering, ABI, fault, threading and code-cache correctness. Separately evaluate
native ARM64 launcher/worker split, Cocoa surfaces or native shell. Consider deeper
SSA/compiler restructuring and parallel encoding only with measured bottlenecks.

Done when: each experiment has its own design and conformance/runtime gates.
An ARM64 build, helper or universal launcher never qualifies guest CPU execution.

## Validation matrix

| Change area | Required evidence |
| --- | --- |
| Mechanical package extraction | Build/dependency closure, focused existing fixtures, equivalent affected runtime checkpoint |
| Native imports/fault/ABI | Native layout/transition tests and real supported-OS execution; preserve native crash evidence |
| Memory/coherence/ordering | Alias/visibility/liveness cases on actual device plus relevant title images/progression |
| Shader/Metal changes | Independent synthetic outputs, device case counts, stage timing; matched title windows separately |
| Scheduling/audio/I/O | Guest semantics fixtures, producer/wait evidence and affected runtime progression |
| Packaging/profile | Clean publish manifest, architecture/dependency audit, relocated packaged launch |
| Release default/update | Supported capability/title matrix, lifecycle/distribution/recovery checks |

Tests should verify contracts and failure behavior, not simply mirror implementation
methods. Reuse existing suites where meaningful. Maintain a small hardware smoke
set per relevant change and a broader qualification suite at milestone/default
changes; do not rerun all titles after unrelated document/mechanical edits.

## First implementation slice

1. W01 full-history bootstrap and source provenance.
2. W02 dependency/native import map; W03 pinned build/staging baseline.
3. W04 quiet-host current-source baseline, explicit backend capabilities.
4. W05 Metal startup independent of MoltenVK.
5. W06-W08 host/toolkit separation with preserved default behavior.
6. W09 enforce the new boundaries; begin W11 backend extraction.

The first slice ends with a reviewable Metal-independent startup and isolated host
packages, not a promised whole-game performance improvement. Critical-path tracing,
ownership design and shader conformance remain visible next steps.

## Mapping to the earlier 18-area roadmap

| Earlier area | Work packages |
| --- | --- |
| 1 Session/lifecycle | W14, W26 |
| 2 CPU/ABI/faults | W10, W30 |
| 3 Scheduling/waits | W15 |
| 4 Memory/ownership | W13, W20 |
| 5 Commands/submission | W19 |
| 6 Shaders/analysis | W16-W17 |
| 7 Compilation/caches | W18 |
| 8 Metal/Vulkan implementation | W05, W11, W24 |
| 9 Images/formats/allocation | W13, W16, W20, W24 |
| 10 Presentation/resolution/color | W12, W24, W26 |
| 11 Queries/advanced GPU | W16, W24 |
| 12 Audio/media | W21 |
| 13 Files/archive/save | W22 |
| 14 Configuration | W25 |
| 15 Frontends/build/app | W03, W25-W28 |
| 16 Diagnostics/replay | W15, W23 |
| 17 Benchmarks/evidence | W04, W28 |
| 18 Upstream/architecture | W01-W02, W06-W11, W29 |
