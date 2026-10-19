// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.ShaderCompiler.Resources;
using Xunit;

namespace SharpEmu.ShaderCompiler.Tests.Resources;

// Portable buffers (ported from upstream PR #1074's tests, on this build's layout): the
// run-time words of a V#, where they sit in the shader data, and what the specialization
// keeps once the program reads them.
public sealed class PortableBufferWordTests
{
    [Theory]
    [InlineData(0x3FFFu, 4012u)]
    [InlineData(0x2000u, 940u)]
    [InlineData(0x1000u, 556u)]
    public void PackingKeepsTheFullStrideAndDescriptorWord3(uint stride, uint swizzle)
    {
        uint[] descriptor = [0x1000, 0xC0000000 | (stride << 16), 1, (22u << 12) | swizzle | (1u << 23)];
        var packed = PortableBufferWord.Pack(descriptor);
        Assert.Equal(2, PortableBufferWord.DwordCount);
        Assert.Equal(stride, packed.Stride);
        Assert.Equal(descriptor[3], packed.DescriptorWord3);
    }

    [Fact]
    public void AShortDescriptorPacksAsZero()
    {
        Assert.Equal((0u, 0u), PortableBufferWord.Pack([]));
        Assert.Equal((0u, 0u), PortableBufferWord.Pack([1, 2, 3]));
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(20u)]
    public void TwoDwordsPerBufferSitBeforeTheDispatchLimits(uint cursor)
    {
        var info = new ShaderResourceInfo { Buffers = Enumerable.Range(0, 5).Select(_ => new BufferResource { Read = true }).ToList() };
        var plain = BindingLayout.Allocate(info, [0u], false, false, true, cursor, usesDispatchThreadLimits: true, portableBuffers: false);
        var portable = BindingLayout.Allocate(info, [0u], false, false, true, cursor, usesDispatchThreadLimits: true, portableBuffers: true);
        Assert.Equal(0u, plain.BufferWordCount);
        Assert.Equal(10u, portable.BufferWordCount);
        Assert.Equal(plain.DispatchThreadLimitsDword, portable.FirstBufferWordDword());
        Assert.Equal(portable.FirstBufferWordDword() + 10, portable.DispatchThreadLimitsDword);
        Assert.Equal(plain.ShaderDataDwordCount + 10, portable.ShaderDataDwordCount);
        Assert.NotEqual(plain, portable);
    }

    [Fact]
    public void AReadOnlyFormattedBufferKeepsNeitherStrideNorFormat()
    {
        var specialization = new BufferSpecialization(0xC000 | 48, 22, 0xFAC);
        var normalized = PortableBufferWord.Normalize(specialization, new BufferResource { Read = true, Formatted = true });
        Assert.Equal(0xC000u, normalized.PackedStride);
        Assert.Equal(DescriptorConstants.InvalidFormat, normalized.DescriptorFormat);
        Assert.Equal(DescriptorConstants.IdentityDestinationSelect, normalized.DescriptorSwizzle);
    }

    [Fact]
    public void AWrittenFormattedBufferKeepsItsFormatForTheStoresConversion()
    {
        var specialization = new BufferSpecialization(0xC000 | 48, 22, 0xFAC);
        var normalized = PortableBufferWord.Normalize(specialization, new BufferResource { Written = true, Formatted = true });
        Assert.Equal(0xC000u, normalized.PackedStride);
        Assert.Equal(22u, normalized.DescriptorFormat);
        Assert.Equal(0xFACu, normalized.DescriptorSwizzle);
        Assert.Equal(normalized, PortableBufferWord.Normalize(specialization with { PackedStride = 0xC000 | 16 }, new BufferResource { Written = true, Formatted = true }));
    }
}
