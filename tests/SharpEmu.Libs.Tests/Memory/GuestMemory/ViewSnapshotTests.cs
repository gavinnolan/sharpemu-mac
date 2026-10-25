// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE.GuestMemory;
using SharpEmu.HLE.Host;
using Xunit;
using ViewSnapshot = SharpEmu.HLE.GuestMemory.SharedBackingViews.ViewSnapshot;

namespace SharpEmu.Libs.Tests.Memory.GuestMemory;

/// <summary>
/// The chunked copy of the view records that single-view accesses search: rebuilt over only the
/// range that changed, it holds exactly the records a full copy would, in order, and finds the same
/// record at or below any address.
/// </summary>
public sealed class ViewSnapshotTests
{
    [Theory]
    [InlineData(1, 40)]
    [InlineData(2, 900)]
    [InlineData(3, 3000)]
    [InlineData(4, 7000)]
    public void ARebuiltSnapshotHoldsWhatAFullCopyHolds(int seed, int initial)
    {
        var random = new System.Random(seed);
        var views = new SortedList<ulong, ViewRecord>();
        const ulong Page = 0x4000;
        for (var index = 0; index < initial; index++)
        {
            var address = (ulong)random.Next(0, 1 << 20) * Page;
            views[address] = new ViewRecord(address, Page * (ulong)random.Next(1, 4), address, HostPageProtection.ReadWrite);
        }

        var snapshot = ViewSnapshot.Build(views);
        for (var change = 0; change < 400; change++)
        {
            var low = ulong.MaxValue;
            var high = 0UL;
            void Note(in ViewRecord record)
            {
                low = Math.Min(low, record.Address);
                high = Math.Max(high, record.Address + record.Size - 1);
            }

            for (var step = random.Next(1, 4); step > 0; step--)
            {
                switch (random.Next(3))
                {
                    case 0:
                    {
                        var address = (ulong)random.Next(0, 1 << 20) * Page;
                        if (views.TryGetValue(address, out var replaced))
                        {
                            Note(replaced);
                        }

                        var added = new ViewRecord(address, Page * (ulong)random.Next(1, 4), address, HostPageProtection.ReadWrite);
                        views[address] = added;
                        Note(added);
                        break;
                    }
                    case 1 when views.Count != 0:
                    {
                        var index = random.Next(views.Count);
                        Note(views.Values[index]);
                        views.RemoveAt(index);
                        break;
                    }
                    case 2 when views.Count != 0:
                    {
                        // Two records next to each other change together, as an unmap that splits one does.
                        var index = random.Next(views.Count);
                        var record = views.Values[index];
                        Note(record);
                        var trimmed = record with { Size = Page };
                        views[record.Address] = trimmed;
                        Note(trimmed);
                        var tail = record.Address + Page * 2;
                        if (!views.ContainsKey(tail))
                        {
                            var split = new ViewRecord(tail, Page, tail, HostPageProtection.ReadOnly);
                            views[tail] = split;
                            Note(split);
                        }

                        break;
                    }
                }
            }

            snapshot = low > high ? snapshot.Republished() : snapshot.Rebuilt(views, low, high);
            AssertHolds(views, snapshot, random);
        }
    }

    [Fact]
    public void EveryPublicationIsANewObject()
    {
        var views = new SortedList<ulong, ViewRecord> { [0x1000] = new(0x1000, 0x1000, 0, HostPageProtection.ReadWrite) };
        var snapshot = ViewSnapshot.Build(views);
        Assert.NotSame(snapshot, snapshot.Republished());
        Assert.NotSame(ViewSnapshot.Build(new()), ViewSnapshot.Build(new()));
    }

    private static void AssertHolds(SortedList<ulong, ViewRecord> views, ViewSnapshot snapshot, System.Random random)
    {
        Assert.Equal(views.Count, snapshot.Count);
        Assert.True(snapshot.ChunkCount <= 2 * (views.Count / ViewSnapshot.ChunkRecords) + 9);
        if (views.Count != 0)
        {
            Assert.True(snapshot.TryFind(views.Keys[0], out var chunk, out var index));
            var position = 0;
            do
            {
                Assert.Equal(views.Values[position], snapshot.At(chunk, index));
                position++;
            }
            while (snapshot.TryNext(ref chunk, ref index));

            Assert.Equal(views.Count, position);
        }

        for (var probe = 0; probe < 64; probe++)
        {
            var address = (ulong)random.NextInt64(0, (1L << 20) * 0x4000 + 0x10000);
            var expected = -1;
            var keys = views.Keys;
            int low = 0, high = keys.Count - 1;
            while (low <= high)
            {
                var middle = (low + high) / 2;
                if (keys[middle] <= address)
                {
                    expected = middle;
                    low = middle + 1;
                }
                else
                {
                    high = middle - 1;
                }
            }

            var found = snapshot.TryFind(address, out var foundChunk, out var foundIndex);
            Assert.Equal(expected >= 0, found);
            if (found)
            {
                Assert.Equal(views.Values[expected], snapshot.At(foundChunk, foundIndex));
            }
        }
    }
}
