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
- W10 structure: `SharpEmu.Cpu.DirectExecution.{MacOS,Linux,Windows}.X64`
  owns native fault handling, thread/TLS primitives and host memory probes. Core
  retains shared guest recovery. See [CPU validation](validation/cpu-platform-split-20261007.md).
- W11 (structure): backend-neutral GPU vocabulary; `SharpEmu.Gpu.Metal`,
  `SharpEmu.Gpu.Vulkan`, `SharpEmu.Platform.MacOS`, `SharpEmu.Adapters.Sdl`.
- W12 (macOS): AppKit window + CAMetalLayer, native keyboard, GameController pads with
  haptics/lightbar/adaptive triggers, CoreGraphics display catalog.
- Extracted namespaces match their assemblies, and explicit host/backend contracts
  replace extracted-package production friend access. Architecture guards check
  source ownership and compiled friend metadata.
- Native Metal image/ALU work is covered by actual device compilation and numerical
  readbacks. Guest presentation cadence is measured separately from host redraws.

Real-game results and screenshots are retained under `artifacts/runs/`; see
[game baseline](game-baseline.md) and the [integrated validation](validation/integration-20261007.md).
Dreaming Sarah renders outdoor gameplay and responds to directional input;
Void Terrarium reaches a readable menu, then a near-black transition without
verified gameplay. The b557990 follow-up clears the former atomic abort in
Silent Hill, Octopath and Oregon, and the texture-cache abort in Superliminal
and Void. Loading/progression and visual issues remain; Poppy and Demon's Souls
retain concrete CPU-fault and GPU alias failures. Physical
DualSense features and native Windows/Linux fault handling remain unverified.
Open: W13+ memory/lifecycle work, further contract/package extraction, shader/resource
coverage and ARM64 execution (deferred, ADR 0003). Process shutdown and clean guest
exit remain separate acceptance checks.

Extracted package namespaces and friend-access cleanup are documented in
[package contracts](package-contracts.md).

## Latest runtime status (2026-10-08)

The local `4b5e0fa` build integrates a bounded, independently checked tile5 R16Uint
volume path and a disabled GPU texture ownership graph. Combined managed checks
pass200/200 on ARM64 and Rosetta; integrated native materialization passes40/40
under Rosetta. The separate actual compute consumer oracle passes4/4 on each
architecture, while production graph activation remains unfinished.

Game verification caught a regression: the added CPU snapshot freshness guard
rejects three attempts in both Poppy and Sarah. Poppy aborts before any frame;
Sarah presents eight initial logo frames and stalls, without the earlier
dd19db6 scene/movement/jump progression. That earlier evidence is historical,
not current acceptance. Snapshot diagnosis/fix and fresh game verification are
required before claiming the current build improves compatibility. See the
[ownership/runtime journal](validation/w13-ownership-iteration-20261007.md).

Guest font isolation/CoreText rendering is in progress. The Avalonia launcher
still carries Skia; a native launcher/settings extraction is a separate remaining
dependency, even after guest fonts stop using Skia on macOS.
