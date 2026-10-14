// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Text.RegularExpressions;
using SharpEmu.ShaderCompiler.Resources;
using Xunit;
using Xunit.Abstractions;

namespace SharpEmu.ShaderCompiler.Metal.Tests;

// Memory, image and cross-lane semantics the translator used to get wrong silently: operand
// overlap on device-descriptor scalar loads, LOD clamp, A16 address packing, UNRM, wave64
// bpermute, swizzled buffers and S_SETREG on MODE.
public sealed class Gen5MslMemoryImageSemanticsTests(ITestOutputHelper output)
{
    private const uint SEndpgm = 0xBF810000;

    // ---- S_BUFFER_LOAD through a device-resolved V# whose destination overlaps it ----

    [Fact]
    public void WideScalarBufferLoadSnapshotsTheDescriptorBeforeWritingAnyDestination()
    {
        // s_buffer_load_dwordx4 s[12:15], s[12:15], 0 on a descriptor the draw cannot bind.
        var program = new Gen5ShaderProgram(0x2000, [
            ReadFirstLane(0, 12, 0),
            MoveScalar(4, 13, 0),
            MoveScalar(8, 14, 16),
            MoveScalar(12, 15, 0),
            ScalarBufferLoad(16, resourceRegister: 12, destination: 12, count: 4),
            new Gen5ShaderInstruction(24, Gen5ShaderEncoding.Sopp, "SEndpgm", [SEndpgm], [], [], null),
        ]);
        var request = Gen5ComputeFixtures.RequestOrThrow(
            program, ShaderStage.Compute, localSizeX: 1,
            runtimeInputs: new ResourceRuntimeInputs { UserData = new uint[16] });
        Assert.True(Gen5MslTranslator.TryCompileProgram(request, out var shader, out var error), error);

        var source = shader!.Source;
        // The load's own first destination store is the last write of s12 (the setup moves precede it).
        var firstStore = Regex.Matches(source, @"(?m)^\s*s12 = ").Last();
        var afterStores = source[firstStore.Index..];
        // Every read of the descriptor words (s12..s14) has to precede the first destination store:
        // after it they hold loaded data, not the V#.
        var staleReads = Regex.Matches(afterStores, @"\bs1[234]\b(?!\s*=[^=])")
            .Select(match => afterStores.Substring(Math.Max(0, match.Index - 40), 60))
            .ToArray();
        Assert.True(staleReads.Length == 0, "descriptor re-read after a destination store: " + string.Join(" | ", staleReads));
    }

    // ---- S_SETREG_B32 ----

    [Theory]
    [InlineData(0x0A01u, false)] // hwreg(MODE, 8, 2): DX10_CLAMP and IEEE, honoured via launch state
    [InlineData(0x0201u, false)] // hwreg(MODE, 8, 1)
    [InlineData(0x1901u, true)]  // hwreg(MODE, 4, 4): FP_DENORM, not honoured anywhere in this translator
    [InlineData(0x0003u, true)]  // hwreg(TRAPSTS): no effect on results
    public void SetregOfModeIeeeOrClampIsRefusedAndOtherFieldsRemainNoOps(uint simm16, bool accepted)
    {
        var fixture = new Gen5ComputeFixture("setreg",
            [0xB9840000u | simm16, SEndpgm], 8, 16); // s_setreg_b32 hwreg(...), s4
        var program = Gen5ComputeFixtures.DecodeOrThrow(fixture);
        Assert.Contains(program.Instructions, static instruction => instruction.Opcode == "SSetregB32");
        var request = Gen5ComputeFixtures.RequestOrThrow(program, ShaderStage.Compute);
        var compiled = Gen5MslTranslator.TryCompileProgram(request, out _, out var error);
        Assert.Equal(accepted, compiled);
        if (!accepted)
        {
            Assert.Contains("MODE.IEEE/DX10_CLAMP", error);
        }
    }

    // ---- buffer swizzle / add_tid ----

    [Theory]
    [InlineData(false, false, true)]
    [InlineData(true, false, false)]  // V# word1 SWIZZLE_ENABLE
    [InlineData(false, true, false)]  // V# word3 ADD_TID_ENABLE
    public void SwizzledOrAddTidBuffersFailTranslationInsteadOfAddressingLinearly(bool swizzle, bool addTid, bool accepted)
    {
        // buffer_load_dword v4, v1, s[8:11], 0 offen
        var fixture = new Gen5ComputeFixture("swizzled-load", [0xE0303000, 0x80020401, SEndpgm], 8, 64);
        var userData = new uint[16];
        userData[8] = 0x200000;
        userData[9] = (16u << 16) | (swizzle ? 1u << 31 : 0u);
        userData[10] = 64;
        userData[11] = DescriptorConstants.IdentityDestinationSelect | (addTid ? 1u << 23 : 0u);
        var request = Gen5ComputeFixtures.RequestOrThrow(Gen5ComputeFixtures.DecodeOrThrow(fixture), ShaderStage.Compute,
            runtimeInputs: new ResourceRuntimeInputs { UserData = userData });
        var compiled = Gen5MslTranslator.TryCompileProgram(request, out _, out var error);
        Assert.Equal(accepted, compiled);
        if (!accepted)
        {
            Assert.Contains("swizzled or add_tid", error);
        }
    }

    // ---- DS_BPERMUTE_B32 under wave64 ----

    [Fact]
    public void Wave64BpermuteShufflesWithinTheSimdgroupAndStaysInItsOwnHalf()
    {
        var shader = Gen5ComputeFixtures.CompileRequestOrThrow(Wave64Bpermute, waveLaneCount: 64, localSizeX: 64);
        // simd_shuffle takes a lane within the 32-wide simdgroup; 32..63 is out of range.
        Assert.Matches(@"simd_shuffle\([^\n]*\(ushort\)\(t\d+ & 31u\)\)", shader.Source);
        if (!RequireDevice()) return;

        Assert.True(MetalNative.TryCompileLibrary(shader.Source, out var library, out var compileError), compileError);
        Assert.True(MetalNative.TryExecuteWithArgumentBuffer(library, shader.EntryPoint, new byte[256],
            new byte[PushData.ByteSize], shader.ArgumentLayout!, 64, out var result, out var runError), runError);
        // Every lane reads byte offset 12 = index 3 of its own half: lane 3 below, lane 35 above.
        for (var lane = 0; lane < 64; lane++)
        {
            Assert.Equal((uint)((lane & ~31) | 3), BinaryPrimitives.ReadUInt32LittleEndian(result.AsSpan(lane * 4)));
        }
    }

    // ---- image sampling: LOD clamp, A16, UNRM ----

    [Fact]
    public void SampleClHonoursTheLodClamp()
    {
        // image_sample_cl on a 2D texture: v0/v1 = coordinates, v2 = min-LOD clamp of 1.
        if (!CompileImage("ImageSampleCl", [F(.75f), F(.25f), F(1f)], out var shader)) return;
        // Mip 1 texel (1, 0): the clamp lifts the level that the explicit level(0) would have picked.
        AssertTexel(RunImage(shader), mip: 1, x: 1, y: 0);
    }

    [Theory]
    [InlineData("ImageSampleCl")]
    [InlineData("ImageSampleDCl")]
    [InlineData("ImageSampleBCl")]
    [InlineData("ImageSampleCCl")]
    public void ClampedSamplesCompileInAFragmentFunctionWithMinLodClamp(string opcode)
    {
        Assert.True(Gen5ImageFixtures.TryCompile(opcode, Gen5ImageFixtures.ImageType2D, out var shader, out var error,
            stage: ShaderStage.Pixel), error);
        Assert.Contains("metal::min_lod_clamp(", shader!.Source);
        if (!RequireDevice()) return;
        Assert.True(MetalNative.TryCompileLibrary(shader.Source, out _, out var compileError), compileError + shader.Source);
    }

    [Fact]
    public void A16SampleBiasOccupiesAFullDwordBeforeThePackedCoordinates()
    {
        // image_sample_b A16: v0 = bias half in the low 16 bits, v1 = pack(u, v). The compute stage
        // samples level 0, so the coordinates alone select the texel; reading u from v0's upper
        // half (the old packing) lands on a different one.
        if (!CompileImage("ImageSampleB", [0x00003C00u, PackHalves(.75f, .25f)], out var shader, a16: true)) return;
        AssertTexel(RunImage(shader), mip: 0, x: 3, y: 1);
    }

    [Fact]
    public void A16SampleDerivativesAreFullDwordsAndOnlyCoordinatesArePacked()
    {
        // image_sample_d A16: v0..v3 = f32 dsdh, dtdh, dsdv, dtdv, v4 = pack(u, v). 0.5 uv/pixel
        // over a 4x4 texture is a two-texel footprint, so the level is 1.
        if (!CompileImage("ImageSampleD", [F(.5f), F(0f), F(0f), F(.5f), PackHalves(.75f, .25f)], out var shader, a16: true)) return;
        AssertTexel(RunImage(shader), mip: 1, x: 1, y: 0);
    }

    [Fact]
    public void UnormSampleAddressesTexelsOfMipZero()
    {
        // image_sample ... unorm at (1.5, 2.5) is texel (1, 2), not normalized coordinates.
        if (!CompileImage("ImageSample", [F(1.5f), F(2.5f)], out var shader, unorm: true)) return;
        AssertTexel(RunImage(shader), mip: 0, x: 1, y: 2);
    }

    [Fact]
    public void UnormCubeSampleFailsTranslation()
    {
        Assert.False(Gen5ImageFixtures.TryCompile("ImageSample", Gen5ImageFixtures.ImageTypeCube, out _, out var error,
            unorm: true));
        Assert.Contains("unnormalized-coordinate", error);
    }

    // ---- helpers ----

    private static Gen5ComputeFixture Wave64Bpermute => new("wave64-bpermute",
    [
        0xD7650000, 0x000100C1, // v_mbcnt_lo_u32_b32 v0, -1, 0
        0xD7660000, 0x000200C1, // v_mbcnt_hi_u32_b32 v0, -1, v0        lane 0..63
        0x7E04028C,             // v_mov_b32 v2, 12                      byte index 3
        0xD8000000u | 0xB3u << 18, 3u << 24 | 0u << 8 | 2u, // ds_bpermute_b32 v3, v2, v0
        0xBF8C0000,             // s_waitcnt 0
        0x34080082,             // v_lshlrev_b32 v4, 2, v0
        0xE0701000, 0x80020304, // buffer_store_dword v3, v4, s[8:11], 0 offen
        SEndpgm,
    ], 8, 256);

    private bool CompileImage(string opcode, uint[] address, out Gen5MslShader shader,
        bool a16 = false, bool unorm = false)
    {
        Assert.True(Gen5ImageFixtures.TryCompile(opcode, Gen5ImageFixtures.ImageType2D, out var compiled, out var error,
            addressValues: address, storeResults: true, a16: a16, unorm: unorm), error);
        shader = compiled!;
        if (Environment.GetEnvironmentVariable("SHARPEMU_TEST_REQUIRE_DEVICE") != "1")
        {
            output.WriteLine("Texture execution requires the exclusive Metal device gate.");
            return false;
        }

        Assert.True(MetalNative.IsAvailable, "The required gate needs a Metal device.");
        return true;
    }

    // A 4x4 two-mip 2D texture whose texels encode mip, x and y.
    private static byte[] RunImage(Gen5MslShader shader)
    {
        var texture = MetalNative.CreateFloatTexture(2, 4, 4, 1, 1, [Texels(0), Texels(1)]);
        Assert.True(MetalNative.TryCompileLibrary(shader.Source, out var library, out var error), error + shader.Source);
        Assert.True(MetalNative.TryExecuteWithArgumentBuffer(library, shader.EntryPoint, new byte[16],
            new byte[PushData.ByteSize], shader.ArgumentLayout!, 1, out var result, out error,
            texture, MetalNative.CreatePointSampler()), error);
        return result;
    }

    private static void AssertTexel(byte[] result, uint mip, uint x, uint y)
    {
        var expected = mip * 10000 + y * 10 + x;
        for (var component = 0; component < 4; component++)
        {
            Assert.Equal(F(expected + component), BinaryPrimitives.ReadUInt32LittleEndian(result.AsSpan(component * 4)));
        }
    }

    private static byte[] Texels(uint mip)
    {
        var size = 4u >> (int)mip;
        var bytes = new byte[size * size * 16];
        var offset = 0;
        for (uint y = 0; y < size; y++)
        for (uint x = 0; x < size; x++)
        for (uint component = 0; component < 4; component++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset), F(mip * 10000 + y * 10 + x + component));
            offset += 4;
        }

        return bytes;
    }

    private static uint F(float value) => BitConverter.SingleToUInt32Bits(value);

    private static uint PackHalves(float low, float high) =>
        BitConverter.HalfToUInt16Bits((Half)low) | ((uint)BitConverter.HalfToUInt16Bits((Half)high) << 16);

    private bool RequireDevice()
    {
        if (Environment.GetEnvironmentVariable("SHARPEMU_TEST_REQUIRE_DEVICE") != "1")
        {
            output.WriteLine("Execution requires the exclusive Metal device gate.");
            return false;
        }

        Assert.True(MetalNative.IsAvailable, "The required gate needs a Metal device.");
        return true;
    }

    private static Gen5ShaderInstruction ReadFirstLane(uint pc, uint scalarDestination, uint vectorSource) =>
        new(pc, Gen5ShaderEncoding.Vop1, "VReadfirstlaneB32", [0u],
            [Gen5Operand.Vector(vectorSource)], [Gen5Operand.Scalar(scalarDestination)], null);

    private static Gen5ShaderInstruction MoveScalar(uint pc, uint destination, uint value) =>
        new(pc, Gen5ShaderEncoding.Sop1, "SMovB32", [0u],
            [Gen5Operand.Source(128 + value)], [Gen5Operand.Scalar(destination)], null);

    private static Gen5ShaderInstruction ScalarBufferLoad(uint pc, uint resourceRegister, uint destination, uint count) =>
        new(pc, Gen5ShaderEncoding.Smem, $"SBufferLoadDwordx{count}", [0u, 0u],
            [Gen5Operand.Scalar(resourceRegister), Gen5Operand.Source(125)],
            [.. Enumerable.Range((int)destination, (int)count).Select(index => Gen5Operand.Scalar((uint)index))],
            new Gen5ScalarMemoryControl(count, 0, null));
}
