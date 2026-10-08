<!-- Copyright (C) 2026 SharpEmu Emulator Project -->
<!-- SPDX-License-Identifier: GPL-2.0-or-later -->

# Owned linear sampled texture allocation

Source6c748541 transfers the privately allocated full linear source into the queued sampled snapshot instead of cloning that same payload again. Shorter logical prefixes retain a detached copy; detile/GPU-detile, storage, selected mip and final observation/retry policy are unchanged. The source originates in a new array immediately before the owned guest read; optional file diagnostics only read it. No shared guest mapping, pool or mutable caller array is exposed.

Three genuine actualCreateTextureSnapshots controls pass per ARM64/x64CLR on.NET10.0.3. Padded3x2/64x2/512x128 snapshots keep the original full owned bytes after later guest mutation. For256KiB payload, warmed current-thread allocation drops528376→266208B;512Bpayload2888→2352B. The original d756 allocation control fails the largecase bound as expected (other2pass), proving that the bound detects the extra full payload. The initial unregistered-backend fixture failures are separately preserved and excluded.

Three actual native sampled upload controls pass perCLR (zero skips):3x2/pitch64,64x2/pitch128,512x128/pitch576. ActualCreateTextureSnapshots thenCreateDrawTexture produces nativeRGBA8Unorm70/type2/oneMip; after poisoning all original guest bytes and forbidding late reads, every logical pixel/channel matches actual native row readback. Exactly one owned full guest read is observed percase. Owned native handles are released, tracker/backend fixture state restored; bounded subprocesses exit0, no timeout/KILL. Shared game lock serializes device use. Foreign REmu presence is recorded; these are correctness controls, not quiet-host performance.

Source peer review clear. Final fixture build7existing warnings/0errors. No production dependency or GC policy changes. The Sarah instrumented baseline trace points at these producer copies, but neither a sustainedFPS gain nor loading reduction is established. A one-change signed d756-based app and matched-scene comparison remain separate qualification.
