# DS R32 mip0 sampled input and mip1 writer — native fixture preparation

Source-only preparation. No build/test/native/game worker has run for this
fixture. The combined source imports root's faithful sampled-mip snapshot and
disjoint admission6bf, sampler importffb, and exact taken-work inspection629,
with inherited8bc diagnostic and corrected3b fatal interception retained.

Actual Compute179 has sampled raw
`[7092C800,C1600000,003FC07F,90900924,0,00700080,0,0]` and a same-resource
storage mip1 descriptor using FAC/base1/last1. The R32Float mode9 resource is
512x256/pitch512 with nine backing mips. Mip0 resides at+0x40000/0x80000,
mip1 at+0x20000/0x20000; their physical source ranges are disjoint. The sampled
raw's four selectors are RRRR, not X001. Dimensions alone grant no admission.

The prepared fresh native Fact is
`ActualR32OriginalMipZeroSampleAndDisjointMipOneWriterPreserveFullChain`.
Use one fresh worker per actual ARM64/x64 CLR (60s watchdog),
`SHARPEMU_TEST_REQUIRE_R32_DISJOINT_NATIVE=1`,
`SHARPEMU_TEST_REQUIRE_DEVICE=1`, `SHARPEMU_METAL_SELECTED_NATIVE_REUSE=1`, and
explicit EXPECT_ARCH/EXPECT_FRAMEWORK/EXPECT_KERNEL_PAGE assertions. No disabled
device row is counted as a native pass.

It maps a continuously owned original 786432-byte Mac shared backing, constructs
the two physical mip payloads using the independent standard64K mode9 bit
equation from the retained selected-mip fixture, and supplies the actual raw
bindings to production CreateTextureSnapshots. It neither calls GnmTiling to
seed or construct expected bytes nor prearms the protected observations.
All131072 mip0 floats and32768 initial mip1 floats are independently varied,
including signs. Every other chain/tail/padding byte is an independent sentinel.

An explicitly hand-authored MSL compute fixture samples each even mip0 texel at
its exact normalized center with nearest filtering and explicit level0, verifies
that all sampled channels follow actual RRRR, then writes every mip1 texel. It
uses production binding layout/materialization/public SubmitComputeDispatch and
DrainGuestWorkCore; frontend preparation cleanup runs after Submit and before
Drain. This is transport/admission/numerical proof, not guest-ISA shader codegen.
The expected32768 R32 output floats are selected directly from the independent
linear mip0 payload. After real status4, all native output bytes and all786432
original guest bytes must match the deliberate full-M0 CPU poison plus untouched
M1/tail/padding; no GPU→guest publication is claimed here. Native sampling must
still return the ORIGINAL detached M0 after that real participating CPU write.
Substituting a different pixel array into a copied DTO refuses. Both original
provider mappings must remain current after M1 cold protection setup and after
poison; neither original is recaptured or refreshed.

The actual sampled texture creation hook delegates to production CreateDrawTexture.
Before materialization it runs original same-chain preflight on a separate managed
thread while the actual selected dispatch is taken, asserting no renderer seed TLS
on that thread. The wait is bounded5s with no presenter gate held; an unknown caller
retains its Task with quarantined native/mapping owners.

Thirteen genuine managed rows stop before source/provider/native operations:
ten complete-raw/shape/access refusal cases, one missing-provider/no-fallback case,
and public SubmitComputeDispatch pending/taken writable-buffer cases. These use
a throwing provider only to assert it is NEVER called; they do not fabricate a
positive mapping. The positive row uses real Mac SharedBackingViews forwarding,
not PhysicalVirtualMemory forwarding, and is counted separately as actual native.

Original mapped copies must show the exact two selected physical source ranges,
real backing IDs/incarnations/kernel page metadata, and zero ordinary reads.
The fixture's wrapper never supplies a replacement observation or CPU writeback
fallback. It intentionally does not hide any cold token-owner replacement caused
by recording different footprint sizes at the same base address.

Cleanup uses existing selected-native exact shader/queue/page ownership. Unknown
native completion retains the native state, shared views, original owned hole,
memory and queue scope in a fresh-worker quarantine. Confirmed cleanup attempts
all independent actions and preserves the original failure; an uncertain view
cleanup prevents freeing the underlying hole. No generic teardown/ISA/native
writer coverage is inferred.
