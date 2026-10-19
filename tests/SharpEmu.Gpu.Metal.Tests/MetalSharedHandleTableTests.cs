// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Gpu.Metal.V2;
using Xunit;

namespace SharpEmu.Gpu.Metal.Tests;

// Libraries and render pipeline states several owners use: the native object goes when the
// last owner lets go, so evicting one owner never releases what another still draws with.
public sealed class MetalSharedHandleTableTests
{
    private const nint State = 0x1000;

    [Fact]
    public void ARegisteredObjectIsFoundByItsKeyAndCountsOwners()
    {
        var table = new MetalSharedHandleTable<string>();
        Assert.False(table.TryAcquire("a", out _, out _));

        Assert.True(table.Register("a", State, default, out var shared));
        Assert.Equal(State, shared);
        Assert.Equal(1, table.OwnersOf(State));

        Assert.True(table.TryAcquire("a", out var found, out _));
        Assert.Equal(State, found);
        Assert.Equal(2, table.OwnersOf(State));
        Assert.Equal(1, table.Count);
    }

    [Fact]
    public void TheNativeObjectIsReleasedOnlyByItsLastOwner()
    {
        var table = new MetalSharedHandleTable<string>();
        table.Register("a", State, default, out _);
        table.TryAcquire("a", out _, out _);
        table.TryAcquire("a", out _, out _);

        // Two owners are evicted; the third still has a live object.
        Assert.False(table.Release(State));
        Assert.False(table.Release(State));
        Assert.Equal(1, table.OwnersOf(State));
        Assert.True(table.TryAcquire("a", out _, out _));
        Assert.False(table.Release(State));

        Assert.True(table.Release(State));
        Assert.Equal(0, table.Count);
        Assert.Equal(0, table.OwnersOf(State));
    }

    [Fact]
    public void AReleasedObjectIsMadeAgainNotFoundAndItsAddressMayBeReused()
    {
        var table = new MetalSharedHandleTable<string>();
        table.Register("a", State, default, out _);
        Assert.True(table.Release(State));
        Assert.False(table.TryAcquire("a", out _, out _));

        // The allocator hands the same address to the object made next.
        Assert.True(table.Register("b", State, default, out var shared));
        Assert.Equal(State, shared);
        Assert.False(table.TryAcquire("a", out _, out _));
        Assert.True(table.TryAcquire("b", out _, out _));
    }

    [Fact]
    public void AnObjectThatWasNeverSharedIsReleasedByItsOwner()
    {
        // The dedupe switched off (SHARPEMU_METAL_V2_PIPELINE_DEDUPE=0) registers nothing.
        var table = new MetalSharedHandleTable<string>();
        Assert.True(table.Release(State));
    }

    [Fact]
    public void ASecondCreatorOfTheSameKeyJoinsTheFirstAndReleasesItsOwnObject()
    {
        var table = new MetalSharedHandleTable<string>();
        table.Register("a", State, default, out _);

        Assert.False(table.Register("a", State + 0x100, default, out var winner));

        Assert.Equal(State, winner);
        Assert.Equal(2, table.OwnersOf(State));
        Assert.Equal(0, table.OwnersOf(State + 0x100));
        Assert.Equal(1, table.Count);
    }

    [Fact]
    public void TheCreatorsSideDataComesBackWithEveryOwner()
    {
        var table = new MetalSharedHandleTable<string>();
        var archiveKey = new UInt128(7, 9);
        table.Register("a", State, archiveKey, out _);

        Assert.True(table.TryAcquire("a", out _, out var aux));
        Assert.Equal(archiveKey, aux);
    }

    [Fact]
    public void ConcurrentOwnersNeverLoseACountOrReleaseEarly()
    {
        var table = new MetalSharedHandleTable<int>();
        table.Register(1, State, default, out _);
        const int Owners = 8;
        const int Rounds = 2000;
        var early = 0;
        Parallel.For(0, Owners, worker =>
        {
            for (var round = 0; round < Rounds; round++)
            {
                Assert.True(table.TryAcquire(1, out _, out _));
                if (table.Release(State))
                {
                    Interlocked.Increment(ref early);
                }
            }
        });

        Assert.Equal(0, early);
        Assert.Equal(1, table.OwnersOf(State));
        Assert.True(table.Release(State));
    }
}
