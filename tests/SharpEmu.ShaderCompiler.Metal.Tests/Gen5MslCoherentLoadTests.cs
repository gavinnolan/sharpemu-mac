// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.ShaderCompiler.Resources;
using Xunit;
using Xunit.Abstractions;

namespace SharpEmu.ShaderCompiler.Metal.Tests;

// GLC on a MUBUF load reads the device-coherent level (RDNA2 ISA 8.1.10). GTA V's
// decoupled look-back scan (compute 0x35B85620669D35A4) polls the flag a predecessor
// workgroup publishes with BUFFER_LOAD_DWORDX2 glc; as a plain Metal load the poll could
// keep reading a stale cached copy until the dispatcher step cap killed the workgroup,
// leaving the scan's outputs unwritten.
public sealed class Gen5MslCoherentLoadTests(ITestOutputHelper output)
{
    private const uint Sentinel = 0xBADCAFE;
    private const uint Published = 0x13572468;

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void OnlyAGlcLoadReadsThroughTheCoherentHelper(bool glc)
    {
        var shader = Compile(Fixture(glc, 32));
        Assert.Equal(glc, shader.Source.Contains("sharpemu_load_dword_coherent(b0,", StringComparison.Ordinal));
        Assert.Equal(glc, shader.Source.Contains("static inline uint sharpemu_load_dword_coherent", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(true, 32u, Published)]
    [InlineData(false, 32u, Published)]
    // Past num_records the coherent load reads zero, as the plain one does.
    [InlineData(true, 64u, 0u)]
    public void GlcLoadReturnsTheSameWordAsAPlainLoad(bool glc, uint offset, uint expectedWord)
    {
        var shader = Compile(Fixture(glc, offset));
        if (Environment.GetEnvironmentVariable("SHARPEMU_TEST_REQUIRE_DEVICE") != "1")
        {
            output.WriteLine("Buffer readback requires the exclusive Metal device gate.");
            return;
        }

        Assert.True(MetalNative.IsAvailable);
        var initial = new byte[64];
        for (var byteIndex = 0; byteIndex < initial.Length; byteIndex += 4)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(initial.AsSpan(byteIndex), Sentinel);
        }

        BinaryPrimitives.WriteUInt32LittleEndian(initial.AsSpan(32), Published);
        var expected = initial.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(expected.AsSpan(48), expectedWord);
        Assert.True(MetalNative.TryCompileLibrary(shader.Source, out var library, out var compileError), compileError);
        Assert.True(MetalNative.TryExecuteWithArgumentBuffer(library, shader.EntryPoint, initial,
            new byte[PushData.ByteSize], shader.ArgumentLayout!, 1, out var result, out var runError), runError);
        Assert.Equal(expected, result);
    }

    private static Gen5MslShader Compile(Gen5ComputeFixture fixture)
    {
        var userData = new uint[16];
        userData[8] = 0x200000;
        userData[10] = 64;
        userData[11] = DescriptorConstants.IdentityDestinationSelect;
        var request = Gen5ComputeFixtures.RequestOrThrow(Gen5ComputeFixtures.DecodeOrThrow(fixture), ShaderStage.Compute,
            runtimeInputs: new ResourceRuntimeInputs { UserData = userData });
        Assert.True(Gen5MslTranslator.TryCompileProgram(request, out var shader, out var error), error);
        return shader!;
    }

    private static Gen5ComputeFixture Fixture(bool glc, uint offset) =>
        new("buffer-load-glc",
        [
            0x7E0202FF, offset,                                               // v_mov_b32 v1, offset
            0xE0000000u | 0x0Cu << 18 | (glc ? 1u << 14 : 0u) | 1u << 12,     // buffer_load_dword v4, v1, s[8:11], 0 offen [glc]
            0x80020401,
            0xBF8C3F70,                                                       // s_waitcnt vmcnt(0)
            0xE0700030, 0x80020400,                                           // buffer_store_dword v4, off, s[8:11], 0 offset:48
            0xBF810000,                                                       // s_endpgm
        ], 8, 64);
}
