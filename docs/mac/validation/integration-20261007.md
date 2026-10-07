# Native macOS integration validation — 2026-10-07

The initial integrated validation app is built from clean source `9498713c5b85a793fbebb822c3e3b0e7a8255e3f`
with .NET SDK 10.0.103 for `osx-x64`. Its integrated upstream revision is
`e1edda7772e34b12b078f726560d14170bfbb077`. Guest CPU execution remains x64 under
Rosetta; ARM64 managed test runs do not establish ARM64 guest execution.

Each game run retains `build-info.json` and hashes of the actual app payloads.
Raw evidence is under `artifacts/validation/integrated-20261007/` and
`artifacts/runs/`. These directories are local artifacts, not committed game data.

## Implemented boundaries

- Per-OS direct-execution packages own native fault layouts, signal/exception
  entry, thread/TLS primitives and host memory probes. Core owns shared guest
  fault recovery and scheduling. The extraction preserves the existing signal
  trampoline and host-stack restore. A narrow POSIX preflight prepares the new
  interface dispatch before fault handlers execute.
- Extracted host, platform and graphics namespaces match their assemblies.
  Narrow public services replace production friend access into shared HLE/Libs
  internals; source and compiled-assembly architecture checks enforce ownership.
- macOS uses Metal, AppKit, GameController and CoreAudio. Publish checks reject
  guest Vulkan, MoltenVK and SDL payloads. Native tool architecture detection
  also works when the publisher runs under Rosetta.
- Metal handles the covered ALU and image dimensions, supplied mip ranges,
  layered/depth uploads, deferred detiling and typed null textures. Unsupported
  formats and incomplete real resources fail explicitly.
- GPU work failure retains its original cause and submission context, stops
  completion acknowledgement for failed work and rejects subsequent work.
  Guest frame cadence is independent of repeated host redraws.

## Final integrated checks at 9498713

| Check | Result | Evidence and scope |
| --- | --- | --- |
| Shader suite, x64, required Metal device | 351 passed, 0 failed/skipped | `metal-shader-final.log` / `.trx`; includes actual MSL compilation and numerical/image readbacks |
| Production Metal resource suite, required device | 31 passed, 0 failed/skipped | `metal-production-final.log` / `.trx`; native upload/readback fixtures plus managed format/rejection checks |
| CPU, architecture, shape, FPS and failure selection, x64/Rosetta | 356 passed, 0 failed/framework skips | `cpu-contract-final-x64.log` / `.trx`; includes isolated protected-page and unsupported-SSE4a fault workers |
| Same managed selection, ARM64 runner | 342 passed, 11 skipped, 0 failed | `cpu-contract-final-arm64.log` / `.trx`; x64-only execution cases explicitly skip |
| macOS bundle dependency and native architecture guards | Passed | `app-final-build.log` |
| Local ad hoc signing verification | Passed | Deep/strict signature verification; Developer ID, notarization and Gatekeeper qualification remain separate |
| CLI `win-x64` / `linux-x64`, Release | Both passed, 0 errors | `cross-platform-final/`; Windows 9 warnings, Linux 3; isolated merge tree equals 9498713 exactly |

Aggregate counts require scope: inherited Windows-only test bodies return early
on macOS, and some CPU instruction fixtures are capability gated. Neither a
framework pass nor a cross-build establishes native Windows/Linux execution.
The required-device gate is set for both final Metal suites; individual managed
format/rejection cases still do not execute on the GPU.
ARM64 discovery collapses three skipped x64-only two-row theories into one skip
each; the different aggregate totals do not indicate missing executed cases.

## Game evidence

The nine-title corpus was collected serially with the native app, a shared
machine-wide game/device lock, bounded runtime and a 12 GiB RSS limit. Keyboard
input plans are retained with the run. Menu input alone does not establish gameplay.
Concurrent non-device builds/tests mean these are compatibility checks rather
than quiet-host performance comparisons.

Dreaming Sarah's final run (`20261007-200133-sarah-final-native`) remained alive
for 150 seconds, with peak RSS 1066 MiB. Screenshots before and after Right/D input
(`shot-0105.png`, `shot-0115.png`) show the player and camera moving into the next
segment; subsequent Left/A input returns toward the starting area. The process
stopped two seconds after SIGTERM, but shutdown still forced a guest unwind and
returned `ORBIS_GEN2_ERROR_NOT_IMPLEMENTED`. This is process-stop evidence, not a
clean guest exit or full-game qualification.

| Game | Observed phase | Result | Peak RSS MiB |
| --- | --- | --- | --- |
| Dreaming Sarah | Outdoor gameplay; Right/D and Left/A movement | Alive 150 s; SIGTERM stops process in 2 s, forced guest unwind | 1066 |
| Silent Hill: The Short Message | No captured visual phase | Abort at 24 s; `BufferAtomicSwap` compute translation unsupported | 1969 |
| Quake | Intro logos and multilingual warning screen | Abort at 64 s; 256 MiB texture exceeds the 128 MiB snapshot cap (8192×8192, format 4/7, tile 24) | 2180 |
| Superliminal | Title/loading screen with coherent dark room | Abort at 53 s; skipped tiled/mip snapshot cannot be recovered after cache loss | 3380 |
| Octopath Traveler II | No captured visual phase | Abort at 31 s; `BufferAtomicSwap` compute translation unsupported | 2536 |
| Poppy Playtime Ch. 1 | No captured visual phase | SIGBUS exit at 23 s; unrecovered guest write fault, cause under investigation | 1358 |
| The Oregon Trail | Black guest window; no menu/scene | Abort at 29 s; `BufferAtomicSwap` compute translation unsupported | 1707 |
| Void Terrarium | Readable title menu and coherent artwork | New Game input triggers cache-loss failure; abort at 31 s | 1230 |
| Demon's Souls | Static splash artwork; zero counted guest frames | Abort at 76 s; skipped tiled/mip snapshot recovery rejected | 1786 |

The eight failing titles exited before their time/RSS limits. They did not
exercise requested process shutdown, so no clean-shutdown inference follows.
The GPU failures now terminate with actionable context rather than falsely
completing work. This makes missing support visible; it does not qualify the
titles as playable.

Quake's descriptor resolves to four bytes per texel; its base snapshot alone is
256 MiB. The producer rejects it before materialization at `MaximumSnapshotBytes`
(128 MiB). A larger-resource path needs explicit memory accounting and lifetime
ownership; the generic snapshot error does not imply that format 4 is unknown.

Void's `20261007-201452-void-final9498713-corpus/shot-0025.png` has readable menu
text and coherent artwork. The earlier `20261007-182951-void-base/shot-0026.png`
is heavily striped. The old bundle's source provenance is not established, so
this visual difference cannot isolate the contribution of one commit. New Game
still fails. Twenty raw/linear texture captures totaling about 145 MiB are
retained in private local evidence directories for follow-up.

Detailed JSON and screenshot reviews are retained in
`artifacts/validation/integrated-20261007/game-corpus/`. Subsequent fixes and
reruns must record their own source revision; these outcomes describe 9498713.

## Follow-up integration at b557990

The new clean application build embeds
`b557990d20fd9144dc0292b2546e89ef905976b8` (same upstream base and SDK/RID).
It adds decoded buffer SWAP/unsigned-max atomics and faithful queued texture
snapshots with retained-array queue accounting. See [snapshot lifetime](queued-texture-lifetime-20261007.md)
for the deliberate copy cost and the existing dirty-epoch tracking limitation.
The 128 MiB per-texture cap remains; Quake now gets precise required/cap bytes.

Integrated required-device tests pass **365/365 shader cases** and **31/31
production resource cases**, with zero failures/framework skips. Evidence:
`metal-shader-followup.{log,trx}`, `metal-production-followup.{log,trx}` and
`app-followup-build.log`. The standalone atomic fixtures include nine native
executions; they use one invocation and do not qualify contention or broad
cross-dispatch synchronization. The original 9498713 app archive and its hash
are retained separately so old evidence remains reproducible.
The integrated queue/shape/failure/package selection also passes 44/44 on both
x64 and ARM64 (`queued-integration-{x64,arm64}.{log,trx}`), without skips.

The controlled x64 native Poppy probe executes the captured 32-byte streaming
store on private/shared writable mappings. Read-only mappings raise Darwin
SIGBUS and resume after write permission is restored; native context offsets
match the extraction. A partial-unmap fixture proves that whole-span restoration
can fail while a surviving page stays read-only. Source review identifies an
existing tracker retirement path that can discard ownership despite such failure.
This is a concrete W13 risk; it does not establish what happened inside Poppy.
Production tracker code remains unchanged pending transactional mapped-ownership
work. Twelve normalized-frame/real-POSIX cases pass. Full evidence and the
acceptance matrix are in `game-corpus/POPPY-REVIEW.md`.

Eight source-linked runs are complete in `game-followup-b557990/REPORT.md`, with
matching app payload hashes. Their inspected phases and outcomes are:

| Game | Observed phase and result | Peak RSS MiB |
| --- | --- | --- |
| Dreaming Sarah | Coherent scene; Right/D and Left/A movement verified; alive 150 s, process stops 2 s after SIGTERM with forced guest unwind | 1200 |
| Silent Hill | Black guest window; atomic abort cleared; alive 90 s, process stops in 4 s | 3829 |
| Octopath | Animated loading crest; atomic abort cleared; alive 90 s, process stops in 4 s | 4505 |
| Oregon | Welcome/legal UI then loading; atomic abort cleared; alive 90 s, process stops in 3 s | 4199 |
| Superliminal | Main-menu labels against almost white background; prior texture abort cleared, later zero FPS/stall; SIGTERM timeout needs SIGKILL | 5375 |
| Void | Readable menu then near-black transition; previous New Game FailFast does not recur in 90 s; process stops in 2 s | 2586 |
| Poppy | No captured visual phase; SIGBUS at 24 s | 1679 |
| Demon's Souls | Static splash, zero guest frames; abort at 45 s on skipped texture alias `0x7092C80000` | 3023 |

Only Sarah has verified directional gameplay/control response. Its scheduled
jump is not visually verified. The other phases do not establish gameplay;
Superliminal's background correctness is unqualified and the older dark loading
room was a different phase, so it is not a matched visual regression comparison.
Quake is not rerun because its snapshot cap is unchanged. FPS reporting describes
these startup/menu/loading periods, not a gameplay performance comparison.
Clean guest exit remains unverified for every run.

Poppy tracing finds two unrecovered normalized write addresses within an armed
sampled-texture span, and a third write that disarms it. Raw `si_addr` is not
recorded; first-write lifetime logging is deferred. This supports but does not
distinguish pending-fault-after-restoration and raw/normalized lookup hypotheses.
See `game-followup-b557990/POPPY-TRACE.md`.

## Final bounded fixes at 382bc5e

Application source `382bc5eda671c338b781bce400fdf39b62f4c020` adds two bounded
changes. The early POSIX tracker path accepts only SIGBUS/SIGSEGV frames
classified as writes and uses the same normalized address as later recovery.
Read, fetch and unclassified faults retain the ordinary fault path. No tracker,
Core, TLS or native callback ABI changes are shipped. The proposed pending-fault
retry is withheld after independent review found that epoch checks cannot reserve
mapping ownership or undo a late `mprotect` against reused backing. Evidence and
required lifetime design are in [write-fault ownership](../write-fault-ownership.md).

The Metal texture failure now includes current descriptor, image variant,
initialization and producer queue/sequence/completion observations. These are
failure-time diagnostics, explicitly not captured ownership provenance. Existing
fatal and allocation failures propagate unchanged. No alias or recovery policy
is changed. The [sampled GPU ownership specification](sampled-gpu-alias-20261007.md)
defines the larger W13 fix and acceptance cases.

The exact committed tree cross-builds both Windows and Linux CLI Release with
zero errors (9 and 3 existing warnings respectively); evidence is retained under
`cross-platform-final/382bc5e/`. Native Windows/Linux execution remains untested.
The b557990 app is separately archived with its hash and embedded metadata.

The rebuilt app embeds clean application source 382bc5e, SDK 10.0.103 and
osx-x64, with the same integrated upstream base. Publish dependency/architecture
guards and deep strict ad hoc signature verification pass. Final integrated
fault/alias/queue/shape/architecture selection passes 136/136 x64 cases;
five isolated native-return/fault workers also pass, including real protected-page
SIGSEGV and unsupported-SSE4a SIGILL recovery. The ARM64 runner passes 96/96
alias/queue/shape/architecture cases; it does not run native x64 guest faults.
Evidence: `final-guard-alias-x64`, `final-native-fault-x64`,
`final-alias-architecture-arm64` `.log`/`.trx` pairs and
`app-final-382bc5e-build.log`.
The final required-device production resource suite passes 31/31 without skips
(`metal-production-382bc5e.{log,trx}`). Shader code has not changed since the
365-case b557990 required-device suite, so that broader shader run is retained
as the shader qualification rather than repeated.

Two final serialized runs use the same clean 382bc5e app and matching payload
hashes, retained in `game-final-382bc5e/REPORT.md` and `index.json`:

- Poppy exits SIGBUS after 23 s, peak 1548 MiB, without a scheduled screenshot.
  Three unrecovered normalized writes lie within the armed texture span;
  deferred logs also show disarm and rearm. Raw signal fields, kernel permissions
  at the fault and native-entry timing remain uncaptured. The guard does not fix
  bounded progression, and this evidence does not isolate the cause.
- Demon's Souls exits on GPU FailFast after 51 s, peak 3094 MiB, with zero guest
  frames. `shot-0045.png` shows black content, no menu or scene. Compute sequence
  231, queue `acb.compute[64]`, submission 19 cannot recover sampled texture
  `0x7064DF0000` (214×120, format 2/7, tile 9). At failure time its address is
  advertised, active/selected aliases are absent, and the current producer
  sequence 219 is pending on `dcb.graphics`, submission 14, not completed or
  executing. This establishes the observed registry/queue state; it is not a
  captured dependency or proof of the prepare-time producer. Ownership and queue
  ordering remain open W13 requirements.

Neither run exercises host-stop acceptance because both exit before their
bounds. No emulator remains running after the batch. Local archives retain
9498713, b557990 and 382bc5e apps; `final-source-382bc5e.json` records exact
test/log/archive hashes and build metadata. `SCREENSHOTS.md` and
`screenshots-b557990.json` provide clickable selected captures, source metadata,
phase labels and image hashes alongside the raw run folders.

## Remaining acceptance

See [Metal gaps](../metal-gaps.md), [CPU validation](cpu-platform-split-20261007.md)
and [package validation](package-integration-20261007.md). Broader resource
formats/tiling, MSAA payloads, cube seam filtering and synchronization need
targeted qualification. Native Windows/Linux fault handling, physical controller
features, clean guest exit and native ARM64 CPU execution are not established by
these checks. No upstream PR or remote merge is part of this local validation.
