// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Gpu.Metal.V2;
using Xunit;

namespace SharpEmu.Gpu.Metal.Tests;

/// <summary>
/// A linear float flip is sRGB-encoded on the way to an SDR drawable, as the Vulkan
/// presenter does; an extended-range layer takes it unchanged.
/// </summary>
public sealed class MetalPresentLinearEncodeTests
{
    [Theory]
    [InlineData(MtlFormat.Rgba16Float)]
    [InlineData(MtlFormat.Rgba32Float)]
    public void FloatFlipsAreEncodedForAnSdrDrawable(ulong format) =>
        Assert.True(MetalV2Presenter.NeedsLinearEncode(format, hdr: false));

    [Theory]
    [InlineData(MtlFormat.Rgba16Float)]
    [InlineData(MtlFormat.Rgba32Float)]
    public void FloatFlipsAreUnchangedOnAnHdrLayer(ulong format) =>
        Assert.False(MetalV2Presenter.NeedsLinearEncode(format, hdr: true));

    [Theory]
    [InlineData(MtlFormat.Bgra8Unorm)]
    [InlineData(MtlFormat.Rgba8Unorm)]
    [InlineData(MtlFormat.Rgb10A2Unorm)]
    public void EncodedFormatsAreNeverEncodedAgain(ulong format)
    {
        Assert.False(MetalV2Presenter.NeedsLinearEncode(format, hdr: false));
        Assert.False(MetalV2Presenter.NeedsLinearEncode(format, hdr: true));
    }
}
