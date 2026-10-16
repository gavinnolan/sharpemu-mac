// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.ShaderCompiler.Resources;
using Xunit;
using Xunit.Abstractions;

namespace SharpEmu.ShaderCompiler.Metal.Tests;

/// <summary>
/// Gathers, compare samples, texel offsets and TFE executed on the device against a C#
/// reference over an 8x8 four-level RGBA32F texture whose every texel and channel is
/// distinct. The host sampler is point-filtered with clamp-to-edge addressing, so the
/// reference clamps footprint texels to the level.
/// </summary>
public sealed class MetalImageGatherExecutionTests(ITestOutputHelper output)
{
    private const uint Size = 8;
    private const uint Levels = 4;
    private const float U = 0.40f;
    private const float V = 0.65f;

    /// <summary>
    /// opcode, address dwords (offsets, bias, reference, x, y, LOD or clamp as the opcode
    /// lays them out), dmask, the level the footprint is read from, x offset, y offset.
    /// Compute has no quad derivatives, so an implicit-LOD gather reads level 0 whatever
    /// its bias, and a _CL clamp raises it to the clamp.
    /// </summary>
    public static IEnumerable<object[]> GatherCases => new List<object[]>
    {
        new object[] { "ImageGather4", new[] { F(U), F(V) }, 1u, 0u, 0, 0 },
        new object[] { "ImageGather4", new[] { F(U), F(V) }, 4u, 0u, 0, 0 },
        new object[] { "ImageGather4Lz", new[] { F(U), F(V) }, 2u, 0u, 0, 0 },
        new object[] { "ImageGather4L", new[] { F(U), F(V), F(1f) }, 1u, 1u, 0, 0 },
        new object[] { "ImageGather4L", new[] { F(U), F(V), F(1.4f) }, 1u, 1u, 0, 0 },
        new object[] { "ImageGather4L", new[] { F(U), F(V), F(1.6f) }, 8u, 2u, 0, 0 },
        new object[] { "ImageGather4L", new[] { F(U), F(V), F(9f) }, 1u, 3u, 0, 0 },
        new object[] { "ImageGather4O", new[] { Offset(2, 1), F(U), F(V) }, 1u, 0u, 2, 1 },
        new object[] { "ImageGather4O", new[] { Offset(-8, 7), F(U), F(V) }, 1u, 0u, -8, 7 },
        new object[] { "ImageGather4O", new[] { Offset(9, -3), F(U), F(V) }, 1u, 0u, 9, -3 },
        new object[] { "ImageGather4O", new[] { Offset(-12, 31), F(U), F(V) }, 1u, 0u, -12, 31 },
        new object[] { "ImageGather4LzO", new[] { Offset(-1, 1), F(U), F(V) }, 1u, 0u, -1, 1 },
        new object[] { "ImageGather4LO", new[] { Offset(3, -2), F(U), F(V), F(1f) }, 1u, 1u, 3, -2 },
        new object[] { "ImageGather4LO", new[] { Offset(-20, 0), F(U), F(V), F(2f) }, 1u, 2u, -20, 0 },
        new object[] { "ImageGather4B", new[] { F(2f), F(U), F(V) }, 1u, 0u, 0, 0 },
        new object[] { "ImageGather4BO", new[] { Offset(1, 0), F(2f), F(U), F(V) }, 1u, 0u, 1, 0 },
        new object[] { "ImageGather4Cl", new[] { F(U), F(V), F(1f) }, 1u, 1u, 0, 0 },
        new object[] { "ImageGather4ClO", new[] { Offset(0, 1), F(U), F(V), F(1f) }, 1u, 1u, 0, 1 },
        new object[] { "ImageGather4BCl", new[] { F(0.5f), F(U), F(V), F(2f) }, 1u, 2u, 0, 0 },
        new object[] { "ImageGather4BClO", new[] { Offset(1, 1), F(0.5f), F(U), F(V), F(1f) }, 1u, 1u, 1, 1 },
    };

    [Theory]
    [MemberData(nameof(GatherCases))]
    public void GatherReturnsTheFootprintOfTheSelectedLevelAndOffset(
        string opcode, uint[] address, uint dmask, uint level, int offsetX, int offsetY)
    {
        Assert.True(Gen5ImageFixtures.TryCompile(opcode, Gen5ImageFixtures.ImageType2D, out var shader, out var error,
            addressValues: address, storeResults: true, dmask: dmask), error);
        if (!DeviceGate())
        {
            return;
        }

        var result = Execute(shader!, 4);
        var component = (uint)System.Numerics.BitOperations.TrailingZeroCount(dmask);
        var expected = Footprint(level, U, V, offsetX, offsetY);
        for (var texel = 0; texel < 4; texel++)
        {
            Assert.Equal(
                Value(level, expected[texel].X, expected[texel].Y) + component,
                BitConverter.UInt32BitsToSingle(BinaryPrimitives.ReadUInt32LittleEndian(result.AsSpan(texel * 4))));
        }
    }

    /// <summary>
    /// GATHER4_C of a colour format: each footprint texel's first channel is tested with the
    /// S# compare function (reference OP texel). The reference equals the level's (x0, y0)
    /// texel, so less, equal and greater all split the footprint.
    /// </summary>
    [Theory]
    [InlineData("ImageGather4CLz", 0u, 0, 0)]
    [InlineData("ImageGather4CLz", 1u, 0, 0)]
    [InlineData("ImageGather4CLz", 2u, 0, 0)]
    [InlineData("ImageGather4CLz", 3u, 0, 0)]
    [InlineData("ImageGather4CLz", 4u, 0, 0)]
    [InlineData("ImageGather4CLz", 5u, 0, 0)]
    [InlineData("ImageGather4CLz", 6u, 0, 0)]
    [InlineData("ImageGather4CLz", 7u, 0, 0)]
    [InlineData("ImageGather4C", 3u, 0, 0)]
    [InlineData("ImageGather4CL", 6u, 0, 0)]
    [InlineData("ImageGather4CLO", 1u, 2, -1)]
    [InlineData("ImageGather4CLzO", 4u, -10, 0)]
    [InlineData("ImageGather4CBClO", 3u, 1, 1)]
    [InlineData("ImageGather4CCl", 2u, 0, 0)]
    public void ColorCompareGatherTestsEachTexelWithTheSamplerFunction(
        string opcode, uint function, int offsetX, int offsetY)
    {
        var flags = Gen5ImageOpcodes.FlagsOf(opcode);
        var level = (flags & (Gen5ImageOp.Lod | Gen5ImageOp.Clamp)) != 0 ? 1u : 0u;
        var footprint = Footprint(level, U, V, offsetX, offsetY);
        var reference = Value(level, footprint[3].X, footprint[3].Y);
        var address = new List<uint>();
        if ((flags & Gen5ImageOp.Offset) != 0) address.Add(Offset(offsetX, offsetY));
        if ((flags & Gen5ImageOp.Bias) != 0) address.Add(F(0.5f));
        address.Add(F(reference));
        address.AddRange([F(U), F(V)]);
        if ((flags & (Gen5ImageOp.Lod | Gen5ImageOp.Clamp)) != 0) address.Add(F(1f));

        Assert.True(Gen5ImageFixtures.TryCompile(opcode, Gen5ImageFixtures.ImageType2D, out var shader, out var error,
            addressValues: [.. address], storeResults: true, dmask: 1, samplerWords: [function << 12, 0, 0, 0]), error);
        if (!DeviceGate())
        {
            return;
        }

        var result = Execute(shader!, 4);
        for (var texel = 0; texel < 4; texel++)
        {
            var passes = Compare(function, reference, Value(level, footprint[texel].X, footprint[texel].Y));
            Assert.Equal(passes ? 1f : 0f,
                BitConverter.UInt32BitsToSingle(BinaryPrimitives.ReadUInt32LittleEndian(result.AsSpan(texel * 4))));
        }
    }

    /// <summary>SAMPLE_C of a colour format returns the S# function's verdict on the first
    /// channel in x, y and z, and one in w.</summary>
    [Theory]
    [InlineData(0u, -1f)]
    [InlineData(1u, -1f)]
    [InlineData(1u, 0f)]
    [InlineData(2u, 0f)]
    [InlineData(2u, 1f)]
    [InlineData(3u, 0f)]
    [InlineData(4u, 1f)]
    [InlineData(4u, 0f)]
    [InlineData(5u, 0f)]
    [InlineData(5u, 1f)]
    [InlineData(6u, -1f)]
    [InlineData(7u, 1f)]
    public void ColorCompareSampleUsesTheSamplerFunction(uint function, float referenceDelta)
    {
        const uint x = 3, y = 2, level = 1;
        var u = (x + 0.5f) / (Size >> (int)level);
        var v = (y + 0.5f) / (Size >> (int)level);
        var reference = Value(level, x, y) + referenceDelta;
        Assert.True(Gen5ImageFixtures.TryCompile("ImageSampleCL", Gen5ImageFixtures.ImageType2D, out var shader, out var error,
            addressValues: [F(reference), F(u), F(v), F(level)], storeResults: true,
            samplerWords: [function << 12, 0, 0, 0]), error);
        if (!DeviceGate())
        {
            return;
        }

        var result = Execute(shader!, 4);
        var verdict = Compare(function, reference, Value(level, x, y)) ? 1f : 0f;
        float[] expected = [verdict, verdict, verdict, 1f];
        for (var component = 0; component < 4; component++)
        {
            Assert.Equal(expected[component],
                BitConverter.UInt32BitsToSingle(BinaryPrimitives.ReadUInt32LittleEndian(result.AsSpan(component * 4))));
        }
    }

    /// <summary>The _O texel offsets of a sample: native inside -8..7, emulated outside it,
    /// both landing on the offset texel of the sampled level (clamped to its edge).</summary>
    [Theory]
    [InlineData("ImageSampleLzO", 0u, 1, 2)]
    [InlineData("ImageSampleLzO", 0u, -8, 7)]
    [InlineData("ImageSampleLzO", 0u, 9, -3)]
    [InlineData("ImageSampleLzO", 0u, -31, 31)]
    [InlineData("ImageSampleLO", 1u, 1, -1)]
    [InlineData("ImageSampleLO", 1u, -9, 0)]
    [InlineData("ImageSampleLO", 2u, 0, 12)]
    public void SampleOffsetsMoveByWholeTexelsOfTheSampledLevel(string opcode, uint level, int offsetX, int offsetY)
    {
        const uint x = 1, y = 0;
        var extent = Size >> (int)level;
        var u = (x + 0.5f) / extent;
        var v = (y + 0.5f) / extent;
        uint[] address = (Gen5ImageOpcodes.FlagsOf(opcode) & Gen5ImageOp.Lod) != 0
            ? [Offset(offsetX, offsetY), F(u), F(v), F(level)]
            : [Offset(offsetX, offsetY), F(u), F(v)];
        Assert.True(Gen5ImageFixtures.TryCompile(opcode, Gen5ImageFixtures.ImageType2D, out var shader, out var error,
            addressValues: address, storeResults: true), error);
        if (!DeviceGate())
        {
            return;
        }

        var result = Execute(shader!, 4);
        var expectedX = Clamp((int)x + offsetX, extent);
        var expectedY = Clamp((int)y + offsetY, extent);
        for (var component = 0; component < 4; component++)
        {
            Assert.Equal(Value(level, expectedX, expectedY) + component,
                BitConverter.UInt32BitsToSingle(BinaryPrimitives.ReadUInt32LittleEndian(result.AsSpan(component * 4))));
        }
    }

    /// <summary>
    /// GATHER4H: four horizontally adjacent texels starting one left of the gather corner,
    /// on the corner's row (the i0j0 texel of each of four gathers at x offsets -1..2), with
    /// normalized or unnormalized coordinates.
    /// </summary>
    [Theory]
    [InlineData(false, 1u)]
    [InlineData(false, 2u)]
    [InlineData(true, 1u)]
    public void HorizontalGatherReturnsFourTexelsOfOneRow(bool unnormalized, uint dmask)
    {
        var u = unnormalized ? U * Size : U;
        var v = unnormalized ? V * Size : V;
        Assert.True(Gen5ImageFixtures.TryCompile("ImageGather4H", Gen5ImageFixtures.ImageType2D, out var shader, out var error,
            addressValues: [F(u), F(v)], storeResults: true, dmask: dmask, unorm: unnormalized), error);
        if (!DeviceGate())
        {
            return;
        }

        var result = Execute(shader!, 4);
        var component = (uint)System.Numerics.BitOperations.TrailingZeroCount(dmask);
        var cornerX = (int)MathF.Floor(U * Size - 0.5f);
        var row = Clamp((int)MathF.Floor(V * Size - 0.5f), Size);
        for (var texel = 0; texel < 4; texel++)
        {
            Assert.Equal(Value(0, Clamp(cornerX + texel - 1, Size), row) + component,
                BitConverter.UInt32BitsToSingle(BinaryPrimitives.ReadUInt32LittleEndian(result.AsSpan(texel * 4))));
        }
    }

    /// <summary>
    /// TFE and LWE: the status dword after the data is written only when the fetch is NACKed
    /// (RDNA2 ISA, MIMG TFE). Guest memory is always resident here, so the register keeps
    /// whatever the program left in it while the data registers take the texel.
    /// </summary>
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void TextureFailEnableLeavesTheStatusRegisterUntouched(bool tfe, bool lwe)
    {
        const uint sentinel = 0xDEADBEEFu;
        const uint x = 3, y = 6;
        Assert.True(Gen5ImageFixtures.TryCompile("ImageSampleL", Gen5ImageFixtures.ImageType2D, out var shader, out var error,
            addressValues: [F((x + 0.5f) / Size), F((y + 0.5f) / Size), F(0f)],
            storeValues: [0, 0, 0, 0, sentinel], storeResults: true, resultRegisters: 5,
            configure: control => control with { Tfe = tfe, Lwe = lwe }), error);
        if (!DeviceGate())
        {
            return;
        }

        var result = Execute(shader!, 5);
        for (var component = 0; component < 4; component++)
        {
            Assert.Equal(Value(0, x, y) + component,
                BitConverter.UInt32BitsToSingle(BinaryPrimitives.ReadUInt32LittleEndian(result.AsSpan(component * 4))));
        }

        Assert.Equal(sentinel, BinaryPrimitives.ReadUInt32LittleEndian(result.AsSpan(16)));
    }

    private bool DeviceGate()
    {
        if (Environment.GetEnvironmentVariable("SHARPEMU_TEST_REQUIRE_DEVICE") != "1")
        {
            output.WriteLine("Texture execution requires the exclusive Metal device gate.");
            return false;
        }

        Assert.True(MetalNative.IsAvailable, "The required gate needs a Metal device.");
        return true;
    }

    private static byte[] Execute(Gen5MslShader shader, int resultRegisters)
    {
        var mipBytes = Enumerable.Range(0, (int)Levels).Select(static level => Texels((uint)level)).ToArray();
        var texture = MetalNative.CreateFloatTexture(2, Size, Size, 1, 1, mipBytes);
        Assert.True(MetalNative.TryCompileLibrary(shader.Source, out var library, out var error), error + shader.Source);
        Assert.True(MetalNative.TryExecuteWithArgumentBuffer(library, shader.EntryPoint, new byte[resultRegisters * 4],
            new byte[PushData.ByteSize], shader.ArgumentLayout!, 1, out var result, out error,
            texture, MetalNative.CreatePointSampler()), error);
        return result;
    }

    // The gather footprint in the ISA's (and MSL's) return order: (x0, y1), (x1, y1),
    // (x1, y0), (x0, y0), with x0 = floor(u * size - 0.5) plus the offset, clamped to the level.
    private static (uint X, uint Y)[] Footprint(uint level, float u, float v, int offsetX, int offsetY)
    {
        var extent = Size >> (int)level;
        var x0 = (int)MathF.Floor(u * extent - 0.5f) + offsetX;
        var y0 = (int)MathF.Floor(v * extent - 0.5f) + offsetY;
        return
        [
            (Clamp(x0, extent), Clamp(y0 + 1, extent)),
            (Clamp(x0 + 1, extent), Clamp(y0 + 1, extent)),
            (Clamp(x0 + 1, extent), Clamp(y0, extent)),
            (Clamp(x0, extent), Clamp(y0, extent)),
        ];
    }

    private static bool Compare(uint function, float reference, float texel) => function switch
    {
        0 => false,
        1 => reference < texel,
        2 => reference == texel,
        3 => reference <= texel,
        4 => reference > texel,
        5 => reference != texel,
        6 => reference >= texel,
        _ => true,
    };

    private static uint Clamp(int value, uint extent) => (uint)Math.Clamp(value, 0, (int)extent - 1);

    private static uint Offset(int x, int y) => ((uint)x & 0x3F) | (((uint)y & 0x3F) << 8);

    private static uint F(float value) => BitConverter.SingleToUInt32Bits(value);

    private static float Value(uint level, uint x, uint y) => level * 1000 + y * 10 + x;

    private static byte[] Texels(uint level)
    {
        var extent = Math.Max(Size >> (int)level, 1u);
        var bytes = new byte[extent * extent * 16];
        var offset = 0;
        for (uint y = 0; y < extent; y++)
        for (uint x = 0; x < extent; x++)
        for (uint component = 0; component < 4; component++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset), F(Value(level, x, y) + component));
            offset += 4;
        }

        return bytes;
    }
}
