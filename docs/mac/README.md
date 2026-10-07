# SharpEmu-Mac planning documents

Status: 2026-10-07. The repository carries full upstream history (`upstream` branch
mirrors sharpemu/sharpemu `main`; `main` is fork work). macOS is native-only with no
fallbacks: Metal, AppKit, GameController, CoreAudio (see ADR 0002). x64/Rosetta CPU
execution is retained; native ARM64 CPU execution is a later milestone.

Start with [platform-packages.md](platform-packages.md) for the recommended code,
package and dependency architecture, then [implementation-plan.md](implementation-plan.md)
for the work packages, prerequisites and acceptance gates.

- [Architecture decision](decisions/0001-platform-packages.md): choices and tradeoffs.
- [Native macOS, no fallbacks](decisions/0002-native-macos-no-fallbacks.md): supersedes Mac Vulkan/SDL fallbacks.
- [Rosetta sunset](decisions/0003-rosetta-sunset.md): native ARM64 guest execution required before macOS 28.
- [Expanded improvement roadmap](architecture-roadmap.md): all 18 improvement areas.
- [Evidence audit](evidence-audit.md): measured findings and limits of earlier runs.
- [Initial Mac proposal](macos-development-plan.md): branch/sync policy and original scope.
- [Note inventory](audit/note-inventory.json): audited local notes.
- [Runtime inventory](audit/runtime-runs.json): retained run evidence index.
- [Proposed package graph](audit/package-plan.json): target dependencies and build profiles.

The platform package proposal refines the earlier project sketches. Read source
and PR status at their recorded snapshots; recheck changing upstream/candidate
state before implementation. Existing review-only work remains review-only.

## Implementation status (2026-10-07)

Done on `main`:

- W01 bootstrap: full upstream history, `upstream` mirror branch, sync script and daily
  sync workflow ([upstream-sync.md](upstream-sync.md)).
- W03 build: osx-x64 default, osx-arm64 rejected, per-RID native assets, publish guard
  that fails macOS outputs containing Vulkan, MoltenVK or SDL, signed `.app` bundle
  (`scripts/macos/build-app.sh`), fork update identity.
- W05 Metal-only macOS: no MoltenVK, no backend fallback.
- W06-W08 host packages: `SharpEmu.Host.MacOS/Linux/Windows`, registered at startup;
  CoreAudio output on macOS.
- W11 (structure): backend-neutral GPU vocabulary; `SharpEmu.Gpu.Metal`,
  `SharpEmu.Gpu.Vulkan`, `SharpEmu.Platform.MacOS`, `SharpEmu.Adapters.Sdl`.
- W12 (macOS): AppKit window + CAMetalLayer, native keyboard, GameController pads with
  haptics/lightbar/adaptive triggers, CoreGraphics display catalog.
- Architecture tests enforce the package boundaries; the full test suite completes on
  macOS after fixing exact guest-address reservation (`mach_vm_allocate`).

Not yet verified: running real games on the native stack, physical DualSense features
and keyboard input at runtime. Open: W10 CPU/fault split, W13+ memory/lifecycle work,
further contract/package extraction, ARM64 execution (deferred, ADR 0003).

Extracted package namespaces and friend-access cleanup are documented in
[package contracts](package-contracts.md).
