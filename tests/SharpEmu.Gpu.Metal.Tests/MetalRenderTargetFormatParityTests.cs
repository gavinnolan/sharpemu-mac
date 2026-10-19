// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
using SharpEmu.Gpu.Metal;
using Xunit;

namespace SharpEmu.Gpu.Metal.Tests;

/// <summary>
/// The guest render-target encodings the Metal backend decodes. The Vulkan backend's
/// <c>TryDecodeRenderTargetFormat</c> decodes the same guest words, and the two tables are
/// meant to stay in step — a title that renders to an encoding only one of them knows
/// does not render wrongly, it stops: the pipeline cache refuses a colour target with no
/// pixel output kind. Poppy Playtime's first level stopped exactly there, on the 8-bit
/// unsigned-integer target below, which the Vulkan table had and this one did not.
///
/// The list is the Vulkan table's (dataFormat, numberType) pairs. It cannot be read from
/// that backend here: a macOS build contains no Vulkan at all.
/// </summary>
public sealed class MetalRenderTargetFormatParityTests
{
    [Theory]
    // 8-bit single component.
    [InlineData(1u, 0u)]
    [InlineData(1u, 4u)]
    [InlineData(36u, 0u)]
    [InlineData(49u, 0u)]
    // 16-bit single component.
    [InlineData(2u, 0u)]
    [InlineData(2u, 1u)]
    [InlineData(2u, 4u)]
    [InlineData(2u, 5u)]
    [InlineData(2u, 7u)]
    // 32-bit single component.
    [InlineData(4u, 0u)]
    [InlineData(4u, 4u)]
    [InlineData(4u, 5u)]
    [InlineData(4u, 7u)]
    [InlineData(20u, 0u)]
    [InlineData(29u, 0u)]
    // Two components.
    [InlineData(3u, 0u)]
    [InlineData(5u, 0u)]
    [InlineData(5u, 4u)]
    [InlineData(5u, 5u)]
    [InlineData(5u, 7u)]
    [InlineData(11u, 4u)]
    [InlineData(11u, 5u)]
    [InlineData(11u, 7u)]
    [InlineData(75u, 0u)]
    // Packed and four-component.
    [InlineData(6u, 7u)]
    [InlineData(7u, 0u)]
    [InlineData(7u, 7u)]
    [InlineData(9u, 0u)]
    [InlineData(10u, 0u)]
    [InlineData(10u, 4u)]
    [InlineData(10u, 5u)]
    [InlineData(10u, 6u)]
    [InlineData(10u, 9u)]
    [InlineData(12u, 0u)]
    [InlineData(12u, 4u)]
    [InlineData(12u, 5u)]
    [InlineData(12u, 7u)]
    [InlineData(13u, 0u)]
    [InlineData(13u, 7u)]
    [InlineData(14u, 0u)]
    [InlineData(14u, 7u)]
    [InlineData(22u, 0u)]
    [InlineData(56u, 0u)]
    [InlineData(62u, 0u)]
    [InlineData(64u, 0u)]
    [InlineData(71u, 0u)]
    public void EveryEncodingTheVulkanTableDecodesIsDecodedHere(uint dataFormat, uint numberType)
    {
        Assert.True(
            MetalGuestFormats.TryDecodeRenderTargetFormat(dataFormat, numberType, componentSwap: 0, out var format),
            $"dataFormat={dataFormat} numberType={numberType} has no Metal render-target format");
        Assert.NotEqual(MtlPixelFormat.Invalid, format.Format);
    }

    // Poppy Playtime's first level renders to this one, and refusing it is fatal rather
    // than merely wrong, so it gets a case of its own that names what it must decode to.
    [Fact]
    public void AnEightBitUnsignedIntegerTargetDecodesToR8Uint()
    {
        Assert.True(MetalGuestFormats.TryDecodeRenderTargetFormat(1, 4, 0, out var format));
        Assert.Equal(MtlPixelFormat.R8Uint, format.Format);
    }

    // The two encodings Metal genuinely cannot represent: it has no scaled pixel formats,
    // where Vulkan has R16Uscaled and R16Sscaled. Listed so the gap stays deliberate and a
    // future reader does not take it for more drift.
    [Theory]
    [InlineData(2u, 2u)]
    [InlineData(2u, 3u)]
    public void TheScaledEncodingsVulkanHasAreKnowinglyAbsent(uint dataFormat, uint numberType) =>
        Assert.False(MetalGuestFormats.TryDecodeRenderTargetFormat(dataFormat, numberType, 0, out _));
}
