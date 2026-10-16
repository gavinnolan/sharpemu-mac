// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Kernel;
using Xunit;

namespace SharpEmu.Libs.Tests.Kernel;

public sealed class GuestFileDescriptorTableTests
{
    [Fact]
    public void AllocatesLowestFreeDescriptorFromThree()
    {
        var table = new GuestFileDescriptorTable(limit: 16);
        Assert.True(table.TryAllocate(out var a));
        Assert.True(table.TryAllocate(out var b));
        Assert.True(table.TryAllocate(out var c));
        Assert.Equal([3, 4, 5], new[] { a, b, c });

        table.Release(4);
        Assert.True(table.TryAllocate(out var reused));
        Assert.Equal(4, reused);
    }

    [Fact]
    public void MinimumSelectsLowestFreeAtOrAbove()
    {
        var table = new GuestFileDescriptorTable(limit: 16);
        Assert.True(table.TryAllocate(out _, minimum: 10));
        Assert.True(table.TryAllocate(out var next, minimum: 10));
        Assert.Equal(11, next);
        Assert.True(table.TryAllocate(out var low));
        Assert.Equal(3, low);
    }

    [Fact]
    public void FullTableFailsAndReleaseFreesASlot()
    {
        var table = new GuestFileDescriptorTable(limit: 6);
        for (var i = 3; i < 6; i++)
        {
            Assert.True(table.TryAllocate(out var fd));
            Assert.Equal(i, fd);
        }

        Assert.False(table.TryAllocate(out _));
        table.Release(5);
        Assert.True(table.TryAllocate(out var again));
        Assert.Equal(5, again);
    }

    [Fact]
    public void ClaimMarksASpecificDescriptorInRange()
    {
        var table = new GuestFileDescriptorTable(limit: 16);
        Assert.True(table.TryClaim(3));
        Assert.True(table.IsAllocated(3));
        // dup2 onto an fd already in use keeps it in use.
        Assert.True(table.TryClaim(3));
        Assert.True(table.TryAllocate(out var next));
        Assert.Equal(4, next);
        Assert.False(table.TryClaim(16));
        Assert.False(table.TryClaim(-1));
    }
}
