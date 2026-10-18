# SharpEmu macOS development proposal

Status: discussion draft, 2026-10-07. The user confirmed Metal first with existing
x64/Rosetta CPU execution; native ARM64 CPU execution comes later. The remaining
architecture and branch policies are proposals. This document does not establish
working Metal compatibility.

The expanded discussion is in [architecture-roadmap.md](architecture-roadmap.md),
with evidence and current upstream/candidate distinctions in
[evidence-audit.md](evidence-audit.md). It extends this initial proposal to CPU
scheduling, memory ownership, audio/media, session lifecycle and benchmark tooling.

The deeper platform package design is now in
[platform-packages.md](platform-packages.md), with ordered work packages in
[implementation-plan.md](implementation-plan.md). These refine the project
sketches below, including separate OS/architecture execution integrations and
target-specific application dependency graphs.

## Starting point

The local repository has no commits or source files yet. Its origin is
`https://github.com/sharpemu/sharpemu-mac`.

Upstream was inspected at `e1edda7772e34b12b078f726560d14170bfbb077`.
It already has:

- `SharpEmu.Core`, `SharpEmu.HLE`, `SharpEmu.Libs`, CLI and Avalonia GUI projects.
- `IHostPlatform` and memory, threading, audio, input and symbol contracts.
- A Windows host and a combined Linux/macOS POSIX host.
- `IGuestGpuBackend`, Vulkan and Metal implementations, and a shared shader IR.
- Separate Vulkan/SPIR-V and Metal/MSL shader compiler projects.
- Native Metal presentation, draw and compute code and Metal runtime tests.

Metal is opt-in through `SHARPEMU_GPU_BACKEND=metal`; Vulkan is the default.
CLI startup still configures and preloads MoltenVK on macOS. Lower rendering
and pipeline interfaces such as `IRenderHost` expose Vulkan types. The Metal
renderer lives in `SharpEmu.Libs`, which also references Vulkan dependencies.

The CPU executes guest x86-64 instructions directly. A native ARM64 process is
rejected; Apple Silicon currently needs an x64 build running through Rosetta.
Native Metal graphics and native ARM64 CPU execution are separate milestones.

Sources:

- [GPU selection](https://github.com/sharpemu/sharpemu/blob/e1edda7772e34b12b078f726560d14170bfbb077/src/SharpEmu.Libs/Gpu/GuestGpu.cs)
- [Host selection](https://github.com/sharpemu/sharpemu/blob/e1edda7772e34b12b078f726560d14170bfbb077/src/SharpEmu.HLE/Host/HostPlatform.cs)
- [Rendering interface](https://github.com/sharpemu/sharpemu/blob/e1edda7772e34b12b078f726560d14170bfbb077/src/SharpEmu.Libs/Gpu/Rendering/IRenderHost.cs)
- [Contribution guidelines](https://github.com/sharpemu/sharpemu/blob/e1edda7772e34b12b078f726560d14170bfbb077/CONTRIBUTING.md)

## Repository and branch policy

Use two permanent branches:

- `upstream`: an exact mirror of `sharpemu/sharpemu:main`, without local commits.
- `main`: the integrated macOS work, preserving the complete upstream history.

Use two Git remotes:

- `origin`: `sharpemu/sharpemu-mac`.
- `upstream`: `sharpemu/sharpemu`, treated as read-only for routine sync.

The branch and remote can share the name `upstream`; scripts should spell refs
explicitly. `refs/remotes/upstream/main` is the fetched upstream branch,
`refs/heads/upstream` is the local mirror, and `origin/upstream` is its copy in
our repository. Avoid the branch name `remote`, which obscures these meanings.

Initialize both branches from the same upstream commit with full ancestry.
Do not create an independent root commit and copy upstream files into it.
Use short-lived topic branches for host extraction, Metal fixes and packaging.
Published `main` should never be periodically rebased or force-pushed.

Proposed sync behavior:

1. Check upstream daily and allow manual sync on demand.
2. Fast-forward the mirror to an explicitly recorded upstream commit. Fail and
   notify on unexpected non-fast-forward history instead of force-updating it.
3. Merge that commit into a temporary integration branch based on our `main`.
4. Open or update one upstream-sync PR. Keep conflicts visible for resolution.
5. Require Windows/Linux/macOS checks before integration; require Mac runtime
   evidence when CPU, memory, scheduling, shader or rendering behavior changes.
6. Record the last fetched and last integrated upstream SHA so mirror freshness
   and tested-main freshness are distinguishable.

Keep the mirror free of fork-specific CI changes. Control workflow execution
through repository settings or branch filters on our development branch. Avoid
duplicated CI/release runs and inherited publication/site-notification actions.
Do not automatically import upstream release tags into our release process.
Use a distinct macOS release suffix and embed both fork and upstream SHAs.

## Architecture: shared emulation, separate host implementations

Split responsibilities along independent axes: guest emulation, host OS,
graphics API, and CPU execution strategy. Keep current project names and paths
where possible until an extraction provides a concrete dependency benefit.

The intended dependency direction is:

`CLI/GUI composition -> shared emulator/contracts <- host and GPU adapters`.

The application chooses implementations. Shared emulation code must not
instantiate Metal/Vulkan devices or call AppKit/Win32 directly. Prefer existing
contracts over a parallel abstraction framework. Extract contracts only when
needed to break actual project dependency cycles.

### Shared layer

Keep loaders, guest HLE semantics, guest memory contracts, GPU command decoding,
guest registers, shader IR, guest formats, and compatibility behavior shared.
Retain the current CLI and Avalonia UI initially. Keep SDL window/input support
where it already works; a native Cocoa UI is an independent product decision.

### Host OS layer

Provide explicit macOS, Windows and Linux implementations. Extract macOS
memory mappings, Mach faults, thread-local state, symbol lookup, CoreAudio,
main-thread window lifecycle and application paths from combined POSIX paths.
Preserve genuinely identical POSIX helpers privately; avoid duplicating them.

CPU fault handling in `DirectExecutionBackend` needs its own adapter boundary
in addition to HLE host services. Fault callbacks must remain safe in their
native callback context; interface extraction must preserve register layouts,
lifetimes and performance.

Proposed project targets, subject to dependency audit:

- `SharpEmu.Host.Abstractions` (if existing HLE contracts need extraction).
- `SharpEmu.Host.MacOS`, `SharpEmu.Host.Windows`, `SharpEmu.Host.Linux`.

OS checks belong at selection/capability boundaries, rather than throughout
shared algorithms. Do not introduce one generic interface per OS function.

### Graphics layer

Build on `IGuestGpuBackend` and existing compiler separation:

- Shared GPU semantics and shader IR.
- `SharpEmu.Gpu.Vulkan`: devices, descriptors, barriers, native resources,
  Vulkan pipeline caches and Vulkan presentation.
- `SharpEmu.Gpu.Metal`: Metal devices, encoders, resource lifetime, texture
  storage, synchronization, Metal pipeline caches and Metal presentation.
- Existing `SharpEmu.ShaderCompiler.Vulkan` and `.Metal` projects.

These are eventual project boundaries, not an instruction to move all files
immediately. Start with folders and controlled dependencies, then extract
projects once callbacks and cycles are understood.

Vulkan-specific lower interfaces can remain private to Vulkan. Generalize only
semantics that both backends actually share. Do not make Metal implement a
Vulkan-shaped interface. Shared contracts must not expose Vulkan enum values,
Metal objects or native resource handles with backend-dependent meanings.

Translate guest shaders through the existing shared IR into SPIR-V or MSL.
Keep guest address/aliasing behavior shared while each backend owns physical
resources, synchronization and API-specific optimizations. Unified memory does
not remove guest CPU/GPU coherence, dirty tracking or lifetime requirements.

### CPU architecture

First target: `osx-x64` with native Metal, on Intel or under Rosetta on Apple
Silicon. Preserve `INativeCpuBackend` as the starting execution boundary.

Native `osx-arm64` guest execution requires a separate x86-64 execution engine,
such as a dynamic translator or interpreter, with instruction, ABI, fault,
memory-ordering and threading correctness. Publishing for ARM64 or calling
Metal from an ARM64 helper does not solve guest execution. Make this a separate
design decision after measuring the x64/Rosetta CPU cost.

## Delivery sequence and acceptance

### 0. Bootstrap and baseline

Import full upstream history, establish the branch policy, audit inherited CI,
pin the SDK/native dependencies, document a reproducible Mac build, and record
current Vulkan and Metal behavior at the same upstream SHA. Include small
synthetic fixtures and repeatable title checkpoints. Do not assume older fixes
or runtime results are present in the current upstream revision.

Acceptance: reproducible CLI/GUI artifacts, three-OS build/test baseline,
and an honest capability matrix distinguishing exercised and untested paths.

### 1. Make backend selection self-contained

Select the GPU backend before loading backend-specific libraries. Explicit
Metal selection must run without MoltenVK installed or staged. Preserve Vulkan
as an explicit comparison option. Extend the current configuration surface and
report the actual selected backend and relevant capabilities.

Acceptance: Metal starts without Vulkan libraries; Vulkan still works when
selected; missing/unsupported configurations produce actionable errors.

### 2. Extract platform services incrementally

Split macOS/Linux host differences and CPU fault adapters in focused steps.
Separate mechanical moves from behavior changes. Validate each service contract
on its supported OS and preserve the existing x64 execution model.

Acceptance: equivalent baseline behavior, no new Windows/Linux regressions,
and enforceable dependency boundaries for the extracted components.

### 3. Qualify and improve Metal

Audit the current renderer before adding another one. Exercise shader semantics,
compute, indexed draws, textures, render/depth targets, format conversion,
detiling, aliasing, CPU/GPU visibility, submission order and shutdown. Compare
with independent fixture outputs as well as Vulkan; backend agreement alone is
not a correctness oracle. Explicitly report unsupported features.

Measure frame-time distributions, CPU/GPU timing, stalls, pipeline compilation,
memory growth, cold/warm caches and sustained progression. Optimize measured
costs. Change the Mac default to Metal only after a defined qualification set
passes; keep the explicit Vulkan option until Metal is dependable enough to
remove the fallback from normal distribution.

### 4. Mac application and distribution

Provide `.app` packaging, reliable relative native-library resolution, icons,
Finder launch/open behavior, application-support/cache locations, Retina and
fullscreen behavior, resize/display changes, sleep/wake and clean exit. Verify
CLI and GUI launch separately. Add signing/notarization and DMG packaging after
bundle behavior is stable; validate entitlements against the actual CPU model.
Avoid a universal-binary claim until both CPU architectures really work.

### 5. Upstream contribution and further CPU work

Prepare a design RFC before broad restructuring; contact maintainers only when
the user asks to publish/send it. Submit useful host fixes, build improvements,
dependency boundaries and Metal changes in focused PRs as they become ready.
Do not wait for one giant final merge. Keep fork packaging policy separable
from common emulator improvements, and track accepted local changes so future
syncs do not duplicate them.

## Validation policy

- Windows and Linux retain build/test coverage throughout Mac development.
- Metal hardware tests must record that GPU execution actually happened.
  Existing tests can return without a Metal device; a green suite alone does
  not establish Metal runtime correctness.
- Use synthetic cases for exact shader, memory and ordering behavior.
- Use the shared single-game lock harness for local title runs once its paths
  and compatibility with this checkout have been verified.
- Record title/build/backend/configuration, hardware/OS, cache state, checkpoint,
  frame image, performance and shutdown. Keep private game assets out of Git.
- Distinguish startup, first frame, menu, gameplay, correctness, sustained
  performance and shutdown as separate outcomes.

## Open decisions

Confirmed initial scope: Metal and macOS host/build quality first, retaining
x64/Rosetta execution. Decide the minimum macOS version and required Intel/Apple
Silicon hardware coverage after querying actual available devices and baseline
Metal capabilities. A native Cocoa UI and native ARM64 CPU execution remain
independent roadmap choices.
