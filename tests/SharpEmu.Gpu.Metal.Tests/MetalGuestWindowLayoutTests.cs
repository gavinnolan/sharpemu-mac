// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Gpu.Metal.V2;
using Xunit;

namespace SharpEmu.Gpu.Metal.Tests;

/// <summary>
/// The guest windows are named with useResource on every encoder, so their mapped total
/// is what the device has to hold resident; a command buffer that names more than the
/// device can hold fails with Insufficient Memory and nothing is drawn at all. These pin
/// the layout choice that keeps the total inside the budget.
/// </summary>
public sealed class MetalGuestWindowLayoutTests
{
    private const ulong Gib = 1024UL * 1024 * 1024;

    // The measured case: a 16.375 GiB backing, 4 GiB windows and a 24 GB M4. Half
    // overlap asks for 30.375 GiB, which the device refuses, so the windows must tile.
    [Fact]
    public void BackingLargerThanHalfTheBudgetTilesInsteadOfOverlapping()
    {
        var aliasSize = (16 * Gib) + (3 * Gib / 8);
        var (stride, count, mapped) = MetalGuestMemory.ChooseWindowLayout(aliasSize, 4 * Gib, 16 * Gib);

        Assert.Equal(4 * Gib, stride);
        Assert.Equal(5, count);
        Assert.Equal(aliasSize, mapped);
        Assert.True(mapped <= aliasSize);
    }

    // A device with room for it keeps the overlap, so no range up to one stride straddles.
    [Fact]
    public void BackingThatFitsTwiceKeepsTheOverlap()
    {
        var aliasSize = (16 * Gib) + (3 * Gib / 8);
        var (stride, count, mapped) = MetalGuestMemory.ChooseWindowLayout(aliasSize, 4 * Gib, 48 * Gib);

        Assert.Equal(2 * Gib, stride);
        Assert.Equal(8, count);
        Assert.True(mapped > aliasSize);
        Assert.True(mapped <= 48 * Gib);
    }

    [Fact]
    public void BackingInsideOneWindowNeedsOneWindow()
    {
        var (stride, count, mapped) = MetalGuestMemory.ChooseWindowLayout(Gib, 4 * Gib, 64 * Gib);

        Assert.Equal(2 * Gib, stride);
        Assert.Equal(1, count);
        Assert.Equal(Gib, mapped);
    }

    // However small the budget, the tiled layout is the floor: it maps each guest byte
    // once, which is the least a no-copy window set can ask for.
    [Fact]
    public void ATinyBudgetStillMapsEveryGuestByteExactlyOnce()
    {
        var aliasSize = (12 * Gib) + 4096;
        var (stride, _, mapped) = MetalGuestMemory.ChooseWindowLayout(aliasSize, 4 * Gib, 1);

        Assert.Equal(4 * Gib, stride);
        Assert.Equal(aliasSize, mapped);
    }
}
