// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Adapters.Sdl.Input;
using Xunit;

namespace SharpEmu.Libs.Tests.VideoOut;

/// <summary>
/// SDL reports sticks and triggers as signed 16-bit axes and the guest pad takes
/// unsigned bytes, so the conversion lives with the SDL adapter rather than in
/// the shared pad layer.
/// </summary>
public sealed class SdlGamepadAxisTests
{
    [Theory]
    [InlineData(short.MinValue, 0)]
    [InlineData(0, 128)]
    [InlineData(short.MaxValue, 255)]
    public void ToStickByteMapsFullSdlRange(short value, int expected)
    {
        Assert.Equal((byte)expected, SdlGamepadStateReader.ToStickByte(value));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(short.MaxValue, 255)]
    public void ToTriggerByteMapsFullSdlRange(short value, int expected)
    {
        Assert.Equal((byte)expected, SdlGamepadStateReader.ToTriggerByte(value));
    }
}
