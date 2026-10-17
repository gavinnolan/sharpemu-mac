// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Gpu.Metal.V2;
using Xunit;

namespace SharpEmu.Gpu.Metal.Tests;

public sealed class MetalDeviceAddressIndexTests
{
    private static List<(ulong FirstPage, ulong PageCount, ulong Base)> Table(Random random, int count, ulong span)
    {
        var starts = new SortedSet<ulong>();
        while (starts.Count < count)
        {
            starts.Add((ulong)random.NextInt64(1, (long)span));
        }

        return starts.Select(start => (start, (ulong)random.Next(1, 5000), 0UL)).ToList();
    }

    [Theory]
    [InlineData(1, 100_000UL)]
    [InlineData(50, 4_000_000UL)]
    [InlineData(3000, 600_000UL)]
    [InlineData(4000, 20_000_000_000UL)]
    public void TheIndexedUpperBoundEqualsTheFullBinarySearch(int count, ulong span)
    {
        var random = new Random(count);
        var entries = Table(random, count, span);
        var index = MetalDeviceAddressIndex.Build(entries);
        Assert.True(index[1] <= MetalDeviceAddressIndex.MaxGranules);
        var probes = new List<ulong> { 0, 1, span, span + 1, ulong.MaxValue >> 20 };
        foreach (var (first, pages, _) in entries)
        {
            probes.AddRange([first - 1, first, first + 1, first + pages - 1, first + pages, first + pages + 1]);
        }

        for (var i = 0; i < 20000; i++)
        {
            probes.Add((ulong)random.NextInt64(0, (long)span + 1000));
        }

        foreach (var page in probes)
        {
            Assert.Equal(
                MetalDeviceAddressIndex.UpperBound(entries, [], page),
                MetalDeviceAddressIndex.UpperBound(entries, index, page));
        }
    }

    [Fact]
    public void ADuplicateFirstPageResolvesLikeTheFullSearch()
    {
        var entries = new List<(ulong FirstPage, ulong PageCount, ulong Base)>
        {
            (10, 1, 0), (4096, 1, 0), (4096, 5, 0), (9000, 1, 0),
        };
        var index = MetalDeviceAddressIndex.Build(entries);
        foreach (var page in new ulong[] { 0, 9, 10, 4095, 4096, 4097, 8191, 8192, 9000, 99999 })
        {
            Assert.Equal(
                MetalDeviceAddressIndex.UpperBound(entries, [], page),
                MetalDeviceAddressIndex.UpperBound(entries, index, page));
        }
    }
}
