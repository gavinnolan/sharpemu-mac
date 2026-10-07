// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Platform.MacOS;
using SharpEmu.Libs.VideoOut;
using Xunit;

namespace SharpEmu.Libs.Tests.Platform;

/// <summary>
/// CGDisplayMode geometry conversion. No CoreGraphics call is involved, so this
/// runs on any host.
/// </summary>
public sealed class MacDisplayCatalogTests
{
    [Fact]
    public void ModeGeometryIsCarriedThrough()
    {
        Assert.Equal(new HostDisplayMode(3840, 2160, 60), MacDisplayCatalog.Convert(3840, 2160, 60.0));
    }

    [Theory]
    [InlineData(59.94, 60)]
    [InlineData(119.88, 120)]
    [InlineData(23.976, 24)]
    [InlineData(0.4, 1)]
    public void FractionalRefreshRatesRoundAwayFromZero(double refreshRate, int expected)
    {
        Assert.Equal(expected, MacDisplayCatalog.Convert(1920, 1080, refreshRate)!.RefreshRate);
    }

    /// <summary>
    /// Built-in Apple panels report refresh rate 0 from CGDisplayModeGetRefreshRate.
    /// The catalog's consumers read 0 as "use the display default", so it must be
    /// preserved rather than invented as 60.
    /// </summary>
    [Fact]
    public void AZeroRefreshRateIsPreserved()
    {
        Assert.Equal(0, MacDisplayCatalog.Convert(3024, 1964, 0.0)!.RefreshRate);
    }

    [Theory]
    [InlineData(0, 1080)]
    [InlineData(1920, 0)]
    [InlineData(-1, 1080)]
    public void DegenerateModesAreDropped(int width, int height)
    {
        Assert.Null(MacDisplayCatalog.Convert(width, height, 60.0));
    }
}
