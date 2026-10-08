# Integrated R16 selected storage and affected game replay

Clean local main `3ae88a16b8d195bb9d879c42234d962c93a97950` integrates the finite
R16Float selected storage class and the separately reviewed managed-watch
generation correction. Guest execution remains osx-x64/Rosetta. ARM64 checks
run the actual ARM64 CLR; they do not enable ARM64 guest execution.

## Qualification

Both actual CLR architectures passed the exact combined selections:

- 376 library controls, including nine repeated/concurrent managed-watch
  notifications and the prior indexed vertex-prefix controls.
- 117 managed Metal controls: the prior77 and the R16/DS59 have19 duplicate
  names. Two existing DS cases had not been in the earlier77 selection.
- 55 required actual native Metal cases, including four R16 cases. Every native
  row asserts/prints its actual architecture and `.NET 10.0.3`; no skip, failure,
  timeout or signal. All native R16 cases reach negative-X bounds, reuse and
  complete native-byte comparisons across all four independent views.

The two bounded builds finished without errors (GPU13 warnings, Libs115).
Source and all assembly hashes remained unchanged through qualification.
`artifacts/validation/all-games-20261008/r16-main-3ae88a16/` retains frozen DLLs,
source, exact names, TRXs and commands:89 files, manifest SHA256
`ce128bcd71681819a668185f11a84fd5a2df30b91a028cfeecdb6ad4a0177872`.

The signed native-only239-file app is
`artifacts/macos/compat-r16-3ae88a16/SharpEmu.app`. Its build metadata records
clean exact source3ae88a16, SDK10.0.103/runtime10.0.3. Independent deep/strict
signature verification and forbidden dependency checks pass. Publish took25.8s;
that is build time, not game loading. Bundle manifest SHA256:
`82dc38bcafee19a63f5df874238b8f33b5e6159cb1f5aee3cb968b71e263d131`.

## Preserved failures and correction

The isolated native a38/b507 runs exposed a fixture error: R16/X001 sampled
out-of-bounds reads retain alpha1. The corrected fixture preserves every
out-of-bounds store byte check; all four native cases then pass on each CLR.
Its independent Apple specification and pinned PDF are recorded in the fixture
README. No production translator was changed to satisfy that expectation.

The first integrated5f run passed375/376 library controls and failed the existing
small linear R16 mip0 control. The candidate classifier had intercepted every
R16 mip chain, including that supported linear case. Source3ae narrows both
snapshot routing and native-format validation through one shared classifier;
the original test is unchanged and passes. All near-raw/selected-mip vetoes
remain active. The original failure's78-file manifest is
`07d3a72a7a9660c1e35e2663847c6980769d119c6f9c786566039ebf32007bab`.
An earlier verifier guessed115 combined GPU names; retained TRXs establish117.
That preflight failure ran no tests and is retained separately.

## Actual game results

The same frozen signed app ran Silent Hill then Octopath, exclusively through
the machine-wide harness, with150s caps and Enter at20/40/60s. Both processes
exited before the cap; surviving reference/helper processes were untouched.
The bundle's239 hashes are identical before and after both runs.

Silent Hill exits134 after31s, peak2269MiB. The old R16 descriptor rejection is
cleared; selected mip0 admission at `0x802F270000` then rejects global mapping
epoch802964→804552. The check does not prove that this resource changed, and
was not weakened. The later closed-command-stream fatal is downstream of this
first specific failure.

Octopath exits134 after36s, peak2893MiB. The old R16 descriptor rejection is
cleared; `DrainGuestImageCpuSync` rejects an ordinary native type3/depth1
singleton array because that path requires2D images. Correct typed physical
synchronization and GPU/CPU ownership still need implementation; simply allowing
the array through a generic RGBA upload would not qualify correctness.

The latest retained screenshots were directly inspected: both viewports are
black. Neither run proves gameplay, rendering fidelity, FPS or fast loading.
Full runs, screenshots, commands and hashes are under
`artifacts/validation/all-games-20261008/r16-game-replays-3ae88a16/`.
The [all-nine report](all-games-current-20261008.md) keeps older titles scoped to
their own binary rather than implying this was a new all-nine replay.
