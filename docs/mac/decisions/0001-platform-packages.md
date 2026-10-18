# ADR 0001: platform packages and static composition

Date: 2026-10-07. Status: proposed for implementation. The user requested deeper
platform separation; detailed package names remain design choices, not implemented
or separately published APIs. See [platform architecture](../platform-packages.md)
and [implementation plan](../implementation-plan.md).

## Context

The inspected upstream already provides host contracts, a guest GPU seam, native
Metal and separate shader emitters. However, HLE references SDL, Libs references
both graphics implementations and media/font bindings, and CLI references GUI.
DirectExecution combines shared execution policy with OS-native fault mechanisms.
Combined POSIX paths contain significant macOS/Linux differences.

The fork needs Mac improvements without maintaining a separate copy of all guest
logic. It also needs full upstream ancestry, frequent sync and improvements that
can be contributed independently. Native Metal is the initial graphics target;
the first guest CPU engine remains x64 direct execution under Rosetta when needed.

## Decision

1. Keep one repository and one shared guest engine. Use separately compiled host
   packages for macOS, Windows and Linux.
2. Split execution implementation from host services and graphics. Use shared
   direct-execution policy plus OS-specific x64 integration packages.
3. Keep Metal/Vulkan independent and build on their existing guest-domain seam.
   Vulkan-shaped lower interfaces stay private to Vulkan.
4. Put contracts in the domain that owns their meaning. Extract small abstraction
   assemblies where they break cycles; do not create a universal mutable services
   package or an interface for every native function.
5. Keep toolkit/media implementations in adapters. Shared Core, HLE, Libs and
   application behavior have no concrete OS/graphics/frontend dependency.
6. Use thin platform factories and target-specific CLI/GUI composition roots.
   Select the managed dependency graph explicitly at build time; select only
   included backends at runtime. Native assets are selected by target RID.
7. Use static project references and explicit factories initially. Version owned
   assemblies together; make NuGet packaging optional except where useful for
   reproducible native assets. Do not auto-discover arbitrary renderer plugins.
8. Preserve session/native ownership and safe guest-to-managed transitions.
   Process-wide hooks remain explicit process resources.
9. Introduce boundaries incrementally with dependency enforcement and runtime
   evidence. Package movement and performance/default changes are separate work.

## Alternatives and reasons

| Alternative | Reason for choosing another approach |
| --- | --- |
| One cross-platform assembly with more OS checks | Keeps unwanted native/framework dependencies connected and obscures ownership |
| Copy all code into Mac/Windows/Linux trees | Multiplies guest bug fixes and makes upstream sync expensive |
| Package every syscall/subsystem separately immediately | Adds permanent APIs, build work and lifecycle edges without demonstrated independent consumers |
| One public POSIX implementation | macOS/Linux native context, memory and runtime behavior differ materially |
| One generic Metal/Vulkan low-level API | Risks forcing Metal into Vulkan synchronization/resource concepts |
| RID-only selection for all managed code | Native asset selection does not automatically prune managed dependency references |
| Independently versioned runtime plugins | Adds discovery, ABI/version failure modes and managed bridge reachability uncertainty |
| ARM64 CPU rewrite in the first milestone | Expands execution correctness scope before Metal/platform foundations are qualified |
| Replace Avalonia with a native UI immediately | Adds another product rewrite before engine/presentation seams are stable |

## Consequences

Benefits: enforceable dependency graphs, smaller selected native closures, clearer
OS/architecture ownership, testable shared code, headless CLI and upstreamable
changes. Native Metal startup can be independent of Vulkan/MoltenVK.

Costs: more project files, explicit composition and contracts, migration facades,
per-OS runtime validation and careful assembly/warmup changes. These are real
costs; splitting packages alone promises neither higher FPS nor lower RAM.

Keep subdomains as folders until a second consumer or dependency/lifecycle issue
justifies another project. Avoid extra projects that merely wrap an existing class.
Preserve current namespace/path names where possible during early extraction.

## Reconsideration triggers

- A subsystem develops independently supported consumers or deployment policy.
- A native shim becomes necessary to guarantee fault/ABI safety or measured speed.
- A working ARM64 execution engine introduces a different execution integration.
- Upstream accepts a different package boundary with equivalent enforceable rules.
- Static composition proves insufficient for a concrete supported extension use case.

## Acceptance

The architecture is implemented only when dependency closures enforce the stated
boundaries and supported runtime behavior is verified. The existence of this ADR,
folders or empty projects does not satisfy it. Milestones and checks are tracked
in the implementation plan.
