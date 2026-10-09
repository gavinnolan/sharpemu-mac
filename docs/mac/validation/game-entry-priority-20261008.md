# Game entry takes priority

The current milestone is to reach responsive, controllable gameplay in every
title in `/Volumes/Untitled/games`. Performance, loading-time optimization and
visual polish are deferred. Renderer changes remain in scope when they remove
a crash or prevent progression. Intro playback, an automatic demo and process
survival do not satisfy this milestone.

## Latest signed game replays

R32 successor `08ebe4cc` passed 722 managed cases on each actual CLR, all
24 fresh native workers, three builds, the 104-file compiled closure and the
signed 239-file native-only payload. The new native case sampled the complete
original mip0 into mip1 after deliberate CPU-source poisoning, verified every
output float and preserved all 786432 post-poison guest bytes on both ARM64
and x64 CLRs. This qualifies the bounded native transport, not game entry.

Its actual Demon's Souls replay stopped before the mip0 CPU copy: six ordinary
native images and six extents trigger the strict writer-inventory refusal.
The first failure is compute `8A02D692EE458D12` at `7092C80000`, with sampled
mip0 and storage mip1 sharing the resource base. The inspected 125-second
capture is black. Exit 134 after 130 seconds and peak RSS 3347 MiB do not
establish progression. All 42 owned process groups and the shared lock were
absent at archive closure; source and payload hashes stayed unchanged.
Evidence is `artifacts/validation/all-games-20261009/ds-r32-disjoint-08ebe4cc-prepared`,
manifest `7e45857fce1f70be94efef0964424496110c4341835c790c46e84f76c06fd489`.
This candidate remains isolated. The next replay adds failure-only cached
image provenance to establish the six images' creation routes and original
ownership before any inventory-policy change.

Combined constructor/writer successor `3b6206c4` passed 709 managed cases on
each actual CLR, all 22 fresh native workers, three builds, the 104-file
compiled closure and the signed 239-file native-only payload. Its actual
replays still do not qualify entry:

- Octopath exited 134 after 33 seconds, peak RSS 2974 MiB. The first failure
  is queue retention of the original volume watch at `8030BF0000`/65536
  while another constructor holds `PublicationBit` (4294967296). Coverage,
  released and revoked flags are zero. The later submission fatal is secondary.
- Silent Hill exited 134 after 27 seconds, peak RSS 1496 MiB. It reproduces
  the same queue-retention collision at `80107D0000`/65536; the original and
  sampled revision both remain 152386. The inspected 25-second capture is black.
- Oregon exited 134 after 32 seconds, peak RSS 1925 MiB. Its first failure is
  three typed-storage original-copy refusals at `8011600000`, a write-only
  1-by-1 Uint volume. The adjacent admission-1 diagnostic refers to a different
  original source, `80115F0000`; it does not establish the failing copy's cause.
  The intro artwork is not gameplay.

All 41 owned process groups and the shared lock were absent at archive closure.
Source and payload hashes remained unchanged. Evidence is
`artifacts/validation/all-games-20261009/entry-busy-integration-3b6206c4-prepared`,
manifest `58321f8ed919124ed02fc0fce6f8bb8c40f8c9fd7635f1edc4e47f8df326dc21`.
The constructor successor remains isolated from main while the demonstrated
queue collision is repaired. Passing these subsystem controls is not gameplay
acceptance. Original ordered native color borrowing for Superliminal remains
a separate source candidate without runtime qualification.

Candidate `8aca3731` passed 643 managed cases on each actual CLR, five fresh
native cases per CLR, three builds and the signed 239-file native-only bundle.
Its 120-second Dreaming Sarah run reached the starting scene. Independent
screenshots at 65, 85 and 105 seconds show the scheduled right movement and
camera scroll, followed by left movement and facing response. Directional
control is verified again; isolated title-bar FPS samples do not establish a
sustained performance result. The run shut down gracefully, peak RSS 930 MiB.

Demon's Souls at 150 seconds still had no scene. All three original selected
copy attempts failed because writer coverage was already incomplete, not
because the watch inventory was full or a writer was merely busy. The exact
cause of that earlier loss is not established by this run. Octopath's first
failure in this candidate is original singleton-volume watch admission 2
during constructor transfer, before any logged APR failure. The earlier APR
abort remains a separate observed failure; its cause is not considered fixed.
The closed archive is
`artifacts/validation/all-games-20261009/entry-ds-apr-first-refusals-8aca3731-prepared`,
manifest `943f63ebf9ce8df2ac57ca847c98375b32e86b73a934962bb8bf589edcc4bfb0`.
All 31 owned groups and the shared lock were absent at archive closure.

Diagnostic successor `ddc045ab` passed 645 managed cases per CLR and all ten
fresh native cases. Superliminal stayed alive for 150 seconds and shut down
gracefully, peak RSS 4141 MiB, but remained at loading with no gameplay. Its
first color refusal now identifies the exact selected 512-by-512 RGBA16Float
native image: request `706A6C0000` for 2 MiB, original ten-mip allocation
`706A610000` for `0x2B0000` bytes, selected mip offset `0xB0000`. The retained
image is initialized, GPU-written, version 1, and has the same original
placement. These diagnostic inventory facts do not authorize borrowing it.
That requires original mapping/content and ordered native producer receipts.
The closed archive is
`artifacts/validation/all-games-20261009/color-overlap-first-view-ddc045ab-prepared`,
manifest `b10eea22464d00fa6352f16d20370ed6ff1967f14e14e2e51de2567964b1a4fd`.
All 27 owned groups and the shared lock were absent at archive closure.

Qualified successor `2fdabb5c` separates expired completed-write history from
lost physical writer coverage. Original watched ranges remain invalidated
before overlapping bytes; failed or unknown writes still invalidate every
original watch. Unwatched publication retains the strict history window and
missing/unknown-record vetoes. Invalid bounds, exhausted identities, active
writer slot exhaustion and quarantined publication remain sticky failures.
Eight additional controls passed. All 653 managed cases passed on each actual
CLR, and all ten fresh native workers passed their complete byte/status-4
oracles. Three builds, 104 compiled closure files and the signed 239-file app
were stable. The Demon’s Souls replay exited 134 after 53 seconds, peak RSS
3294 MiB. It recorded seven expired history records and no writer coverage
loss. Original copy attempt 1 qualified with unchanged original mapping,
watch, read generation, protected content token and session. The former
all-attempt coverage refusal is cleared. The next primary failure is compute
sequence 177: an address-only image read at `7092C80000` cannot consume the
native-only selected storage mip. There is still no scene or gameplay.
The closed archive is
`artifacts/validation/all-games-20261009/history-watch-coverage-2fdabb5c-prepared`,
manifest `b2861eac33f2a03e59192f9795ce96f700dafbf912220a778a7c743bfd880d34`.
All 27 owned groups and the shared lock were absent at archive closure.
Local merge `100838fc` promotes this qualified source and the earlier APR/color
diagnostics unchanged; only this progress document differs from the candidate.
No remote push or merge was performed. Clearing this veto is not game-entry
acceptance.

## Earlier combined qualification and first failures

Successor `d5d198f7` passed 639 managed cases per CLR and all ten fresh native
workers. The initial native depth capture now selects its original full provider
mapping before arming the selected-face observation; it requires an active,
unchanged read generation, immutable content token, original mapping before and
after token comparison, and unchanged presenter session. Real Metal fixtures
changed an unrelated PVM mapping inside initial validation for all six faces;
both full six-face byte oracles passed. The aggregate mapping epoch remains
diagnostic metadata on this scoped route, without weakening generic snapshot
validation or replacing an original observation.

The signed Superliminal replay remained alive for 150 seconds, peak 5163 MiB,
and shut down gracefully. Both prior depth epoch failures were cleared. The
next first failure is a finite color-allocation overlap at compute shader
`9CA5D69E4285127A`, sampled descriptor `7068DC0000` and storage descriptor
`7069010000`. Loading artwork still stalled, so there was no 600-second
extension or gameplay acceptance. Complete evidence is in
`artifacts/validation/all-games-20261009/entry-watch-fixes-d5d198f7-prepared`,
manifest `aa68de84fd602cdad835be43cc9b659371493c1adf79abe6fad8c372c48b389e`.
All 27 owned groups, the shared lock and source/binary/payload closure checks
were clear at archive closure.

Local main integration `153ac2ce` includes the qualified `d5d198f7` production,
tests, scripts and build inputs unchanged; its only tree difference from that
candidate is this progress document. No remote push or merge was performed.

Candidate `43a89254` passed 634 managed cases per actual ARM64 and Rosetta/x64
CLR, plus five fresh real Metal cases per CLR. Both depth cases checked all
6,291,456 float texels after an actual unrelated PVM mapping changed the global
epoch while the original face mappings and content tokens remained unchanged.
Three builds, 104 compiled closure files and the signed 239-file native-only
bundle were retained. All 28 owned process groups and the shared lock were gone
at archive closure. This is qualification of the candidate, not gameplay.

- **Superliminal:** the 150-second replay reached title/loading and stalled.
  Its first rejection is now initial `Project` admission: global mapping epoch
  418685 → 418688 despite a copied, current original provider mapping and equal
  presenter session. The current content token was not queried after that
  short circuit. The scoped `RequireOriginalCurrent` fix passed the native
  controls; initial capture still needs its own scoped mapping validation.
- **Octopath:** the original watch enrollment revision fix advanced the replay
  to two applied canonical GPU publications. The game exited 134 after 32
  seconds. Its first guest error is APR `74006e` followed by an abort. This code
  can represent a command header or result/output memory failure; the existing
  trace does not establish a host file read failure. A later volume-watch
  rejection sees admission 1 with intact ownership and coverage. That later GPU
  rejection must not be assigned as the cause of the earlier guest APR abort.
- **Oregon Trail, diagnostic candidate `73e0ee8d`:** the replay fails before the
  sampled-alias check, during original singleton-volume content validation.
  The first retained read watch sees writer admission 2; the sampled-image
  shape diagnostic is therefore not exercised.
- **Demon's Souls, `73e0ee8d`:** all three selected-mip copy attempts fail watch
  enrollment. Original session, token and global mapping epoch remain equal.
  The inner enrollment reason still needs attribution; increasing limits or
  bypassing the original watch is not justified by this result.

No additional title has reached verified controllable gameplay. Void Terrarium
has reached its main menu, as recorded below. The qualified fixes are now in
local main; the subsequent APR, read-watch and retry candidates remain isolated.

Immutable evidence:
`artifacts/validation/all-games-20261009/entry-watch-fixes-43a89254-prepared`
(manifest `c43d2bc0d35170faaae0852170e6cfebb104e567f0db1c8198c422855914030e`),
and `artifacts/validation/all-games-20261009/entry-first-refusals-73e0ee8d-prepared`
(manifest `8dbaed3423f42fbf2f005dd8f1171d4a5a6b2f03c2d8aa239f22d5aaa9378185`).

## Quake: texture-budget crash cleared, gameplay not accepted

Isolated source `decd4910d3f2ac482d60de8ab9e41ca3d5fe7929`, based on main
`662ac366`, admits a complete 256 MiB single-mip extended 2D snapshot. Arrays,
volumes and mip chains retain the 128 MiB limit. Both logical and padded
physical byte counts are checked before the source read. No content,
ownership, mapping-currentness or completion checks are removed.

The focused build succeeded. Both actual ARM64 and Rosetta/x64 host runtimes
passed the same 210 managed cases with zero skips. These are budget and
read-attempt controls, not proof of texture layout or scene correctness.
The native-only osx-x64 app was signed and all 239 bundle file hashes matched
before and after the replay. Guest CPU execution remains x64/Rosetta.

Actual run:
`.claude/worktrees/root-quake-snapshot-budget/artifacts/runs/20261008-200941-quake-game-entry-decd4910`.
Qualification receipts: `artifacts/validation/quake-entry-decd4910`.

The game remained alive at 150 seconds, peak RSS 4422 MiB. The previous
8192-by-8192 R32Float snapshot-budget fatal did not recur. The engine loaded
`demo1.dem` and The Necropolis and produced a few level frames. The inspected
120-second screenshot is visibly corrupted. Scheduled menu and movement
inputs do not establish player control; automatic demo loading is not gameplay
acceptance. The scoped budget change is now included in the qualified local
`d5d198f7` integration; Quake gameplay remains unverified.

Shutdown did not finish within the harness's ten-second TERM grace period,
so the owned game required KILL. The shutdown log also records command-stream
synchronization failure. The wrapper returned zero and retained stable bundle
hashes, but that is not a clean game-shutdown result. No owned process group
or shared game lock remained. An unrelated emulator process was present and
was left untouched; this run makes no performance claim.

## Earlier entry blockers and isolated candidates

These earlier observations explain the candidate lineages. The signed replays
at the top of this document supersede them where the same failure has been
retested. Dreaming Sarah remains the only title with verified directional
control; none of these earlier runs accepts an additional title.

- **Octopath and Silent Hill:** successor `1610948d` captures original display
  ownership at the initial copy and passed 150 managed cases on each host
  architecture. Both signed game replays passed the former display-provenance
  refusal. Octopath retained its first native storage producer at `0x80302C0000`,
  then exited 134 after 35 seconds because provider publication preparation
  was rejected. Silent Hill completed two native-producer-to-canonical
  publications before its parser rejected an original selected storage seed's
  first-use receipt. It remained alive for 60 seconds with a black capture and
  zero guest frames. Successor diagnostics `cbbf7cc3` passed 196 managed cases
  per host architecture. Its Octopath replay refused the retained singleton
  volume's watch/version earlier, before any publication preparation. Its
  Silent Hill replay completed two publications, then dirty validation refused
  writer-history continuity: original revision 157585 versus 159138, a distance
  of 1553 exceeding the 512-record ring. That run had no new first-use error.
  Original-watch continuity candidate `4b39506d` passed 205 managed cases and
  two real native publication cases on each host architecture. Its signed
  Octopath replay completed one canonical publication and multiple dirty
  acknowledgments, then exited 134 after 33 seconds. Silent Hill completed two
  publications and exited 134 after 26 seconds. Both now fail the ordinary
  post-release volume-owner check; no history-window failure was logged.
  The earlier diagnostic budget was exhausted by expected ordinary reads under
  the held publication reservation, so it does not identify the later watch
  predicate. Source-only diagnostic `743f10dc` separates those bounded budgets
  and records the actual admission guard value without changing policy.
  The continuity fix retains the original physical veto and creates
  private successor watches only for exact applied provider writes; unwatched
  receipts retain the existing history limit.
- **Demon's Souls:** the same signed `9e3adfcb` bundle remained alive for 60
  seconds but had zero guest frames and a black capture. Shutdown exposed a
  prior worker failure: selected storage mip copy changed during all three
  attempts at `0x7092C80000`. Process survival does not establish progression.
- **Superliminal:** `f4ebd249` passed the original six-face CPU depth-cube
  snapshot and real translated comparison controls on both host architectures.
  Its 150-second game replay still failed because the sampled cube overlaps an
  existing native depth producer. The successor must retain each original
  selected depth face and its ordered native producer; it cannot substitute a
  CPU copy over that producer. Cold, previously untracked faces must retain
  independent original content tokens. The actual captured cube descriptor is
  1024-by-1024, with 2 MiB per D16 face. The earlier native successor's finite
  256-by-256 qualifier rejected that descriptor. Corrected successor `f71dbdb7`
  passed 30 managed cases and two actual native cases per host architecture,
  checking all six faces and 6,291,456 float texels in each native case. Its
  signed 150-second game replay reached title/loading, then failed original
  depth-face mapping/session/content admission. It remained alive with zero
  later guest frames; no control or gameplay is accepted. Diagnostic successor
  `4663db20` passed 33 managed cases and two full native cases per host
  architecture. Its signed 150-second replay identifies the first predicate:
  the mapped-copy provider rejected the original 2 MiB face probe at
  `0x7080050000`. Session, mapping-currentness, epoch and content-token checks
  were not reached. This isolated lineage lacks the newer protection-change
  receipts already present in the publication lineage; a combined replay is
  required before attributing the provider refusal.
- **Void Terrarium:** the 600-second entry run and a separate 300-second shader
  diagnostic run did not verify control. The actual rejected shader contains
  LDS operations, six barriers, GS allocation and primitive export target 20.
  It needs native mesh translation. Transport candidate `d2639278` passed real
  mesh rasterization on both host runtimes, including both triangles and every
  pixel of the complete test target. Guest translation and gameplay remain pending.
- **Quake:** the bounded 256 MiB texture candidate passed the earlier budget
  blocker. A further 300-second run used the correct Options mapping (Tab) and
  movement inputs but did not establish player control; captures remain corrupt.
  Shutdown required the harness's owned KILL after its TERM grace period. An
  initial wrapper cleanup assertion saw a brief owned zombie; a separate fresh
  check found the group absent. The original rejection is retained.
- **Poppy Playtime:** actual ordinary-provider testing exposed Rosetta's
  4 KiB placement inside the measured 16 KiB kernel page. Final candidate
  `d9d1dc8e` retains a doubled original Mach allocation and protects a contained
  aligned page. Its 24 managed controls passed on each host runtime, and three
  fresh x64 native workers passed actual permission restore, SFENCE, lifetime
  drain, full 16 KiB byte checks and real 128/129 receipt capacity. Genuine
  guest signal continuation and gameplay remain pending; these constructed
  ordinary frame inputs do not qualify signal activation.
- **Oregon Trail:** sampled-image ownership remains an entry blocker.

Current immutable evidence:
`artifacts/validation/all-games-20261008/octopath-volume-publication-watch-9e3adfcb-qualified`,
`artifacts/validation/all-games-20261008/display-original-1610948d-prepared`,
`artifacts/validation/all-games-20261008/canonical-publication-decline-cbbf7cc3-prepared`,
`artifacts/validation/all-games-20261009/persistent-publication-watch-4b39506d-prepared`,
`artifacts/validation/all-games-20261008/depth-cube-entry-f4ebd249-qualified`,
`artifacts/validation/all-games-20261009/superliminal-native-depth-faces-f71dbdb7`,
`artifacts/validation/all-games-20261009/superliminal-depth-first-veto-4663db20`,
`artifacts/validation/void-skipped-shader-400e5c64`,
`artifacts/validation/native-mesh-entry-d2639278`,
`artifacts/validation/quake-entry-decd4910`, and
`artifacts/validation/all-games-20261008/poppy-native-permission-receipt-d9d1dc8e-qualification`.

## Combined entry candidate

Local worktree `root-game-entry-integration` combines the original publication
watches, native depth faces, first-refusal diagnostics, original Void draw
capture, native mesh transport and Quake snapshot budget with the current main
command-stream and failure handling. The first build at `d8267dc9` exposed two
missing arguments to the revised snapshot-budget error helper. Successor
`420b33e2` supplies the existing 128 MiB limit at both sites; admission bounds
are unchanged. Successor `0e0e76a4` additionally grants the Metal test assembly
access to its internal layout type. All three builds, 532 distinct managed
cases and five fresh actual native cases passed on each host architecture.
The signed native-only bundle retained all 239 file hashes through four runs.

These combined replays supersede the earlier isolated runtime results:

- Superliminal ran for 150 seconds and rendered loading artwork. The earlier
  mapped-probe refusal did not recur; the first failure is now
  `RequireOriginalCurrent` during depth-face enqueue. The final capture remains
  at loading. No menu or control is accepted.
- Octopath exited 134 after 32 seconds. Original writer revision `-1` is the
  first publication-preparation refusal; sampled admission and coverage loss
  were both zero. No canonical publication completed in this run. A successful
  watch enrollment must record its original revision before copy bytes; a
  younger post-copy sample cannot replace that original event.
- Silent Hill exited 134 after 26 seconds, after two canonical publications.
  The exact original volume watch fails on admission `2`, while released,
  revoked and coverage-loss flags are zero. Its native owner, version and
  volume state match. An active writer must not be treated as a successful
  ordinary currentness check.
- Void Terrarium ran for 300 seconds with no fatal. Captures at 205 and 245
  seconds show the main menu with New Game selected. The later capture is dark
  after selection; movement/control and gameplay remain unverified. The two
  original rejected shaders and draw metadata are retained for mesh work.

All owned processes exited and the shared run lock was released. No additional
title is accepted for gameplay. This earlier combined candidate was subsequently
qualified and integrated through the successor described above. Complete receipts are in
`artifacts/validation/all-games-20261009/game-entry-integrated-0e0e76a4-prepared`.

## Earlier entry checks (superseded by the observations above)

- Superliminal: qualify isolated `aa38ff5a`, which separates selected color
  mip 0 from the disjoint storage view that currently causes startup failure,
  then replay the signed game with the opt-in enabled. The successor guards
  complete crossing-start image spans before seed consumption/allocation/read;
  its 18 focused managed cases passed on both host architectures. Actual
  game entry remains unqualified.
- Octopath: isolated `3d49ba8e` passed managed and native publication controls,
  but its real game replay still failed before the first canonical producer.
  Diagnostic successor `ddba460f` records the exact other image whose original
  provenance is missing. Identify its actual creation route before changing
  admission.
- Demon’s Souls and Silent Hill: replay the qualified original-owner path
  against the observed global mapping-epoch rejection.
- Poppy Playtime and Oregon Trail: resolve the previously observed CPU-store
  fault and sampled-image ownership rejection respectively.
- Void Terrarium: drive the game after loading and prove movement/progression;
  allow a longer bounded entry run when intro/loading consumes the short run.

Dreaming Sarah is the only title with prior verified directional control.
No additional title is accepted by this checkpoint. The original rendering,
loading and sustained-frame-rate goal remains outstanding after entry works.

## Octopath: actual missing owner identified

Diagnostic source `ddba460f249dc4e1cd6ff60707940271f458b0a2` passed the same
52 focused managed cases on ARM64 and Rosetta/x64, then was published as a
signed native-only app. Actual replay
`artifacts/runs/20261008-201706-octo-canonical-cohort-offender-ddba460f`
exited 134 after 34 seconds, peak RSS 2231 MiB. It does not qualify gameplay.

The bounded original-cache diagnostic identifies the other image at
`0x8030BE0000`: initialized, GPU-written, content version 2, 1×1×1,
native type 7, native format 73. Its original creation was storage dispatch
24, queue `dcb.graphics`, submission 2, mode 27, guest format 10/4, type 10;
raw descriptor `[8030BE00,03C00000,00000000,A1B00FAC,00000000,00700000,00000000,00000000]`.
The failure branch is `origin-missing`, not a demonstrated mutation of the
target's original protected token. The next fix must capture original mapping
and ownership at this singleton volume's creation; it must not manufacture
provenance at lookup or ignore this GPU writer.

## Superliminal: next depth-comparison entry blocker

Signed native-only `aa38ff5a5f18e46ba6e0a8e40feb97667715a7b4` passed its
18 managed cases per host architecture and retained all 239 bundle hashes
across the actual game replay. Run
`.claude/worktrees/agent-metal-color-physical-view/artifacts/runs/20261008-202353-superl-color-view-entry-aa38ff5a`
exited 134 after 71 seconds, peak RSS 3924 MiB. The inspected 55-second
screenshot shows the title and loading indicator over a corridor scene;
controllable gameplay was not reached.

The first fatal is now depth-comparison admission at graphics sequence 2540,
submission 84: guest format 2/0, type 11 is rejected by the current
Float-R32/Unorm-R16 2D/array-only gate. The color/storage conflict did not
appear before that failure, but execution of the revised color-view path
has not been established. Native depth-cube support is the next entry target;
this observation does not authorize treating a cube as an ordinary 2D image.

The wrapper returned zero despite the game's exit 134. The owned game exited
without KILL and no owned process group or shared lock remained. No new title
is accepted by this replay.
