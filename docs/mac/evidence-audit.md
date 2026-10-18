# Evidence audit for the macOS roadmap

Snapshot: 2026-10-07. This is a planning audit, not a fresh game benchmark.

## Scope and method

Reviewed the current Demon’s Souls report supplied by the user, the compatibility
hub's architecture/performance report, readiness report, comparison report,
observation history, notes, port survey, capture/replay guide and reference index.
Inventoried the registered SharpEmu worktrees and their unique Markdown notes,
including ignored evidence directories. Screened 54 distinct Markdown documents
after deduplication; see `audit/note-inventory.json` for paths and content hashes.
Detailed conclusions below come from the cited primary reports and selected raw
logs, not from assuming every historical note remains true.

Scanned all retained run result files and stdout/head/tail logs in the hub at the
snapshot: 148 directories, 147 result files, 1,050,881,731 bytes of log content.
The resulting index is `audit/runtime-runs.json`. It includes final FPS log samples,
diagnostic markers and termination records; it deliberately does not infer a
scene or gameplay success from those fields.

Termination classifications at that snapshot:

- 106 reached the harness limit and subsequently stopped gracefully.
- 29 exited before the limit; this includes deliberate/error exits, not just crashes.
- 12 required SIGKILL after the grace period.
- 1 had no result yet and may have been active or interrupted.

These counts are not a compatibility pass rate. The harness's historical phrase
`alive at ... (killed)` is followed by a separate graceful/forced stop result.

Also read relevant prior memory summaries, current Kairo register-footprint notes,
REmu's CPU/GPU front-end design, and available T3 SharpEmu investigation messages.
Searched Projects, Documents, the current Codex worktree directory, and T3 scratch
for other SharpEmu records. Older run directories referenced in September/early
October notes and some prior worktrees are no longer present in the scanned hub.
The old zero-copy spike report referenced by the architecture note is absent.
Archived/deleted logs were not recovered from backups. Cloud documents/mail were
not searched because no evidence linked them to this emulator work. No proprietary
SDK headers or game payloads were needed for this architecture audit.

## Primary sources

E01. [Current Demon’s Souls report](/Users/xnetcat/Projects/xnetcat/sharpemu-compat/evidence/ds-main-profile-20261007/REPORT.md)

E02. [Architecture and performance investigation](/Users/xnetcat/Projects/xnetcat/sharpemu-compat/docs/architecture-perf-2026-10-06.md)

E03. [Readiness report, with rollback correction](/Users/xnetcat/Projects/xnetcat/sharpemu-compat/docs/pr-readiness-2026-10-07.md)

E04. [PR 1061/1066 rendering and memory comparison](/Users/xnetcat/Projects/xnetcat/sharpemu-compat/evidence/sh-1061-1066-20261007/comparison.md)

E05. [Full observation history](/Users/xnetcat/Projects/xnetcat/sharpemu-compat/observation.md)

E06. [Older cross-project port survey](/Users/xnetcat/Projects/xnetcat/sharpemu-compat/port-candidates.md)

E07. [Capture/replay guide](/Users/xnetcat/Projects/xnetcat/sharpemu-compat/cases/README.md)

E08. [Direct-buffer experiment and limitations](/Users/xnetcat/Projects/xnetcat/sharpemu-wt-direct-memory/docs/direct-guest-buffers.md)

E09. [Candidate internal-resolution behavior](/Users/xnetcat/Projects/xnetcat/sharpemu-wt-consolidated/docs/rendering-resolution.md)

E10. [Kairo register-footprint evidence](/Users/xnetcat/Projects/xnetcat/kairo/docs/deviations.md:255)

E11. [REmu front-end design](/Users/xnetcat/Projects/xnetcat/REmu/docs/superpowers/specs/2026-08-10-cpu-gpu-front-end-performance.md)

## Findings that change the roadmap

### 1. Producer starvation is a stronger Demon’s Souls lead than GPU saturation

E01's usable historical language-menu profile has 206 frames in 52.6 seconds:
3.916 FPS, or 255.34 ms per presented frame on average. IdleNoQueuedWork is
132.89 ms/frame (52.04%); GPU completion waits are 28.68 ms (11.23%). Adjacent
GPU timestamp windows show 26.49% command-buffer occupancy. CPU/GPU times overlap
and must not be added. Occupancy is not full-device utilization.

The historical build's exact SHA is unknown. These figures describe a language
menu on October 6, not current-main gameplay. The current-main 720-second run
never produced a usable steady game-frame window under extreme host contention.
No current-main steady performance baseline has been established by this report.

Interpretation: trace why guest workers produce work late before optimizing
shaders as the primary Demon’s Souls intervention. The idle phase does not identify
whether the cause is guest execution, HLE locks, thread scheduling, GC or cadence.

### 2. Startup compilation and recurring frame cost need independent budgets

E01 records 126.8 seconds cumulative pipeline creation and 122.278 seconds cache
initialization in today's contended run. The measurements overlap. Current source
starts the cache timer after `File.ReadAllBytes`, around native cache creation;
this is driver initialization, not measured USB read time. Older E05 observations
also report large shader modules and synchronous compiler stalls.

Interpretation: separate translation, native compilation, archive/cache import,
I/O and first-use latency. A warm pipeline cache is not proof of instant startup.

### 3. Unified memory is promising but does not settle correctness or performance

E02/E08 identify page-level false sharing, mirrored buffer duplication, producer
tracking and guest physical aliases. Optional imports removed the original counter
page waits, but the compared Silent Hill rooftop control was 4.38 FPS versus
1.72 FPS with 64 MiB regions and 3.14 FPS with 16 MiB regions. Pinned backing was
3.62 GB and 2.53 GB respectively. The later 4 MiB comparison was contaminated.

Mixed imported/mirrored physical aliases can diverge; ordinary waiting alone does
not guarantee simultaneous shader aliasing. Translated atomic validation is also
incomplete. Interpretation: a common physical ownership model is a prerequisite
for broadly enabling zero-copy, including a future Metal implementation.

### 4. Shader footprint and semantics deserve a dedicated optimization program

Current upstream MSL emits fixed `s[128]` and `v[256]` arrays. E10 records substantial
register-footprint gains in Kairo, including interleaved runs and identical output.
This supports a SharpEmu experiment, not a transferable speedup claim. Current
SharpEmu must be measured independently with conservative dynamic-index bounds.

The T3 shader investigation records roughly 7.5% lower fog draw-scope time from a
compute-only EXEC-run transform; applying it to graphics regressed a pixel shader
about 28%. Narrowing private LDS below the shader's declared needs produced an
invalid apparent gain. Interpretation: stage/capability gates and exact semantics
must accompany optimization. Do not blanket-copy transformations between backends.

### 5. Several apparent graphics problems were synchronization or HLE problems

E05 includes addressed query-event loss, incoherent query publication, stale
image publication, undersized constant vertex streams, and an audio queue-level
implementation that held up Wwise/the guest GameThread. Earlier claims that no
character draw existed were corrected after full traces. Fog and hair passes
were observed to execute; that does not establish their lighting inputs are right.

Interpretation: record producer/consumer state and intermediate results before
changing final colors, forcing visibility or skipping guest work.

### 6. Memory numbers need allocation ownership and checkpoint matching

E04 observed about 11 GB process footprint near the candidate title and about
3.9 GB registered images. Its control was still at an earlier loading stage;
the footprints are not a valid reduction comparison. Reported 27 GB gameplay
usage was not reproduced at the same checkpoint. Source changes released retained
managed shader payload and accounted actual allocations including transfer twins,
but driver/native/backing/pool/deferred objects still contribute to process usage.

Interpretation: use an allocation ledger and lifetime events, and benchmark
memory at the same scene, cache, build and instrumentation state. RSS alone misses
compression/swap effects on this host. A cache budget is not a process ceiling.

### 7. The benchmarking system has correctness gaps of its own

E04 found that earlier scripts wrote Enter to a file the tested binary did not
read. Later evidence recorded both input delivery and the guest pad bits. Some
run manifests identify only a publish directory and a Local build banner. E07
notes truncated captures, missing device-generated geometry and no depth/stencil
output diffing. Reference images include key art and thumbnails; boot durations
include estimates. Those are useful investigation aids, not exact scene oracles.

Interpretation: make proven input acceptance, binary/source hashes, explicit
checkpoints, complete captures and quiet-host eligibility part of the harness.

## Current upstream versus local candidates

GitHub was checked live at upstream `e1edda7772e34b12b078f726560d14170bfbb077`.
[Rollback #1065](https://github.com/sharpemu/sharpemu/pull/1065) restores the
pre-merge tree. [Replacement #1066](https://github.com/sharpemu/sharpemu/pull/1066)
is open at `8d0a022c` and explicitly review-only. It contains the 18 reverted
changes. Their historical merge status does not mean they are in current main.
[Occlusion #1042](https://github.com/sharpemu/sharpemu/pull/1042) and
[imports #1060](https://github.com/sharpemu/sharpemu/pull/1060) remain separate drafts.
This audit did not modify, merge, comment on or enable any of those candidates.

The September port survey also drifts: its proposed S_ADDK_I32 SCC fix is already
present in both current upstream emitters. Its AAC discussion concerns the AJM
path; an AAC-looking symbol in another codec API does not settle that path's
support. Treat old missing-feature lists as leads to re-audit, not a task queue.

## Current source structure

At the inspected SHA, `src` contains 693 C# files and about 244,090 lines.
The largest areas include native CPU execution, kernel memory/thread exports,
shader emitters, the GUI window, loader, media and GPU caches. Source contains
258 distinct directly read SHARPEMU environment switches and 190 textual OS checks.
These are structural observations, not proof that file size or an OS check costs FPS.

Core depends directly on Libs; Libs owns both GPU backends and their dependencies;
CLI references the GUI. Shared contracts and composition need careful extraction
to avoid creating cycles or increasing unsafe guest-stack JIT work.
