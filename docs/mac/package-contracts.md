# Extracted package contracts

The extracted assemblies use their own namespaces: `SharpEmu.Gpu.Metal`,
`SharpEmu.Gpu.Vulkan`, `SharpEmu.Platform.MacOS`, `SharpEmu.Adapters.Sdl` and
`SharpEmu.Host.MacOS/Linux/Windows`. The mechanically shared POSIX stub emitter
remains linked source under `SharpEmu.Host.Posix`; it is not a platform fallback.

HLE and Libs no longer grant production friend access to any extracted package.
MacOS presentation and SDL adapters no longer grant their graphics backend friend
access either. Existing `SharpEmu.Core` and `SharpEmu.Libs.Tests` grants in HLE/Libs
are retained: Core still consumes the pre-existing execution/loader internals,
and tests inspect state that must not become a production API. Extracted packages
grant access only to the test assembly.

The public boundary consists of:

- Host memory validation and mapping bookkeeping, window input, and backing
  ownership. A backing object's handle/descriptor can only be initialized;
  view operations and disposal use the same gate. Disposal calls the owning
  host's release callback once, under that gate.
- Guest GPU backend interfaces, opaque compiled shader interfaces, guest resource
  descriptors and staging ownership. These contracts contain no native graphics
  handles. The shared guest-data pool is used by both sides of ownership transfer.
- `AgcBackendServices` for command translation and guest texture semantics;
  `VideoOutBackendServices` for display state, flip reservation/retirement and
  presentation reporting; `GuestBackingServices` for PRT-backed resource reads;
  `MoviePresentationServices` for frame delivery/layout; `ShaderPrewarmCompiler`
  for recorded shader compilation through the same resource plan as live work.
- Backend/platform observation methods on the shared profiling/trace services.
  Their aggregation buffers, mutable shader context and test snapshots remain
  internal. They retain allocation-free scope types on the render path.
- Explicit concrete platform window APIs, including the SDL window owned by its
  adapter. The Vulkan-only descriptor scratch-array pool now lives inside Vulkan;
  its implementation is not exported by the shared engine.

`ShaderProgramCache`, `HostMovieBridge`, `HostMovieYuv420` and the headless test
stream stay internal. Vulkan's test stream hook consumes a small
`IGuestCommandStream` interface rather than the headless implementation. The
scheduler's cross-assembly fatal-report test forwarding was removed: the test
assembly accesses the existing shared test hook directly.

This makes the current seam reviewable without extracting a new abstractions
assembly and changing the whole dependency graph at once. Some shared DTOs and
translation types still live under AGC/VideoOut namespaces; moving them to a
separate contracts package is a later mechanical step. Public integration APIs
are not a commitment to binary compatibility while this fork is being developed.

## Validation scope

`ExtractedPackageContractTests` enforce owning namespaces, reject new production
friend grants, check the compiled HLE/Libs friend metadata, keep stateful
implementations private and check backing release ownership. Existing host and
backend vocabulary tests continue to enforce inward dependencies.

The focused managed test selection passes 190 tests on ARM64 and 190 under x64
Rosetta. It includes architecture rules, scheduler/order/failure tests, presenter
startup fixtures, shader prewarm, profiler counters, scratch ownership and pad
axis conversion. No game, window, audio-device or GPU-device run is included in
those results. Real game and native device verification must be performed on the
integrated main checkout with the serialized runtime harness.

Release CLI builds pass for `osx-x64`, `win-x64` and `linux-x64`. Windows and
Linux are cross-build checks on macOS, not runtime verification on those OSes.
The macOS CLI dependency manifest contains Metal and the macOS host and contains
no Vulkan, MoltenVK or SDL library. Existing compiler nullable/unused-field and
platform-annotation warnings remain; these checks do not claim warning-free CI.
