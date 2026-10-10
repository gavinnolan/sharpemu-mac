# Current native Mac game-entry checkpoint

Priority: make all nine games reach a menu or controllable scene first; performance and visual polish follow entry.

Current qualification update (October 10): the four-fix source candidate
`638c3bdf` passes its first Host build (`rc=0`, 7.835 seconds). ROOT verifies
all 13 checkpoint artifacts, 2,054 source files, 91 compiled files and fresh
absence of its eight process roots, heavy workers and shared lease. The runner
successfully observes its pinned kernel process identities during this build;
no testhost, native fixture or game result follows. Libs/GPU builds are next,
then the 108 focused and 1,354 ordinary cases per actual CLR. Host checkpoint:
`ecf29b4d2f5a6387bc93f1e82ffbfb9e34c2839ae8d51f2225953230afa7738b`.

Separate diagnostic `2a919bd1` freezes the reviewed Poppy raw trap-number
observation without changing recovery. ROOT verifies 2,056 live/archive source
hashes. Its 11 new Mac metadata rows and six decoder controls are unexecuted;
it remains separate from the current build candidate and is not integrated.
Source manifest: `d3e8ef16e4bec8b1141ca851a308730e35f52bf0ee4b12f58922bc82455940b8`.

Local main integrates qualified source `e2535177` at local merge `02e7dbcf`;
its `src`, `tests` and `scripts` trees match the tested source exactly. This adds
a stable GPU view of underlying memory across per-thread CPU trackers and resolves
that view before original capture/check. It also adds bounded first-refusal,
publication-admission and Silent Hill fault-thread import diagnostics, while
preserving ownership and original validation guards. The prior native Fill,
pending-only continuation, cold enrollment, GPU-produced R32, physical-placement
and rejected-draw capture changes remain integrated. No remote push or upstream
merge was performed. Historical replays keep their original executable source
and cannot be relabelled as results of this implementation.

Latest state: candidate `e2535177` passes all three builds and 1,246 ordinary
cases on each actual CLR. All 246 separate workers pass, including the six
formerly failing Busy-family workers. Signed publication passes. The first
Demon's Souls attempt was rejected before emulator launch while the external disk
was disconnected; that failure remains preserved. At 23:23 UTC on October 9 the
volume returned, and all nine expected game executables are present. ROOT freshly
reverified 2,042 source files, 104 compiled files, 239 signed app files and all
1,818 qualification artifact rows. A fresh, separately tagged Demon's Souls
180-second empty-input replay on that same qualified app ends naturally after
57 seconds (guest exit 134, peak sampled RSS 2,479 MiB). The previous
`guest-memory-replaced` failure is absent; the winning refusal now occurs at
ordered action 177 / graphics submission 4 in `original-native-r32-immutable-copy`.
Its exact read-watch guard observes ordinary admission 2; a later sample is zero.
The source range is `0x7092CC0000 / 524288`. Four captures show only static launch
artwork, with no guest frames or menu. Root independently confirms driver,
harness, game and input roots absent by PID/PPID/PG at 23:33:20 UTC, no actual
heavy worker and no shared lease. No signals or input were issued. No same-source
DS retry is authorized. Immutable replay manifest:
`556b35942d1bc11b5c1592e6bd623c488cfea117d538e6c3952c8d5e818a5db8`;
ROOT verifies all 317 retained hashes. Bounded same-original Busy handling is being reviewed
in isolated source. No rebuild or new provider/Metal fixture execution is granted.
Accepted gameplay remains 1/9 (Dreaming Sarah); no new gameplay or performance
acceptance follows from these fixtures. Kairo's v557 offline window is terminal;
SharpEmu holds the heavy lane for game entry. Kairo remains source/light until
explicit coordinated return.
Separate Poppy foundation `d69faf76` passes three builds, 53 focused controls and
all 1,298 ordinary cases per actual CLR; it remains unqualified for native/provider
or game execution and is not integrated. Void's `6f5d16d3` effect fixture remains
source-only and unqualified.

## Next source candidates and current runtime coordination

### Fresh reconnected-volume Superliminal replay

The source-exact `e2535177` Superliminal replay `20261010-014132` renders the
corridor/title loading scene and spinner, then exits 134 after 77 seconds,
peak sampled RSS 4,027 MiB. The winning failure at log 7889/7890 is
`other-native-image-original-missing`, image `0x7012540000`, 1920x1080
RGBA16F, native version 136, during offscreen draw sequence 2683 / submission 91.
The later command-memory completion failure is secondary to that image-provenance
refusal. No menu, input or gameplay is observed. Root independently confirms
all four roots absent and no actual heavy worker/shared lease at 23:43:19 UTC;
no process signals were issued. Immutable archive manifest:
`2ef3a688494513972729a3b6078c41f1bc549392314b5bb5bea5f36308ba76a9`;
ROOT verifies all 324 retained hashes. Earlier telemetry intervals include guest
frames; late zero-frame intervals cannot establish a cumulative zero. The
qualification sidecar preserves that distinction without changing the archive:
`35a7ad67f2400392c5581a54304f37f38861dfb2d1b99fb49174c0d3210edd94`.

### Fresh reconnected-volume Silent Hill replay

The source-exact `e2535177` Silent Hill replay `20261010-014521` exits 134
after 25 seconds, peak sampled RSS 1,906 MiB. No capture is retained before exit;
one telemetry interval reports one guest frame, which establishes no visible
menu or scene. Log 2286 first records held publication admission at
`0x8010BC0000 / 65536`; the winning log 2446/2448 refusal is canonical dirty
acknowledgment's `other-image-original-volume-watch-or-version`. No input or
process signal was issued. The winning log 2446 local fallback identifies a
PublicationValidation ticket, owner 517, with reserved/watched backing 1 and
`watched-watch-initial / admission-not-exclusive = 4294967297`. The offending
sibling is `0x8010DF0000`; its native/version predicates passed before the watch
refusal. This is the same transient PublicationBit+1 defect targeted by the CAS
candidate; it does not demonstrate a distinct version replacement. Root
independently confirms all four roots absent, no actual heavy worker and no
shared lease at 23:47:50 UTC. Immutable manifest:
`c455c636da93f7baf6b06e009d924d67f5fb8acfb24d94316897c7b8461ffa21`;
ROOT verifies all 300 retained hashes.

### Fresh reconnected-volume Octopath replay

The source-exact `e2535177` Octopath replay `20261010-015114` exits 134 after
32 seconds, peak sampled RSS 3,305 MiB. Its retained capture at 25 seconds is
black; one earlier telemetry interval reports a guest frame. No menu or input is
observed. The terminal exception at log 1355/1356 is deferred canonical dirty
ORIGINAL receipt revalidation. The earlier log 1297 local validation sees the
same PublicationBit+1 counter defect, but it is not the terminal exception;
the receipt lifecycle requires separate source attribution. Root confirms all
four roots/groups absent and no lease at 23:52 UTC. Immutable manifest:
`9659b4fdd37e9012fa8dc695044ffcbc98bd1252bf2993135f6063bdf233a9ce`;
ROOT verifies all 303 retained hashes. No unchanged retry is scheduled.

### Reviewed memory fixes awaiting execution

The DS candidate retains the same original snapshot and waits at most 500 ms
only when its known provider reports temporary ordinary-writer Busy. Stale,
publication and ownership failures remain final. Independent source review
covers all ten production paths and 22 new test rows; execution is pending.
CAS donor `a4fbe82c` admits ordinary writers only through a bounded compare and
swap, so a refused writer cannot transiently increment an exclusive publication
counter. Twelve new source rows cover held reservations and refusal boundaries.
The reviewed union is frozen locally at `d914f004`, with 2,047 worktree/archive
source hashes, exactly 16 donor paths and 2,031 unchanged parent paths verified
by ROOT. Its source manifest is
`a11eef0632e01b98eeb526574563014a2a55af49b7c5a88edfcc8fd39af11403`.
These are unqualified source candidates, with no game recovery claim.

The revised R3 process controller's 26 pure ownership regression controls pass
once, with no failures, errors, skips or OS signals. ROOT verifies all 23 archive
hashes and both roots/groups absent. The unsafe old-behavior demonstration is an
expected passing signal-spy model, not an actual foreign signal or red execution.
This establishes pure control behavior only; no actual Darwin path lookup,
compiler, CLR/testhost, provider, Metal worker or game is qualified by it.
Archive manifest:
`5c43c2ed8a5c92bbe8f1f0043a55bb8cf71f6aed4115f9ef266f768269712adc`.

### First candidate build: controller refusal retained

The first `d914f004` Host build is unqualified. R3 captures the direct .NET
worker, then rejects a new descendant whose kernel executable differs from the
selected dotnet image. The refused candidate's path was not retained, so its
identity is unknown. The build later logs the Host DLL output, but its child
return code and terminal receipt are unobserved. No later build or focused test
runs, and no process is signaled. Root verifies all 32 failed archive hashes,
then independently confirms both known PID/PPID/PG roots absent and no actual
heavy worker. Exact dead-owner lease 21945/nonce `4d09d480...` is archived and
administratively cleared; the old controller's unknown state and stopped ledger
remain intact. Failed manifest:
`e59e696a9a725c637068c5c82036ec338f26fe8843d159d3eac0ca184a38a2f4`.

The SDK can launch its own native C# compiler apphost. R4 pins that exact
SDK path and hash as build-only authority, keeps runtime workers restricted to
the selected CLR, and record every refused candidate before policy evaluation.
This is a justified controller revision; it does not identify the unrecorded
child or qualify the failed build. No broad shell/tool exception is planned.

R4's five isolated image-policy controls pass. Its first actual Host build then
stops after 2.24 seconds because known compiler PID 30196 changes one of its
recorded process identity fields. The changed row was not retained; its cause
is unknown. All three observed workers have exact executable records. No later
build, test, native worker or game runs, and no process is signaled. ROOT verifies
all 48 failed artifact hashes and fresh closure, then archives and clears only
the exact dead-owner lease. The failed manifest is
`9ccabccc320ed725bb9e2d63771bc83c3f6a21aa6c38540606d0d6f7c6871317`.
The stopped controller and unqualified outputs remain unchanged.

R5 adds the first mismatched process row, including process state, to that same
refusal receipt. It changes no identity or signal authority. ROOT verifies its
31 payload hashes and the three changed helper functions; source, case lists,
SDK pins and adapters remain unchanged. Its one pure diagnostic control passes.
The diagnostic Host build stops after 1.48 seconds: the exact first mismatch
changes only the compiler's displayed command from its path to `(csc)`, with
state `R`; PID, parent, group and displayed start time remain unchanged. This
does not prove PID reuse or a zombie. ROOT verifies all 51 failed artifact hashes,
2,047 source hashes and 91 unqualified binaries, then confirms exact root/group
closure and archives/removes only the exact dead-owner lease. No process is
signaled, and all subsequent phases remain unrun. Failed manifest:
`889abc52064e529cc5f01f088eb097b54d313cd0ae75fb47574e8f4a425c789a`.
Prepared manifest:
`22bda8c5007560adf4f3e8696bec927993ea186bd266a7a179ecd0aa3c07169b`.

Apple's `ps/print.c` argument-retrieval error path prints the parenthesized
kernel command name; displayed `comm` is consequently unsuitable as immutable
process authority. R6 source preparation replaces that display check with a
kernel process incarnation and exec-generation token, retaining command text
as diagnostics. Exact image, original parent/group, finite caps, refusal and
cleanup rules remain required. No R6 execution is granted by this note.

### Reviewed Octopath retry successor

Immediately before Octopath's terminal refusal, preparation reports ordinary
admission 2. Its later retention predicate is unobserved because the general
trace budget is exhausted. A separate reviewed successor records that predicate
with an independent bounded diagnostic. It also preserves exact original
receipt bookkeeping when the first attributed preparation refuses for ordinary
Busy and a later status is Current. This creates no completion, publication or
native effect; a later real preparation remains necessary. Missing attribution,
publication, stale mappings and changed owners still refuse. All 23 new ordinary
source rows and both production changes pass independent source review; builds
and tests remain unrun. This possible retry window is not established as the
terminal live-game cause. R2 source inventory:
`90c6a4d64cc9fb2a467b4aecef44a3fa0f5bf36e9cb353981c8bdc2ede156768`.

The four reviewed paths are frozen at local successor `37bb1dec`, with source
manifest `429c07d336944b1b80dbc052ceacba9c0dc2344c1347cde0e16986029d6bd3a7`.
ROOT verifies all seven artifacts, 2,049 live/archive files and 2,045 unchanged
parent paths. Its expected ordinary inventory is 1,303 cases per actual CLR;
246 separate worker identities remain unchanged. This successor is unqualified
and remains separate from main.

### Fresh Quake replay

The source-exact `e2535177` Quake replay `20261010-022046` runs to its 180-second
cap. Captures show publisher/intro frames and a readable advisory, followed by
gray bands and colored corruption at 135 seconds, then a black client at 175
seconds. No usable menu, input or controllable scene is observed. Initialization,
demo and map log messages do not establish gameplay. There are no fatal or
unsupported exceptions before the first host shutdown request; 9,450 retained
semaphore-timeout rows are symptoms with no established causal link.

The controller/harness returns zero after 192.58 seconds, but the game does not
exit within ten seconds of the harness's SIGTERM and requires its existing
SIGKILL escalation. This is not a graceful shutdown or a recorded guest exit
code. Peak sampled RSS is 6,264 MiB. Neutral input is cleared before terminal
cleanup; no manual input occurs. ROOT verifies all 385 archive artifacts, 2,042
source files, 104 compiled files and 239 app files, then independently confirms
all four roots/groups absent and no actual heavy worker/shared lease. Archive:
`d4c9e6e7d7108c253e3610207cf3469477927d5d02b3a0d61a95591724860d0e`.
No unchanged Quake retry is scheduled. Six titles have fresh game-entry evidence at this point; the subsequent
Poppy check is recorded below. Void and Sarah checks remain pending.

### Current Poppy diagnostic check

The exact `e2535177` Poppy replay `20261010-023523` exits 138 after 19 seconds,
with peak sampled RSS 1,811 MiB. Its sole 15-second capture is black. Actual
SIGBUS records show the same instruction address `0x80135FB0B` with two
separate fault addresses; neither is recovered. Earlier nonzero guest-frame
intervals do not establish a menu or gameplay. No controller signal or host
shutdown request is observed. The native trace file has the expected 655,488
bytes; ROOT independently verifies all 310 archive artifacts, 2,042 source files,
104 compiled files and 239 app files, plus fresh four-root/group absence and
no actual heavy worker or shared lease. The pinned offline decoder returns
zero: 80 published records, zero dropped records and 4,016 unused/unpublished
slots. Invocation 3 recovers after an Armed owner restores RW protection with
return zero. Invocations 4/5 observe Disarmed state and decline. A later RO
rearm precedes their final declined dispositions. These records do not prove
actual trap permissions or allocation lifetime. Terminal archive:
`da4697548725f536e7a6ad807812345a1c1d1bc3bdad31e97c7cc9fd43891b16`.

ROOT verified the prepared payloads, 2,042 source files, 104 compiled files,
239 app files, 1,818 qualification links and original executable before this
single empty-input replay. Existing fault-thread import diagnostics and the
fresh native trace are the only added runtime diagnostics. The fault records
are observations, not proof of unsupported ISA or a protection failure.

### Combined boot-fix source candidate

Local `638c3bdf` combines the DS bounded original-watch wait and admission CAS
fixes with the reviewed Octopath dirty-retention and Superliminal regular-color
owner changes. The Octopath/Superliminal donor paths are disjoint and preserved
byte for byte. ROOT verifies all 2,054 live/archive source files and 13 exact
donor paths; no new production code is added during integration. Expected
ordinary inventory is 1,354 cases per actual CLR (293 Host, 956 Libs, 105 GPU).
The 246 separate native workers are unchanged. This is source review only:
no build, test, game or publish result is inherited by the candidate. It remains
separate from main until qualification. Archive:
`75374bcd4f8cb22009e529f042927305d5b02542bc1d6572af1d2c33f501e800`.

### Fresh reconnected-volume Oregon replay

The source-exact `e2535177` Oregon replay `20261010-013557` exits 134 after
38 seconds, peak sampled RSS 2,237 MiB. Its one client capture is black and guest
frames remain zero. The winning singleton-original failure is at log 1444,
address `0x80115F0000 / 65536`, during deferred canonical-dirty acknowledgment.
The provider reports Stale under publication admission `4294967296`; the exact
local validation diagnostic names owner 371, PublicationValidation / Prepared,
and rejects `watched-watch-final / admission-not-exclusive` at `4294967297`.
Reserved and watched backing identities are both 1. This separately measured
local guard is not ordinary Busy authority. Source investigation targets losing
ordinary attempts incrementing the counter under an exclusive reservation;
no stale/publication guard is relaxed. No input or process signal was issued.
Root independently confirms all four roots absent and no actual heavy worker or
shared lease at 23:37:27 UTC. Immutable archive manifest:
`00851170936a4e512a4b62717c872a1ff64a415ea736da82ad220b9073c1b8d0`;
ROOT verifies all 307 retained artifact hashes.


Kairo's one offline v557 build/test window is terminal. Two helper pins pass,
with one ignored capture diagnostic in that filter. Four separately activated
63-index diagnostics fail on incomplete retained CPU pages: prefixes 0/1 and 3
stop at buffer-load pc `0xd0`; prefix 2 requests an uncaptured index page. These
are fixture-resource limits, not new live-game bugs or LDS/gameplay qualification.
ROOT independently verifies all 62 observed identities and groups absent in a
fresh wide process scan, no actual heavy worker and no shared lease. Evidence
pins: `kairo-v557-terminal-runtime-handoff/root-preflight.json`.
SharpEmu holds the next lane for a fresh game-entry replay after the corpus
returns. The real Darwin-provider and supplied-wave Metal drivers remain held
before execution. Kairo stays source/light pending explicit return.

The eight real Darwin-provider fixtures are frozen separately at `81e10d48`,
off the qualified ordinary foundation `d69faf76`. ROOT reviews all eight
scenarios and independently verifies 2,053 worktree/archive source files,
all 2,051 parent files unchanged and the exact inverse parent tree. Only a
test file and qualification note are added; production and scripts are unchanged.
These fixtures require separate fresh workers, actual runtime identity, real
owned bytes/backings and explicit known cleanup. Their execution is still unrun;
the proposed 16 provider workers supply no fault-recovery or game-entry claim.

The first complete external `6f5d16d3` Metal driver preparation is held before
execution. Independent source review identifies a PID-reuse cleanup defect and
an ownership-uncertainty reset; a corrected preparation must preserve exact
original worker identities and keep ownership loss sticky. No driver from that
preparation ran or acquired the shared lease. The shader fixture itself remains
unchanged and unqualified.

Earlier handoff: Kairo's one bounded v556 window is terminal. ROOT independently
verifies at 20:38:53 UTC that runner/game `11260`/`11264` and their process groups
are absent, no actual game/compiler/test worker remains, and the shared lease is
absent. Evidence: `kairo-v556-terminal-runtime-handoff/root-preflight.json`.
SharpEmu now owns the qualification window; the first f76 build failure and
the successor's first native failure are retained. Kairo stays source/light until coordinated return. Its movie delivery
and bounded captures do not establish menu/gameplay.

The first isolated source union is `f76fa0d6fe4f91cf9df49db668d66b92a36e4cfa`.
It combines the reviewed first admission-owner/local validation diagnostics,
Superliminal failed-command metadata, Silent Hill first ordinary acquisition
currentness diagnostics, and an explicit stable GPU memory view. The GPU view
lets per-thread `TrackedCpuMemory` objects refer to the same underlying memory
for GPU binding; distinct roots, opaque decorators and malformed views still
refuse. It preserves the presenter identity guards. This targets the observed
DS `guest-memory-replaced` failure; a new game result is pending.

This union is **not qualified**. No build/test/game result is
inherited from `1893dc25`. ROOT verifies all 2,030 worktree and source archive
hashes; the expected ordinary inventory is 1,203 cases per actual CLR: 279 Host,
819 Libs and 105 GPU. The 238 separate workers remain a separate inventory.
Source manifest SHA-256:
`3fc63f6696106229afa5603a7d64a570cc6b51ddbf0a901b8a4eb6497ca697fd`.
Local main retains the qualified `1893dc25` implementation.

The first f76 build passes Host, then Libs fails `CS0103`: the new GPU-view test
partial omitted `using SharpEmu.Libs.Gpu` for `GuestGpu`. GPU build and every
test/native/publication/game phase remain unrun. ROOT verifies all 13 retained
failure artifacts, 2,030 unchanged source hashes, 60 partial binary hashes, and
fresh closure of all four owned roots. Failure manifest SHA-256:
`28dfa37146657f2da344b1a721d5082ee36e6fad3ce23e05aa3471a4de8e6b5b`.
Peer-reviewed successor `537e9fff4ac84b02ed3fa5814ab6f8c4c919e7c7` adds only the
missing namespace import in that test partial. Production and all case identities
are unchanged. The corrected source passes all three builds and all 1,203 ordinary cases on each
actual ARM64/x64 .NET 10.0.3 CLR, with zero failures or skips. ROOT verifies all
15 build and 46 managed artifacts, exact TRX identities and unchanged source/
compiled hashes. Build manifest `94f675219b8422ce77de0fe16d130d46ee8373fdf43255fb3f3b643d775d4c0a`;
managed manifest `506b83895defd89dc24944fa4f61b7ed2000fb5730a39e68c5d90ab6dbcd599c`.
The separate phase passes 26 synthetic cold metadata workers and six actual
native/provider workers, then the first ARM64 color-consumer Fill worker fails
at producer submission: `Native color producer lost its ORIGINAL preparation`.
The other 205 workers, publication and games remain unrun. ROOT independently
verifies all 244 failed-phase artifact hashes, unchanged source/binaries and
all 33 single-case TRX outcomes. All owned roots are closed; only the exact dead
owned lease is removed after closure. Failed manifest SHA-256:
`9b4028d24cf511d4642c0765be1ac9dd18fe52014587cc948ead04a8a85399ed`.
Neither failed source is retried or relabelled.

Source review establishes a direct factory mismatch: presenter attachment
resolves a tracked wrapper to its underlying GPU reader, but color projection
retains that wrapper in its original placement. The unchanged identity guard
therefore cannot admit that placement. This does not establish the sole winning
runtime predicate or a broader raw production bypass. A reviewed source
successor resolves explicit GPU views before acquisition at six factory/host
boundaries, preserving opaque decorators, different-root refusal and captured
receipt identity. It adds 24 ordinary controls and four separately attributed
fresh activation metadata rows per CLR; existing native fixtures/oracles remain
unchanged. Factory donor `88d1b27bbc1ed8c93618bd41da1ce03759685485` is frozen;
ROOT verifies its 2,033 source/archive hashes and exact inverse parent tree.

Reviewed source `9985abae01bbfc141d7593c93789c58ee3effe42` adds default-off,
bounded fault-thread import history including fast paths. It records no raw
argument-pack reads and preserves the original dispatch body and result. Its
15 ordinary controls are unrun. Frozen qualification union
`07698f965631dcee4772c8aa3ea3bd378725b952` includes these exact disjoint donors.
ROOT verifies all 2,037 worktree/archive source hashes, all sixteen donor paths,
and three inverse parent tree proofs. Expected ordinary inventory is 1,242 per
actual CLR (279 Host, 858 Libs, 105 GPU). Its 246 separate workers comprise the
unchanged 212 native/provider cases and 26 cold metadata cases, plus eight new
fresh activation metadata workers. No qualification is inherited. Source
manifest SHA-256:
`906565e717ea83c7604e0e2817dbe9431d4d6a24d4abb203f0db3b51564d880f`.
Review then identifies an integer-boundary defect in the new diagnostic ring:
the inclusive sequence increment can wrap after `long.MaxValue` and fail the
bounded snapshot traversal. No game occurrence or infinite hang is established.
Qualification is halted after the already launched workers: Host has an actual
rc0 receipt; Libs output has zero errors but its return code is unrecorded after
the authorized driver stop; GPU and all later phases are unrun. This partial
archive is not a test failure and supplies no successor qualification. ROOT
verifies all eighteen artifacts, 2,037 source hashes, 101 partial binary hashes
and exact seven-root closure; manifest SHA-256:
`932f84ff72166b59e514aefaff31df489f7c476499f27131ac08060003cbd26e`.

Corrected source `b5fe2c0b5b6db3d8795277fe35b82e366403f28d` changes only the
ring traversal and adds two metadata controls. It bounds iteration by retained
count, preserving the last valid sequence and history when the next Begin
overflows. ROOT verifies all 2,037 source/archive hashes and exact inverse
parent tree. Its fresh three builds and all 1,244 ordinary cases per actual
ARM64/x64 .NET 10.0.3 CLR pass, with zero failures or skips. ROOT independently
verifies all seventeen build and forty-eight managed artifacts, all six exact
TRX inventories and unchanged source/104 compiled hashes. Build manifest:
`8204faa9b8079d3cabb052748d24d9789e910029e5261d331da3af8e4d89d046`;
managed manifest:
`896679cd74f92de80dc9ab6d1cbe2cd0b362afafd21b6fb337d8f4068553196a`.
Its separate phase passes all eight new activation metadata and twenty-six cold
metadata workers, then the first ARM64 native cold case fails before any positive
native byte/status-4/EOP result. It refuses `mapped-copy-refused-or-missing-original`
while materializing a volume original. The other 211 workers, Fill, publication
and games remain unrun. ROOT independently verifies all 258 artifacts, unchanged
source/binaries, all 35 exact single-case outcomes and fresh closure of 37 owned
roots. Failed manifest SHA-256:
`a1c5204f55757e3b7a4ef469a144b51d810c10339399f4ffd24345fe64874263`.
The absent-versus-zero R32 flag is equivalent under its exact `== "1"` initializer;
the flag difference does not establish causality.

Source inspection finds the common native fixture directly installs tracked
memory into the presenter's private field, bypassing production attachment. The
new factory selects its underlying GPU reader; the unchanged enrollment identity
guard therefore refuses. A separate fixture correction will use the production
attachment API and retain all native oracles and cleanup. No guard is relaxed,
failed source retried, or game result claimed. Local main still retains qualified
`1893dc25` production bytes.

Fixture-only successor `d85fbaa639a581284485f882ad5b990e81278d03` uses
production attachment and adds one ordinary identity control, retaining all
native case identities, byte/status-4/once/EOP oracles and cleanup. ROOT verifies
all 2,039 worktree/tar source hashes, exact inverse parent tree, unchanged
production/scripts and all twenty-three prepared artifacts. Expected inventory
is 1,245 ordinary cases per actual CLR (279 Host / 861 Libs / 105 GPU), with the
same 246 separate workers. All three fresh builds and all 1,245 ordinary cases per actual ARM64/x64
.NET 10.0.3 CLR pass, with zero failures or skips. ROOT independently verifies
all seventeen build and forty-eight managed artifacts, the six exact TRX
inventories, unchanged 2,039 source/104 compiled hashes and fresh owned closure.
Build manifest `e49e71e295e5d2e7fc7a17bcf777b4527ef50e0f7f07bb2cc8be58d09d579fa6`;
managed manifest `d6b6ee5606e82c5e3157bb4e39e7c15b1485bc4366b43ddd7bad47b1eb1063fc`.
Separate qualification is underway; no native/publication/game result is yet
qualified or inherited. Source archive manifest:
`480b1186d75cdf7c835840474772107223c493edafea06fe1c780e009c568b7f`;
prepared manifest:
`6faf49d9813e9d72b152180f148f9d0c68a0080e622bc77509e5f03a835bb14c`.

The d85 separate phase stops at the mixed buffer/image prebuffer case after
131 passes: eight activation metadata, twenty-six cold metadata and ninety-seven
actual native/provider cases. The first failed case is
`ActualStatus4PrebufferWaitCommitsMixedBufferAndImageOnceAfterOriginalWriter`;
its buffer at `0x71F6000000` has no unchanged original complete
mapping/session/input proof. Two volume subobjects already emit status-4 and
full-byte results, but the mixed case fails; no mixed publication/EOP success is
claimed. The other 114 workers, publication and games remain unrun. ROOT
independently checks all 937 artifact hashes, 2,039 source/104 compiled hashes,
132 exact single-case outcomes and all 131 passing full oracles, plus fresh
closure of 134 owned roots. Failed manifest:
`dc722c5dd01f7bbe1658584cce899dd2af2ad8edf2442286811331974f983aa9`.

The common fixture's manual `CaptureBuffer` still supplies the raw CPU tracker
for both the initial mapped copy and original registration. Actual host
preparation supplies its already selected GPU reader. The saved buffer origin
therefore differs from the attached presenter identity. A fixture-only successor
will use the host's selected memory for both initial operations and assert the
saved identity before queueing; CPU writers remain tracked. No saved receipt is
normalized, original recaptured, production guard changed or failed source
retried. The remaining manual capture paths are being audited.

Frozen fixture successor `3bd17de5e3acf1de6112a0d64b34c29056c1e0d5`
changes that first buffer capture and registration to the host-selected reader
and adds one managed ledger identity regression. ROOT verifies all 2,041
worktree/tar source hashes, four archive artifacts, inverse full d85 tree,
unchanged production/scripts, twenty-three prepared artifacts, seven inherited
inventories and all six minimal driver retargets. All 1,245 old ordinary rows
remain; expected ordinary inventory is 1,246 per CLR (279 / 862 / 105), with the
same 246 separate workers. All three fresh builds pass with actual exit code 0.
ROOT independently verifies seventeen build artifacts, 2,041 unchanged source
hashes, 104 compiled hashes and fresh closure of all six owned roots. The Libs
build's residual helper is closed after its successful return; no timeout or
remaining worker is observed. Build manifest:
`1b0bbab73bcd1c9758a7574ce36ad6db666ab2d13517c52b0c0d03a6c9b45ab2`.
All 1,246 ordinary cases pass on each actual ARM64/x64 .NET 10.0.3 runtime,
with zero failures or skips. ROOT independently verifies forty-eight managed
artifacts, all six exact case inventories, unchanged source/compiled hashes and
fresh closure of fourteen owned roots. Managed manifest:
`72cde28def460c16f6456bc32d312a9451a9cf3acaae2e3c73c4b59ad2546a28`.
Separate native qualification stops at the first Busy-retry fixture assertion;
publication and games remain unrun.
Both actual CLR workers now pass the formerly failing mixed buffer/image case
with its full original status-4, writer, once-only publication and completion
oracle unchanged. This resolves that earlier fixture failure point, but does
not qualify the later failed Busy-retry case or remaining workers.
Source manifest:
`fa6f9ebd2311d8675815fac2065394341ce02502ff0398b13f6bf7189ff10c63`;
prepared manifest:
`1f4c89ca658c419c68940fe631caec35178b4f1088de080fce87a65f53dccf82`.
No successor qualification, publication or game result is inherited.

The 3bd separate phase retains 176 passing workers (eight activation metadata,
twenty-six cold metadata and 142 native/provider), one failure and 69 unrun.
`ActualNativeBusyPostReleaseRestoresDirtyAndBlocksFrameAndFutureEopUntilOriginalRetry`
fails `Assert.Same` in `CheckOriginalVolumes`: expected `TrackedCpuMemory`, actual
`PhysicalVirtualMemory`, the correctly selected GPU reader. Earlier status-4 and
canonical completion rows do not make this failed case pass. ROOT verifies all
1,256 artifacts, 2,041 source/104 compiled hashes, 177 exact outcomes and all
176 passing full oracles, then confirms all 179 owned roots absent and the
shared lease closed. Failed manifest:
`ac081f4e4261ff8b81d0529aa8d0265bef03206523e97d5399f03b067c8a4567`.
The failed source remains frozen. A complete remaining fixture identity audit
precedes its successor; production guards and native byte checks remain intact.

That audit covers all 119 listed case identities across seventeen fixture
partials. It identifies exactly two contradictory reader checks: the failed
Busy assertion and a failure-path cleanup comparison against the CPU tracker.
Fixture-only source `e2535177` freezes the host-selected reader and each exact
original volume before scenario effects; later checks use those saved identities.
Cleanup matches the retained original Memory and Source alongside the existing
Layout/Session checks. Faithful tracker metadata delegates and opaque wrappers
remain unchanged. ROOT verifies all 2,042 worktree/tar hashes, eleven linked
source artifacts, inverse full 3bd tree and unchanged production/scripts.
All 1,246 ordinary and 246 separate identities are preserved; the six Busy
workers run immediately after activation metadata. ROOT verifies all
twenty-three preparation artifacts, seven inherited inventories, six minimal
driver retargets and the approved exact worker reorder. All three builds pass,
with seventeen retained artifacts, 2,042 unchanged source hashes and 104 compiled
hashes independently verified. Build manifest:
`c7d7088d181cc555066571fdd5ef1530d2c01a211bf4449b31b370f5c03ce675`.
All 1,246 ordinary cases pass on each actual ARM64/x64 .NET 10.0.3 CLR with
zero failures/skips. ROOT verifies forty-eight managed artifacts, all six exact
inventories, stable compiled/source hashes and closure of fourteen owned roots.
Managed manifest:
`4f2d1041e922db7486d4183a291555951be0a15646fb2249733b942752be9f69`.
All 246 fresh separate workers pass: eight activation metadata, twenty-six cold
metadata and 212 native/provider workers. ROOT independently verifies all 1,740
artifact hashes, 2,042 source hashes, 104 stable compiled hashes, exact case order,
actual CLR/framework, complete per-worker oracles and fresh absence of all 248
owned process roots. No actual heavy worker or shared lease remains. Native
manifest: `0d46f5db5b7336cf5201e1005e98669d70de0cf02cdc604af1ddab88bdd427f2`.
The signed publication passes: ROOT verifies thirteen artifacts, all 239 native-only
payload files, exact clean-source build metadata, strict signing, x86_64 executable,
104 stable compiled files and fresh closure of all three owned roots. Publication
manifest: `9ed2e5b9120b59b22c65fbdc9bc055a614b17f1de6c28cb10882586b8f551c95`.
The first DS replay is granted with root pins to all four actual completed
qualification/publication manifests; other title grants await its closure.
Source manifest:
`29fa86bbeb5b25d0fba1523e380fec478b820d37d2aa8ef566898c48da2cafff`.
Preparation manifest:
`3cee5a42d7e117cd67df372027dbff333815007d6a0e7713e45465c8369fadfe`.

The DS harness returns exit 2 after 3.138 seconds: the expected `eboot.bin` is
missing. No emulator process, game capture, input or run directory is created.
This is a pre-launch environment failure, not a boot/render regression. All nine
title mappings share the absent game root, so no other title is attempted. The
user is asked to reconnect the disk or supply its new path. ROOT independently
verifies the 279-artifact pre-launch failure archive, unchanged qualified app,
source, binaries and four completed manifests, and closure of all three owned
roots. Failed manifest:
`c25ec36fcbe8e11c0587d2007ec6addd598e8d8ed1fc94f451e2d65e93fbf680`.
Failed receipts stay
immutable; any future launch needs restored game files and a fresh evidence target.

The external Quake one-action helper passes ten isolated Python controls,
including actual atomic key writes and a pending stop signal during neutral
release. Ownership/lease/time samples in those controls are explicitly synthetic;
no game, CLR or native worker is created. ROOT verifies all 110 evidence hashes
and fresh closure of twelve owned roots. Regression manifest:
`5dd09cc4db37ea27779c8b7159cbd28dd2902655987b13b90296b56f615e8e5e`.
Actual use requires a separate current owned-capture action grant and qualified
payload. No actual game input has yet used this helper; guest pad writes still
cannot establish game logic consumption or gameplay.

Poppy's source audit still finds
uncovered executable-allocation ownership, protection changes and late code
writes; no safe boot behavior change or native fault authority is established.
Separate source `8d7ceaa5`, off the unqualified Host/PVM foundation `b75d45d0`,
retains exact original allocation metadata once at image load and copies bounded
historical image metadata into local options at each dispatch boundary. It does
not refresh old cookies or claim current permission/lifetime ownership. ROOT
reviews all six Core paths and twenty-four added ordinary source rows, then
verifies 2,025 worktree/tar hashes and the exact inverse parent tree. No compile,
test, native module-install, signal recovery or game execution is qualified.
Source archive manifest:
`ff9686229827a145e5d4ebe8f2cbaf5739e9025f26f2cc3a35c773a5e3b7649f`.

Frozen Poppy foundation union `d69faf76` combines that allocation/transport
source with the exact `e2535177` boot parent. ROOT verifies all 2,051 worktree/tar
hashes, fifteen unchanged donor blobs, both PhysicalVirtualMemory hunk inverses,
unchanged boot paths and the inverse full parent tree. Its source manifest is
`f6daf4c2b6daaf4bc6e1bbf275842db3f04fa9d3d93a721f28fe98ea5e08f212`.
Phase-one drivers are prepared only: three builds, 53 focused controls per CLR
(52 new plus an inherited actual architecture/framework proof in that same
testhost), then all 1,298 ordinary cases per CLR. ROOT verifies fifteen linked
preparation hashes, exact reused helper ASTs and preservation of the old ordinary
inventories. Preparation manifest:
`6e2595ef8b87d8d4e8eec55b1f15833c65e23bc678497295938f20474799c85f`.
All three foundation builds now pass. ROOT independently verifies seventeen
actual build artifacts, 2,051 clean source hashes, 104 compiled hashes and fresh
closure of six owned process roots. Build manifest:
`f4549ebbaca48f1f09887d1a3c8384aef0bb585e33e882278ba2e57ee9d556fb`.
All 53 focused controls pass on each actual ARM64/x64 .NET 10.0.3 CLR, including
the architecture/framework fact in the same testhost. ROOT verifies twenty-three
focused artifacts, both exact inventories, all counters, unchanged compiled/source
hashes and fresh closure of six owned roots. Focused manifest:
`3bd7ae119b850c9df7c8a22e161f9a8f8d4d10f90d85a82a72550b0af13d393a`.
All 1,298 ordinary cases pass on each actual CLR (279 Host, 914 Libs, 105 GPU),
2,596 passed with zero failures/skips or other outcomes. ROOT independently
verifies forty-eight actual artifacts, all six exact inventories/counters/runtime
markers, 2,051 clean source hashes, 104 stable compiled hashes and fresh closure
of fourteen owned roots. Managed manifest:
`0640b76ad88e6e1edd1acb137e53d45679d2162383a1a01e3975179a0b254dde`.
Phase-one validation is complete. At 22:41:57 UTC the lane returns to Kairo for
one bounded build/test batch; SharpEmu remains source/light pending corpus return.
No native/provider/publication/game driver is added, and the
separate eight actual-provider scenarios remain design-only. This source does
not activate Poppy fault/store/trace recovery.

The separate offline NGG scalar source `53eaad89` adds strict mathematical
effects for five captured scalar opcodes, with forty-eight source controls.
ROOT verifies all 1,809 worktree/tar hashes, original primary references and the
exact inverse parent tree. Its static comparison covers seventy of the 707
captured instruction sites; it does not execute the C# binder, compile MSL or
qualify a native shader. All eight launch obligations remain missing and
`CanExecute` stays false. Source archive manifest:
`3d8ac731c959aa4ab34f19872495db6ea7f9c5c77f56239d55c1432f7dc06507`.
Its separate child `2a8810b5` adds comparison and conditional-select scalar
effects plus fifty-two unrun ordinary rows. ROOT reviews the complete source,
checks the original AMD definitions and verifies all 1,812 worktree/tar hashes,
retained primary references and inverse parent tree. Combined static coverage
is 104 of 707 captured sites, with 603 outside these models; no runtime shader
acceptance changes. Source archive manifest:
`ee1c3f61d4c1ef4da24863c8f55866fd801b61906cf49594a8cdbdef8fe88fa1`.
Source-only child `456d2e18` models nine captured unsigned VCMPX sites with
explicit supplied 32-lane inputs and old-EXEC intersection. ROOT reads the
generation-specific primary definitions, reviews fifty-nine unrun controls,
and verifies 1,815 source/tar hashes, fifteen retained primary-reference files
and the inverse parent tree. Static model coverage is 113/707; the other 590
sites and actual guest lane inputs remain unproved. No runtime route or MSL
acceptance is changed. Source manifest:
`dc26dc59f91262966175ff6b4cc3163c6743b4285b8415c0f00acbe94837f054`.
Frozen child `6f5d16d3` adds reusable bounded owned-instruction effect plans and
MSL fragments, plus a strict supplied-wave SIMD32 fixture source. ROOT verifies
all 1,821 worktree/tar hashes, unchanged 1,815 parent files, twenty-one primary
references, full inverse parent tree and the six reviewed new paths. Its 26
ordinary and eight native rows are unexecuted. Full per-lane/per-step bank/cohort
oracles, one native attempt and unknown-owner quarantine are source requirements,
not execution results; an external worker cap remains mandatory. Runtime shader
gates stay unchanged, all eight obligations remain missing and `CanExecute=false`.
Source manifest: `4264f0eaf7d2b0249a738669ec28a32f9426faf35abdc166d178db06eebe6feb`.


The following predecessor history preserves failures and their original lane
state; it is not the current execution queue.

Combined source `bf7b3516` includes the reviewed Fill, pending-only canonical,
first-installation diagnostic and cold first-capture enrollment candidates.
Its first build passes Host but fails Libs with `CS0136`: a new test catch
variable collides with an enclosing pattern variable. GPU build and every
test, publication and game phase remain unrun. ROOT verifies the immutable
13-artifact failure archive, all 2,016 unchanged source hashes and closure of
owned roots `34305`/`34309`/`34345`/`34473`; manifest SHA-256
`4e71f9698cd38e1ffc008efbd61a779f998faad9b642ef7980e9ee0177499f0d`.

Successor `a27c011f` renames only that catch variable. Production code and test
cleanup behavior are unchanged. ROOT verifies all 2,016 source hashes and all
16 preparation artifacts, byte-identical inventories and the minimal driver
substitutions. Preparation manifest SHA-256:
`3b6eaaac59a69ba44467f70af5bd8e09ad209d837a6a35841203f24d69cd7fa4`.
Expected ordinary cases per actual CLR are 238 Host, 793 Libs and 105 GPU;
106 native/provider cases and 13 explicitly synthetic cold frontend cases
run separately per CLR. All three fresh builds then pass. ROOT verifies 15
build artifacts, 2,016 source hashes and 104 compiled hashes; manifest SHA-256
`384f868b314ad64b4b8f66d8f8547b483e6e4ef59023256981ec9df13734c07c`.

The ARM64 ordinary phase passes all 238 Host cases, then Libs passes 786 of 793
with seven failures and no skips. All seven new canonical work-refusal controls
begin with a CPU-seeded fixture lacking `GpuWritten`; the correct production
owner guard therefore refuses before the injected work guard. ROOT verifies
21 immutable failure artifacts, unchanged source/binaries and exact owned-root
closure; manifest SHA-256
`584ab7cdcf19e4cf066dbada2659d1a0797ffba72e9f47746789751a6c800850`.
GPU ARM64, all x64, all separate workers, publication and games remain unrun.
Successor `62de4f79` changes only those scalar controls to save/set/restore
`GpuWritten`, retaining all eight case identities and every refusal assertion.
It changes no production code or authority. Its fresh three builds and all
1,136 ordinary cases per actual ARM64/x64 CLR pass with zero skips; ROOT verifies
46 managed artifacts, exact case identities and unchanged source/compiled files.
Managed manifest SHA-256:
`52b931d50559d27532339e491ba15107981ecea89051a678ea8cf847ea3a1bd3`.

The separate phase passes all 26 synthetic cold frontend controls and the
first two real ARM64 cold Metal/provider cases, including the complete byte,
status-4 and EOP oracles. The third ordinary-writer control fails because its
fixture hook remains installed and intercepts the later real EOP Write32.
The remaining 209 workers, publication and games remain unrun. ROOT verifies
all 216 failure artifacts, 29 exact attempted cases, both native positive
oracles and exact closure of 30 roots. Failed manifest SHA-256:
`149b5b993a69b2430e1ab6dc1682789db0e723d9b5ca7e3fef9b6671994a3007`.
Only its verified dead owned lease is removed after closure.

Peer-reviewed successor `1893dc25` owns the writer hook before its first
mutation and restores it once when the controlled cold copy ends, before
later native publication/EOP writes. Partial installation, primary errors and
unknown ownership retain the existing conservative cleanup behavior. Only
the native test partial changes; production and all case identities remain
unchanged. ROOT verifies 16 preparation artifacts and 2,016 source hashes;
manifest SHA-256:
`9d6176cd23fb23bf0f5aa5b0d25b9826a490fae6d69176671798a821558306a6`.
Fresh qualification passes all three builds and 1,136 ordinary cases per actual ARM64/x64 .NET 10.0.3 CLR, with zero failures or skips. All 238 separate fresh workers pass: 26 synthetic cold frontend controls and 212 native/provider cases. ROOT independently verifies every exact case and all byte/status-4/once/EOP oracles, unchanged source and compiled hashes, and exact process/lease closure. Native manifest SHA-256: `908dff39a52f0ee80d4d46422963c5fbf765a104a311484422dc148342a1c03e`.

Publication passes with a signed native-only 239-file osx-x64 app. ROOT independently verifies the 2,016 source hashes, 104 compiled hashes, every app and publication artifact, strict deep signature and x86_64 executable. Published manifest SHA-256: `37ec523d9c69df98224ec840f5e916aa96da298ca570e3d23709bc588e62f211`. The local integration changes no tested `src`, `tests` or `scripts` bytes. Actual Oregon, Superliminal, Silent Hill, Demon’s Souls and longer interactive Void replays follow separately; qualification is not game-entry evidence. The historical checkpoints below retain their original source and lane state.

After the four `8c4b352f` SharpEmu attempts closed, Kairo owned one bounded v4.554 validation and wall-clock Silent Hill replay batch. SharpEmu's heavy work was held while source implementation and review continued. The then-prepared SharpEmu candidate was DS+Void `bfc988ed`: ROOT verifies its clean exact HEAD, all 2,001 source hashes and all 12 prepared driver artifacts. Its 1,064 ordinary cases per CLR comprise 208 Host, 751 Libs and 105 GPU cases. Its 178 separate workers comprise 73 Metal/GPU and 16 ordinary Mac-provider cases per CLR. All original `8c4b352f` case identities remain present. Preparation manifest: `artifacts/validation/all-games-20261009/void-input-on-watched-ds-boot-bfc988ed-prepared/prepared-manifest.json`, SHA-256 `67950a64f5d2abcd1d97cebf615f4185fbd7b072608cfb91d092763c7d9ba2af`. At that preparation checkpoint, no build, test, publication or game result existed for BFC. Its first build failure is retained below.

Kairo's bounded v4.554 batch subsequently closes through its owned AX window; the user hands the lane back and ROOT independently verifies runner `69115` and game `69119` absent by PID/PPID/process group, no actual game/compiler/test workers and no shared lock. Kairo stays source/light only during SharpEmu's next batch. Kairo's calibration image correctness remains unverified: two origami images are expected by the prompt, so their presence is not a confirmed duplication defect.

The first BFC build then passes Host but fails Libs with `CS0246` because `MetalStoragePhysicalPlacement.cs` was omitted from the selective DS port. GPU build and all tests/publication/games remain unrun. ROOT verifies all 26 failed-checkpoint artifacts, 2,001 source hashes, 58 partial compiled hashes and closure of roots `74835`/`74904`/`74968`. Failed manifest SHA-256: `e74bf7c95e11735513c6267e6a9dac8bc089903a95cd996cb9f4bad58fb378c7`. The earlier source review wrongly assumed this dependency was inherited; mechanical donor equality did not establish dependency closure.

New source `5016bc82` adds only the exact reviewed `6c33e428` helper and a truthful dependency note. ROOT verifies the donor bytes, all 2,003 source hashes, clean exact HEAD and unchanged inheritance of every BFC blob. Source manifest SHA-256: `a6fa41b3d6031a31b5db0c4a451d58dc3045f4cbd6089037aff1bb697b4d3edf`. Its ordinary and native inventories remain unchanged. ROOT reviews the new source/WT/tag-only driver changes and verifies all 12 prepared hashes and byte-identical inventories before granting its first fresh three-build phase. No BFC failure is overwritten or rerun on the same source.

The corrected `5016bc82` source passes all three builds and all six managed suites on actual ARM64/x64 .NET 10.0.3: 208 Host, 751 Libs and 105 GPU cases per CLR, with no skips or failures. ROOT independently verifies all 46 managed artifacts, 2,003 source hashes, 104 compiled hashes, exact TRX counters and absence of twelve owned process roots. Managed manifest SHA-256: `1fb9b2d30a84c5565dbab926615cbf2846e1e889041213d63ae62584ee015c37`. All 178 separate native/provider workers also pass: 89 exact single-case workers per actual CLR. ROOT independently verifies all 1,257 native artifacts, source/compiled stability, every actual full-byte/status-4/once/EOP oracle and absence of all 179 owned roots. Native manifest SHA-256: `d2340fc3f68ee03429098a593a68a5f3859646214c4c4b6b09177394cd785af1`. Publication passes with a source-exact, deeply verified signed native-only 239-file osx-x64 app; ROOT verifies all ten publication artifacts, payload hashes, source/compiled stability and owned process closure. Published manifest SHA-256: `eb043f93d4e234ca44c515049ba21c4dc7f0e544b4f1dff4ec84c63958cdef06`. Actual replays are recorded separately below.

The publication-admission audit identifies four actual slot owner families: watch enrollment, cancel-only original-read validation, cancel-only publication validation and real publication. The slot has no owner identity or completion promise. Lost cancellation bookkeeping can retain it without setting coverage-incomplete, so its presence cannot be labelled temporary or treated as Current. ROOT verifies the audit's source and artifact hashes; manifest SHA-256 `48c97f39ca1d64cd2994bbe631715ba05e8b3c20828bc298cf1edc681e8246e4`. A separate opt-in cold-copy design must keep the first provider mapping and backing pin before any byte is copied, wait at most 500 ms outside inherited gates, then obtain strict currentness and perform one copy. Retained unknown ownership must refuse or time out without bytes, forced release or a new observation. Existing-watch handling is separate; the first Octopath Stale result remains unchanged by this design.

Superliminal's bounded non-GDS Fill successor `aadd7248` and Silent Hill's pending-only post-release continuation `a1db7b57` have passed source review; compilation and execution remain pending. Fill must retain the real mirror/cache route and refuse an existing or newly installed native mirror destination; its payload is limited to one aligned 64 KiB transfer chunk. Silent Hill must preserve the first Pending/Batch/Taken/Executing predicate, permit only pending bookkeeping to retain the same already-Applied dirty owner after all lower proofs are Current, and retry before future queued work. No actual winning Silent Hill subpredicate or new entry success is inferred from these proposals.

Inactive Poppy prototype `d632c098` adds a deterministic atomic per-thread claim before same-receipt native ticket publication. ROOT verifies all ten source hashes and reviews that specific nested-admission fix. The prototype remains uncompiled, uninstalled and source-only; native tracker authority stays zero. Actual private ELF allocation ownership, code sealing after patching, raw protection exclusion, cross-receipt ingress and the CoreCLR signal chain remain unresolved. No game recovery or safety closure follows from this source review.

## Actual boot replays on `1893dc25`

Each attempt uses the exact signed publication and actual four-manifest pins,
with fresh foreign-process, memory and shared-lease preflight. ROOT independently
verifies each retained archive and exact driver/harness/game closure. Earlier
failed attempts are unchanged. Void reproduces a menu; none adds gameplay acceptance.

| Title | Observed result | First blocker / reached stage |
| --- | --- | --- |
| Oregon / `20261009-220911` | Exit 134 after 36 seconds, peak 2,057 MiB; one observed guest frame, black capture. | Cold enrollment is passed in this attempt, then the existing singleton cohort watch at `0x80115F0000` refuses `original-read-watch`, Stale, first admission `4294967296`. The local validation ticket is false; its lower predicate and actual slot owner remain unknown. |
| Superliminal / `20261009-221110` | Exit 134 after 33 seconds, peak 3,195 MiB; black captures. | Command operation 375, submission 18 cannot prepare its ORIGINAL write. Exact first writer-reservation CAS rejects admission 2, before original-watch/history validation. Its operation kind/extent are not captured after the trace budget expires. This does not establish the old Fill blocker as fixed or unchanged. |
| Silent Hill / `20261009-221248` | Exit 139 after 36 seconds, peak 1,822 MiB; black capture. | Several ordinary-write wait receipts return `OriginalChanged`, followed by guest memory-fault returns and SIGSEGV at `0x8049A8381`, writing through `0x28`. The failed acquisition does not yet save its exact currentness predicate. Canonical acknowledgments occur; no canonical post-release or GPU fatal is recorded. Crash causality remains unproved. |
| Demon’s Souls / `20261009-221448` | Exit 134 after 58 seconds, peak 2,858 MiB; static launch artwork. | Compute `3E9247D2A2F42DBB` first installation rejects `guest-memory-replaced`, registry count 0, address `0x70FFF00000`. The later command-stream shutdown error is consequential. Source inspection finds per-thread tracking wrappers compared against the first worker’s wrapper. The explicit stable GPU-view candidate awaits qualification and replay. |
| Void Terrarium / `20261009-221717` | Alive at the 600-second cap, graceful stop in three seconds, peak 2,251 MiB. Readable publisher/title/menu captures. | New Game selected at 255–295 seconds. Recorded exact-owned-window Enter press/release spans 11.763 seconds; menu fades, then captures through the final frame are black. Guest consumption is not independently certified. No fatal appears in the 3,037-line log; no gameplay or FPS acceptance. |

Individual manifests in `boot-cold-hook-successor-1893dc25-game-entry-prepared`:
Oregon `c1a10d6ed094b1ddc4295c70dffa631e773ce3ad1c18a3d9f3e50c21b48ce250`,
Superliminal `d512791bdbfe55d90aaba34acd6773e6718c3dc164dddcc7a5f93076604586ab`,
Silent Hill `621e7f103881252a6da32f7f1618a941eb0d4fafe2ba3048ed8ed35a01131d08`,
DS `ab0c2a6d00380cc67fe3a69531825c7b04fbb8afcfbea8ee754351250571d5c0`.
Void `049500bd08838883e6ff221f45e65c1f9595ac30e531bc47e15fddf87521d193`.

Void's rejected-input archive contains 20 files for two merged-stage exports:
code sizes 4,352 and 4,864 bytes, stages `0x00402020`, eight draw records and
raw one-point indexed payloads. The first rejection precedes host confirm; the
second appears afterward. Neither the CPU register/index facts nor this order
prove packed geometry inputs, LDS semantics, output correctness, or the sole
cause of the black transition. The current vertex MSL path does not implement
this merged geometry execution; merely accepting the stage mask is insufficient.

ROOT verifies all 686 artifacts in the frozen five-title final manifest,
SHA-256 `4fe65931e88de5bfb6d5de22935fbfc2b08341520f8b48ffd049cd5cf567c5d7`,
and its root closure supplement,
`cdd6749c9c61d5207c3f043128899a63d74460afffefc464894f100e401b8c07`.
All 20 owned driver/harness/game/input-helper roots are terminal at the recorded
handoff, no shared lease remains then, and source/binaries/signed payload are
unchanged. This historical closure does not describe Kairo's later active lane.
Accepted directional gameplay remains **1/9**, on the historical Sarah payload.

## Actual DS/Void replays on `5016bc82`

The source-exact replay companion links 560 read-only actual qualification and
publication records. ROOT verifies those targets and all four prerequisite
manifest hashes before launching each title. Neither replay overlaps a foreign
game or is censored by the monitor.

| Title | Observed result | Reached stage |
| --- | --- | --- |
| Demon’s Souls / `20261009-211122` | Exit 134 after 51 seconds, peak RSS 2,905 MiB. First command-stream error at lines 633–634 is Compute `3E9247D2A2F42DBB`, ordinary storage original installation at `0x715FF00000`; both read/storage-write bindings describe a 64×64 float 2D image. The combined guard does not establish capacity, replacement or another winning predicate. | Static launch artwork, zero observed guest frames; no menu or gameplay. ROOT views `shot-0045.png`. |
| Void Terrarium / `20261009-211336` | Alive for the 150-second cap, graceful stop in two seconds, peak RSS 1,545 MiB. No fatal or rejected draw appears; the enabled isolated rejected-input capture directory remains empty because the diagnostic path is not reached. | Publisher logo at 115–145 seconds, then black transition; no title/menu/control. ROOT views `shot-0145.png` and `shot-final.png`. |

ROOT verifies the DS 37-artifact manifest, SHA-256
`9eaa583f46f2145688abbe43a4cd9a19ed497e635dde0ed75f5bbe36c83586ba`,
and Void 81-artifact manifest, SHA-256
`9411c1e416f7e105bdc66c99f83e30a502f29fa576f3919fa65bc676f4bad7ae`.
Both retain all 2,003 source, 104 compiled and 239 signed-payload hashes and
unchanged prerequisites. DS driver/harness/game roots `13692`/`13699`/`13716`
and Void driver/harness roots `16751`/`16758` are absent by PID, PPID and process
group. The existing monitor did not retain Void’s individual game PID; closure
is recorded using its owned harness group and fresh absence of actual SharpEmu
workers. All actual emulator/compiler/test workers and the shared lock are
absent before the lane returns to Kairo for one bounded v555 batch. SharpEmu
continues source/light work until the coordinated terminal handoff.

The next unqualified source union `a13a75e7` combines the exact reviewed Fill
and pending-only canonical donors over `5016bc82`, with 1,092 ordinary cases and
103 separate workers expected per CLR. ROOT verifies all 2,009 source hashes,
16 exact donor paths, unchanged base inheritance and full inverse tree. Child
`d792cdf4` preserves the DS installation guard order and records its first
winning predicate under the same lock; it changes no bound, authority or retry.
The separate cold first-capture enrollment path remains default off and
uncompiled; its provider, frontend and actual native controls are source work.
Accepted directional gameplay remains 1/9, on the historical Sarah payload.

## Prior qualified boot candidate: 09bc1fce

Source `09bc1fce` passes three builds, 972 managed cases per actual ARM64/x64 CLR (199 Host, 693 Libs, 80 GPU), and 64 native cases per CLR. Its twelve typed native cases and both mixed buffer/image regressions pass first. ROOT independently verifies 1,981 source hashes, 104 compiled hashes, 979 native qualification artifacts, and absence of all 129 native process roots. Native manifest SHA-256: `3121b1510c26ac9dbf23e995e3d02bd1a20c821d1086e28e3a17435728646ad4`.

The source-exact native-only app contains 239 files and passes deep strict signature verification. ROOT independently verifies all 11 publication artifacts, all 239 payload hashes, and closure of the two publication roots. Publish manifest SHA-256: `56325aafa33b6c2994dfb057debf27fbd82509a1f3a35990fc74b6bd2ef94f38`. Superliminal, Oregon Trail, Octopath and Silent Hill replays are separate validation; subsystem tests and packaging do not establish their boot progression.

## Four prior boot replays on 09bc1fce

The source-exact `09bc1fce` app reaches these first failures, with no foreign overlap or monitor censor:

| Title / run prefix | First observed failure | Reached stage |
| --- | --- | --- |
| Superliminal / `20261009-111031` | Eight-byte `WRITE_DATA`, sequence 16, cannot reserve its first original. The captured writer revision advances by 2,022, exceeding the 512-entry completed-write history. No Applied event follows. This does not identify every intervening physical destination. | Exit 134 after 19 seconds; no capture. |
| Oregon Trail / `20261009-111224` | Guest APR command-buffer reset returns `80020101` at header `0x7060F50608`, then the guest aborts. The later deferred-canonical revalidation error follows abort and is not the entry cause. APR-specific diagnostic flags were absent, so the inner predicate remains unknown. | Colorful illustrated intro artwork at 25 seconds, independently viewed by ROOT; no menu or control. Exit 134 after 33 seconds. |
| Octopath / `20261009-111448` | Compute sequence 54, submission 3, rejects an original native owner/version in pre-encode validation. The generic guard combines registry identity, saved native fields and volume-watch metadata; adjacent admission samples do not identify its winner. | Black capture; exit 134 after 36 seconds. |
| Silent Hill / `20261009-111922` | First command-stream slice error includes three watched-copy refusals at `0x80107F0000`: publication admission, then ordinary admission. Later constructor sequence 36 rejects the strict original-read-watch Boolean at `0x80107D0000`, with same-query `admission-nonzero` / primary 1 and classification disabled. This is not proof of a healthy Busy source. | No capture; exit 134 after 25 seconds. |

ROOT independently verifies all 239 new game artifact hashes and absence of all eight owned driver/harness roots. Final manifest: `artifacts/validation/all-games-20261009/boot-typed-original-diagnostic-09bc1fce-game-entry-commandtrace-prepared/final-game-manifest.json`, SHA-256 `fa50de42ac949e60f1c850a2e84151a7a6418f6eb6597828890fab4b77e042ae`. Source, compiled files, signed payload, prior qualification and 413 read-only prerequisite links remain unchanged. These four attempts add no verified menu or gameplay title.

After the replay batch closes and a fresh scan shows no game or heavy worker, the runtime lane is handed back to Kairo for a bounded validation/replay batch. SharpEmu continues source fixes and evidence review until the next coordinated window. Both tasks remain active.

## Qualified watched-write and constructor successor

Frozen source `8c4b352f` combines first watched command-write capture/lifetime (`6438de00`) and exact first-status constructor continuation (`66e63834`). ROOT independently verifies all 1,990 source hashes, all four exact donor blobs and unchanged inheritance outside the five integration paths. Source manifest SHA-256: `e01e0628a7613940a310f9ec8f104d4261c486a0fee2adc9ef43bfab2631666a`. Root, CPU, Metal and Package complete source review. Three builds and all 1,020 ordinary cases on each actual ARM64/x64 CLR pass, with no skipped cases. All 132 fresh native workers pass (66 per CLR). ROOT independently verifies the 12 build artifacts, 44 managed artifacts, 1,004 native artifacts, all 132 exact TRX identities, 1,990 source hashes, 104 compiled hashes and absence of all 133 native roots. Native manifest SHA-256: `0f39630cda6d0d0bc185bd3fdc58864c7e522829ebb7b3227daeb069708595cf`. Local main integrates this tested source at `299dcb4b`. The source-exact signed native-only app contains 239 files. ROOT independently verifies all 8 actual publication artifacts, every payload hash, closure of publication roots `49914`/`49918` and a fresh deep strict signature check. Publish manifest SHA-256: `fe6ccef515f4b72563490a224efc7139a8a991839118121eced586f5bb9351ae`. Four game replays are now separate entry validation.

The ordinary inventory is 1,020 cases per CLR (208 Host, 732 Libs, 80 GPU); the separate native inventory is 66 per CLR. The new native constructor controls complete an actual disjoint admitted writer after its first Busy refusal and before continuation. The existing typed-write positive now verifies the exact queued original watch before 2,022 actual disjoint physical writes exceed the 512-entry history. Full native, canonical byte, once-only write and EOP oracles remain required. These controlled sources do not establish a new game result. Silent Hill's separate refusal before an original watch is enrolled at `0x80107F0000` remains unresolved.

Kairo returns the runtime/build/test lane after its bounded v4.553 replay closes. ROOT independently confirms owned PIDs `28310` and `28307` absent, no game/compiler/test worker present and the shared lock absent. SharpEmu now owns the next qualification/replay batch; Kairo stays active for source and light analysis until coordinated return. Kairo's one-frame stalled replay and default step-clock limitation remain separate evidence.

## Four watched-write and constructor replays

The source-exact `8c4b352f` app closes four uncensored attempts, all exit 134. None establishes a new menu, player control or gameplay result.

| Title / run prefix | First observed refusal | Reached stage |
| --- | --- | --- |
| Superliminal / `20261009-202142` | Eight-byte `WRITE_DATA` sequence 16 now reaches Applied once. Later offscreen draw 2637 rejects pending `FILL` 2638, destination `0x70803CDC50`, four bytes, value zero, as an unknown accepted sibling writer. | Rendered title/loading corridor; ROOT independently views `shot-0055.png`. Exit at 79 seconds, peak 3,824 MiB. No menu/control. |
| Oregon Trail / `20261009-202345` | First 65,536-byte watched storage copy at `0x8011680000` refuses enrollment: publication admission, then ordinary admission 1/1. No original snapshot is returned; coverage/history loss remain zero. APR and ordinary diagnostics are enabled, with no preceding APR failure in this run. | No capture. Exit at 35 seconds, peak 1,727 MiB. |
| Octopath / `20261009-202521` | Canonical dirty validation at `0x8030BE0000` rejects its first exact original watch as Stale: publication admission `4294967296`, classification `publication-admission`. This is not ordinary Busy. Earlier ordinary-write rows do not prove its cause. | One capture, no accepted control/menu. Exit at 37 seconds, peak 2,508 MiB. |
| Silent Hill / `20261009-202648` | Canonical dirty post-release validation at `0x8002AE0000` refuses `pending-work-or-batch`. Original/current protected token 18, generation zero, match; the merged guard does not identify its winning pending/batch subpredicate. Earlier ordinary-write failure is separate evidence. | No capture. Exit at 32 seconds, peak 1,476 MiB. |

ROOT independently verifies all 272 new game artifact hashes and absence of all eight driver/harness roots. Final game manifest SHA-256: `adb19f1cf1fe31ee92a285d4dd2308b91c62c49592b3f904615a934bfad8a3b9`. Source 1,990, compiled 104, payload 239, native qualification 1,004, publication 8 and 425 read-only prerequisite links remain unchanged. Five unrelated old `semu-launch` wrappers have zero CPU activity and are left untouched. No actual game/compiler/test worker or shared lock remains.

The runtime lane returns to Kairo for one bounded validation/replay batch. SharpEmu stays active for Fill ownership, pending-only canonical continuation and publication-admission source work. Separately reviewed DS source `aca828f2` and DS+Void source `bfc988ed` preserve all boot-base changes but remain unbuilt/unrun; their inventories are 1,045/1,064 ordinary cases and 89 native cases per CLR. No new Poppy recovery handler is activated.

## Earlier qualification and three game replays on 1e17bc8d

Source `1e17bc8d` passes three builds, 912 managed cases per actual ARM64/x64 CLR and 58 native cases per CLR. Both mixed buffer/image cases pass first, followed by the remaining native inventory. ROOT independently verifies the native checkpoint's 830 artifact hashes and worker closure. The source-exact native-only signed package contains 239 files; ROOT verifies its 15 publication artifacts, all 239 payload hashes and deep strict signature. Native manifest SHA-256: `5d1798217e3c6b63f1efc3faa7febe6e4e1952e909b7ee3baceba8efd5ae7bb3`; publish manifest SHA-256: `e84d636ff0132f531a2bf9aeaead02970ba86c420bb6df93d791ade3306df7a3`.

Three fresh attempts on that package fail before menu/gameplay:

| Title / run prefix | Observed first failure | Result |
| --- | --- | --- |
| Oregon / `20261009-095207` | Canonical dirty acknowledgment refuses another image's original-volume watch or version. The exact winning subtype is not captured. | Exit 134 after 31 seconds; black client capture. |
| Octopath / `20261009-095323` | Compute sequence 54, graphics submission 3; strict original-read-watch refusal at `0x8030BA0000` during post-Applied completion. Its same-query fields were not captured. | Exit 134 after 38 seconds; black client capture. |
| Silent Hill / `20261009-095420` | Same canonical dirty-owner refusal class as Oregon. Earlier ordinary-write failures are separate observations, not proof of this winning guard. | Exit 134 after 28 seconds; black client capture. |

ROOT independently verifies all 208 new game artifacts and absence of all six owned driver/harness process roots; the shared lock is absent. No monitor censor or retry occurred. Archive: `artifacts/validation/all-games-20261009/finite-buffer-continuation-1e17bc8d-game-entry-41f54-prepared/final-game-manifest.json`, SHA-256 `5f2385a5e3bef9cafd647eea725391d8ee89379fc47463ef5ec8a3b4f8872919`. These attempts do not establish additional accepted titles, FPS or loading speed.

On the user's request, the separate Kairo session now holds new game launches and heavy checks until coordinated handoff from this thread. Both tasks stay active: Kairo continues source edits and lightweight evidence analysis, while SharpEmu owns the runtime replay window. Kairo acknowledges the hold and verifies no game or heavy worker remains. Earlier overlapping archives retain their original provenance and limitations.

Exact qualification passed three builds, all 838 managed cases on each actual ARM64 and x64 CLR, and 43 fresh native workers per CLR with no skipped cases. The native inventory contains 38 Metal and five ordinary Mac-provider workers. The 104 compiled files remained stable; the signed native-only macOS payload contains 239 unchanged files and passes deep strict signature verification. ROOT independently verified all 506 qualification artifacts against the immutable manifest.

Evidence: `artifacts/validation/all-games-20261009/game-entry-readback-color-188b9c28-prepared/qualification-manifest.json`, SHA-256 `629ea214dce4cc64a223c91bc983d589543a154795ef4fa5a2d4e13736415033`. That manifest closes the prerequisite workers before games; it is not an all-games acceptance report. This archive’s qualified executable source is exactly `188b9c28`; later diagnostic qualification does not relabel these game runs.

## Typed command-write successor: first native fixture failure

Combined source `28d0b8cc` passes all three builds and 970 managed cases on each actual ARM64/x64 CLR (199 Host, 691 Libs, 80 GPU). The first ARM64 native typed-write worker fails before applying the write: its fixture invokes `DrainGuestWorkCore` directly and bypasses the production `TryDrainGuestWork` drain-depth scope. The exact taken/executing receipt guard therefore refuses execution. No production check is relaxed; a separate fixture correction will use the actual production wrapper. The other 127 native workers, publication and game replays remain unrun.

ROOT independently verifies 1,980 source hashes, 104 compiled hashes, all 44 managed artifacts and 86 failed-native artifacts, the six exact passing managed TRX counters, and absence of all twelve managed and two native worker roots. Only the exact dead singleton lock owner `46283` is removed after a fresh process check. The immutable failure remains at `artifacts/validation/all-games-20261009/boot-typed-original-diagnostic-28d0b8cc-prepared/failed-native-manifest.json`, SHA-256 `f55f2efa423d18b840d28e2c02b1ae98c9190015f8dffdfae296c50645ef2b0b`; verification and lock release are recorded in a separate sidecar. This is fixture localization, not an additional game-entry result.

## Fresh entry failures on the qualified payload

Runs are under `.claude/worktrees/agent-package-ordered-queue-scope/artifacts/runs/`.

| Title / run | First observed failure | Reached level |
| --- | --- | --- |
| Octopath Traveler II / `20261009-053910-octo-game-entry-readback-color-188b9c28` | Compute sequence 57, graphics submission 3: singleton original-content validation in `WithFiniteStorageValidation` / `FinalizeFiniteStorageWrites`. Two producer completions and applied publications, one canonical completion. Adjacent admission diagnostics do not identify the exact failing predicate. | No accepted gameplay. |
| The Oregon Trail / `20261009-054029-oregon-game-entry-readback-color-188b9c28` | Singleton original-content validation during canonical dirty acknowledgment. Two canonical completions precede the fatal. Adjacent watch samples do not establish its exact origin or predicate. | No accepted gameplay. |
| Silent Hill: The Short Message / `20261009-054140-sh-game-entry-readback-color-188b9c28` | Guest `APR setBuffer failed 80020101`; the import writes the command-buffer header at `0x7060EBB648`, not the target buffer at `0x75A0E34000`. Earlier constructor header writes also return memory fault. The inner provider/permission cause is unresolved. | No accepted gameplay. Transient unrelated Kairo overlap censors timing; no causal attribution to Kairo. |
| Superliminal / `20261009-055748-superl-game-entry-readback-color-188b9c28` | Native color predecessor validation rejects an unknown accepted sibling writer at offscreen draw sequence 2469 / graphics submission 82. The refused accepted DTO is not yet identified by the generic exception. | Title/loading scene; no accepted gameplay. Exit 134 after 73 seconds, peak RSS 4,946 MiB. |
| Dreaming Sarah / `20261009-061658-sarah-game-entry-readback-color-188b9c28` | No entry failure observed. Captures at 65, 85 and 105 seconds show rightward movement/camera scrolling followed by leftward movement and facing direction. ROOT independently inspected all three. | Accepted directional gameplay on the current payload. Alive at 120 seconds, graceful stop, peak RSS 905 MiB. |
| Quake / `20261009-062027-quake-game-entry-readback-color-188b9c28` | Initialization marker observed at 52.852 seconds; all scripted holds follow it. The guest starts `demo1.dem`; sparse guest flips and corrupted late captures do not establish player control. The final command-stream memory-read fault occurs after host interruption during shutdown. | Warning screen and automatic demo; no accepted gameplay. Alive at 300 seconds, graceful stop, peak RSS 6,354 MiB. |

Superliminal's first launch was rejected before creating a game worker because another Kairo game was active; the observed replay above followed its natural exit. Sarah's first attempt was likewise rejected before any game worker; its successful replay followed natural foreign-process exit. A transient foreign process during Quake is recorded separately and censors timing; no agent signal was issued to it. Games run sequentially through the shared single-game harness. Foreign processes are left untouched.

The completed six-run archive is `artifacts/validation/all-games-20261009/game-entry-readback-color-188b9c28-prepared/final-manifest.json`, SHA-256 `1dcb103df9497be4a9ea545562deed38d6d815c99f2a0f11bd22957c8d371ae1`. ROOT independently verified all 648 listed artifact hashes and absence of its 108 owned PID/PPID/process-group roots. The closure records unchanged source, compiled files and signed payload, and no remaining game lock.

## Acceptance and remaining work

Dreaming Sarah remains the only accepted title out of nine, now freshly verified on `188b9c28`. Its sampled title-bar FPS values do not establish sustained 60 FPS. No other title is accepted, and no fast-loading claim follows from fixture results or loading frames.

Separate source work records the first singleton original refusal, first APR header-write failure and actual refused native-color queued operation without relaxing validation or retrying effects. Demon's Souls still needs x64 native prerequisite closure for its GPU-produced private R32 path. Poppy's ordinary protection-metadata controls and executor-cell foundation do not yet implement the real fault/store/trace recovery path. Void Terrarium requires captured real shader-launch inputs before its workgroup primitive-generation path can be admitted. Quake's next diagnostic observes the pad bytes already written to guest memory; longer holds can then distinguish missed polling windows from a game-loop stall. The anchored input-owner log alone does not prove guest consumption.

The diagnostic Demon's Souls x64 prerequisite at `c9756ec8` fails before GPU work: the first owned 1 MiB Mach allocation converts successfully despite a 4 KiB remainder within the measured 16 KiB ledger page; the second allocation, with a 12 KiB remainder, returns false. Both selected windows are contained. Alignment alone is not an established cause. A separate provider-first-return diagnostic will distinguish ledger refusal from actual `mmap` failure without retry or mapping expansion. This isolated correctness worker overlapped one explicitly recorded Kairo process; it supplies no game/performance result. ROOT independently verified its two owned process trees were absent and removed only their exact stale lock.

The subsequent provider-first-return source `4fc49b52` passes 787 managed cases per actual CLR, then its first x64 native worker identifies `internal-alias-overlap` before GPU producer work. The refused raw extent `0x1BC90B000 / 0x100000` rounds to `0x1BC908000 / 0x104000` and intersects a neighboring alias's rounded extent `0x1BC848000 / 0xC4000`. Two earlier unaligned conversions succeeded; this third rounded overlap is the observed refusal, with `native_called=False` and errno unclassified. The corrective fixture work requests an initially aligned owned Mach extent; it does not widen an existing allocation or relax the ledger. This is prerequisite evidence, not Demon's Souls entry. Its immutable failed manifest SHA-256 is `5910e64047b021aafda3dc585b55518dcb13b8795476c90fabc2735793ca077d`; ROOT independently verified all 17 owned roots absent and released only stale owner `20008`'s lock. Recorded unrelated Kairo overlap supplies no performance or causality claim.

The aligned owned-hole fixture `6c33e428` passes three builds and 787 managed cases per actual CLR. Its first fresh x64 native GPU worker now passes: six actual GPU producers, private copy and consumer complete with status 4, independent mip values and full post-poison guest-byte verification, followed by the ordered completion event. All six raw holes use the measured 16 KiB kernel alignment; production mapping policy is unchanged. Only this one x64 native case is qualified so far; the other 83 workers and an actual Demon’s Souls replay remain outstanding. ROOT independently verified all 76 first-worker archive hashes and absence of its 17 owned roots. Immutable manifest SHA-256: `16c31548a568225c7603d9753c564aeb90b6abfc07e5ab7adfe7b6b29925b816`.

The corrected Poppy metadata source `d7e27a1e` passes all 33 focused controls per actual CLR. Broader tests exposed a stale PageGuard expectation; test-only `62f77985` corrects it, and that formerly failing batch passes 382 cases per CLR. A later x64 batch instead fails one APR completion expectation (401/402 passed). Source audit finds that completion, write-address and streaming tests concurrently reuse the same key in a static APR command-buffer registry; `ad502b58` joins completion tests to the existing `AmprFileRegistry` collection. This fixes a demonstrated isolation hazard, but the exact failing interleaving was not captured and the successor is not yet executed. Failed manifest SHA-256: `1b66f973825293efcd5378612188a1b1d8287c8f189cd0b7a29a800b0ebaf14a`. Remaining ordinary regressions and native provider workers are outstanding; these controls do not qualify actual signal/TF recovery or Poppy entry.

The combined first-refusal/provider/input diagnostic source `a3cada8b` passes three builds and 866 managed cases per actual ARM64/x64 CLR (156 Host, 630 Libs, 80 GPU), without skips. Two additional fresh x64 pad workers pass: disabled tracing produces no pad rows; enabled tracing records the existing successful guest-memory write. Host traces resolve the x64 .NET 10.0.3 runtime. All 43 fresh native workers per actual CLR also pass, with zero skips, failures, signals, timeouts or remaining workers; ROOT independently checked their exact TRX case identities and runtime/architecture records. The 104 compiled files remain stable, and publication produces the signed, source-exact 239-file native-only payload. These diagnostics preserve original validation and input bytes; test success alone does not establish new game entry.

## Five diagnostic replays on `a3cada8b`

Runs are under `.claude/worktrees/root-game-entry-provider-and-pad-diagnostics/artifacts/runs/`. None adds an accepted title.

| Title / run | Current evidence | Reached level |
| --- | --- | --- |
| Silent Hill / `20261009-071349-sh-game-entry-provider-and-pad-a3cada8b` | Primary GPU failure is deferred canonical dirty acknowledgment: `other-image-original-volume-watch-or-version`. A separate saved ordinary eight-byte write at `0x7260EFCCD8` loses original mapping currentness after admission. Its exact failed inner predicate and any causal link to the GPU failure remain unproved. | No accepted gameplay. Exit 134 after 26 seconds. |
| Octopath / `20261009-071457-octo-game-entry-provider-and-pad-a3cada8b` | Deferred canonical dirty acknowledgment has the same generic original-owner refusal. Adjacent watch diagnostics save Busy admissions 2/1 but later sample admission 0. Those samples do not prove which metadata predicate caused this failure. | No accepted gameplay. Exit 134 after 32 seconds. |
| Oregon / `20261009-071602-oregon-game-entry-provider-and-pad-a3cada8b` | Compute 57, graphics submission 3: finite physical-history validation preparation is rejected after provider Applied. This is a separate phase from earlier precommit/readback retries; replaying the applied write is not safe. | No accepted gameplay. Exit 134 after 36 seconds. |
| Superliminal / `20261009-071658-superl-game-entry-provider-and-pad-a3cada8b` | The exact refused pending DTO is an opaque ordered Action named `command_stream memory operation`, sequence 2613, graphics submission 88. Its consumer is executing at sequence 2612; the Action carries no typed address/effect metadata. | Title/loading corridor and spinner; no accepted gameplay. Exit 134 after 73 seconds. |
| Quake / `20261009-071821-quake-game-entry-provider-and-pad-a3cada8b` | Initialization is followed by `demo1.dem`. Pad diagnostics prove a held W input was written to guest memory at 105.429 seconds, but do not prove player control. Another Kairo game appears during the run; the executor stops only its owned SharpEmu process. The final synchronization fault follows that stop and is teardown evidence. | Automatic demo; censored at 122 seconds. No accepted gameplay or timing result. |

The completed archive is `artifacts/validation/all-games-20261009/game-entry-provider-and-pad-a3cada8b-prepared/final-manifest.json`, SHA-256 `c6a5e5f2d488f1ceec23e6bddf07899e1d1f8360775fffce265a6d1847df4cd0`. ROOT independently verifies all 784 artifact hashes and absence of all 109 owned PID/PPID/process-group roots. Source, compiled files and payload remain unchanged; the shared lock is closed.

Qualified successor `6cb2135a` preserves the first actual Busy result from canonical dirty-owner classification even if that writer finishes before deferral. A new native regression uses distinct actual mappings, joins that writer before deferral, requires no acknowledgment or dirty consumption, and then retries the same original owner through a real drain. Three builds, 872 managed cases per actual CLR, and 44 fresh native workers per actual CLR pass, with no skipped cases, failures, test signals, timeouts or remaining workers. The native regression passes first on both runtimes. Compiled files remain stable and the signed native-only payload is source-exact. The same successor includes first-return ordinary-write currentness diagnostics, preserving the original checks and write behavior.

Two source-exact replays are under `.claude/worktrees/root-canonical-dirty-first-owner-busy/artifacts/runs/`:

- Silent Hill, `20261009-074737-sh-canonical-first-owner-busy-6cb2135a`: guest exit 134 after 26 seconds, peak RSS 1,725 MiB. The primary failure is now the actual `original-read-watch` check at `0x80107C0000 / 65536`, with saved first status Stale during canonical dirty validation. Later state 1 and unknown-producer false are diagnostic samples. Two producer, applied and canonical completions precede the failure. No ordinary-write-currentness refusal is observed in this run. No accepted gameplay.
- Octopath, `20261009-074823-octo-canonical-first-owner-busy-6cb2135a`: guest exit 134 after 29 seconds, peak RSS 2,959 MiB. After the first provider Applied, the acquired validation callback rejects the singleton volume at `0x8030BE0000`; its saved read-watch admission is 1, with released/revoked/coverage loss all zero. Compute sequence 54 / graphics submission 3 fails before canonical finalization. This is a later cohort-validation phase than cancel-only preparation. No accepted gameplay.

Both replays have no foreign-game overlap. Their differing failure paths do not by themselves prove the old race caused earlier game failures or that loading improved. Immutable final manifest SHA-256 `92e6a5ef212274821dac851ad4cdb0c9ea1d603ae7eb77795e77886ece2a314f`: ROOT independently verifies all 734 artifact hashes and absence of all 106 owned roots; source, 104 compiled files and 239 signed payload files remain unchanged, with no remaining shared lock. Subsequent Quake runs use separate archives.

### Quake reaches Accessibility; gameplay remains unverified

The separate `6cb2135a` run `20261009-075142-quake-canonical-first-owner-busy-6cb2135a-quake-long-holds` stays alive for its 330-second cap and stops gracefully. ROOT and Package inspect `shot-0295.png`: the game displays **Options → Accessibility**, including “Disable all settings” and “Customize accessibility settings…”. This is readable menu progress, not accepted gameplay. Actual successful pad writes include Options at 65.551 seconds, Cross at 105.259, 159.332, 201.826 and 222.575 seconds, and forward movement at 261.265 seconds. No accepted player-controlled scene follows from those writes or the console's automatic `demo1.dem` marker.

A bounded three-second sample of the owned game finds the emulation thread parked in CoreCLR `Monitor_Wait` below guest return address `0x80024E464`. Its unsymbolized Rosetta frames do not identify the exact import or lock; no performance claim follows. A foreign Kairo process appears only in the post-terminal inventory, with its start time unavailable, so final-interval overlap remains unknown. It is not signaled. The shutdown command-stream failure is recorded after the bounded stop, not as an entry failure.

The separate immutable `canonical-first-owner-busy-6cb2135a-quake-long-holds/final-quake-manifest.json` has SHA-256 `5afeac1e7acb74aefab4a45c002c15ccd41408889b2f82a798c265f1c96aa700`. ROOT independently verifies its 152 new artifact hashes and absence of all three owned roots. The earlier 734-artifact manifest is unchanged. A proposed 700-second replay is rejected by the harness's 600-second limit before guest launch; its failed receipt is preserved.

The revised 600-second replay `20261009-081459-quake-canonical-first-owner-busy-6cb2135a-quake-onboarding-600` launches with no foreign game present, then is censored after Kairo starts. Only the owned harness is signaled. It ends after 50 seconds, before initialization, the matching import probe or onboarding input; it adds no entry result. ROOT verifies all 121 new hashes and absence of three owned roots. Its separate `final-censored-quake-manifest.json` SHA-256 is `3f3404b4c50c0af669b0b13ab81bedbe4c221ff588232fa2f4fcd9cdf0c801a2`. The completed menu run and prior archives remain unchanged.

### Post-Applied validation successor awaits execution

Frozen source `9f7d4da8` distinguishes exact original/cohort Busy during validation preparation and immediately after acquiring its cancel-only ticket. It cancels that ticket once before waiting and reuses the same already-Applied receipt; it does not repeat the write or native encoder. Stale, unknown cancellation and the later strict callback race remain conservative. Root, CPU, Metal and Package complete source review. Its first Host build fails with ten fixture access errors: the new tests directly call internal backing writer methods. The existing reflection test seam supplies the corrective route; production API visibility will not be widened. ROOT verifies all 18 failed-checkpoint hashes and absence of both owned roots. Failed manifest SHA-256: `b6009dde39b9ef5ebff9450c464641e21d5a27f966c8e48e52553e5eddaca3cd`. Other builds, all 886 managed cases and 58 fresh native workers per CLR, publication and games are unrun. These source changes are not integrated into main and do not establish Oregon or Octopath entry.

The two-file fixture correction `47174a43` uses the existing reflection seam without widening production access or changing test expectations. All three builds and 886 managed cases per actual CLR pass; ROOT independently verifies the 70-artifact managed checkpoint, six exact TRX counters and absence of twelve owned roots. Managed manifest SHA-256: `7be193b6938523b889a7c0d71bdb8d301516357580700bfe12b477f1730b2c04`.

Its first actual ARM64 native acquired-disjoint worker fails after one provider Applied and before finalization. Both original-volume producers first complete with status 4 and full native/canonical-neighbor verification. The new Applied-original loop then throws the generic original-volume guard. The process-start canonical diagnostics flag was absent, so the log does not identify its winning predicate. Validation stops: zero native passes, one failure, all other workers and publication/games unrun. ROOT verifies all 86 failed-native hashes and absence of both owned roots, then removes only the exact stale owner `33279`'s singleton lock. Failed-native manifest SHA-256: `8e6b1b56863791c0a945c23799f624a8c3174a3b8e8b265bc07ca290f577a94e`. This successor remains unintegrated and cannot qualify the game fix. Frozen diagnostic successor `91ce5d5e` carries the same fixture correction and adds eighteen same-query attribution controls; its fresh applied workers will enable the existing canonical diagnostics flag.

That diagnostic successor passes three builds and 904 managed cases per actual CLR (188 Host, 636 Libs, 80 GPU). ROOT verifies all 70 managed checkpoint hashes, six TRX counters and twelve owned roots absent. Its single fresh ARM64 acquired-disjoint diagnostic fails with the exact first guard `original-read-watch / Stale / revoked`; admission is not queried, and the local validation ticket cannot certify it. The refused original starts at `0x7788450000` and spans 65,536 bytes. The fixture's volume base is `0x7788430000`, so its writer computes `0x7788458000`, inside that original. This establishes an erroneous positive-fixture overlap and a correct refusal; it does not justify treating Stale as Busy. ROOT verifies all 88 failed-native hashes and removes only dead owner `45014`'s singleton lock. Failed-native manifest SHA-256: `2268358d540b02ca5318b84b21cd92e1ae5da87cf880f6b3b1885fd7bcb1c011`.

Fixture-only successor `592bc6ae` moves the positive writer into the unused prefix and derives negative overlap/token/stale addresses from the saved original's actual source address. Before any effects, real provider mapping metadata verifies full physical disjointness/overlap against both 64 KiB originals and the own image. All original byte, encoder, Applied, completion and refusal assertions remain. All source peers approve this correction; three builds and 904 managed cases per actual CLR pass. ROOT verifies all 69 managed checkpoint hashes, exact six case inventories, 1,966 source files, 104 compiled files and absence of twelve owned roots. Managed manifest SHA-256: `16e18794411a25b77847569433671c28e0336c8f501f76cb92a8e35308a06b4f`. It remains unintegrated.

Its first thirteen ARM64 native workers pass: all seven acquired-ticket scenarios and preparation scenarios 0–5. The acquired-disjoint worker verifies one actual compute encoder, one Applied write, full original native bytes/canonical neighbors and completion after canonical publication. Before preparation scenario 6 launches, preflight finds an unrelated compiler and stops between workers; no native test fails and no foreign process is signaled. ROOT verifies all 173 partial checkpoint hashes, thirteen passed TRX/receipts and fourteen owned roots absent, then removes only exact dead lock owner `59659`. The immutable partial manifest SHA-256 is `7b7e02617820d3729c40b15751fe4b5656690f0554c702e633a4bd0bbb356591`. The reviewed resumer validates these retained passes and is authorized to execute only the remaining 103 workers. Publication and game replays remain unrun.

The continuation retains those thirteen passes and reaches 29 ARM64 native passes, including all fourteen new Applied-validation scenarios. Existing mixed buffer/image case `ActualStatus4PrebufferWaitCommitsMixedBufferAndImageOnceAfterOriginalWriter` then fails after image Applied and before canonical finalization. Source audit identifies the new post-Applied loop calling the pre-effect buffer-origin check after successful buffer publication has consumed its origin entry. A corrective successor must preserve the exact buffer transaction's successful continuation and consume-once evidence; re-enrolling the old payload or capturing younger content cannot repair this lifecycle mismatch. Execution stops with one actual failure and 86 workers unrun, including all x64 native cases. ROOT verifies all 299 failed checkpoint hashes and absence of 32 owned roots, removing only dead owner `63385`'s lock. Failed manifest SHA-256: `8ab1462ae82817ec69f4694899129d3b65d981e4b07130425332a5c46c5bde4f`. No publication or game replay is qualified by this successor.

The user requests keeping the separate Kairo task active. Future entry-only replays may therefore coexist with a verified known Kairo binary when resources allow, with process-specific captures, overlap provenance and cleanup limited to owned SharpEmu processes. Such replays supply no FPS, loading-speed or isolated performance acceptance. Earlier strict/censored archives retain their original policy and results.

The separate known-Kairo `6cb2135a` Quake replay `20261009-091157-quake-canonical-first-owner-busy-6cb2135a-quake-known-kairo-600` reaches readable Accessibility onboarding choices. It is censored after 387 seconds when Kairo rebuilds to a digest different from the reviewed executable; only the owned SharpEmu harness is signaled. No scripted movement or gameplay is verified, and the exact return-address import probe has zero matches. One bounded managed stack report identifies the guest wait as `CommandStreamQueue.Done → AgcExports.SuspendPoint`, while the render thread is replacing layered texture contents. This is a synchronization snapshot, not evidence of GPU wait duration or a texture defect. Host interruption precedes the teardown command-stream fatal. ROOT verifies all 280 new hashes, five owned roots absent and no shared lock. Immutable censored manifest SHA-256: `6b3d609ed74d94e9c92951e0f49c12bb44854a424f148ae737e4f932b58ba93b`. The original qualification and earlier Quake archives remain unchanged. Read-only identity verification approves the rebuilt Kairo digest `6304024949e3eb5b3604a5c5ded88bc056c325a41ddfc4c52218b0372d6e2834` for future correctness-only companions; it does not alter this completed replay or authorize automatic acceptance of future unknown builds.

The Poppy isolation successor `ad502b58` passes three builds, all 33 focused controls per CLR, and the previously failing APR batch (402 cases per CLR). Both CLR runs pass Host/GPU and Libs batches 0–5. Execution stops at ARM64 Libs batch 6: 375/377 pass; both `TransientProtectionDoesNotAllocateAfterGuestThreadEntry` cases allocate 37,600 bytes instead of zero over 100 calls. Source audit identifies unconditional allocating raw protection preparation in `MacHostViews.ChangeAccess`; this is not attributed to the newer tracked metadata exclusion without further evidence. Remaining batches and native workers are unrun. Failed manifest SHA-256 `807f6233df8194014786e38d39361b649e1aec3e22ab52a13f2eb4d3a4af621c`; ROOT independently verifies all 147 hashes and absence of all 23 owned roots. The allocation requirement remains unchanged, and actual Poppy fault/store/trace recovery is still unimplemented.

This document preserves both the six entry replays and the five later diagnostic replays. Earlier failed source, native workers and game evidence remain immutable. Accepted gameplay remains 1/9; sustained 60 FPS and fast loading are not established.

### Revised runner control results

The revised R6 runner passes all 60 supplied-state controls (28 kernel/censor,
26 retained ownership, one detached-diagnostic and five image-policy controls),
with zero failures, errors or skips. All four isolated Python children return
zero without signals or native/controller invocations. ROOT verifies the 114
archive artifacts, 2,047 unchanged source files and fresh five-root/group absence;
no actual heavy worker or shared lease remains. These are runner controls, not
boot-fix or live kernel-ABI qualification. Archive:
`d13bd0707c32e3280b813aa7bcb6745d280c070281933c96c2f3a8575b891515`.

A separate Poppy diagnostic draft retains the first supplied trap number in a
schema4 entry while keeping recovery behavior unchanged. Its new metadata and
decoder controls are source only, not executed or integrated. The four-fix build
candidate remains `638c3bdf`; the diagnostic is a separate successor.
