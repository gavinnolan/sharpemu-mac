// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.ShaderCompiler.Resources;
using Xunit;
using Xunit.Abstractions;

namespace SharpEmu.ShaderCompiler.Metal.Tests;

/// <summary>
/// The scalar and vector opcodes the MSL translator used to reject while the
/// SPIR-V one handled them. Each fixture is a real instruction encoding whose
/// results are stored to the dense buffer, so the oracle is the returned bits.
/// </summary>
public sealed class Gen5MslScalarVectorParityTests(ITestOutputHelper output)
{
    private const uint SEndpgm = 0xBF810000;

    [Theory]
    // Signed high multiply: -2 * 0x10000 has 0xFFFFFFFF above the low dword,
    // where the unsigned form would report 0x0000FFFF.
    [InlineData(0xFFFFFFFEu, 0x00010000u, 0xFFFFFFFFu)]
    [InlineData(0x00000002u, 0x00010000u, 0x00000000u)]
    [InlineData(0xFFFFFFFFu, 0xFFFFFFFFu, 0x00000000u)]
    public void SignedHighMultiplyKeepsTheSignOfBothOperands(uint left, uint right, uint expected)
    {
        var result = Run("s-mul-hi-i32",
        [
            0xBE8003FF, left,     // s_mov_b32 s0, left
            0xBE8103FF, right,    // s_mov_b32 s1, right
            0x9B020100,           // s_mul_hi_i32 s2, s0, s1
            0x7E000202,           // v_mov_b32 v0, s2
            .. StoreDword(0, 0),
            SEndpgm,
        ], 16);
        if (result is null) return;
        Assert.Equal(expected, BinaryPrimitives.ReadUInt32LittleEndian(result.AsSpan(0)));
    }

    [Theory]
    // Leading zero count, 0xFFFFFFFF for a source with no set bit.
    [InlineData(0x00FF0000u, 8u)]
    [InlineData(0x80000000u, 0u)]
    [InlineData(0x00000001u, 31u)]
    [InlineData(0x00000000u, 0xFFFFFFFFu)]
    public void FindLastBitCountsLeadingZeroes(uint source, uint expected)
    {
        var result = Run("s-flbit-i32-b32",
        [
            0xBE8003FF, source,   // s_mov_b32 s0, source
            0xBE811500,           // s_flbit_i32_b32 s1, s0
            0x7E000201,           // v_mov_b32 v0, s1
            .. StoreDword(0, 0),
            SEndpgm,
        ], 16);
        if (result is null) return;
        Assert.Equal(expected, BinaryPrimitives.ReadUInt32LittleEndian(result.AsSpan(0)));
    }

    [Fact]
    public void BitReplicateBroadcastsTheSourceIntoBothHalvesOfThePair()
    {
        var result = Run("s-bitreplicate",
        [
            0xBE8003FF, 0x12345678, // s_mov_b32 s0, 0x12345678
            0xBE823B00,             // s_bitreplicate_b64_b32 s[2:3], s0
            0x7E000202,             // v_mov_b32 v0, s2
            0x7E020203,             // v_mov_b32 v1, s3
            .. StoreDword(0, 0),
            .. StoreDword(1, 4),
            SEndpgm,
        ], 16);
        if (result is null) return;
        Assert.Equal(0x12345678u, BinaryPrimitives.ReadUInt32LittleEndian(result.AsSpan(0)));
        Assert.Equal(0x12345678u, BinaryPrimitives.ReadUInt32LittleEndian(result.AsSpan(4)));
    }

    [Theory]
    // The bit index spans the whole pair, so 33 lands in the high dword. Both
    // forms read the value already at the destination and write one bit of it.
    [InlineData(0xBE821E00u, 0x00000000u, 33u, 0x00000000u, 0x00000002u)] // s_bitset1_b64
    [InlineData(0xBE821E00u, 0x00000000u, 4u, 0x00000010u, 0x00000000u)]
    [InlineData(0xBE821C00u, 0xFFFFFFFFu, 33u, 0xFFFFFFFFu, 0xFFFFFFFDu)] // s_bitset0_b64
    [InlineData(0xBE821C00u, 0xFFFFFFFFu, 4u, 0xFFFFFFEFu, 0xFFFFFFFFu)]
    public void PairBitsetUpdatesTheSelectedBitOfTheDestinationPair(
        uint word, uint initial, uint bit, uint expectedLow, uint expectedHigh)
    {
        var result = Run("s-bitset-b64",
        [
            0xBE8003FF, bit,        // s_mov_b32 s0, bit
            0xBE8203FF, initial,    // s_mov_b32 s2, initial
            0xBE8303FF, initial,    // s_mov_b32 s3, initial
            word,                   // s_bitset0/1_b64 s[2:3], s0
            0x7E000202,             // v_mov_b32 v0, s2
            0x7E020203,             // v_mov_b32 v1, s3
            .. StoreDword(0, 0),
            .. StoreDword(1, 4),
            SEndpgm,
        ], 16);
        if (result is null) return;
        Assert.Equal(expectedLow, BinaryPrimitives.ReadUInt32LittleEndian(result.AsSpan(0)));
        Assert.Equal(expectedHigh, BinaryPrimitives.ReadUInt32LittleEndian(result.AsSpan(4)));
    }

    [Theory]
    // D.u64 = S1.u64 << (S0.u32 & 0x3F), so a shift of 36 moves bit 0 into the
    // high dword's bit 4.
    [InlineData(36u, 0x00000001u, 0x00000000u, 0x00000000u, 0x00000010u)]
    [InlineData(4u, 0x10000000u, 0x00000000u, 0x00000000u, 0x00000001u)]
    [InlineData(0u, 0x89ABCDEFu, 0x01234567u, 0x89ABCDEFu, 0x01234567u)]
    public void ShiftLeftOfARegisterPairCarriesAcrossTheDwordBoundary(
        uint shift, uint low, uint high, uint expectedLow, uint expectedHigh)
    {
        var result = Run("v-lshlrev-b64",
        [
            0x7E0002FF, shift,      // v_mov_b32 v0, shift
            0x7E0402FF, low,        // v_mov_b32 v2, low
            0x7E0602FF, high,       // v_mov_b32 v3, high
            0xD6FF0004, 0x00020500, // v_lshlrev_b64 v[4:5], v0, v[2:3]
            .. StoreDword(4, 0),
            .. StoreDword(5, 4),
            SEndpgm,
        ], 16);
        if (result is null) return;
        Assert.Equal(expectedLow, BinaryPrimitives.ReadUInt32LittleEndian(result.AsSpan(0)));
        Assert.Equal(expectedHigh, BinaryPrimitives.ReadUInt32LittleEndian(result.AsSpan(4)));
    }

    [Fact]
    public void ScratchStoresAndLoadsRoundTripThroughPrivateStorage()
    {
        // scratch_store_dword off, v2, s0 / scratch_load_dword v3, off, s0 both
        // name the FLAT private segment, which has no resource of its own.
        var result = Run("scratch-round-trip",
        [
            0xBE800380,             // s_mov_b32 s0, 0           (scratch offset)
            0x7E0402FF, 0x0BADF00D, // v_mov_b32 v2, 0x0BADF00D
            0xDC704000, 0x00000200, // scratch_store_dword off, v2, s0
            0xDC304000, 0x03000000, // scratch_load_dword v3, off, s0
            .. StoreDword(3, 0),
            SEndpgm,
        ], 16, scratchDwords: 64);
        if (result is null) return;
        Assert.Equal(0x0BADF00Du, BinaryPrimitives.ReadUInt32LittleEndian(result.AsSpan(0)));
    }

    [Fact]
    public void ScratchDeclaresAPrivateArraySizedFromTheRequest()
    {
        var shader = Compile("scratch-declaration",
        [
            0xBE800380,             // s_mov_b32 s0, 0
            0x7E0402FF, 0x0BADF00D, // v_mov_b32 v2, 0x0BADF00D
            0xDC704000, 0x00000200, // scratch_store_dword off, v2, s0
            SEndpgm,
        ], 16, scratchDwords: 64);
        Assert.Contains("thread uint sharpemu_scratch[64]", shader.Source, StringComparison.Ordinal);
        Assert.Contains("% 64u]", shader.Source, StringComparison.Ordinal);
    }

    [Fact]
    public void ProgramCounterAndDependencyWaitsAreNoOps()
    {
        // S_SETPC_B64 is a dynamic branch the decoder has already linearized
        // away, and S_WAITCNT_DEPCTR waits on counters the MSL has none of.
        var shader = Compile("control-no-ops",
        [
            0xBFA30000,             // s_waitcnt_depctr 0
            0xBE802000,             // s_setpc_b64 s[0:1]
            SEndpgm,
        ], 0);
        Assert.NotEmpty(shader.Source);
    }

    [Theory]
    // A 16-bit compare reads only the low half of each register.
    [InlineData(0x0001FFFFu, 0x00020001u, false)] // 0xFFFF < 0x0001 is false unsigned
    [InlineData(0x00010001u, 0x0002FFFFu, true)]
    public void UnsignedShortCompareIgnoresTheUpperHalfOfItsSources(uint left, uint right, bool expected)
    {
        var result = Run("v-cmp-lt-u16",
        [
            0x7E0002FF, left,       // v_mov_b32 v0, left
            0x7E0202FF, right,      // v_mov_b32 v1, right
            0x7D520300,             // v_cmp_lt_u16 vcc, v0, v1
            0x7E04026A,             // v_mov_b32 v2, vcc_lo
            .. StoreDword(2, 0),
            SEndpgm,
        ], 16);
        if (result is null) return;
        Assert.Equal(expected ? 1u : 0u, BinaryPrimitives.ReadUInt32LittleEndian(result.AsSpan(0)) & 1u);
    }

    [Theory]
    // A FLAT address whose high dword is an aperture constant lands in LDS or in
    // scratch instead of going through the page table, and the translator has to
    // route it there like the hardware does.
    [InlineData(0x000000EBu, "sharpemu_lds[", "7u")]       // src_shared_base
    [InlineData(0x000000EDu, "sharpemu_scratch[", "8u")]   // src_private_base
    public void FlatAccessesThroughAnApertureReachTheStorageItNames(
        uint apertureOperand, string storage, string apertureValue)
    {
        uint[] words =
        [
            0x7E000280,                     // v_mov_b32 v0, 0
            0x7E020200u | apertureOperand,   // v_mov_b32 v1, src_shared/private_base
            0x7E0402FF, 0x00C0FFEE,         // v_mov_b32 v2, 0xC0FFEE
            0xDC700000, 0x007F0200,         // flat_store_dword v[0:1], v2
            0xDC300000, 0x037F0000,         // flat_load_dword v3, v[0:1]
            .. StoreDword(3, 0),
            SEndpgm,
        ];
        var shader = Compile("flat-aperture", words, 16, scratchDwords: 64);
        Assert.Contains(storage, shader.Source, StringComparison.Ordinal);
        Assert.Contains($" == {apertureValue}", shader.Source, StringComparison.Ordinal);
        // The global fall-through stays, for a lane whose address is not in the aperture.
        Assert.Contains("sharpemu_load_device_dword", shader.Source, StringComparison.Ordinal);

        var result = Run("flat-aperture", words, 16, scratchDwords: 64);
        if (result is null) return;
        Assert.Equal(0x00C0FFEEu, BinaryPrimitives.ReadUInt32LittleEndian(result.AsSpan(0)));
    }

    // buffer_store_dword vN, off, s[8:11], 0 offset:byteOffset
    private static uint[] StoreDword(uint register, uint byteOffset) =>
        [0xE0700000u | byteOffset, 0x80020000u | register << 8];

    private static Gen5MslShader Compile(
        string name, uint[] words, int backingBytes, uint scratchDwords = 0)
    {
        var fixture = new Gen5ComputeFixture(name, words, 8, backingBytes);
        var program = Gen5ComputeFixtures.DecodeOrThrow(fixture);
        var userData = new uint[16];
        if (backingBytes != 0)
        {
            userData[8] = 0x200000;
            userData[10] = (uint)backingBytes;
            userData[11] = DescriptorConstants.IdentityDestinationSelect;
        }

        var request = Gen5ComputeFixtures.RequestOrThrow(
            program,
            ShaderStage.Compute,
            runtimeInputs: backingBytes != 0 ? new ResourceRuntimeInputs { UserData = userData } : null,
            scratchDwords: scratchDwords);
        Assert.True(Gen5MslTranslator.TryCompileProgram(request, out var shader, out var error), $"[{name}] {error}");
        return shader!;
    }

    private byte[]? Run(string name, uint[] words, int backingBytes, uint scratchDwords = 0)
    {
        var shader = Compile(name, words, backingBytes, scratchDwords);
        if (!MetalNative.IsAvailable)
        {
            Assert.False(
                OperatingSystem.IsMacOS() && Environment.GetEnvironmentVariable("SHARPEMU_TEST_REQUIRE_DEVICE") == "1",
                $"The required gate cannot execute {name} without a Metal device.");
            output.WriteLine($"No Metal device on this host; {name} execution skipped.");
            return null;
        }

        Assert.True(MetalNative.TryCompileLibrary(shader.Source, out var library, out var compileError),
            $"[{name}] {compileError}\n{shader.Source}");
        Assert.True(MetalNative.TryExecuteWithArgumentBuffer(library, shader.EntryPoint,
            new byte[backingBytes], new byte[PushData.ByteSize], shader.ArgumentLayout!, 1,
            out var result, out var runError), $"[{name}] {runError}");
        return result;
    }
}
