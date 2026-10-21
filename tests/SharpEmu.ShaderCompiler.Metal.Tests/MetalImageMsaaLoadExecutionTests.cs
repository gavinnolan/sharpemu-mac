// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.ShaderCompiler.Resources;
using Xunit;
using Xunit.Abstractions;

namespace SharpEmu.ShaderCompiler.Metal.Tests;

/// <summary>
/// IMAGE_MSAA_LOAD over a rendered four-sample texture: the one DMASK component of the
/// fragments FRAGID..FRAGID+3 (the host keeps samples directly, so a fragment ID is a
/// sample index), zero past the sample count, and the four halves packed into two
/// registers under D16.
/// </summary>
public sealed class MetalImageMsaaLoadExecutionTests(ITestOutputHelper output)
{
    private const uint Width = 4;
    private const uint Height = 4;
    private const uint Samples = 4;

    [Theory]
    [InlineData(Gen5ImageFixtures.ImageType2DMsaa, 1u, 3u, 2u, 0u, 0u)]
    [InlineData(Gen5ImageFixtures.ImageType2DMsaa, 4u, 0u, 1u, 0u, 0u)]
    [InlineData(Gen5ImageFixtures.ImageType2DMsaa, 8u, 2u, 3u, 0u, 2u)]
    [InlineData(Gen5ImageFixtures.ImageType2DMsaaArray, 2u, 1u, 3u, 4u, 0u)]
    [InlineData(Gen5ImageFixtures.ImageType2DMsaaArray, 1u, 0u, 0u, 2u, 3u)]
    public void MsaaLoadReturnsOneComponentOfFourConsecutiveFragments(
        uint descriptorType, uint dmask, uint x, uint y, uint layer, uint fragment)
    {
        var arrayed = descriptorType == Gen5ImageFixtures.ImageType2DMsaaArray;
        uint[] address = arrayed ? [x, y, layer, fragment] : [x, y, fragment];
        Assert.True(Gen5ImageFixtures.TryCompile("ImageMsaaLoad", descriptorType, out var shader, out var error,
            addressValues: address, storeResults: true, dmask: dmask), error);
        if (!DeviceGate())
        {
            return;
        }

        var result = Execute(shader!, arrayed ? 6u : 1u, 4);
        var component = (uint)System.Numerics.BitOperations.TrailingZeroCount(dmask);
        for (uint index = 0; index < 4; index++)
        {
            var sample = fragment + index;
            var expected = sample < Samples ? Value(layer, sample, x, y) + component : 0f;
            Assert.Equal(expected,
                BitConverter.UInt32BitsToSingle(BinaryPrimitives.ReadUInt32LittleEndian(result.AsSpan((int)index * 4))));
        }
    }

    [Fact]
    public void D16MsaaLoadPacksFourHalvesIntoTwoRegisters()
    {
        const uint x = 1, y = 2;
        Assert.True(Gen5ImageFixtures.TryCompile("ImageMsaaLoad", Gen5ImageFixtures.ImageType2DMsaa, out var shader, out var error,
            addressValues: [x, y, 0], storeResults: true, dmask: 4, d16: true, resultRegisters: 2), error);
        if (!DeviceGate())
        {
            return;
        }

        var result = Execute(shader!, 1, 2);
        for (uint index = 0; index < 4; index++)
        {
            var half = BinaryPrimitives.ReadUInt16LittleEndian(result.AsSpan((int)index * 2));
            Assert.Equal(Value(0, index, x, y) + 2, (float)BitConverter.UInt16BitsToHalf(half));
        }
    }

    [Theory]
    [InlineData(Gen5ImageFixtures.ImageType2D)]
    [InlineData(Gen5ImageFixtures.ImageType2DArray)]
    public void MsaaLoadOfASingleSampledImageIsRefused(uint descriptorType)
    {
        Assert.False(Gen5ImageFixtures.TryCompile("ImageMsaaLoad", descriptorType, out _, out var error, dmask: 1));
        output.WriteLine(error);
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

    private static byte[] Execute(Gen5MslShader shader, uint layers, int resultRegisters)
    {
        var texture = MetalNative.CreateMultisampleFloatTexture(Width, Height, Samples, layers);
        Assert.True(MetalNative.TryCompileLibrary(shader.Source, out var library, out var error), error + shader.Source);
        Assert.True(MetalNative.TryExecuteWithArgumentBuffer(library, shader.EntryPoint, new byte[resultRegisters * 4],
            new byte[PushData.ByteSize], shader.ArgumentLayout!, 1, out var result, out error, texture), error);
        return result;
    }

    private static float Value(uint layer, uint sample, uint x, uint y) => layer * 1000 + sample * 100 + y * 10 + x;
}
