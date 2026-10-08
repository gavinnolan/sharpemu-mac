# Original native texture ownership qualification — 2026-10-08

Isolated clean `efe7235ca7e794ba9bd73c4e850dd6ad873a7f5b` now passes 17
physical read-watch controls, four native-owner cleanup controls and six actual
Metal scenarios on each ARM64 and Rosetta/x64 .NET 10.0.3 host runtime. All
cases pass, with zero skips. Every native case runs in its own bounded process
under the machine-wide directory/PID lock; command-buffer status 4 is required.
The current closure's 31 DLL hashes remain unchanged across qualification;
all owned workers are absent and the lock is released.

The native cases use real Mac/SharedBackingViews allocations and the production
SubmitComputeDispatch/DrainGuestWorkCore path. They verify seeded upload/store,
subsequent read/store following unrelated effects, and refusal after overlapping
physical or managed writes, mapping ABA, or session replacement. The retained
original mapping/watch authority is transferred only after successful native
upload; it is not replaced by a newer observation when the global epoch changes.
Cleanup consumes exact ownership before callbacks and attempts remaining cleanup
without losing the primary cause. Runtime use remains an opt-in candidate.

These scenarios do not exercise the complete PhysicalVirtualMemory forwarding
path, guest/JIT writers, general GPU-to-guest RAM publication, game progression,
rendering fidelity, FPS or loading. Its subsequent signed Octopath replay exits
after 33 seconds at GPU-produced/pending-write ownership, peak 3216 MiB, with
one early guest frame and no gameplay. See the [latest integrated and isolated
runtime checkpoint](main-prefix-and-wait-profile-20261008.md). This candidate
is not integrated into main.

The earlier ARM output-marker failure remains preserved. A GPU-only rebuild
also changed ten production DLL hashes through revision metadata; the attempted
reuse of old production hashes was rejected. Qualification therefore reran all
21 managed and six native cases per runtime against the full fresh current
closure; no binary-equivalence shortcut is claimed.

Root archive:
`artifacts/validation/all-games-20261008/selected-native-original-efe7235c`.
Source/qualification transfer SHA256:
`e083adbb77615a9e8bf67b07438cd4be46579951fcd8735352af86d462f1342a`.
It copies 69 evidence/source files and retains 31 verified original binary
references. Original qualification manifest SHA256:
`cf82db592eddb092683e31375abd9a8e8a5a2401838478fe74b2cf5d4231b1a3`.
