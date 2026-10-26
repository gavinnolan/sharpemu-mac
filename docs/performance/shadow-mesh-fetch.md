<!-- Copyright (C) 2026 SharpEmu Emulator Project
SPDX-License-Identifier: GPL-2.0-or-later -->

# Portable vertex-fetch follow-up

The user-authorized October 26 diagnostic reproduced 2.49–2.50 guest FPS with
scripted input. Five six-layer depth/shadow encoders averaged 55.9–56.7 ms each
(281.3 ms summed means). Metal's vertex/tiling phase accounted for 99.7% of their
aggregate measured duration; even the 32×32×6 target took about 56 ms. These are
encoder observations, not an exclusive frame-time decomposition. The exact
visual gameplay scene was not confirmed.

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

## Acceptance limits

The runtime capture identifies an expensive path. This patch accelerates a synthetic
operation present in that path; it has not yet established how frequently the game's
live descriptors take it or a gameplay FPS improvement. No additional game run was
performed for the implementation. The retained original runtime payload has hashes
but no verified source commit; matching the three generated MSL bodies establishes
shader-source correspondence, not whole-binary provenance.
