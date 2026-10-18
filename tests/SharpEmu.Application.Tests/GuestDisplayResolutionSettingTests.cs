// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using GuiSettings = SharpEmu.Application.ApplicationSettings;
using Xunit;

namespace SharpEmu.Application.Tests;

public sealed class GuestDisplayResolutionSettingTests
{
    [Fact]
    public void MissingAndUnknownValuesAre1080p()
    {
        Assert.Equal("1080p", GuiSettings.NormalizeFromJson("{}").GuestDisplayResolution);
        Assert.Equal("1080p", GuiSettings.NormalizeFromJson("""{"GuestDisplayResolution":"banana"}""").GuestDisplayResolution);
    }

    [Theory]
    [InlineData("720P", "720p")]
    [InlineData("1440p", "1440p")]
    [InlineData("2160p", "2160p")]
    public void OtherResolutionsStaySelectable(string saved, string expected)
    {
        var json = "{\"GuestDisplayResolution\":\"" + saved + "\"}";
        Assert.Equal(expected, GuiSettings.NormalizeFromJson(json).GuestDisplayResolution);
    }
}
