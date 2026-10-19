// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Gpu.Metal.V2;
using SharpEmu.Libs.Gpu.Abstractions;
using SharpEmu.Libs.Gpu.Images;
using Xunit;

namespace SharpEmu.Gpu.Metal.Tests;

/// <summary>
/// The presenter samples logical R,G,B,A whichever cached image a display request resolves
/// to. A B-first float display buffer is swapped on upload, but a B-first render target
/// keeps memory order, so presenting the render target needs a B,G,R,A view.
/// </summary>
public sealed class MetalPresentSwizzleTests
{
    private static readonly GpuComponentMapping Bgra = new(
        GpuComponentSwizzle.B, GpuComponentSwizzle.G, GpuComponentSwizzle.R, GpuComponentSwizzle.A);

    [Fact]
    public void MatchingOrdersPresentThroughAnIdentityView()
    {
        Assert.Equal(default, MetalRenderHost.PresentSwizzle(false, false));
        Assert.Equal(default, MetalRenderHost.PresentSwizzle(true, true));
    }

    [Fact]
    public void DisagreeingOrdersPresentThroughABgraView()
    {
        Assert.Equal(Bgra, MetalRenderHost.PresentSwizzle(true, false));
        Assert.Equal(Bgra, MetalRenderHost.PresentSwizzle(false, true));
    }

    [Fact]
    public void AnAlternateOrderFloatTargetShownAsABlueFirstDisplayBufferPresentsRedFirst()
    {
        var target = GuestSurfaceFormats.RenderTargetFormat(
            ChannelLayout.Bits16_16_16_16, ChannelType.Float, ChannelOrder.Alternate);
        Assert.Equal(GpuFormat.R16G16B16A16Sfloat, target.HostFormat);

        // A render target's image description never carries Bgra16: its texture is memory order.
        var targetImage = ImageDescription.Create();
        Assert.False(targetImage.Bgra16);

        Assert.True(DisplayFormatRule.TryDecode(0xC001000600000000, out var display));
        Assert.Equal(target.HostFormat, display.HostFormat);
        Assert.True(display.Bgra16);

        var mapping = MetalRenderHost.PresentSwizzle(display.Bgra16, targetImage.Bgra16);
        Assert.Equal(GpuComponentSwizzle.B, mapping.R);
        Assert.Equal(GpuComponentSwizzle.R, mapping.B);
    }
}
