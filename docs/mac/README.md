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
