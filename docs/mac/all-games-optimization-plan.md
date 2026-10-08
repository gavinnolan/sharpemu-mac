<!-- Copyright (C) 2026 SharpEmu Emulator Project -->
<!-- SPDX-License-Identifier: GPL-2.0-or-later -->

# All-games compatibility and Mac performance plan

Updated 2026-10-08. Target: every installed title renders correctly, reaches
controllable gameplay, loads quickly and meets its intended frame cadence.
The requested 60+ FPS target is not achieved across the collection. A title's
own frame cap must be distinguished from host performance; changing guest time
or silently interpolating duplicate frames does not satisfy that target.

The [frozen all-nine baseline](validation/all-games-baseline-20261008.md) records
actual blockers. The [current optimization evidence](validation/mac-optimization-20261008.md)
separates code changes, numerical device fixtures and game observations.
macOS remains native Metal/AppKit/CoreAudio/GameController/CoreText. Guest CPU
execution remains x64/Rosetta; ARM64 CLR tests qualify host code only.

## Order of work

1. Clear each earliest reproducible correctness blocker with an owned source,
   exact raw descriptor and independent byte/coordinate oracle.
2. Repeat the affected game on one signed source snapshot, capture the next
   blocker, and require visible progression. A successful fixture is not a game.
3. Once the scene is controllable and correct, measure producer work, submission,
   drawable wait, GPU execution and display pacing separately.
4. Compare one performance change at a time on matched scenes and cache states.
   Promote defaults only after correctness, native lifecycle and pacing checks.
5. Re-run the collection before presenting a release as broadly compatible.

| Title | Current blocker / evidence | Next qualified change |
| --- | --- | --- |
| Dreaming Sarah | Controllable outdoor scene; same-binary876 ABBA: off63.34/65.86Hz; idle-on73.34/69.41Hz, tails remain33.8–36.2ms | Reduce unnecessary drawable acquisition; then queue-drain attribution and controlled same-build pacing comparison |
| Demon’s Souls | DF identifies global mapping epoch change at admission; zero guest frames | Preserve content authority while replacing broad vetoes with qualified per-range lifetime/operation ownership |
| Silent Hill: The Short Message | d756 passes RGBA16Float and rejects R8Uint mode24 singleton array | Qualify full64KiB origin seed, primary8bpp mode24 geometry, native Uint/bounds |
| Octopath Traveler II | d756 rejects same R8Uint mode24 family | Exact title raw replay and native Uint origin/bounds proof |
| The Oregon Trail | d756 rejects RGBA8Uint mode27 identity array | Full4-byte unsigned seed/store/load proof; separate title progression |
| Poppy Playtime Chapter 1 | Fatal write fault; later b7 record observes tracker Disarmed after a successful RW restore | Filtered schema3 retains85/0drops and three actual Disarmed CAS declines after successful RW restore; require permission/lifetime ownership, no unconditional retry |
| Superliminal | d756 fixes MSL collisions; loading hallway then same-address512x512→128x64 view mismatch | Establish resource/view lifetime and authoritative alias contents before changing typed identity |
| Quake | Native movie plays; first 8192² R32 tiled depth image exceeds 128 MiB snapshot bound | Independent PS5 tile-mode-24 address oracle, bounded upload staging and correctly filtered depth comparison |
| Void Terrarium | Scene visible but progression and gameplay unverified under contention | Input/checkpoint qualification on quiet host, first visual discrepancy and scene-specific GPU/producer attribution |

## Ownership before speed

Native GPU objects may outlive CPU command construction. Queue ordering and
retained Metal handles do not retain arbitrary guest mappings. A memory-object
identity, mapping-generation number, tracker token or successful copy is an
observation; each has an explicitly limited authorization scope.

- Preserve owned copies on presently qualified paths. Zero-copy needs a current
  backing lease that covers aliases, replacement/unmap and CPU/GPU writers.
- Same-queue ordering must be explicit; another queue cannot reuse a writable
  selected view without qualified synchronization and content transfer.
- Construction/upload/publication failures release exactly the new native retain
  and roll back only the metadata this construction installed.
- The unqualified texture-source graph and mapping/fault adapters stay dormant.
  Bringing those graphs online requires their own native mapping, alias,
  write-observation, shutdown and game gates.
- GPU command-buffer completion, CPU borrow release and successful guest teardown
  are separate events. A graceful host TERM does not prove guest thread joins.

## Native Metal performance

Current Sarah evidence points to drawable acquisition and guest-work draining,
not a measured Metal shader execution bottleneck. Apple recommends acquiring
[drawables late and releasing them promptly](https://developer.apple.com/library/archive/documentation/3DDrawing/Conceptual/MTLBestPracticesGuide/Drawables.html).

- First experiment: omit identical owned-front-buffer redraws. Continue draining
  guest commands and AppKit/input every pump; a bounded idle wait runs after
  payload/pool release. Always redraw mutable guest images, resized/restored
  surfaces and HUD visibility transitions. Keep this policy opt-in until actual
  surface behavior and matched gameplay pacing pass.
- Next: native display scheduling with
  [CAMetalDisplayLink](https://developer.apple.com/documentation/quartzcore/cametaldisplaylink/preferredframelatency).
  Display callbacks cannot starve guest command submission; latency and callback
  threading require explicit AppKit integration and window lifecycle tests.
- Batch compatible offscreen passes and detile/upload/blit work into fewer
  command buffers without starving the GPU. Apple documents the
  [submission tradeoff](https://developer.apple.com/library/archive/documentation/3DDrawing/Conceptual/MTLBestPracticesGuide/CommandBuffers.html).
- Use reusable staging buffers only with completion-owned reclamation. Limit
  in-flight work; inspect memory pressure and record stalls rather than allocating
  unlimited per-frame buffers. Follow
  [triple buffering guidance](https://developer.apple.com/library/archive/documentation/3DDrawing/Conceptual/MTLBestPracticesGuide/TripleBuffering.html).
- Prefer GPU detiling for qualified layouts. REmu's same-command-buffer staging,
  compute and private-texture blit is an architectural reference, not proof that
  its tile equations match these PS5 descriptors.
- Select shared/private/memoryless resources by actual usage and synchronization;
  unified memory does not remove CPU/GPU hazards. See Apple's
  [resource storage guidance](https://developer.apple.com/documentation/metal/choosing-a-resource-storage-mode-for-apple-gpus).
- Add persistent native pipeline/binary archives keyed by shader bytes, translator
  revision, complete specialization/layout, device/OS and archive format; reject
  incompatible entries and boundedly compile misses. Measure compilation stalls
  separately from game assets and CPU initialization. See
  [Metal binary archives](https://developer.apple.com/documentation/metal/metal-binary-archives).
- For Quake, an 8 MiB upload ring can bound staging, but a 256 MiB final texture
  and any retained 256 MiB guest source still count against the resource budget.
  Raising a safety cap alone is not a layout, filtering or memory fix.

## .NET, C# and instruction-level work

The accepted selector cache removes UTF-8 marshaling/PInvoke on cache hits and
has actual warmed timings and Tier1 assembly on both CLR architectures. This
is a measured host optimization, not an FPS claim.

- Keep tiered compilation and dynamic PGO as the reference configuration. Tune
  only with cold/warm and scene-specific evidence; quick JIT and loop policy can
  trade startup for steady-state code quality. Microsoft's
  [compilation configuration](https://learn.microsoft.com/en-us/dotnet/core/runtime-config/compilation)
  describes those choices.
- Evaluate [ReadyToRun](https://learn.microsoft.com/en-us/dotnet/core/deploying/ready-to-run)
  independently for cold loading: record bundle size, disk reads, first scene,
  later Tier1 code and relocation. NativeAOT is a separate architecture project
  because dynamic guest code/export reachability and reflection require an audit.
- Evaluate newer .NET patches in an isolated, checksum-verified toolchain. Current
  qualified bundles use 10.0.3; the official
  [.NET 10 download page](https://dotnet.microsoft.com/en-us/download/dotnet/10.0)
  lists 10.0.12. Runtime upgrades require native fault/TLS/memory, signing, game and
  load comparisons; do not replace the global SDK or silently change the baseline.
- Sample actual CPU self-time before rewriting decoders, detilers, command parsing
  or export dispatch. Optimize branch/bounds work, eliminate duplicate copies,
  reuse bounded owned allocations and inspect generated machine code.
- Specialize hot texture layouts and row/block copies using Span and supported
  intrinsics only after independent physical-address oracles. Preserve edge,
  mip-tail, array, volume and signed-coordinate semantics.
- Use actual runtime ISA capabilities. This Rosetta CLR reports SSE2 supported
  and AVX2 unsupported; ARM64 CLR reports AdvSimd supported. Do not force AVX
  flags or mistake ARM host assembly for ARM guest execution.
- No speculative GC/priority/affinity changes: establish allocations, pauses,
  memory pressure and thermal behavior first. Avoid aggressive polling and
  high-priority producer starvation on the AppKit thread.
- Package changes need a measured bottleneck, compatible API/ABI, license,
  provenance and native closure. No new third-party package is required for the
  selector cache, frame diagnostics or native texture fixes.

## Loading, macOS integration and platform boundaries

Separate cold app launch, loader/module setup, native compilation, HLE startup,
asset reads, first rendered frame, menu and first controllable scene. USB asset
latency, shader cache initialization and guest synchronization need separate
attribution. Preserve game dumps and save/config data.

Keep shared guest semantics in Core/HLE/Libs and format/shader contracts. Mac
window/display, audio, controller, fonts, paths, lifecycle and diagnostics belong
in their existing platform packages. CPU fault ABI, page geometry and native
mapping permissions stay in the matching OS/execution packages. Do not duplicate
shared PM4/shader logic across operating systems or expand production friend
access to bypass package contracts.

Explore QoS for bounded workers only with measured scheduling starvation;
thermal/memory-pressure notifications and native signposts should improve
observability before changing policy. Async cache preparation must have cancellation,
queue bounds and useful native errors. Launcher settings, window resize/restore,
controller reconnect, audio underruns and shutdown each need actual OS checks.

## Acceptance record for each title

One serialized native run at a time, source-frozen signed bundle, actual payload
hashes, runtime/OS/device, input acknowledgement and scene screenshots. Quiet
compiler state and unrelated activity/thermal/swap observations are part of the
record. Keep fatal logs and next-blocker descriptors.

Record separately: launch; first guest frame; readable menu; controllable scene;
visual correctness; cold/warm time to each checkpoint; guest FPS; p50/p95/p99
commit gaps; GPU execution; scanout; peak memory; guest joins; owned host stop.
For the requested 60-FPS target, use sustained recognized gameplay and report any
guest cap explicitly. Repeated host redraws and 24-FPS native movies do not count.

Compare the same binary with a policy on/off when possible, alternate run order,
repeat enough matched scenes to expose variation, and include failed comparisons.
Promote only a demonstrated gain without a correctness/lifetime regression.
Native Windows/Linux qualification remains separate from Mac cross-builds.
Upstream fetch on 2026-10-08 confirms b02c061 is still the latest fetched main and
already in local main. Changes remain local; remote publication/merges are separate.
