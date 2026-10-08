# Combined Metal completion qualification

Clean source `724d906daa3c6ed31fd5c186d3ba72374b3f6ebf` combines the separately
qualified completion candidate `80fe3dd8` with the current R16, vertex-prefix
and mapped-copy work. After the checks and Sarah replay below, local main was
fast-forwarded to this exact commit. No remote push, PR or upstream merge ran.

## Changed behavior

Metal owns end-of-pipe label payloads instead of letting the interpreter publish
them before GPU work. The ordered completion writes the label before its
interrupt/flip acknowledgement and rejects failed guest writes. Other hosts
retain their existing immediate-label policy.

Buffer readback accepts successful command-buffer completion, rejects Error
status and invalid/incomplete upload extents, and treats failed guest writeback
as an execution failure. Nonempty buffer writeback requires a committed command
buffer. Empty synchronization can still have no preceding native commands.

Initial no-device completion callbacks retain an exact outside-gate receipt.
Queue/presentation admission, startup and close cannot overtake it. Self-reentry
and timeout preserve the failure and callback ownership. Writable presentations
without an ordered producer/writeback receipt retain a veto; replacing them
does not silently clear that debt.

## Combined checks

Apple M4, macOS 27.2, SDK10.0.103/runtime .NET10.0.3. Clean bounded reference
builds passed: library 29.79 s, 128 warnings/zero errors; Metal tests 2.89 s,
zero warnings/errors. On each actual ARM64 and Rosetta x64 CLR:

| Suite | Passed | Scope |
| --- | ---: | --- |
| Library union | 489 | Prior376, mapped-copy metadata27 and completion86 |
| Managed Metal regression union | 117 | Current selected-mip/materialization controls |
| Required native Metal regression union | 55 | Actual device layout, typed materialization and shader checks |

All exact case unions passed with zero skips, failures, timeouts or forced
signals; source and DLL hashes match before/after. The 55 native cases are
regression evidence, not new native proof of every deferred completion/failure
route. The 86 new controls call production paths with managed wait/status hooks;
they do not certify GPU lifetime or full guest teardown. ARM64 host checks do
not enable ARM guest execution.

The earlier standalone candidate's two compile failures and a zero-case CLI
platform-option failure remain archived. The combined driver declined an
initial wrong-checkout invocation before building. The game driver declined a
missing fresh-run directory before launching; the directory was then created.
Only the successful frozen-source executions contribute to counts above.

## Signed Sarah replay

The source-specific app is retained at
`.claude/worktrees/root-metal-completion-combined/artifacts/macos/compat-completion-724d906d/SharpEmu.app`.
Publish took38.76 s. Deep/strict ad-hoc signature and 239-file native closure
checks passed; no Vulkan/MoltenVK/SDL/UI fallback is in the bundle. Bundle
manifest SHA256:
`6d2ab45f94ab16552638daad67cc833f3ee2ae7b4599fe4dde8c4e5761255fb2`.

Dreaming Sarah remained alive90 s, peak RSS859 MiB. The retained outdoor scene
responds to rightward travel and leftward return: snapshots63/67/77 show the
raised starting platform, travel to its right and the later leftward return.
Early directional requests overlap the waking/title sequence; late requests
verify movement. A jump request was sent, but these captures do not conclusively
prove an airborne jump. No owned compiler/test/native/game workers overlapped
this run. Bundle hashes remain unchanged. Owned TERM returned without KILL;
guest join/CPU-trap logs during stop do not certify clean guest shutdown.

Late reported guest-frame windows were approximately59 Hz, below the requested
sustained60+ threshold. The earlier source reported around100 Hz in some Sarah
runs. Dense screenshots, window focus, changed completion semantics and
simulation/display cadence limit comparison; this is neither a measured FPS
gain nor an isolated causal regression estimate. Full scene fidelity and fast
loading remain unqualified.

## Evidence and remaining work

`artifacts/validation/all-games-20261008/metal-completion-combined-724d906d/`
retains commands, exact case lists, TRXs, logs,20 source paths,22 frozen DLLs,
signed bundle hashes, screenshots and the original runtime directory manifest.
The [PVM forwarding qualification](pvm-mapped-copy-forwarding-workers.md) remains
a separate source-specific native memory result.

Storage textures still acquire GPU-written debt without canonical typed
GPU-to-guest publication. Completion of the command buffer alone does not copy
texture results into guest backing. The current dirty path also lacks correct
physical typed layout handling. The next implementation connects exact finite
texture staging, padding-preserving scatter and checked mapped publication
before guest completion/EOP. Selected CPU seed admission, later native owner
reuse, sampled aliases, Poppy recovery, Quake depth layout and the other title
blockers remain in scope. This source was not replayed across all nine titles.
