# SharpEmu-Mac planning documents

Planning status: 2026-10-07. Native Metal comes first; x64/Rosetta CPU execution is
retained initially. Native ARM64 CPU execution is a later milestone. No emulator
source has been imported or changed in this repository yet.

Start with [platform-packages.md](platform-packages.md) for the recommended code,
package and dependency architecture, then [implementation-plan.md](implementation-plan.md)
for the work packages, prerequisites and acceptance gates.

- [Architecture decision](decisions/0001-platform-packages.md): choices and tradeoffs.
- [Expanded improvement roadmap](architecture-roadmap.md): all 18 improvement areas.
- [Evidence audit](evidence-audit.md): measured findings and limits of earlier runs.
- [Initial Mac proposal](macos-development-plan.md): branch/sync policy and original scope.
- [Note inventory](audit/note-inventory.json): audited local notes.
- [Runtime inventory](audit/runtime-runs.json): retained run evidence index.
- [Proposed package graph](audit/package-plan.json): target dependencies and build profiles.

The platform package proposal refines the earlier project sketches. Read source
and PR status at their recorded snapshots; recheck changing upstream/candidate
state before implementation. Existing review-only work remains review-only.
