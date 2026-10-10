# Actual native GPU completion timing — 2026-10-08

Local source `57f59acdb4b23f1e3f96c2f886a970c2ae20c0b0` adds bounded, opt-in GPU timestamps at the renderer's existing successful native command-buffer waits. It changes no scheduling, completion acknowledgement or resource lifetime. Neither native waits nor command-buffer retains are added. Managed status/wait overrides and zero handles never query native timestamps.

The timestamps follow Apple's [GPUStartTime completion requirement](https://developer.apple.com/documentation/metal/mtlcommandbuffer/gpustarttime): read only after successful native completion. Caller wall wait is counted separately. A 4096-entry ring retains actual native intervals; interval union merges overlap and duplicates without adding idle gaps. Consecutive repeats are counted separately, while nonconsecutive duplicates also cannot double-count the union. Missing/invalid timestamps are explicit. Presentation command buffers, scanout and whole-process GPU utilization are outside this measurement.

## Qualification

Release builds succeed. Each ARM64 and Rosetta/x64 runtime passes 189 focused managed cases, zero skips/failures, exact identities and stable DLL hashes. Three new cases independently check overlap/nesting/duplicates, invalid timestamps and disjoint interval gaps. One fresh actual GPU fixture per runtime executes a real shader and verifies all 64 output words, successful status 4, exact native timestamps, two successful waits producing one interval and a zero handle producing no observation. Neither fixture uses a synthetic native status. Existing worker and completion checks remain in the managed union.

The signed clean-source native-only bundle has 239 files. Its hashes and signature remain unchanged after a real Sarah replay. Tests/source reviews are not game fidelity or performance acceptance.

## Sarah measurement

Run `artifacts/runs/20261008-191949-sarah-main-gpu-profile-57f59acd` stays alive for 150 seconds, peak 856 MiB, and stops via owned TERM without KILL or remaining process group/lock. Reviewed screenshots 110/125/135 establish rightward outdoor scene displacement and a subsequent left-facing return. The final 12 short guest cadence windows range from 51.83 to 60.49 Hz; sustained 60+, scanout, complete fidelity and loading remain unqualified.

The renderer records 16,861 successful wait calls, 8,431 consecutive repeated native intervals, and zero invalid timestamps. The entire observed caller wall wait is 44,734.487 ms; it is not GPU execution time.

The retained 4096 unique intervals cover a 70,811.418 ms native timestamp span, with interval union 17,573.986 ms (24.818% coverage) and mean native command-buffer duration 4.291 ms. Earlier 4334 intervals are overwritten. This coverage describes the retained successfully waited guest command buffers only; presentation work is excluded. It must not be reported as whole-process GPU utilization or added to overlapping CPU/presenter measurements.

The last 512 unique guest command-commit samples average 17.091 ms between commits, 8.706 ms in guest-work drain, 0.016 ms in materialization, 7.917 ms acquiring a drawable and 0.200 ms encoding/committing presentation. These are presenter wall phases, containing waits as well as work. Their window differs from the GPU ring. This evidence directs the next performance experiment toward presentation pacing and CPU/render coordination, rather than assuming shader execution alone explains the observed cadence. Apple's [nextDrawable documentation](https://developer.apple.com/documentation/quartzcore/cametallayer/nextdrawable%28%29) confirms that acquisition waits for an available drawable.

The existing `--vsync=off` option can separate display pacing from rendering capacity in an explicit experiment. It is not enabled by this change: Apple documents possible tearing for [unsynchronized layer updates](https://developer.apple.com/documentation/quartzcore/cametallayer/displaysyncenabled). Guest timing and visual correctness still need validation before any default changes.

## Cache qualification remains incomplete

The isolated cache's corrected 74c source passes 27 managed cases per runtime, but its first actual ARM cold case fails before launching the compiler: the installed toolchain exceeds the old two-GiB identity bound. No warm hit, offline library link, GPU parity or timeout result is certified on 74c. The terminal worker leaves no owned group; the retained directory/PID lock is removed only after independent ownership/process review. Original failure logs and source are preserved.

Metadata inspection finds 419 enumerated file paths representing 4,986,033,379 logical bytes; the physical regular-file inventory has 354 files totaling 3,201,958,867 bytes, with 65 file and 15 directory symlinks. Isolated successor 880 hashes the complete regular content once and includes link identities under a finite four-GiB bound; its native qualification is still pending. Identity overhead must be measured separately before claiming a loading gain. No cache default is promoted and no new package/runtime tool dependency is shipped.

Evidence: `artifacts/validation/main-gpu-wait-profile-57f59acd` contains frozen source, exact managed/native case results, current binary manifests, signed publish context, game command and ownership receipts. The run's `runtime-summary.json` independently recomputes native interval union, preserves reviewed screenshot names and hashes original run files. The cache's failed qualification remains in its isolated worktree at `artifacts/validation/offline-library-74c-qualification`.

The all-nine goal remains active: one title has verified controllable gameplay; no title meets every rendering, sustained 60+ and fast-loading gate. No remote push or upstream merge occurred.
