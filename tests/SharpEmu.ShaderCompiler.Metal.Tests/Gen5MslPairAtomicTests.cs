// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.ShaderCompiler.Resources;
using Xunit;
using Xunit.Abstractions;

namespace SharpEmu.ShaderCompiler.Metal.Tests;

// GLC reads the device-coherent value on every load path, not only raw loads from a bound
// buffer, and a dword pair a guest X2 atomic writes is read whole by a GLC LOAD_DWORDX2
// (Templates/pair_atomic_prelude.msl). Without GLC nothing changes, which is what keeps
// the goldens as they were.
public sealed class Gen5MslPairAtomicTests(ITestOutputHelper output)
{
    private const uint Published = 0x13572468;
    private const uint Sentinel = 0xBADCAFE;
    private const uint Glc = 1u << 14;

    private static bool DeviceRequired => Environment.GetEnvironmentVariable("SHARPEMU_TEST_REQUIRE_DEVICE") == "1";

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void OnlyAGlcDwordx2LoadFromABoundBufferReadsThePairUnderItsLock(bool glc)
    {
        var source = CompileBound(BoundLoad(0x0D, glc, dataRegister: 4, storeCount: 2));
        Assert.Equal(glc, source.Contains("sharpemu_load_pair_coherent(b0,", StringComparison.Ordinal));
        // The lock table is declared only by a program that takes a pair lock.
        Assert.Equal(glc, source.Contains(Gen5MslArgumentLayout.PairLocksName, StringComparison.Ordinal));
    }

    [Fact]
    public void ASwapX2RunsBothDwordsUnderThePairLock()
    {
        var source = CompileBound(new Gen5ComputeFixture("buffer-swap-x2",
        [
            0x7E0202FF, 32,                                  // v_mov_b32 v1, 32
            0x7E080281,                                      // v_mov_b32 v4, 1
            0x7E0A0282,                                      // v_mov_b32 v5, 2
            0xE0000000u | 0x50u << 18 | 1u << 12, 0x80020401, // buffer_atomic_swap_x2 v[4:5], v1, s[8:11] offen
            0xBF810000,                                      // s_endpgm
        ], 8, 64));
        Assert.Contains("sharpemu_pair_try_lock(", source, StringComparison.Ordinal);
        Assert.Contains("sharpemu_pair_unlock(", source, StringComparison.Ordinal);
        Assert.Contains($"device atomic_uint* {Gen5MslArgumentLayout.PairLocksName}", source, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0x08u, true)]  // buffer_load_ubyte
    [InlineData(0x08u, false)]
    [InlineData(0x0Au, true)]  // buffer_load_ushort
    [InlineData(0x00u, true)]  // buffer_load_format_x
    [InlineData(0x00u, false)]
    public void SubwordAndFormatLoadsReadCoherentlyOnlyWithGlc(uint opcode, bool glc)
    {
        var source = CompileBound(BoundLoad(opcode, glc, dataRegister: 4, storeCount: 1));
        Assert.Equal(glc, CoherentCall(source));
        Assert.DoesNotContain(Gen5MslArgumentLayout.PairLocksName, source, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0x0Cu, true, "sharpemu_load_device_dword_coherent(sharpemu_ranges")]
    [InlineData(0x0Cu, false, null)]
    [InlineData(0x0Du, true, "sharpemu_load_device_pair_coherent(sharpemu_ranges")]
    [InlineData(0x0Du, false, null)]
    [InlineData(0x08u, true, "sharpemu_load_device_unaligned_coherent(sharpemu_ranges")]
    public void ALoadThroughADescriptorReadFromMemoryReadsCoherentlyOnlyWithGlc(uint opcode, bool glc, string? call)
    {
        // The V# comes from an s_load at a runtime address, so the load has no host binding
        // and goes through the device page table.
        var source = Compile(new Gen5ComputeFixture("device-descriptor-load",
        [
            0x7E080500,                                                 // v_readfirstlane_b32 s4, v0
            0xBE850380,                                                 // s_mov_b32 s5, 0
            0xF4080202, 0xFA000000,                                     // s_load_dwordx4 s[8:11], s[4:5], 0
            0xBF8CC07F,                                                 // s_waitcnt lgkmcnt(0)
            0xE0000000u | opcode << 18 | (glc ? Glc : 0u), 0x80020400,  // buffer_load_* v4, off, s[8:11], 0 [glc]
            0xBF810000,                                                 // s_endpgm
        ], 8, 64), runtimeInputs: null);
        Assert.Equal(glc, CoherentCall(source));
        if (call is not null) Assert.Contains(call, source, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(12u, true, "sharpemu_load_device_dword_coherent(sharpemu_ranges")]
    [InlineData(12u, false, null)]
    [InlineData(13u, true, "sharpemu_load_device_pair_coherent(sharpemu_ranges")]
    [InlineData(13u, false, null)]
    [InlineData(8u, true, "sharpemu_load_device_unaligned_coherent(sharpemu_ranges")]
    public void AGlobalLoadReadsCoherentlyOnlyWithGlc(uint opcode, bool glc, string? call)
    {
        // global_load_* v[8:], v[9:10], off [glc]; GLC is bit 16 of a FLAT/GLOBAL word.
        var source = Compile(new Gen5ComputeFixture("global-load",
            [0xDC00_8000u | opcode << 18 | (glc ? 1u << 16 : 0u), 0x0810_0309, 0xBF81_0000], 16, 64), runtimeInputs: null);
        Assert.Equal(glc, CoherentCall(source));
        if (call is not null) Assert.Contains(call, source, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TheScalarMemoryGlcBitIsDecoded(bool glc)
    {
        var program = Gen5ComputeFixtures.DecodeOrThrow(ScalarBufferLoad(glc));
        var control = Assert.IsType<Gen5ScalarMemoryControl>(program.Instructions[0].Control);
        Assert.Equal(glc, control.Glc);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AGlcScalarBufferLoadReadsTheDeviceCoherentWord(bool glc)
    {
        var source = CompileBound(ScalarBufferLoad(glc));
        Assert.Equal(glc, source.Contains("sharpemu_load_dword_coherent(b0,", StringComparison.Ordinal));
        if (!DeviceRequired)
        {
            output.WriteLine("Buffer readback requires the exclusive Metal device gate.");
            return;
        }

        var initial = SentinelBuffer(64);
        BinaryPrimitives.WriteUInt32LittleEndian(initial.AsSpan(32), Published);
        var expected = initial.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(expected.AsSpan(48), Published);
        Assert.Equal(expected, Execute(CompileBoundShader(ScalarBufferLoad(glc), 64), initial, 1, out _));
    }

    [Fact]
    public void AGlcDwordx2LoadReturnsBothWordsOfThePair()
    {
        var shader = CompileBoundShader(BoundLoad(0x0D, glc: true, dataRegister: 4, storeCount: 2), 64);
        if (!DeviceRequired)
        {
            output.WriteLine("Buffer readback requires the exclusive Metal device gate.");
            return;
        }

        var initial = SentinelBuffer(64);
        BinaryPrimitives.WriteUInt32LittleEndian(initial.AsSpan(32), Published);
        BinaryPrimitives.WriteUInt32LittleEndian(initial.AsSpan(36), ~Published);
        var expected = initial.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(expected.AsSpan(48), Published);
        BinaryPrimitives.WriteUInt32LittleEndian(expected.AsSpan(52), ~Published);
        Assert.Equal(expected, Execute(shader, initial, 1, out _));
    }

    // GTA V's decoupled look-back scan in miniature, across many workgroups at once. Each
    // workgroup takes a ticket t, publishes the pair at slot t twice with SWAP_X2 -
    // (2t+1, 2t+1) and then (2t+2, 2t+2) - and polls its predecessor's slot with a GLC
    // LOAD_DWORDX2 until that slot reaches its final value 2t. Every pair ever published
    // has equal halves, so a poll that sees two different halves saw a torn pair; each
    // workgroup ORs low ^ high over every poll and stores it.
    [Fact]
    public void PollersAcrossWorkgroupsNeverSeeATornPublishedPair()
    {
        const uint groups = 64;
        const int bufferBytes = 4096;
        var shader = CompileBoundShader(LookBackFixture(), bufferBytes, localSizeX: 32);
        Assert.Contains("sharpemu_load_pair_coherent(b0,", shader.Source, StringComparison.Ordinal);
        Assert.Contains("sharpemu_pair_try_lock(", shader.Source, StringComparison.Ordinal);
        if (!DeviceRequired)
        {
            output.WriteLine("Multi-workgroup execution requires the exclusive Metal device gate.");
            return;
        }

        var initial = new byte[bufferBytes];
        var result = Execute(shader, initial, groups, out var stepCapHits);
        Assert.Equal(0u, stepCapHits);
        Assert.Equal(groups, BinaryPrimitives.ReadUInt32LittleEndian(result));
        for (var ticket = 0u; ticket < groups; ticket++)
        {
            var slot = (int)(64 + ticket * 8);
            Assert.Equal(2 * ticket + 2, BinaryPrimitives.ReadUInt32LittleEndian(result.AsSpan(slot)));
            Assert.Equal(2 * ticket + 2, BinaryPrimitives.ReadUInt32LittleEndian(result.AsSpan(slot + 4)));
            Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(result.AsSpan((int)(1024 + ticket * 4))));
            Assert.Equal(2 * ticket, BinaryPrimitives.ReadUInt32LittleEndian(result.AsSpan((int)(2048 + ticket * 4))));
        }
    }

    private static Gen5ComputeFixture LookBackFixture() =>
        new("look-back-pairs",
        [
            0xBEFE0381,                         // s_mov_b32 exec_lo, 1
            0x7E080281,                         // v_mov_b32 v4, 1
            0xE0C84000, 0x80020400,             // buffer_atomic_add v4, off, s[8:11], 0 glc   (v4 = ticket t)
            0xBF8C3F70,                         // s_waitcnt vmcnt(0)
            0x34040883,                         // v_lshlrev_b32 v2, 3, v4     (8t)
            0x341A0882,                         // v_lshlrev_b32 v13, 2, v4    (4t)
            0x34180881,                         // v_lshlrev_b32 v12, 1, v4    (2t, the predecessor's final value)
            0x4A0C1881,                         // v_add_nc_u32 v6, 1, v12
            0x7E0E0306,                         // v_mov_b32 v7, v6
            0xE1401040, 0x80020602,             // buffer_atomic_swap_x2 v[6:7], v2, s[8:11], 0 offen offset:64
            0x4A0C1882,                         // v_add_nc_u32 v6, 2, v12
            0x7E0E0306,                         // v_mov_b32 v7, v6
            0xE1401040, 0x80020602,             // buffer_atomic_swap_x2 v[6:7], v2, s[8:11], 0 offen offset:64
            0x7E160280,                         // v_mov_b32 v11, 0
            0xE0345038, 0x80020802,             // poll: buffer_load_dwordx2 v[8:9], v2, s[8:11], 0 offen offset:56 glc
            0xBF8C3F70,                         // s_waitcnt vmcnt(0)
            0x3A141308,                         // v_xor_b32 v10, v8, v9
            0x3816150B,                         // v_or_b32 v11, v11, v10
            0x7D821908,                         // v_cmp_lt_u32 vcc, v8, v12
            0xBF87FFF9,                         // s_cbranch_vccnz poll
            0xE0701400, 0x80020B0D,             // buffer_store_dword v11, v13, s[8:11], 0 offen offset:1024
            0xE0701800, 0x8002080D,             // buffer_store_dword v8, v13, s[8:11], 0 offen offset:2048
            0xBF810000,                         // s_endpgm
        ], 8, 4096);

    // buffer_load_* v[dataRegister:], v1(=32), s[8:11] offen [glc], stored back at 48.
    private static Gen5ComputeFixture BoundLoad(uint opcode, bool glc, uint dataRegister, int storeCount)
    {
        var words = new List<uint>
        {
            0x7E0202FF, 32,                                                // v_mov_b32 v1, 32
            0xE0000000u | opcode << 18 | (glc ? Glc : 0u) | 1u << 12,       // buffer_load_* v[4:], v1, s[8:11], 0 offen [glc]
            0x80020001 | dataRegister << 8,
            0xBF8C3F70,                                                    // s_waitcnt vmcnt(0)
        };
        for (var index = 0u; index < storeCount; index++)
        {
            words.Add(0xE0700030 + index * 4);                             // buffer_store_dword v[4 + index], off, offset:48 + 4 * index
            words.Add(0x80020000 | (dataRegister + index) << 8);
        }

        words.Add(0xBF810000);                                             // s_endpgm
        return new("bound-load", words.ToArray(), 8, 64);
    }

    private static Gen5ComputeFixture ScalarBufferLoad(bool glc) =>
        new("scalar-buffer-load",
        [
            0xF4200404u | (glc ? 1u << 16 : 0u), 0xFA000020, // s_buffer_load_dword s16, s[8:11], 0x20 [glc]
            0xBF8CC07F,                                      // s_waitcnt lgkmcnt(0)
            0x7E080210,                                      // v_mov_b32 v4, s16
            0xE0700030, 0x80020400,                          // buffer_store_dword v4, off, s[8:11], 0 offset:48
            0xBF810000,                                      // s_endpgm
        ], 8, 64);

    private static bool CoherentCall(string source) =>
        System.Text.RegularExpressions.Regex.IsMatch(source, @"=[^;\r\n]*sharpemu_load_\w*coherent\(");

    private static string CompileBound(Gen5ComputeFixture fixture) => CompileBoundShader(fixture, 64).Source;

    private static Gen5MslShader CompileBoundShader(Gen5ComputeFixture fixture, uint bytes, uint localSizeX = 32)
    {
        var userData = new uint[16];
        userData[8] = 0x200000;
        userData[10] = bytes;
        userData[11] = DescriptorConstants.IdentityDestinationSelect;
        return CompileShader(fixture, new ResourceRuntimeInputs { UserData = userData }, localSizeX);
    }

    private static string Compile(Gen5ComputeFixture fixture, ResourceRuntimeInputs? runtimeInputs) =>
        CompileShader(fixture, runtimeInputs, 1).Source;

    private static Gen5MslShader CompileShader(Gen5ComputeFixture fixture, ResourceRuntimeInputs? runtimeInputs, uint localSizeX)
    {
        var request = Gen5ComputeFixtures.RequestOrThrow(Gen5ComputeFixtures.DecodeOrThrow(fixture), ShaderStage.Compute,
            localSizeX: localSizeX, runtimeInputs: runtimeInputs);
        Assert.True(Gen5MslTranslator.TryCompileProgram(request, out var shader, out var error), error);
        if (DeviceRequired)
        {
            // The emitted source must be valid Metal on every path.
            Assert.True(MetalNative.TryCompileLibrary(shader!.Source, out var library, out var compileError), compileError + "\n" + shader.Source);
            if (library != 0) MetalNative.ReleaseCollisionLibrary(library);
        }

        return shader!;
    }

    private static byte[] SentinelBuffer(int bytes)
    {
        var buffer = new byte[bytes];
        for (var offset = 0; offset < bytes; offset += 4)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(offset), Sentinel);
        }

        return buffer;
    }

    private static byte[] Execute(Gen5MslShader shader, byte[] initial, uint groups, out uint stepCapHits)
    {
        Assert.True(MetalNative.IsAvailable);
        Assert.True(MetalNative.TryCompileLibrary(shader.Source, out var library, out var compileError), compileError);
        try
        {
            Assert.True(MetalNative.TryExecuteWithArgumentBuffer(library, shader.EntryPoint, initial,
                new byte[PushData.ByteSize], shader.ArgumentLayout!, 32, out var result, out stepCapHits, out var runError,
                threadgroupCount: groups), runError);
            return result;
        }
        finally
        {
            MetalNative.ReleaseCollisionLibrary(library);
        }
    }
}
