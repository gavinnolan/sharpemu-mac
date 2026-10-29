<!-- Copyright (C) 2026 SharpEmu Emulator Project
SPDX-License-Identifier: GPL-2.0-or-later -->

# Portable vertex-fetch follow-up

The user-authorized October 26 diagnostic reproduced 2.49–2.50 guest FPS with
scripted input. Five six-layer depth/shadow encoders averaged 55.9–56.7 ms each
(281.3 ms summed means). Metal's vertex/tiling phase accounted for 99.7% of their
aggregate measured duration; even the 32×32×6 target took about 56 ms. These are
encoder observations, not an exclusive frame-time decomposition. The initial capture did not establish the visual scene. The later comparison below
confirms the rooftop/title scene with screenshots.

The three mesh shaders in those encoders contain 32, 34 and 35 dynamic formatted
buffer-fetch sites. Sites in different branches need not all execute. Offline
translation matched their recorded MSL keys exactly:

| Guest shader hash | Recorded MSL library key | Dynamic fetch sites |
| --- | --- | ---: |
| `76DB3753289971F0` | `A66DB853C407D44A` | 32 |
| `B482CD3A01A37F0F` | `1E6B8DB38CF962DD` | 34 |
| `9FC48AA9F987C1A4` | `4E13425202FEDA43` | 35 |

No game code, captured MSL, descriptors or raw runtime logs are checked in.

## Change

Portable buffer descriptors retain their run-time formats and strides. Previously,
formatted loads decoded four component layouts and numeric conversions even for
identity-swizzled 32-bit float/integer streams. A guarded path now loads the required
dwords together when the **current** descriptor proves that shape and the full requested
range is in bounds. Float3/integer3 fetched as XYZW supplies the appropriate W=1.
When that full run would cross the buffer end, or other components are missing, the
general decoder checks only the selected components and supplies the missing values.
Unaligned accesses use the existing word-assembly helper.

Other formats/swizzles, out-of-bounds elements, placed/swizzled addresses, device
address descriptors and D16 packing retain the general decoder. EXEC masking and
GLC coherent reads remain in effect. `SHARPEMU_MSL_RUNTIME_DWORD_FORMAT=0` restores
the old decoder path for comparison. No shader hash or title controls this behavior.

The offline shader compilation tool also now applies the runtime's translation
switches before decoding portable specializations. Without this, erased format
and stride fields were baked into regenerated shaders: only 53/498 library keys
matched the capture. With runtime switches, 497/497 generated keys matched (two
recorded variants collapse to one source). This is an offline-tool correction;
the runtime already applied those switches.

## Reproduction

Run the synthetic tests and opt-in benchmark serially on a Metal-capable Mac:

```sh
SHARPEMU_TEST_REQUIRE_DEVICE=1 dotnet test tests/SharpEmu.ShaderCompiler.Metal.Tests \
  -c Release --filter MslRuntimeFormatLoadTests \
  --settings docs/performance/shader-serial.runsettings --blame-hang-timeout 120s

SHARPEMU_TEST_REQUIRE_DEVICE=1 SHARPEMU_GPU_BENCH=1 \
  dotnet test tests/SharpEmu.ShaderCompiler.Metal.Tests -c Release \
  --filter MslRuntimeFormatLoadTests.BenchmarkDynamicFormats \
  --settings docs/performance/shader-serial.runsettings --blame-hang-timeout 120s \
  --logger 'console;verbosity=detailed'
```

The benchmark translates a fabricated guest program both ways, checks equal output,
and times 1,024 groups × 32 threads × 256 formatted loads using Metal command-buffer
GPU timestamps. Input addresses vary in the loop; each thread writes a separate
output outside the input range. Four outer rounds alternate before/after ordering;
each arm reports the best of five GPU samples after one warmup. Other apps stay open.
These cache-resident compute-kernel timings isolate fetch overhead. They do not
measure mesh-stage occupancy, a complete shadow pass, or game FPS.

Correctness checks reuse the same compiled shaders while changing all 128 descriptor
format codes, then cover each supported dword format with alternate swizzles and
unaligned/end-of-buffer ranges. Five test cases cover X/XY/XYZ/XYZW and coherent XYZW:
1,600 descriptor/bounds combinations, compared with the old decoder, plus independent
CPU expectations for optimized layouts and their missing components.

## Measured GPU results

Apple M4, macOS 27.2 (26B5112a), x64 .NET 10.0.3 host; GPU timestamps measure device
execution. Each value is the median of four alternating-arm round minima, as described
above. These are synthetic operation measurements, **not game FPS estimates**.

| Workload | Decoder ms | Fast path enabled ms | Change |
| --- | ---: | ---: | ---: |
| 32x4-float-identity | 2.0751 | 0.1396 | -93.27% |
| 32x4-uint-identity | 1.7670 | 0.1425 | -91.94% |
| 32x3-float-missing-w | 1.8795 | 0.1423 | -92.43% |
| 32x1-float-missing-yzw | 1.4884 | 1.5353 | +3.15% |
| 16x4-float-fallback | 2.1888 | 2.2610 | +3.30% |
| 32x4-float-swizzle-fallback | 2.0894 | 2.1261 | +1.76% |

The supported paths improve 12.4–14.9× in this cache-resident benchmark. Fallback
cases cost 1.8–3.3% more; this is an accepted conditional-path cost, not a universal
speedup. Two broader missing-component implementations were rejected because their
fallback costs increased 26–27%. Those implementations are absent from the final diff.

## Validation

- Release Metal shader suite: **3,302 passed, 0 failed, 0 reported skips** with a required
  Metal device and serial execution. The benchmark is opt-in and returns without timing
  when its environment flag is absent; its measured run is listed separately below.
- Focused correctness plus opt-in benchmark: **6 passed, 0 failed**, including 1,600
  changing descriptor/bounds combinations and six alternating-arm timing workloads.
- Offline retranslation: **896 records, 0 failures, 0 unstable outputs**, 497 distinct
  MSL sources. The three regenerated shadow mesh libraries all compile with the OS Metal
  compiler (MSL 3.2, relaxed arithmetic, precise floating-point functions). This is library
  compilation, not a draw replay or full pipeline validation.
- REUSE lint: compliant, 2,819/2,819 files have copyright and license information.

## Silent Hill runtime comparison

A subsequent authorized comparison used only **SILENT HILL: The Short Message**
(PPSA10112), at 1920×1080, render scale 1, VSync on, HDR auto and strict compute on.
Screenshots confirm the rooftop/title menu. This is not a traversal of playable levels.
Both processes were freshly published in Release from clean commit
`3cf2d655e485aea8faf55ce767b6bdfba192756f`: self-contained x64 emulator and native
ARM64 GPU host, .NET SDK 10.0.103/runtime 10.0.3, ReadyToRun disabled in both arms.
Source provenance and both payloads' SHA256 manifests were recorded before launch.

The same executable and independent APFS copies of one saved user/cache state were
used sequentially. The sole difference between final arms was
`SHARPEMU_MSL_RUNTIME_DWORD_FORMAT=0` versus `1`. Both used
`SHARPEMU_SHADER_PREWARM=0` to exclude background precompilation from the steady
comparison. Other apps stayed open; system memory pressure/paging remained present.
Input repeated Enter every six seconds until `UI_TitleMovie01` appeared, then stopped.
The local harness used the shared game lock, captured pass timings/memory/screenshots,
and stopped 60 seconds after the shadow workload began presenting. Native samples
were taken after the measured interval. Both final processes exited **0** after SIGTERM.

Each measurement covers **70 ≤ elapsed seconds < 100** from process launch, after
initial rendering and before thread sampling. FPS statistics use 15 two-second guest
presentation reports per arm, not host window refreshes. Counts at interval boundaries
are partial frames. One interleaved/truncated baseline pass line was excluded and
recorded; no optimized-arm pass line was excluded. No shader library, pipeline
compilation or library wait time was reported in either measured interval.

| Measurement | Fast path disabled | Fast path enabled |
| --- | ---: | ---: |
| Guest FPS, median | 2.49 | 3.98 |
| Guest FPS, minimum–maximum report | 1.99–2.50 | 3.00–4.00 |
| Complete parsed pass records | 10,311 | 16,636 |
| Observed frame IDs (partial boundaries) | 70 | 114 |
| Five target shadow encoders, observations | 350 | 565 |
| Target shadow encoder duration, weighted mean | 56.509 ms | 26.753 ms |
| 512×512×6, 84 draws, mean | 56.299 ms | 26.692 ms |
| 512×512×6, 102 draws, mean | 57.173 ms | 27.141 ms |
| Two 256×256×6 encoders, pooled mean | 56.539 ms | 26.694 ms |
| 32×32×6, 102 draws, mean | 55.995 ms | 26.544 ms |

The five shadow encoders retain the same shader identities, draw counts and logical
mesh threadgroups. Their mean duration falls **52.7%**, while the observed median
scene FPS rises about **60%**. The optimized encoders still spend about 99% of their
measured time in Metal's vertex/tiling phase. Summed encoder means are not an exclusive
frame-time decomposition. This paired observation is not a statistical confidence
interval or a general gameplay FPS claim.

A separate pair with normal prewarming enabled observed 2.49 versus 3.98 median FPS
and 56.638 versus 26.723 ms mean shadow encoders. Its control retained 9.489 seconds
of background library work (zero reported blocking library waits); it is corroborating
evidence, not the primary compilation-free comparison.

The remaining large encoder uses a **72×41×96** layered target and mesh shader
`80BB12DEB34BFB24` / pixel shader `B56A794C42C12B7B`. Its 46-draw variant averages
41.263 ms in the control and 42.340 ms with the fast path, across 69 and 97 observations
respectively. This path has no demonstrated improvement. Its vertex/tiling work and
the remaining roughly 27 ms shadow encoders explain why the scene is still slow.
No shader/title-specific bypass or quality reduction was introduced.

Local evidence identifiers (raw captures contain game data and are not committed):

- Control: `20261026-201309-silent hill short message.zar-dword-off-steady`.
- Optimized: `20261026-201514-silent hill short message.zar-dword-on-steady`.
- Default-prewarm pair: `20261026-200501-…-dword-on-3` and
  `20261026-200737-…-dword-off-2`.
- Aggregate JSON: `artifacts/runtime-diagnosis-20261026/runtime-comparison-steady.json`;
  each run retains `game.tlog`, environment/provenance, screenshots, memory telemetry,
  bounded pass analysis and actual process exit status.

Earlier attempts are explicitly excluded: an initial cold optimized run had prolonged
pipeline compilation and a shutdown abort; a control ended with `ENOSPC` and a shutdown
abort; a retry stopped at the new 1 GiB free-space guard. The abort stack shows an APR
worker reading a ZArchive decompressor after disposal. That lifetime defect is recorded,
not fixed by this shader change. Idle generated A/B artifacts were relocated with all
5,775 file hashes verified; idle compiler cache and this run's rebuildable intermediates
were cleared before the completed pairs. Original source, saves and logs were retained.

## Acceptance limits

The runtime result now demonstrates a substantial improvement in the captured title
scene, but roughly four FPS is still poor performance. The 96-layer pass, remaining
shadow cost, host memory pressure and observed shutdown lifetime defect need further
work. No full-game visual/correctness or gameplay-performance claim is made. The
original runtime payload had no verified source commit; the new comparisons above use
one fully identified build in both arms. The existing GUI was not replaced by this
isolated diagnostic build.
