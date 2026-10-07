// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.ShaderCompiler.Resources;
using Xunit;
using Xunit.Abstractions;

namespace SharpEmu.ShaderCompiler.Metal.Tests;

public sealed class Gen5MslBufferAtomicTests(ITestOutputHelper output)
{
    private const uint Sentinel = 0xBADCAFE;
    private const uint Before = 0xFEDCBA98;
    private const uint Replacement = 0x11223344;

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void SwapDecoderAndDenseBufferCodegenPreserveItsOperandContract(bool glc)
    {
        var fixture = Fixture(glc, 1, 8, 4, indexed: true);
        var program = Gen5ComputeFixtures.DecodeOrThrow(fixture);
        var instruction = Assert.Single(program.Instructions.Where(static instruction => instruction.Opcode == "BufferAtomicSwap"));
        var control = Assert.IsType<Gen5BufferMemoryControl>(instruction.Control);
        Assert.Equal(1u, control.DwordCount);
        Assert.Equal(4u, control.VectorData);
        Assert.Equal(0u, control.VectorAddress);
        Assert.Equal(8u, control.ScalarResource);
        Assert.Equal(4, control.OffsetBytes);
        Assert.True(control.IndexEnabled);
        Assert.True(control.OffsetEnabled);
        Assert.Equal(glc, control.Glc);
        var shader = Compile(fixture, 16);
        Assert.Contains("atomic_exchange_explicit", shader.Source);
        Assert.Contains("v[4], memory_order_relaxed", shader.Source);
        Assert.Contains("(v[0] * 0x10u)", shader.Source);
        Assert.Contains("4u <= sharpemu_resources.buffer_bytes[0]", shader.Source);
        Assert.Contains("sharpemu_resources.buffer_bytes[0] - 4u", shader.Source);
        Assert.DoesNotContain("+ 4u <= sharpemu_resources.buffer_bytes", shader.Source);
    }

    [Theory]
    [InlineData(0x3Cu, "BufferAtomicInc")]
    [InlineData(0x3Du, "BufferAtomicDec")]
    [InlineData(0x50u, "BufferAtomicSwapX2")]
    public void UnqualifiedAtomicVariantsRemainExplicitlyUnsupported(uint opcode, string name)
    {
        var fixture = Fixture(true, 0, 0, 0, indexed: false, opcode);
        var request = Request(fixture, 0);
        Assert.False(Gen5MslTranslator.TryCompileProgram(request, out _, out var error));
        Assert.Contains(name, error);
        Assert.Contains("unsupported memory opcode", error);
    }

    [Fact]
    public void DecodedUnsignedMaxUsesTheUnsignedNativeAtomic()
    {
        var fixture = Fixture(true, 1, 8, 4, indexed: true, opcode: 0x38);
        var instruction = Assert.Single(Gen5ComputeFixtures.DecodeOrThrow(fixture).Instructions
            .Where(static candidate => candidate.Opcode == "BufferAtomicUmax"));
        Assert.IsType<Gen5BufferMemoryControl>(instruction.Control);
        Assert.Contains("atomic_fetch_max_explicit((device atomic_uint*)", Compile(fixture, 16).Source);
        if (!RequireDevice()) return;
        var initial = SentinelBuffer(32);
        var expected = initial.ToArray();
        // Before has its sign bit set: signed max would incorrectly select
        // Replacement, while unsigned max must leave Before in memory.
        BinaryPrimitives.WriteUInt32LittleEndian(expected.AsSpan(48), Before);
        Assert.Equal(expected, Execute(Compile(fixture, 16), initial));
    }

    [Fact]
    public void SwapCapturesAliasedAddressAndReplacementBeforeWritingReturnedVdata()
    {
        var fixture = new Gen5ComputeFixture("atomic-aliased-vdata",
            [0x7E0802FF, 32, 0xE0C05000, 0x80020404, 0xE0700030, 0x80020400, 0xBF810000], 8, 64);
        var instruction = Assert.Single(Gen5ComputeFixtures.DecodeOrThrow(fixture).Instructions
            .Where(static candidate => candidate.Opcode == "BufferAtomicSwap"));
        var control = Assert.IsType<Gen5BufferMemoryControl>(instruction.Control);
        Assert.Equal(4u, control.VectorData);
        Assert.Equal(4u, control.VectorAddress);
        Assert.True(control.Glc);
        var shader = Compile(fixture, 0);
        if (!RequireDevice()) return;
        var initial = SentinelBuffer(32);
        var expected = initial.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(expected.AsSpan(32), 32u);
        BinaryPrimitives.WriteUInt32LittleEndian(expected.AsSpan(48), Before);
        Assert.Equal(expected, Execute(shader, initial));
    }

    [Theory]
    [InlineData(true, 1u, 8u, 4u, true, true, 32, true)]
    [InlineData(false, 1u, 8u, 4u, true, true, 32, true)]
    [InlineData(true, 0u, 60u, 0u, false, true, 60, true)]
    [InlineData(true, 0u, 64u, 0u, false, true, 0, false)]
    [InlineData(true, 0u, 0xFFFFFFFCu, 0u, false, true, 0, false)]
    [InlineData(true, 0u, 3u, 0u, false, true, 0, false)]
    [InlineData(true, 1u, 8u, 4u, true, false, 32, false)]
    public void SwapExecutesWithIndependentAddressReturnAndSentinelOracle(bool glc, uint index,
        uint offset, uint immediate, bool indexed, bool active, int target, bool writes)
    {
        // Do not touch a device outside the shared exclusive test/game window.
        if (!RequireDevice()) return;
        var fixture = Fixture(glc, index, offset, immediate, indexed, active: active);
        var shader = Compile(fixture, indexed ? 16u : 0u);
        var initial = SentinelBuffer(target);
        var expected = initial.ToArray();
        if (writes) BinaryPrimitives.WriteUInt32LittleEndian(expected.AsSpan(target), Replacement);
        // GLC returns the pre-operation word into VDATA; disabled EXEC leaves it
        // untouched, while an out-of-range active returning access reads zero.
        var returned = !active || !glc ? Replacement : writes ? Before : 0u;
        BinaryPrimitives.WriteUInt32LittleEndian(expected.AsSpan(48), returned);
        Assert.Equal(expected, Execute(shader, initial));
    }

    private bool RequireDevice()
    {
        if (Environment.GetEnvironmentVariable("SHARPEMU_TEST_REQUIRE_DEVICE") != "1")
        {
            output.WriteLine("Atomic readback requires the exclusive Metal device gate.");
            return false;
        }
        Assert.True(MetalNative.IsAvailable);
        return true;
    }

    private static byte[] SentinelBuffer(int target)
    {
        var initial = new byte[64];
        for (var byteIndex = 0; byteIndex < initial.Length; byteIndex += 4)
            BinaryPrimitives.WriteUInt32LittleEndian(initial.AsSpan(byteIndex), Sentinel);
        BinaryPrimitives.WriteUInt32LittleEndian(initial.AsSpan(target), Before);
        return initial;
    }

    private static byte[] Execute(Gen5MslShader shader, byte[] initial)
    {
        Assert.True(MetalNative.TryCompileLibrary(shader.Source, out var library, out var compileError), compileError);
        Assert.True(MetalNative.TryExecuteWithArgumentBuffer(library, shader.EntryPoint, initial,
            new byte[PushData.ByteSize], shader.ArgumentLayout!, 1, out var result, out var runError), runError);
        return result;
    }

    private static Gen5MslShader Compile(Gen5ComputeFixture fixture, uint stride)
    {
        Assert.True(Gen5MslTranslator.TryCompileProgram(Request(fixture, stride), out var shader, out var error), error);
        return shader!;
    }

    private static ShaderCompileRequest Request(Gen5ComputeFixture fixture, uint stride)
    {
        var userData = new uint[16];
        userData[8] = 0x200000;
        userData[9] = stride << 16;
        userData[10] = 64;
        userData[11] = DescriptorConstants.IdentityDestinationSelect;
        return Gen5ComputeFixtures.RequestOrThrow(Gen5ComputeFixtures.DecodeOrThrow(fixture), ShaderStage.Compute,
            runtimeInputs: new ResourceRuntimeInputs { UserData = userData });
    }

    private static Gen5ComputeFixture Fixture(bool glc, uint index, uint offset,
        uint immediate, bool indexed, uint opcode = 0x30, bool active = true)
    {
        var words = new List<uint>
        {
            0x7E0002FF, index,       // v_mov_b32 v0, index
            0x7E0202FF, offset,      // v_mov_b32 v1, offset
            0x7E0802FF, Replacement, // v_mov_b32 v4, replacement
        };
        if (!active) words.Add(0xBEFE0380); // s_mov_b32 exec_lo, 0
        // RDNA2 MUBUF: GLC bit14; IDXEN bit13; OFFEN bit12. For indexed
        // fixture addr=imm4 + scalar4 + vectorOffset8 + index1*stride16=32.
        words.Add(0xE0000000u | opcode << 18 | (glc ? 1u << 14 : 0) |
            (indexed ? 1u << 13 : 0) | 1u << 12 | immediate);
        words.Add(((indexed ? 0x84u : 0x80u) << 24) | 2u << 16 | 4u << 8 | (indexed ? 0u : 1u));
        if (!active) words.Add(0xBEFE03C1); // restore EXEC for result store
        words.AddRange([0xE0700030, 0x80020400, 0xBF810000]); // buffer_store_dword v4 at48; endpgm
        return new("buffer-atomic", words.ToArray(), 8, 64);
    }
}
