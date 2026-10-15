// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.VideoOut;
using Xunit;

namespace SharpEmu.Libs.Tests.VideoOut;

/// <summary>
/// The display a title is told about decides how much pixel work it does: it asks the system
/// what the television is and sizes its render targets from the answer. A 4K report can cost
/// four times the pixels of a 1080p one, so there is one policy, every query reads it, and the
/// default is the resolution the host is most likely actually driving.
/// </summary>
public sealed class GuestDisplayResolutionTests
{
    [Fact]
    public void TheDefaultDisplayIs1080p()
    {
        var options = HostVideoOptions.Default;

        Assert.Equal(GuestDisplayResolution.Hd, options.DisplayResolution);
        Assert.Equal((1920u, 1080u), options.GuestDisplaySize);
        Assert.Equal(1, options.GuestResolutionClass);
    }

    [Theory]
    [InlineData(GuestDisplayResolution.Hd720, 1280u, 720u, 1)]
    [InlineData(GuestDisplayResolution.Hd, 1920u, 1080u, 1)]
    [InlineData(GuestDisplayResolution.Qhd, 2560u, 1440u, 1)]
    [InlineData(GuestDisplayResolution.Uhd, 3840u, 2160u, 2)]
    public void EverySizeAndClassComesFromTheOneSetting(
        GuestDisplayResolution resolution, uint width, uint height, int resolutionClass)
    {
        var options = new HostVideoOptions { DisplayResolution = resolution };

        Assert.Equal((width, height), options.GuestDisplaySize);
        Assert.Equal(resolutionClass, options.GuestResolutionClass);
    }

    /// <summary>Only 4K is the 4K class; 1440p reports the same class as 1080p, which is what
    /// the console does — the class is a tier, not a size.</summary>
    [Fact]
    public void OnlyFourKReportsTheFourKClass()
    {
        Assert.Equal(
            2,
            new HostVideoOptions { DisplayResolution = GuestDisplayResolution.Uhd }.GuestResolutionClass);
        Assert.Equal(
            1,
            new HostVideoOptions { DisplayResolution = GuestDisplayResolution.Qhd }.GuestResolutionClass);
    }

    [Fact]
    public void AnUnknownSettingFallsBackToTheDefaultDisplay()
    {
        var options = new HostVideoOptions { DisplayResolution = (GuestDisplayResolution)42 }.Normalize();

        Assert.Equal(GuestDisplayResolution.Hd, options.DisplayResolution);
    }

    /// <summary>The window and the reported display are independent: a 4K window does not tell
    /// the guest it is driving a 4K television, and a 1080p report does not shrink the window.
    /// Conflating them is what made the report follow whatever the title had already chosen.</summary>
    [Fact]
    public void TheWindowSizeAndTheReportedDisplayAreIndependent()
    {
        var options = new HostVideoOptions
        {
            Width = 3840,
            Height = 2160,
            DisplayResolution = GuestDisplayResolution.Hd,
        }.Normalize();

        Assert.Equal(3840, options.Width);
        Assert.Equal((1920u, 1080u), options.GuestDisplaySize);
        Assert.Equal(1, options.GuestResolutionClass);
    }
}
