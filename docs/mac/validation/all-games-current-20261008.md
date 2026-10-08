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

The4K R8 candidate now has21 new managed rows, full8,294,400-texel independent offset/byte agreement under the existing emulator topology, and4 actual native typed cases on both CLRs. Its game replay is separate and pending. Actual console topology remains unknown. Indexed vertex-prefix reduction, finite color-mip projection and a mapped-copy observation contract remain isolated candidates; they are not in the2ad bundle. A native vertex fixture failure is retained and corrected source awaits rerun.

Next work clears exact4K texture and color-mip blockers, then qualifies sampled aliases, range/mapping authority and fault recovery. One-change quiet-host pacing/loading comparisons follow stable controllable scenes. No all-game60FPS or fast-loading improvement is claimed. No remote publication or merge was performed.
