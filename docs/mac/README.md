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

## Latest status (2026-10-08)

Local main now includes the native AppKit launcher, modeless settings, bounded
console output and private child-process groups. macOS composition references
`SharpEmu.Launcher.MacOS`; Windows/Linux retain their GUI adapter. Mac publish
policy rejects Avalonia, SkiaSharp, HarfBuzz and portable font/UI packages as
well as Vulkan, MoltenVK and SDL. The latest signed default bundle contains clean `e627027`, with the native
AppKit launcher and no portable UI dependencies.

The integrated signed `e627027` launcher passes a 139s visible synthetic session:
modeless settings, sustained20,000-line output, natural root exit7 with
inherited-pipe descendant cleanup and CmdQ/owned Stop&Quit. A real TERM EPERM
receipt displays an error and cancels auto-Quit; later actual group/process
completion is recorded separately. A second CmdQ exits0. Minimum resize and
full frontend parity remain open. Candidate controls pass25/25 per CLR.

Canonical startup host-memory routing is integrated: legacy HostMemory and
host services share one immutable selected provider. Integrated104a614
controls pass309/309 Rosetta and292/292 ARM64, zero skips; ARM excludes17
x64-only cases. Native memory controls pass16/16 per CLR on actual4KiB/16KiB
pages. Metal required-device ownership/color cases pass25/25 under Rosetta.
The final actual native return/fault/telemetry workers pass4/4 after bundle
publish with unchanged assembly hashes. Mapping/domain leases, alias retirement
and fault retry receipts remain unactivated.

Fresh signed `e627027` Sarah 150s verifies coherent scene, right/left movement,
airborne jump and landing, peak 1214MiB. TERM process stop needs no KILL. Actual
host-stop return retains CPU_TRAP without fabricated import-loop/NOT_IMPLEMENTED
diagnosis. The guest join error still prevents a clean-teardown claim.
Concurrent builds and dense capture prevent a performance comparison.
[Latest screenshots](../../artifacts/validation/integrated-20261007/SCREENSHOTS.md).

A separately reviewed host-stop correction is integrated next: accepted main
entry redirect, actual native return and absence of execution error are required
for HostShutdownRequested. It retains CPU_TRAP/false and preserves known errors;
it does not declare successful guest exit or clean teardown. Candidate15/15
x64 controls and an actual normal-return worker pass. Integratedeb334c1 checks pass318/318 Rosetta and292/292 ARM64; ARM excludes26
x64-only cases. Launcher lifecycle controls pass25/25 on each CLR and seven
output-policy cases pass. Signed `e627027` bundle, Sarah game and visible native launcher qualification
are complete. Windows/Linux CLI cross-builds pass with 13 warnings and no errors
each; native OS runtime remains unqualified.

CoreText/CoreGraphics guest fonts and neutral `SharpEmu.Application` services
are integrated. Their earlier CPU/ABI controls remain distinct from title
usage. The GPU graph stays hard-disabled despite qualified native offscreen
color producers and accepted CPU payload receipts. Native presentation borrowing,
backing leases/coherence and broader storage/presentation writers remain open.
Poppy's traced Invalidating recovery race, Demon's Souls GPU alias failure,
3D visual/progression coverage, Windows/Linux native runtime and physical
DualSense behavior remain unqualified. See the
[ownership/runtime journal](validation/w13-ownership-iteration-20261007.md) for
exact revisions, failed preparation/regression runs and retained evidence.
