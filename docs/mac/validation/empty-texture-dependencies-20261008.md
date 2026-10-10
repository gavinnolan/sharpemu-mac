<!-- Copyright (C) 2026 SharpEmu Emulator Project -->
<!-- SPDX-License-Identifier: GPL-2.0-or-later -->

# Empty texture-dependency allocation optimization

The exact actual work-owner table and every input texture-lease association are inspected under the existing caller gate. Only an owner-free, association-free work item returns the shared empty dependency array. Captured, predecessor-only, released owner/lease and failed version paths retain the prior validation pipeline. No graph-enabled flag is used as a bypass; no backing/native lifetime authority is inferred.

The Sarah d756 EventPipe trace identified dependency iterator/set allocations on the drain thread. This candidate avoids building those objects for the common private-snapshot case. Three actual admitted Compute cases (0/1/16 private textures) warm the exact private lookup, then invoke it4096 times: candidate allocates0 bytes on both CLR architectures; original a8c source allocates1,507,328 bytes (368B/lookup) in each ARM control. The intentional3/3 baseline failures demonstrate the allocation bound; they are not emulator regressions. The three final candidate controls pass again after restoring/rebuilding the source. Reflection, delegate construction, admission and warming are outside measurement. This is a Compute metadata allocation result, not a draw/native/FPS claim.

64 genuine scheduling, admission, lease and metadata cases pass per ARM64/x64CLR, including the3new allocation controls; zero skips. Existing captured producer and failed/released/predecessor paths remain qualified. Source peer review clear; no native operation changes. Full build126existing warnings/0errors, final incremental8warnings/0errors. No new package or GC policy. Game FPS/total-drain allocation changes remain unmeasured.
