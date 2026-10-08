# Current all-games native Mac qualification

The complete nine-title collection was replayed sequentially on clean signed source `2adf328d231d3e2c67c8dfe951f61939a60395a1`, SDK10.0.103/runtime10.0.3, osx-x64/Rosetta on Apple M4/24GiB. The bundle uses Metal, AppKit, CoreAudio and GameController; no Vulkan/MoltenVK fallback. All-nine rendering, gameplay, sustained60+ FPS and fast-loading acceptance remains incomplete.

Compilers and our test/native workers were paused during games. Unrelated REmu processes repeatedly appeared and were left untouched. These runs qualify current compatibility blockers, not quiet-host or one-change performance comparisons. Game files, reference repositories and save/config data were preserved.

| Title | Actual observation on this source | Remaining blocker / evidence limit |
| --- | --- | --- |
| Demon’s Souls | Zero reported guest frames in150s, peak2743MiB | Selected-mip admission rejects global mapping epoch323319→323321 at0x7092C80000; affected-resource mutation or stable content is not proved. |
| Dreaming Sarah | Outdoor scene visible; peak1181MiB | Short late reporting windows63–72Hz follow47–56Hz windows. Sustained60, complete fidelity and load timings are unqualified. |
| Octopath Traveler II | One early frame, then black final screenshot; peak3387MiB | Exact3840×2160 R8Uint mode24 singleton storage array is rejected. |
| Poppy Playtime Chapter1 | Exit138 after28s; peak1653MiB | Unrecovered streaming-store fault at guest RIP0x80135FB0B persists. |
| Quake | Intro and warning text visible; exit134 after67s, peak2210MiB | 8192² R32Float depth-compare source requires256MiB, exceeding128MiB snapshot bound. Layout and filtered comparison also require qualification. |
| Silent Hill: The Short Message | One early frame; exit134 after31s, peak2210MiB | Passes old1024² BGRA guard, then rejects the same4K R8Uint array as Octopath. |
| Superliminal | Loading/title frames; exit134 after44s, peak3887MiB | Selected512² RGBA16Float color from a ten-mip allocation is conflated with a disjoint128×64 storage view. |
| The Oregon Trail | One early frame; exit134 after32s, peak1959MiB | Passes oldBGRA guard; GPU-written1×1 RGBA8 sampled alias lacks captured ownership/view provenance. |
| Void Terrarium | Dim cave scene and Move prompt visible; peak2056MiB | Late reporting windows10.5–12Hz; controlled progression is unqualified. |

A separate Void directional replay on the same bundle remained alive150s, peak2581MiB. Enter at100s advanced its New Game menu toward the cave scene; directional inputs overlapped loading. Screenshots65/105/125/135/final do not establish responsive movement or progression. Late reporting windows were about9.5–11Hz. It is an extra observation, not another accepted title.

Reported guest-frame windows include movies and loading; they are not scanout or sustained gameplay FPS. Runtime survival is not loading duration. A successful owned host TERM does not prove guest thread joins. Numerical texture fixtures do not establish scene fidelity.

## Accepted local changes in this bundle

- Native backing ownership and ordinary-operation pins preserve provider allocation/page geometry and exact close/quarantine behavior. Pins establish lifetime, not mapping currentness or exclusion of writers.
- Ordinary owned linear texture snapshots remove one duplicate payload copy. The256KiB fixture drops from528,376 to266,208 allocated bytes; actual native full-image checks pass after guest-source mutation.
- Work without captured source leases returns shared empty dependencies:4096 warmed calls allocate0B, versus1,507,328B in the control. Lease validation remains active when a lease exists.
- Loader relocation filtering scans the private owned list through a readonly span and allocates only eligible import capacity. The synthetic778,191-descriptor/1000-import control drops from24,902,240 to32,056B. Its JIT/timing evidence does not measure whole-game loading.
- The exact1024² BGRA storage array now has complete independent layout and actual native full-image typed store/load proof. The three affected games advance past that specific rejection and expose the distinct blockers above.

At exact source2ad, each CLR passed23 backing managed,256 library managed and77 Metal managed checks, plus4 required actual native backing and43 required actual native Metal cases; zero skips/failures. DLL hashes match before/after. Shader/parser source is unchanged from earlier119 pure and5 actual native parser checks per CLR. ARM64 host controls do not enable ARM guest execution.

## Evidence and next candidates

`artifacts/validation/all-games-20261008/all-nine-2adf328d` retains the nine original run directories by manifest, screenshots, fatal descriptors and exact commands. `combined-2adf328d` contains exact-case unions, TRXs, commands and frozen DLL/native closure checks. `void-input-2adf328d` records the additional input run.

The4K R8 candidate now has21 new managed rows, full8,294,400-texel independent offset/byte agreement under the existing emulator topology, and4 actual native typed cases on both CLRs. Its separate signed replay passes that guard in Silent Hill and Octopath, then rejects static mip1 of a2048² R16Float mode27 eleven-level resource (1024² selected view). The qualified R8 change is integrated locally; see [exact evidence](r8-fourk-array-20261008.md). Actual console topology remains unknown.

The [R16 selected-storage increment](r16-selected-main-20261008.md) is now integrated and qualified on clean3ae88a16:376 library,117 managed Metal and55 actual native Metal checks pass on each CLR, zero skips/failures. A separate signed3ae replay clears the R16 descriptor rejection in both titles. Silent Hill exits134 after31s, peak2269MiB, with a global mapping epoch802964→804552 admission veto at802F270000/selected0. Octopath exits134 after36s, peak2893MiB, when CPU dirty synchronization rejects native type3/depth1 singleton arrays. Both latest screenshots are black; each has at most one reported guest frame. No gameplay or FPS/loading acceptance follows. The table above remains the all-nine2ad snapshot; these newer two-title observations supersede only their specific blocker entries.

The [standalone Poppy probe](poppy-rosetta-store-probe-20261008.md) executes the exact streaming-store encoding under Rosetta with owned private/shared mappings. It reproduces concurrent Disarmed-decline failures with streaming, ordinary256-bit and split128-bit stores; successful isolated permission restoration cannot authorize arbitrary production retries or resolve the lifetime gap before the retried guest store.

Bounded indexed vertex-prefix reduction is now imported locally after corrected actual native stage_in checks and a [four-run Sarah comparison](vertex-prefix-abba-20261008.md): enabled captures copied99.988% fewer producer vertex bytes relative to their original full extents; final30s observed guest commit cadence was94.667/96.713Hz versus55.424/63.933Hz off. Window-focus differences remain a performance confound. No full-game60FPS, scanout, loading or all-title rendering acceptance follows. The signed3815 comparison app and2ad all-nine app are separate artifacts. Finite color-mip projection remains isolated pending currentness/writer authority.

The [mapped-copy integration](mapping-copy-main-20261008.md) is qualified on clean115316f0:403 library,60 host-memory managed and4 actual native mapped-copy checks pass on each CLR. The contract has no Metal consumer yet. Its signed Sarah replay verifies right/left movement in the outdoor scene; short reported guest-frame windows are around100Hz, with sustained scanout and complete fidelity unqualified. Two earlier signed073d diagnostic Octopath replays remain black after global mapping-epoch admission vetoes, before the typed-array diagnostic. These observations do not replace the all-nine2ad table or certify other titles on115.

The [combined completion integration](metal-completion-main-20261008.md) is now local at724d906d after489 library/117 managed Metal/55 native Metal regression checks per CLR. Its signed90 s Sarah replay verifies right/left movement, peak859MiB; late reported windows are around59Hz and do not meet the requested sustained60+ gate. These results do not certify typed texture GPU-to-guest publication or other games on724.

The [native depth comparison integration](native-depth-comparison-20261008.md) is now local at8783b897:128 managed GPU/8 managed shader/68 actual native Metal/6 actual native fragment compiler checks pass per CLR. Its signed90 s Sarah replay verifies right/left movement, peak910MiB, with short late58.99–62.45Hz windows after slower earlier windows. This does not certify sustained60+, loading, Quake gameplay or all-nine support. Quake's256MiB tiled snapshot limit remains unchanged; comparison-after-filtering is fixed for qualified normalized R32/R16 depth classes.

Next work implements initial detached-seed admission, typed CPU image synchronization and canonical GPU texture publication, color-mip and sampled-alias ownership, and fault recovery. One-change quiet-host pacing/loading comparisons follow stable controllable scenes. No all-game60FPS or fast-loading improvement is claimed. No remote publication or merge was performed.

The [typed storage runtime checkpoint](typed-storage-runtime-20261008.md) is separately qualified at candidate3986109a but remains isolated. Its new typed upload controls pass; Octopath aborts earlier during provider mapping-copy rejection, with unchanged content observation. Diagnostic7e46 confirms Rejected/no mapping observation. A successful-protection ledger successor and explicit guest/base versus temporary host permission handling are being prepared to address source lifecycle gaps. Sarah movement survives the candidate, with fluctuating short55–63Hz windows. Canonical first-use publication1b passes30 managed/four actual Mac memory cases per CLR; range-history615 now passes44 managed/four actual Mac memory cases per CLR. Actual two-dispatch Metal publication and native/JIT write ordering remain incomplete.

[Current signed Quake controls](quake-completion-regression-20261008.md) narrow an early-abort regression to the completion increment: 2ad and115 still reach the256MiB depth-image limit, whereas724,878 and diagnostic7e46 abort inside guest code after only three presentations. The old all-nine observation does not certify current-main Quake progression. Completion ordering must be repaired before its large depth producer can be traced on current source; no depth-cap increase or gameplay acceptance has been made.

The isolated [actual two-dispatch storage checkpoint](finite-storage-two-publications-20261008.md) now passes32 staging/22 typed managed and two fresh actual Metal cases per CLR. It verifies full128KiB padding-preserving canonical publication, disjoint EOP ordering and an overlapping CPU-write refusal. Production flags remain inactive; it does not certify game/JIT writers, general protection changes or Octopath's different format. The Quake abort probe identifies its actual frame-start GPU context-label timeout; command-memory ordering is being corrected before replay.

The [command-memory ordering replay](command-memory-order-runtime-20261008.md)
at isolated49e424a2 passes104 focused managed rows on each runtime configuration
and publishes a signed native-only app. Its100s Quake replay still aborts through
the same guest context-label timeout after three presentations. Traced EOP
labels are admitted before the abort and applied afterward, directing the next
investigation toward startup/readiness and delayed queue drain. An ordinary
overwrite is not observed in the bounded trace. No new game is accepted.

The [protection-intent runtime checkpoint](protection-intent-runtime-20261008.md)
passes71 managed and four fresh actual Mac cases per CLR at7a286abb. Combined
typed/protection sourcec24c327a additionally passes647 managed and78 native
Metal/compiler cases per runtime configuration. Its signed Octopath replay
clears the earlier mapping-copy rejection, then exits134 after38s at8030300000
because GPU-produced/pending-write ownership cannot authorize a dirty CPU seed.
It remains isolated; no gameplay or performance gate is met.

Timed successor46b3de48 passes106 focused managed rows per runtime. Its25s
signed Quake probe still aborts during queued OffscreenGuestDraw2, whose measured
startup wall time is166.551ms; EOP3 starts about177.566ms after admission. This
identifies the preceding slow draw, without assigning its time to CPU or GPU.
For Octopath, the rejection guard combines image GPU-written, global pending
work and open-batch flags; the current log does not identify the triggering
term. Exact per-resource ownership and completion remain the next checks.
