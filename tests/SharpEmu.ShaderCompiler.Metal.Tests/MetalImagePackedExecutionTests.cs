// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Numerics;
using SharpEmu.ShaderCompiler.Resources;
using Xunit;
using Xunit.Abstractions;

namespace SharpEmu.ShaderCompiler.Metal.Tests;

/// <summary>
/// IMAGE_LOAD_PCK(_SGN) and IMAGE_STORE_PCK on the device: the texel's memory bits move
/// as dwords without format conversion, DMASK naming the dwords. The reference is the
/// texel's bytes themselves (little-endian dwords), with _SGN sign-extending a texel
/// narrower than a dword.
/// </summary>
public sealed class MetalImagePackedExecutionTests(ITestOutputHelper output)
{
    private const uint Size = 2;

    [Theory]
    [InlineData("ImageLoadPck", 56u, 70u, 4u, "1280FE01", 1u)] // 8_8_8_8 UNORM
    [InlineData("ImageLoadPckSgn", 56u, 70u, 4u, "1280FE01", 1u)]
    [InlineData("ImageLoadPck", 1u, 10u, 1u, "9C", 1u)] // 8 UNORM
    [InlineData("ImageLoadPckSgn", 1u, 10u, 1u, "9C", 1u)]
    [InlineData("ImageLoadPckSgn", 7u, 20u, 2u, "0180", 1u)] // 16 UNORM
    [InlineData("ImageLoadPck", 12u, 24u, 2u, "FEFF", 1u)] // 16 SINT
    [InlineData("ImageLoadPckSgn", 12u, 24u, 2u, "FEFF", 1u)]
    [InlineData("ImageLoadPckSgn", 60u, 73u, 4u, "FF7F8001", 1u)] // 8_8_8_8 UINT
    [InlineData("ImageLoadPck", 29u, 65u, 4u, "003C00C0", 1u)] // 16_16 FLOAT
    [InlineData("ImageLoadPck", 69u, 113u, 8u, "0100020003000400", 3u)] // 16_16_16_16 UINT
    [InlineData("ImageLoadPck", 69u, 113u, 8u, "0100020003000400", 2u)]
    [InlineData("ImageLoadPck", 75u, 123u, 16u, "01000000020000000300000004000000", 10u)] // 32_32_32_32 UINT
    [InlineData("ImageLoadPck", 77u, 125u, 16u, "0000803F000000C00000C07F01000080", 15u)] // 32_32_32_32 FLOAT
    [InlineData("ImageLoadMipPck", 56u, 70u, 4u, "1280FE01", 1u)]
    [InlineData("ImageLoadMipPckSgn", 1u, 10u, 1u, "80", 1u)]
    public void PackedLoadReturnsTheTexelBits(string opcode, uint unifiedFormat, uint pixelFormat,
        uint bytesPerPixel, string texelHex, uint dmask)
    {
        var mip = Gen5ImageOpcodes.Has(opcode, Gen5ImageOp.Mip);
        var registers = BitOperations.PopCount(dmask);
        Assert.True(Gen5ImageFixtures.TryCompile(opcode, Gen5ImageFixtures.ImageType2D, out var shader, out var error,
            unifiedFormat: unifiedFormat, dmask: dmask, addressValues: mip ? [1, 1, 0] : [1, 1],
            storeResults: true, resultRegisters: registers), error);
        if (!DeviceGate())
        {
            return;
        }

        var texel = Convert.FromHexString(texelHex);
        var bytes = new byte[Size * Size * bytesPerPixel];
        for (var index = 0; index < bytes.Length; index++)
        {
            bytes[index] = (byte)(0x40 + index);
        }

        texel.CopyTo(bytes.AsSpan((int)(3 * bytesPerPixel)));
        var texture = MetalNative.CreateRawTexture(pixelFormat, Size, Size, bytesPerPixel, bytes);
        Assert.True(MetalNative.TryCompileLibrary(shader!.Source, out var library, out error), error + shader.Source);
        Assert.True(MetalNative.TryExecuteWithArgumentBuffer(library, shader.EntryPoint, new byte[registers * 4],
            new byte[PushData.ByteSize], shader.ArgumentLayout!, 1, out var result, out error, texture), error);

        var words = new byte[16];
        texel.CopyTo(words, 0);
        var signed = Gen5ImageOpcodes.Has(opcode, Gen5ImageOp.PackedSigned);
        if (signed && texel.Length < 4 && (texel[^1] & 0x80) != 0)
        {
            words.AsSpan(texel.Length, 4 - texel.Length).Fill(0xFF);
        }

        var register = 0;
        for (var word = 0; word < 4; word++)
        {
            if ((dmask & (1u << word)) == 0)
            {
                continue;
            }

            Assert.Equal(
                BinaryPrimitives.ReadUInt32LittleEndian(words.AsSpan(word * 4)),
                BinaryPrimitives.ReadUInt32LittleEndian(result.AsSpan(register++ * 4)));
        }
    }

    [Theory]
    [InlineData("ImageStorePck", 56u, 70u, 4u, 1u, new uint[] { 0x01FE8012 })]
    [InlineData("ImageStorePck", 7u, 20u, 2u, 1u, new uint[] { 0x00008001 })]
    [InlineData("ImageStorePck", 29u, 65u, 4u, 1u, new uint[] { 0xC0003C00 })]
    [InlineData("ImageStorePck", 60u, 73u, 4u, 1u, new uint[] { 0x01807FFF })]
    [InlineData("ImageStorePck", 69u, 113u, 8u, 3u, new uint[] { 0x00020001, 0x00040003 })]
    [InlineData("ImageStorePck", 75u, 123u, 16u, 5u, new uint[] { 7, 9 })]
    [InlineData("ImageStorePck", 77u, 125u, 16u, 15u, new uint[] { 0x3F800000, 0xC0000000, 0x7FC00000, 0x80000001 })]
    [InlineData("ImageStoreMipPck", 56u, 70u, 4u, 1u, new uint[] { 0x80FF0001 })]
    public void PackedStoreWritesTheTexelBits(string opcode, uint unifiedFormat, uint pixelFormat,
        uint bytesPerPixel, uint dmask, uint[] data)
    {
        var mip = Gen5ImageOpcodes.Has(opcode, Gen5ImageOp.Mip);
        Assert.True(Gen5ImageFixtures.TryCompile(opcode, Gen5ImageFixtures.ImageType2D, out var shader, out var error,
            unifiedFormat: unifiedFormat, dmask: dmask, addressValues: mip ? [1, 0, 0] : [1, 0], storeValues: data), error);
        if (!DeviceGate())
        {
            return;
        }

        var initial = new byte[Size * Size * bytesPerPixel];
        var texture = MetalNative.CreateRawTexture(pixelFormat, Size, Size, bytesPerPixel, initial);
        Assert.True(MetalNative.TryCompileLibrary(shader!.Source, out var library, out error), error + shader.Source);
        Assert.True(MetalNative.TryExecuteWithArgumentBuffer(library, shader.EntryPoint, new byte[16],
            new byte[PushData.ByteSize], shader.ArgumentLayout!, 1, out _, out error, texture), error);

        var words = new byte[16];
        var source = 0;
        for (var word = 0; word < 4; word++)
        {
            if ((dmask & (1u << word)) != 0)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(words.AsSpan(word * 4), data[source++]);
            }
        }

        var expected = (byte[])initial.Clone();
        words.AsSpan(0, (int)bytesPerPixel).CopyTo(expected.AsSpan((int)bytesPerPixel));
        Assert.Equal(Convert.ToHexString(expected), Convert.ToHexString(MetalNative.ReadRawTexture(texture, Size, Size, bytesPerPixel)));
    }

    /// <summary>A conversion the view cannot undo exactly is refused rather than guessed.</summary>
    [Theory]
    [InlineData(8u)] // 16 SNORM: -1.0 has two encodings
    [InlineData(130u)] // 8_8_8_8 SRGB
    public void PackedLoadOfAnInexactFormatIsRefused(uint unifiedFormat)
    {
        Assert.False(Gen5ImageFixtures.TryCompile("ImageLoadPck", Gen5ImageFixtures.ImageType2D, out _, out var error,
            unifiedFormat: unifiedFormat, dmask: 1));
        output.WriteLine(error);
    }

    /// <summary>A format the host cannot sample is bound as a placeholder, whose bits a
    /// _PCK access must not return: materialization refuses the draw.</summary>
    [Fact]
    public void PackedLoadOfAnUnsampledFormatFailsMaterialization()
    {
        // 8 SNORM is not a sampled format here.
        var exception = Record.Exception(() => Gen5ImageFixtures.TryCompile(
            "ImageLoadPck", Gen5ImageFixtures.ImageType2D, out _, out _, unifiedFormat: 2u, dmask: 1));
        Assert.NotNull(exception);
        Assert.Contains("descriptor materialization failed", exception.Message);
    }

    /// <summary>
    /// A sampled view applies the T# DST_SEL, so a _PCK access is refused when it permutes
    /// the format's own components, and allowed when only the absent ones are swizzled
    /// (an 8 UNORM image read as X, 0, 0, 1).
    /// </summary>
    [Theory]
    [InlineData(1u, 0x204u, true)]
    [InlineData(56u, 0xFACu, true)]
    [InlineData(56u, 0xEAEu, false)]
    public void PackedLoadThroughASwizzledViewIsRefused(uint unifiedFormat, uint dstSelect, bool accepted)
    {
        var exception = Record.Exception(() => Assert.True(Gen5ImageFixtures.TryCompile(
            "ImageLoadPck", Gen5ImageFixtures.ImageType2D, out _, out var error,
            unifiedFormat: unifiedFormat, dstSelect: dstSelect, dmask: 1), error));
        if (accepted)
        {
            Assert.Null(exception);
        }
        else
        {
            Assert.NotNull(exception);
            Assert.Contains("descriptor materialization failed", exception.Message);
        }
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
}
