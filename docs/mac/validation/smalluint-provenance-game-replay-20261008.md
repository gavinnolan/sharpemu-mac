<!-- Copyright (C) 2026 SharpEmu Emulator Project -->
<!-- SPDX-License-Identifier: GPL-2.0-or-later -->

# Signed a8c65ae1 progression and provenance replay

Clean source a8c65ae1a2cb0f19ad17b35a0a25cc860d776656, signed native-only Rosetta app at artifacts/macos/compat-a8c65ae1/SharpEmu.app, SDK10.0.103/runtime10.0.3. Includes exact smallUint arrays and default-off image provenance. CPU backing, linear ownership and empty-dependency performance candidates remain excluded. Same20/40/60 Enter input prefix and150-second cap; Superliminal alone opts into SHARPEMU_METAL_IMAGE_PROVENANCE=1. Shared game lock, compiler/test pause and sampled process records retained. This is correctness progression and diagnostic evidence, not a quiet-host performance comparison. A foreign REmu was seen after the batch and left untouched.

| Title | Actual result | Earliest next blocker |
| --- | --- | --- |
| Silent Hill | Alive to150s,2013MiB, TERM0s/noKILL; one guest frame reported early, later windows zero |1024x1024 RGBA8Unorm mode27/type13/F2E BGRA/PERF7 write array; raw[80102E00,C3800000,00FFC0FF,D1B00F2E,0,00700000,0,0], Compute9E4711118129A6D1, log2355 |
| Octopath II | Alive to150s,2920MiB, TERM0s/noKILL; one guest frame reported early, later windows zero | Same finite layout, raw[80303000,C3800000,00FFC0FF,D1B00F2E,0,00700000,0,0], Compute8CB66A4E5F3BEB4C, log1280 |
| Oregon Trail | Alive to150s,1753MiB, TERM0s/noKILL; one guest frame reported early, later windows zero | Same finite layout, raw[801C6F00,C3800000,00FFC0FF,D1B00F2E,0,00700000,0,0], Compute5E3C7E57AC8F959E, log1404 |
| Superliminal | Title/loading hallway visible, then exit134 at43s/3433MiB | Strict512x512→128x64 RGBA16Float115/type2/depth1/layer1 conflict, full provenance below |

The three earlier small integer texture failures advance; all three still stop guest command production at the next array rejection. Host survival to TERM is not game success. The singleton origin addressing proof does not authorize a1M-texel multi-block array. Keep complete raw layout and independent coordinate/byte qualification before broader admission.

Superliminal's same address706B150000 was created by color-target work sequence29413, dcb.graphics/submission1483, with512x512 nativeRGBA16Float. Its last explicit mutation was that color-draw; initialized=true, GPUwritten=true, contentVersion1. Captured color words decode Width511/Height511/MaxMip9, mip0, RenderTarget layout, Bits16_16_16_16/Float and noDCC. Incoming write-only T# raw[706B1500,C4700000,000FC01F,91B00FAC,0,00700000,0,0] requests128x64/pitch128/singlemip0 at the same address, source65536 bytes. This identifies the original producer and a distinct view tuple. It does not prove allocator reuse, a mip/subrect interpretation, old GPU write publication, or authorize stale CPU seed replacement. REmu/Kairo reference designs require typed layout and GPU-content debt/lifetime evidence before rebinding.

Before app build, integrated123Libs +74 genuine presenter controls passed per ARM64/x64CLR, plus36 actual native numerical Metal cases perCLR; zero skips. Parser proof was unchanged and not redundantly repeated. App publication uses separate bundle profile outputs. Tests are numerical contracts, not full-game visual correctness.

All original logs, commands/inputs, screenshots and exact build/payload hashes are retained under the linked run paths in commands.json and summary.json. No sustained gameplay/60FPS or loading improvement is established by this batch.
