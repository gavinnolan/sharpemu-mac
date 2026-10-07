# Platform package architecture

Status: proposed implementation architecture, 2026-10-07. This document expands
the [architecture roadmap](architecture-roadmap.md) and is the authoritative
package-boundary proposal where the earlier sketches differ. It does not claim
these packages exist. The repository still contains planning documents only.

Confirmed scope: native Metal first, existing x64/Rosetta guest execution first,
native ARM64 guest execution later. Windows and Linux remain supported development
targets. Evidence and source provenance are in [evidence-audit.md](evidence-audit.md).
Execution order and completion gates are in [implementation-plan.md](implementation-plan.md).
The machine-readable [package graph](audit/package-plan.json) describes 40 eventual
runtime projects, including existing shared projects, six thin application entry
points and optional adapters. It is a target graph, not 40 projects to create in
the first change. Native dependency packages and existing build/debug tools are
tracked separately from that owned runtime graph.

## 1. The central decision

Use a single monorepo with separately compiled platform packages. Separate five
independent axes: guest semantics, host OS, execution strategy/process architecture,
graphics API, and frontend/distribution. A macOS build selects a macOS dependency
graph; it should not merely carry Windows/Linux implementations behind OS checks.

Prefer SDK-style C# projects and project references during development. Package
boundaries are assembly/dependency boundaries, not a requirement to publish every
assembly to NuGet. Keep managed packages on the pinned upstream .NET target
initially. Package native dependencies by explicit target RID. Public package
publication and independently versioned packages are unnecessary for the fork.

Create a separate project when it removes a native/framework dependency, isolates
unsafe ABI code, enables independent consumption/testing, or changes the supported
runtime. Use folders for smaller responsibilities within that project. This gives
more platform isolation without a project per syscall or per Metal object.

## 2. Proposed repository layout

This is the eventual physical layout. Preserve existing paths while introducing
the first seams; do not rename everything during upstream bootstrap.

```text
src/
  shared/
    SharpEmu.Host.Abstractions/
    SharpEmu.Memory.Abstractions/
    SharpEmu.Cpu.Abstractions/
    SharpEmu.Gpu.Abstractions/
    SharpEmu.Presentation.Abstractions/
    SharpEmu.Memory/
    SharpEmu.HLE/
    SharpEmu.Libs/
    SharpEmu.Gpu/
    SharpEmu.Core/
    SharpEmu.Application/
    SharpEmu.Media/
    SharpEmu.Diagnostics/
    SharpEmu.Logging/
  execution/
    SharpEmu.Cpu.DirectExecution/
  graphics/
    SharpEmu.Gpu.Metal/
    SharpEmu.Gpu.Vulkan/
  shaders/
    SharpEmu.ShaderCompiler/
    SharpEmu.ShaderCompiler.Metal/
    SharpEmu.ShaderCompiler.Vulkan/
  adapters/
    SharpEmu.Adapters.Sdl/
    SharpEmu.Media.FFmpeg/
    SharpEmu.Fonts.Skia/
  platforms/
    macos/
      SharpEmu.Host.MacOS/
      SharpEmu.Cpu.DirectExecution.MacOS.X64/
      SharpEmu.Platform.MacOS/
    windows/
      SharpEmu.Host.Windows/
      SharpEmu.Cpu.DirectExecution.Windows.X64/
      SharpEmu.Platform.Windows/
    linux/
      SharpEmu.Host.Linux/
      SharpEmu.Cpu.DirectExecution.Linux.X64/
      SharpEmu.Platform.Linux/
  frontends/
    SharpEmu.Frontend.Cli/
    SharpEmu.Frontend.Avalonia/
  apps/
    macos/                    # Thin CLI and GUI composition projects
    windows/
    linux/
native/
  macos/                      # Optional narrow native shims, only if needed
  windows/
  linux/
build/
  profiles/                   # Explicit RID/backend/frontend publish profiles
  platforms/{macos,windows,linux}/
  dependencies/               # Version/hash/license/ABI manifests
tests/
  shared/
  contracts/
  platforms/{macos,windows,linux}/
  graphics/{metal,vulkan}/
  runtime/
tools/
  capture/
  replay/
  benchmark/
  architecture/
docs/
  decisions/
```

Native binary outputs live under build artifacts or a local package feed, not in
source directories. Preserve existing generators, codec projects, debugger and
debug client; their extraction follows actual dependency findings. A debugger
server belongs below the application layer; a debug client is a separate consumer.

Keep C# as the engine language. Use a small C/Objective-C shim only where a verified
ABI/fault-safety requirement or measured interop cost warrants it. Such shims have
a narrow versioned C ABI, fixed native layout/calling-convention tests and explicit
callback ownership. Keep native headers/declarations in the owning OS or graphics
package. Swift is a possible later UI implementation, not a prerequisite for Metal.

## 3. Package responsibilities

### Contracts: domain ownership, not a universal Contracts assembly

| Package | Owns | Must not reference |
| --- | --- | --- |
| `Host.Abstractions` | Host memory/thread/clock/symbol/audio/input service contracts and capability data | Core, Libs, SDL, OS implementation packages |
| `Memory.Abstractions` | Guest address/range types, physical backing identity, lifetime leases, access/version semantics | Graphics APIs, host implementation classes |
| `Cpu.Abstractions` | Execution interface, stable guest execution context/options and failure results | DirectExecution, OS contexts, loader implementations |
| `Gpu.Abstractions` | Guest submission/shader/resource contracts, completion tokens and capabilities | Metal/Vulkan/SDL/native surface handles |
| `Presentation.Abstractions` | Window/surface lease, drawable size, presentation scheduling and lifecycle contracts | Avalonia, SDL, concrete graphics bindings |

Extract each only when needed to break a real dependency cycle. Keep symbols
internal unless another assembly needs them. Preserve old interfaces where their
meaning is sound; use bounded compatibility facades during migration. Do not add
one forwarding interface around every method.

Presentation interoperability is an intentional exception to guest-contract
purity: OS-tagged native surface data may cross a narrow borrowed surface lease.
Only a backend's presentation bridge may consume it. Never expose that data to
HLE, guest command decoding or shader IR. Ownership, main-thread requirements,
resize generations and release order must be part of the lease contract.

### Shared engine packages

| Package | Owns | Important dependency rule |
| --- | --- | --- |
| `Memory` | Guest VA mappings, physical identity, aliases, dirty tracking and CPU/GPU ownership | Host memory is injected; backend allocations stay outside |
| `HLE` | Guest kernel/thread/wait/clock/ABI-facing services | No SDL, device drivers or concrete CPU engine |
| `Libs` | Guest library exports and guest-visible library behavior | Delegate GPU/media/audio work through contracts |
| `Gpu` | Guest packets/registers, submission order, dependency analysis and resource descriptions | No Silk.NET.Vulkan, MetalNative or window toolkit |
| `ShaderCompiler` | Guest shader decoding, semantics, IR, analysis and legal shared transforms | Backend emitters depend inward, never the reverse |
| `Media` | Codec contracts, guest media state, queues, timing and portable codec logic | No FFmpeg.AutoGen or host playback device APIs |
| `Libs` font service | Guest font handles, guest metrics/rasterization behavior and a narrow rasterizer contract | SkiaSharp belongs in Fonts.Skia, not in guest exports |
| `Core` | Executable loading, engine/session state and coordinating injected services | No concrete platform, graphics, frontend or FFmpeg adapter |
| `Application` | Settings/profiles, title library, user actions, session supervision and application state | No GUI framework or native window implementation |
| `Diagnostics` | Bounded event model, trace/capture schema and run provenance | No concrete renderer; native probes stay with adapters |

`Core` may use HLE/Libs through their shared entry points. HLE/Libs cannot reference
Core to obtain a loader or session: extract a narrow callback/service contract
owned by the consuming domain. `Application` depends on Core, not the reverse.

Do not move a utility into shared code just because two platforms have functions
with similar names. Their native struct layout, errno, signals, clocks and VM
semantics may differ.

### Platform host packages

Each `Host.<OS>` is a concrete OS-services package with these internal areas:

```text
Memory/        Threading/     Clocks/       Symbols/
Files/         Audio/         Input/        Power/
Paths/         Diagnostics/   Interop/
```

macOS owns Mach/VM primitives, macOS pthread/TLS details, executable mappings,
actual host page geometry, CoreAudio implementations, system controller support
when added, power/display notifications, application paths and native diagnostics.
Linux owns its mmap/mprotect/signals/futex capabilities, ELF symbol loading, ALSA
or an explicitly selected audio adapter, XDG paths and native diagnostics. Windows
owns VirtualAlloc/protection, Win32 threading/TLS, PE symbols, its audio drivers,
application paths and native diagnostics. Capabilities describe supported behavior;
shared code does not guess behavior from an OS string.

The inspected default POSIX host uses SDL audio. Preserve that behavior through
injection initially; moving existing CoreAudio/ALSA implementations does not make
them qualified defaults. Native output migrations require latency, underrun,
clock, device-change and shutdown evidence.

No combined public `Host.Posix` package. Share genuinely identical private helpers
by explicit source inclusion where useful; require tested per-OS native declarations.
Create a common helper assembly only if its dependency and reuse benefits exceed
another permanent package. macOS and Linux do not share fault context layouts.

A future `Host.MacOS.Audio` or `.Desktop` package is justified when there is a
second independent consumer, framework/deployment requirement, or substantially
different lifecycle. Initially keep these subareas in Host.MacOS or the dedicated
desktop adapter. Do not split all subareas simply to maximize package count.

### Execution packages: OS and architecture are separate from graphics

`Cpu.DirectExecution` owns reusable x86 instruction patching, guest import/ABI
bridging and direct execution policy. Its OS integration entry points are scoped
to this implementation; the general Cpu.Abstractions contract stays independent
of direct execution. The existing INativeCpuBackend is the migration starting point.

The three `Cpu.DirectExecution.<OS>.X64` packages own their native context layouts,
exception/signal installation, host/guest stack transition, TLS and native worker
details. They construct the shared execution engine with the appropriate adapter.
Shared DirectExecution never references these concrete packages.

macOS Mach exception handlers and Rosetta mitigation policy belong to the Mac
execution integration, not to the Metal renderer. Existing Windows-specific
IHostFaultHandling is a useful implementation seam, not proof that the same
Windows first-chance-filter vocabulary fits Mach and Linux signals.

Fault callbacks must preserve allocation-free, nonblocking native safety where
required. No reflection, logging dispatch, Task creation, service lookup or new
virtual hot path is added casually. ABI structs and callback delegates retain
layout and lifetime tests. Process-wide fault hooks require an explicit owner and
reference/lifetime discipline; session disposal must not tear them down while a
guest thread can still use them.

Future ARM64 translation gets a new execution implementation. Process architecture,
physical CPU architecture and guest ISA are three distinct capability fields.
An ARM64 launcher or Metal helper does not establish native ARM64 guest execution.

### Graphics and presentation packages

`Gpu.Metal` owns Metal resources, encoder/pass organization, native synchronization,
pipelines/archives, device probes and its presentation bridge. `Gpu.Vulkan` owns
the corresponding Vulkan implementation, including existing Vulkan-shaped lower
interfaces. Each depends on its existing shader emitter and the shared GPU seam.

Extract decoding/register/submission logic into Gpu incrementally. Some resource
preparation remains backend-specific: do not create a lowest-common-denominator
rendering API that forces Metal to simulate Vulkan barriers or descriptor sets.
Shared dependency analysis describes required guest ordering and visibility;
backends supply the native synchronization needed to satisfy it.

`Adapters.Sdl` initially owns SDL window/input/audio adapters. It depends on
presentation and host contracts, not concrete renderers. A negotiated surface
factory bridges SDL-created windows to the chosen backend. Audit SDL's Metal view
and Vulkan surface creation calls explicitly; hiding native handles in `object`
without ownership rules is not isolation.

Metal can create a headless device/compute context without creating a window.
The core can be tested with fake GPU/audio/input implementations without loading
SDL. Headless CLI emulation may deliberately use SDL audio; it still must not
load Avalonia or initialize SDL video unnecessarily.

Keep Avalonia initially. A future Cocoa/SwiftUI shell would be another consumer
of Application. AppKit surface support and a fully native UI are independent
changes; neither should require rewriting guest emulation.

SkiaSharp currently implements guest font behavior in Libs/Font; it is not merely
a frontend dependency. Extract a rasterization adapter into Fonts.Skia and keep
guest handles/metrics and the font contract in the shared guest layer. Validate
font loading, fallback, metrics and glyph coverage before removing Skia references
from Libs. A future system font adapter must not silently change guest output.

### Platform composition packages and applications

`Platform.MacOS`, `.Windows`, `.Linux` are thin factories for their host and CPU
implementations, supported capability probes and platform runtime policy. They
do not import GUI or graphics backends. Optional adapters are supplied explicitly.
They depend inward; shared Core does not reference them.

`Frontend.Cli` parses commands and calls Application. `Frontend.Avalonia` binds
view models/windows to Application. Neither chooses concrete OS implementations.
Use distinct thin platform CLI and GUI application projects once shared command
and UI logic has been extracted. Six small entry points are cheaper to maintain
than embedding every OS and frontend dependency in a universal executable.

An application/publish profile explicitly selects:

- target OS and process RID;
- direct-execution integration;
- included graphics backends and requested runtime backend;
- frontend and presentation adapter;
- media/audio adapters and their native assets;
- platform runtime configuration, bundle resources and release channel.

Use static composition and explicit factories. No automatic plugin discovery,
Assembly.Load scanning, independent-version backend ABI or hot reload initially.
This reduces startup surprises and preserves the guest-to-managed warmup audit.
Version all owned packages together with fork SHA and integrated-upstream SHA.

## 4. Dependency enforcement

Enforce direct project references AND the transitive publish closure. The latter
catches unwanted native libraries that an apparently portable project imports.

Rules for the proposed architecture checker:

1. Shared guest/engine packages cannot reference any concrete OS, graphics,
   frontend, SDL or FFmpeg adapter package, including transitively.
2. No OS implementation may reference another OS implementation. Native imports
   belong in an allowed platform/adapter package, not HLE or Libs.
3. Metal and Vulkan cannot reference each other. Their emitter dependencies are
   explicit; the shared compiler cannot reference either emitter.
4. Frontend.Cli cannot reach Avalonia. Core cannot reach GUI frameworks;
   frontend code calls Application rather than manipulating Core internals.
5. The generic CPU contract cannot expose OS fault structs or direct-execution
   machinery. Only the execution integration layer can access raw contexts.
6. Common OS checks are allowed only in an explicit selection/probe/validation
   allowlist; backend feature checks are capability checks, not OS checks.
7. New InternalsVisibleTo declarations need a stated purpose. No blanket friends
   or mutable global service locator used to bypass a project cycle.
8. Architecture tests must examine project assets/publish manifests as well as
   source namespaces. Platform API analyzers supplement those rules.

Use the existing target framework plus OS annotations where appropriate; do not
assume net10.0-macos is a drop-in replacement for the current desktop .NET setup.
Apple workload/binding adoption would be a separate build decision. Keep existing
manual P/Invoke or generated imports where correct and measured. .NET's platform
analyzer supports OS annotations but does not detect every custom native import.
[Platform analyzer documentation](https://learn.microsoft.com/en-us/dotnet/standard/analyzers/platform-compat-analyzer).

## 5. Native dependency packages and build profiles

First extraction candidates: FFmpeg asset acquisition, SDL/Skia assets and the optional
MoltenVK distribution. Use reproducible RID-scoped native dependency packages or
equivalent explicitly manifested staging targets. Upstream-owned managed projects
remain source references. Third-party managed bindings remain pinned dependencies.

Example native package content, with actual versions/hashes filled during bootstrap:

```text
SharpEmu.Native.FFmpeg.<version>.nupkg
  runtimes/osx-x64/native/<FFmpeg dylibs>
  runtimes/win-x64/native/<FFmpeg DLLs>
  runtimes/linux-x64/native/<FFmpeg shared libraries>
  licenses/
  dependency-manifest.json
```

Use portable RIDs such as osx-x64, win-x64 and linux-x64. ARM64 assets are added
only for consumers that actually support them. Native assets use
`runtimes/<rid>/native`; their nested directories are flattened by SDK copying,
so filename collisions and dependency install names require validation. A custom
bundle-layout staging step is separate from NuGet asset selection. NuGet RID
selection does not automatically remove managed PackageReferences by OS.
[Native asset guidance](https://learn.microsoft.com/en-us/nuget/create-packages/native-files-in-net-packages),
[RID guidance](https://learn.microsoft.com/en-us/dotnet/core/rid-catalog).

Do not create a "Mac" meta-package that depends on both GPU backends and then
expect RID selection to remove Vulkan. Explicit application profile references
must govern managed/native dependency inclusion. Profiles name the build target,
not the OS of the machine performing the build. Restore/build outputs must be
isolated by profile/RID to prevent stale files leaking between builds.

| Profile | Included graphics | Initial process RID | Purpose |
| --- | --- | --- | --- |
| macos-metal | Metal | osx-x64 | Metal-only development and qualification artifact |
| macos-compare | Metal + Vulkan/MoltenVK | osx-x64 | Controlled A/B and compatibility work |
| windows-vulkan | Vulkan | win-x64 | Preserve upstream Windows runtime |
| linux-vulkan | Vulkan | linux-x64 | Preserve upstream Linux runtime |
| portable-fixtures | Fake implementations | Test host RID | Engine/contract fixtures without native graphics |

Metal-only distributions become the normal Mac release after qualification.
Until then, a clearly labeled comparison build may retain Vulkan. Explicit Metal
selection must never silently fall back to Vulkan after device creation fails.
Report requested backend, actual backend, unsupported capability and dependency
version. Missing device support is a visible failure or an explicit user-selected
fallback before execution.

Native acquisition is a separate restore/fetch step with pinned URL/version,
checksum, architecture, ABI, license and expected library list. No unverified
download side effect in ordinary shared-library compilation. Validate unpacked
archives, support an already prepared offline cache and stage only selected assets.
Preserve upstream FFmpeg compatibility initially rather than changing codec ABI
during packaging extraction. Treat package license/build configuration as data;
do not assume every FFmpeg binary has identical licensing.

## 6. Lifecycle and interop ownership

Use explicit constructors at composition roots. Inject narrow dependencies into
guest services; do not pass an all-powerful PlatformServices bag everywhere.
Dependencies are initialized in stages, not all in static type initializers.

| Scope | Examples | Owner |
| --- | --- | --- |
| Process | Fault interception, native runtime initialization, UI main loop | Platform application; bounded shared owners |
| Application | Settings, title library, selected device policy | Application/composition root |
| Session | Guest VA, threads, HLE exports, GPU queue, audio streams | EmulationSession |
| Submission | Physical backing leases, command resources, native completion | GPU backend + shared ownership ledger |
| Surface generation | Drawable/view for a window size/display | Presentation adapter + backend bridge |

Guest stop: stop admitting work, request guest-thread exit, unblock permitted
waiters, join/stop execution, retire submitted GPU work, persist validated caches,
release session resources. Keep native callbacks alive until their users stop.
Orderly stopping is not accomplished by passing a CancellationToken into every
native operation. Cancellation of a CPU wait does not retire its GPU resources.

SafeHandle-like owners are suitable for ordinary native resources; ref-counted
Objective-C ownership and Metal in-flight resource retention must be explicit.
Finalizers are last-resort cleanup, not the normal GPU lifetime policy. Core's
memory ownership ledger tracks actual backing and visibility regardless of API.

Eager JIT/type initialization currently protects managed transitions on guest
stacks. Splitting assemblies can change that reachable graph. Preserve warmup
until native bridge fixtures and an audited generated manifest support a change.
No blanket trimming, NativeAOT or GC-policy cleanup during mechanical extraction.
The existing Mac non-concurrent GC setting is process policy, not GPU policy;
carry it into the Mac application profile and revalidate with Rosetta runs.

## 7. macOS distribution design

Use a self-contained, directory-based .app as the initial bundle target. Prefer
explicit native files and stable relative resolution over native self-extraction
inside a compressed single-file app. Keep the current single-file format as a
comparison/legacy profile until the new bundle is verified. This is a proposed
tradeoff, not a measured startup improvement. .NET supports native extraction
and documents its path and startup implications.
[Single-file guidance](https://learn.microsoft.com/en-us/dotnet/core/deploying/single-file/overview).

```text
SharpEmu-Mac.app/Contents/
  Info.plist
  MacOS/                      # apphost, managed runtime payload/config
  Frameworks/                 # selected third-party native dylibs/shims
  Resources/                  # icons, localization, notices, version manifest
```

The actual .NET payload placement and resolver/install-name rules must be proven
by a packaged launch, not assumed from this sketch. Apple system frameworks are
linked/loaded from the OS; do not copy Metal/AppKit/CoreAudio into the app.
Use per-assembly native resolution for owned libraries, validated architecture
slices and stable @rpath/@loader_path relationships. Bundled dylibs must resolve
their own transitive dependencies after relocation. No user-installed Homebrew
library path should be needed by the distributable artifact.

Signing/notarization is a later distribution gate: audit actual code generation,
executable memory and third-party libraries, derive minimal justified entitlements,
sign nested code, validate signatures, submit/staple/assess and test the downloaded
artifact. Local unsigned debug builds do not require notarization. No exported
artifact exists yet; this plan establishes no distribution readiness.
[Apple distribution guidance](https://developer.apple.com/documentation/security/notarizing-macos-software-before-distribution).

Store saves/config under Application Support and disposable caches under Caches;
include explicit portable-mode behavior and migration from existing locations.
Maintain distinct fork bundle identifier, release channel, updater identity and
version manifest. Automatic updates must preserve saves and provide recovery.
Finder launch, CLI launch, quarantine, spaces/non-ASCII paths, external volumes,
read-only installation, resize/display changes, sleep/wake and graceful exit all
need separate acceptance evidence. Avoid promising resumable guest sleep until
guest/device time semantics have been implemented.

## 8. Concrete source extraction map

Inspected source snapshot: e1edda7772e34b12b078f726560d14170bfbb077. Paths below are
relative to upstream src/. Recheck at the implementation base before moving files.

| Existing area | Target | Preserve/check |
| --- | --- | --- |
| HLE/Host/IHost* and neutral host types | Host.Abstractions | Classify GuestAudioClock/guest semantics separately |
| HLE/Host/Windows | Host.Windows | Native declarations and audio equivalence |
| HLE/Host/Posix | Host.MacOS + Host.Linux | Split per file/function; no blind directory move |
| HLE/Host/Sdl, WindowHostInput | Adapters.Sdl | Subsystem ownership and injected defaults |
| Core/Cpu/Native/Windows | Cpu.DirectExecution.Windows.X64 | Context layout, handler lifetime, thunks |
| Core/Cpu/Native/*MachExceptions*, *PosixSignals*, *PosixSignalStack* | OS execution integrations | Distinguish shared policy from native ABI |
| Core/Cpu/Native/remaining engine | Cpu.DirectExecution | HLE context/import graph and warmup dependencies |
| Libs/Gpu/Metal | Gpu.Metal | SDL presenter boundary and native retention |
| Libs/Gpu Vulkan-specific rendering/pipelines | Gpu.Vulkan | Keep Vulkan-shaped interfaces private |
| Libs/Gpu guest packets/registers | Gpu + Gpu.Abstractions | Move only backend-neutral semantics |
| Libs media/FFmpeg bindings | Media + Media.FFmpeg | Guest queue/clock separate from decode adapter |
| Libs/Font SkiaSharp usage | Shared font contract + Fonts.Skia | Preserve guest glyph metrics and native asset selection |
| CLI's GUI startup coupling | Frontend.Cli + platform app roots | Retain default GUI launch in GUI artifact |
| CLI FFmpeg download/copy targets | Native dependency staging | Explicit target RID and verification |
| Core/Diagnostics/WindowsCrashCapture.Native | Host.Windows or Windows execution diagnostics | Stack/register capture ownership |
| CLI OS GC/publish settings | build/platforms + app profiles | Preserve existing Rosetta mitigations |

More fine-grained source classification is a first implementation deliverable,
including every native import and project reference. This map is grounded in the
inspected snapshot but is not an assertion that every method is already separable.

## 9. Decisions and remaining measurements

Recommended now: monorepo; static composition; platform-specific projects;
separate execution integrations; backend-specific rendering; headless CLI; RID
native assets; shared guest state; qualified Metal default; versioned run evidence.
See [ADR 0001](decisions/0001-platform-packages.md) for tradeoffs.

Needs measurement before a product promise: minimum macOS deployment version,
Intel GPU coverage, native audio default, Metal feature floors, performance/memory
budgets, .app payload layout, signing entitlements and update mechanism. Maintain
an explicit device capability matrix; don't pick an OS floor from SDK availability
alone. Native ARM64 execution and a native Cocoa UI remain later designs.
