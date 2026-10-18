// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Gpu.Metal.V2;
using SharpEmu.ShaderCompiler.Metal;
using SharpEmu.ShaderCompiler.Resources;
using Xunit;
using Xunit.Abstractions;

namespace SharpEmu.Gpu.Metal.Tests;

/// <summary>
/// The granule layout of the guest windows without a device: the configured sizes, the
/// window-id sets, the device-address table's runs over granules and windows and its reach
/// flags, and the persisted reach profile across granule sizes.
/// </summary>
public sealed class MetalGuestWindowLayoutTests(ITestOutputHelper output)
{
    private const ulong Mib = 1024UL * 1024;
    private const ulong Gib = 1024 * Mib;

    [Theory]
    [InlineData(null, 32UL)]
    [InlineData("", 32UL)]
    [InlineData("64", 64UL)]
    [InlineData("16", 16UL)]
    [InlineData("1", 1UL)]
    [InlineData("1024", 1024UL)]
    [InlineData("48", 32UL)]
    [InlineData("2048", 32UL)]
    [InlineData("0", 32UL)]
    [InlineData("big", 32UL)]
    public void GranulesAreThirtyTwoMebibytesUnlessConfigured(string? configured, ulong megabytes)
        => Assert.Equal(megabytes * Mib, MetalGuestMemory.ResolveGranuleBytes(configured));

    // SHARPEMU_METAL_V2_WINDOW_MB picked the window size; it now caps the largest window one
    // binding may name, with the same range and the old 1 GiB default.
    [Theory]
    [InlineData(null, 1024UL)]
    [InlineData("4096", 4096UL)]
    [InlineData("512", 512UL)]
    [InlineData("256", 1024UL)]
    [InlineData("8192", 1024UL)]
    public void TheLargestWindowIsOneGibibyteUnlessConfigured(string? configured, ulong megabytes)
        => Assert.Equal(megabytes * Mib, MetalGuestMemory.ResolveMaxWindowBytes(configured));

    [Fact]
    public void AWindowSetGrowsPastSixtyFourAndEnumeratesInOrder()
    {
        var set = new MetalWindowSet();
        Assert.True(set.IsEmpty);
        Assert.True(set.Add(700));
        Assert.True(set.Add(3));
        Assert.False(set.Add(3));
        Assert.True(set.Add(64));
        Assert.Equal([3, 64, 700], set.ToArray());
        Assert.Equal(3, set.Count);

        var other = new MetalWindowSet();
        other.Add(64);
        other.Add(5);
        var both = new MetalWindowSet();
        both.SetIntersection(set, other);
        Assert.Equal([64], both.ToArray());
        var difference = new MetalWindowSet();
        difference.SetDifference(set, other);
        Assert.Equal([3, 700], difference.ToArray());
        Assert.True(set.UnionWith(other));
        Assert.False(set.UnionWith(other));
        Assert.Equal([3, 5, 64, 700], set.ToArray());
        Assert.True(both.IsSubsetOf(set));
        set.Remove(700);
        Assert.False(set.Contains(700));
        Assert.False(set.Contains(-1));
    }

    // An entry in a granule outside the named set carries the flag and the granule above
    // the page count; one inside it carries the page count alone, as the shader expects.
    [Fact]
    public void AnEntryOutsideTheNamedGranulesIsFlaggedWithItsGranule()
    {
        var named = new MetalWindowSet();
        named.Add(7);
        Assert.Equal(0x400UL, MetalDeviceAddressTable.FlaggedPageCount(0x400, 7, named));
        Assert.Equal(0x400UL, MetalDeviceAddressTable.FlaggedPageCount(0x400, 300, null));
        var flagged = MetalDeviceAddressTable.FlaggedPageCount(0x400, 300, named);
        Assert.Equal(0x400UL, flagged & MetalDeviceAddressTable.PageCountMask);
        Assert.NotEqual(0UL, flagged & MetalDeviceAddressTable.UnnamedFlag);
        Assert.Equal(300UL, (flagged >> MetalDeviceAddressTable.GranuleShift) & MetalDeviceAddressTable.MaxGranule);
    }

    // Runs are cut where the backing stops being contiguous and at every granule edge, so
    // each entry belongs to one granule and, while windows are one per granule, one window.
    [Fact]
    public void TableRunsAreCutAtGranuleEdges()
    {
        var layout = new FakeLayout(granuleBits: 25);
        // 100 MiB contiguous from backing 20 MiB: granules 0..3 (edges at 32, 64, 96 MiB).
        layout.Map(0x10_0000_0000, 100 * Mib, 20 * Mib);
        var table = new MetalDeviceAddressTable();
        table.Rebuild(layout, visit => visit(0x10_0000_0000, 100 * Mib), (int)Gen5MslTranslator.MaxAddressRangeCount);

        Assert.Equal([0, 1, 2, 3], table.EntryGranules.ToArray());
        Assert.Equal([0, 1, 2, 3], table.EntryWindows.ToArray());
        var pages = table.Entries.Select(entry => entry.PageCount << DeviceAddressPaging.PageBits).ToArray();
        Assert.Equal([12 * Mib, 32 * Mib, 32 * Mib, 24 * Mib], pages);
        Assert.Equal(100 * Mib, table.SpanBytes);
        Assert.Equal(layout.WindowAt(20 * Mib).GpuAddress, table.Entries[0].Base);
    }

    // A window edge a range moved off a granule edge cuts the runs again, without looking
    // the guest mappings up: each entry lies in one window and points into it, and an entry
    // outside the named windows is flagged with its granule.
    [Fact]
    public void TableEntriesFollowTheWindowEdgesAsTheyMove()
    {
        var layout = new FakeLayout(granuleBits: 25);
        layout.Map(0x10_0000_0000, 100 * Mib, 20 * Mib);
        var table = new MetalDeviceAddressTable();
        table.Rebuild(layout, visit => visit(0x10_0000_0000, 100 * Mib), (int)Gen5MslTranslator.MaxAddressRangeCount);
        Assert.Equal(layout.PartitionVersion, table.PartitionVersion);

        // A range across the 64 MiB edge moves it to 66 MiB.
        Assert.Equal(MetalWindowPartition.Placement.Moved, layout.Place(62 * Mib, 66 * Mib));
        Assert.NotEqual(layout.PartitionVersion, table.PartitionVersion);
        table.Recut(layout);

        var pages = table.Entries.Select(entry => entry.PageCount << DeviceAddressPaging.PageBits).ToArray();
        Assert.Equal([12 * Mib, 32 * Mib, 2 * Mib, 30 * Mib, 24 * Mib], pages);
        Assert.Equal([0, 1, 2, 2, 3], table.EntryGranules.ToArray());
        var windows = table.EntryWindows.ToArray();
        Assert.Equal(windows[1], windows[2]);
        Assert.NotEqual(windows[2], windows[3]);
        Assert.Equal(layout.WindowAt(64 * Mib).GpuAddress, table.Entries[2].Base);
        Assert.Equal(layout.WindowAt(66 * Mib).GpuAddress, table.Entries[3].Base);
        Assert.Equal(4, table.Windows.Count);

        // Name the window holding granule 1 (and the head of granule 2): the entries in the
        // other windows are flagged with their granules.
        var named = new MetalWindowSet();
        var granules = new MetalWindowSet();
        granules.Add(1);
        table.AddWindowsOfGranules(granules, named);
        Assert.Equal([windows[1]], named.ToArray());
        var bytes = new byte[table.ByteSize];
        table.Write(bytes, named);
        var counts = Enumerable.Range(0, table.Count)
            .Select(index => BitConverter.ToUInt64(bytes, (index * MetalDeviceAddressTable.EntryBytes) + 8))
            .ToArray();
        Assert.Equal(0UL, counts[1] & MetalDeviceAddressTable.UnnamedFlag);
        Assert.Equal(0UL, counts[2] & MetalDeviceAddressTable.UnnamedFlag);
        Assert.Equal(MetalDeviceAddressTable.FlaggedPageCount(30 * Mib >> DeviceAddressPaging.PageBits, 2, new MetalWindowSet()), counts[3]);
        Assert.NotEqual(0UL, counts[0] & MetalDeviceAddressTable.UnnamedFlag);
        Assert.NotEqual(0UL, counts[4] & MetalDeviceAddressTable.UnnamedFlag);
    }

    // A Demon's Souls-sized mapping (~11.2 GiB in ~1750 spans over an 18 GiB backing, a
    // twentieth of the spans fragmented in the backing) needs the table entries the 1 GiB
    // layout needed plus about one per granule edge: far inside the 16384 the shader holds.
    [Theory]
    [InlineData(30)]
    [InlineData(25)]
    [InlineData(24)]
    public void ADemonsSoulsSizedMappingFitsTheTable(int granuleBits)
    {
        var (spans, coarse) = DemonsSoulsLike(granuleBits: 30);
        var (_, fine) = DemonsSoulsLike(granuleBits);
        output.WriteLine(
            $"granule {1UL << granuleBits >> 20} MiB: spans={spans} entries={fine.Count} (1 GiB: {coarse.Count}) " +
            $"span_mb={fine.SpanBytes / Mib} granules={fine.Granules.Count}");
        Assert.True(fine.Count < Gen5MslTranslator.MaxAddressRangeCount / 2, $"entries={fine.Count}");
        Assert.True(fine.Count <= coarse.Count + (int)(fine.SpanBytes >> granuleBits) + spans,
            $"entries={fine.Count} coarse={coarse.Count}");
    }

    private static (int Spans, MetalDeviceAddressTable Table) DemonsSoulsLike(int granuleBits)
    {
        var random = new Random(1342);
        var layout = new FakeLayout(granuleBits);
        var spans = new List<(ulong Address, ulong Size)>();
        var guest = 0x70_0000_0000UL;
        var backing = 0UL;
        var page = 64 * 1024UL;
        while (spans.Count < 1750)
        {
            // Mostly small spans, some large ones, mean ~6.5 MiB.
            var size = (ulong)(random.Next(1, 40) * (int)page);
            if (random.Next(10) == 0)
            {
                size = (ulong)random.Next(16, 180) * Mib;
            }

            if (random.Next(20) == 0)
            {
                // Fragmented: eight pieces scattered in the backing.
                var piece = Math.Max(page, (size / 8) & ~(page - 1));
                for (var offset = 0UL; offset < size; offset += piece)
                {
                    var length = Math.Min(piece, size - offset);
                    layout.Map(guest + offset, length, (17 * Gib) + ((ulong)random.Next(0, 4096) * page));
                }
            }
            else
            {
                layout.Map(guest, size, backing);
                backing += size + ((ulong)random.Next(0, 4) * page);
            }

            spans.Add((guest, size));
            guest += size + (2 * Mib);
        }

        var table = new MetalDeviceAddressTable();
        table.Rebuild(layout, visit =>
        {
            foreach (var (address, size) in spans)
            {
                visit(address, size);
            }
        }, (int)Gen5MslTranslator.MaxAddressRangeCount);
        return (spans.Count, table);
    }

    // Released single-copy chunks cut the runs at their edges into trap entries: bit 62 and the
    // entry's first backing page instead of a granule, never the unnamed flag, the rest of the run
    // untouched. A table with no room for the cuts flags nothing and says so.
    [Fact]
    public void TrapChunksAreCutIntoFlaggedEntries()
    {
        const ulong Chunk = 64 * 1024;
        var layout = new FakeLayout(granuleBits: 25);
        // 4 MiB contiguous from backing 20 MiB: one granule, one window.
        layout.Map(0x10_0000_0000, 4 * Mib, 20 * Mib);
        layout.Trap = new byte[(int)(64 * Mib / Chunk)];
        var first = (int)((20 * Mib + Mib) / Chunk);
        layout.Trap[first] = 1;
        layout.Trap[first + 1] = 1;
        layout.Trap[first + 5] = 1;
        var table = new MetalDeviceAddressTable();
        table.Rebuild(layout, visit => visit(0x10_0000_0000, 4 * Mib), (int)Gen5MslTranslator.MaxAddressRangeCount);

        var bytes = table.Entries.Select(entry => entry.PageCount << DeviceAddressPaging.PageBits).ToArray();
        Assert.Equal([Mib, 2 * Chunk, 3 * Chunk, Chunk, 4 * Mib - Mib - 6 * Chunk], bytes);
        Assert.Equal(
            [-1L, (long)((20 * Mib + Mib) >> DeviceAddressPaging.PageBits), -1L,
                (long)((20 * Mib + Mib + 5 * Chunk) >> DeviceAddressPaging.PageBits), -1L],
            table.EntryTrapPages.ToArray());
        Assert.Equal(2, table.TrapEntryCount);
        Assert.Equal(3 * Chunk, table.TrapBytes);
        Assert.Equal(table.Entries[0].Base + Mib, table.Entries[1].Base);

        // Written with no window named: the trap entries carry their page, the rest the unnamed flag.
        var span = new byte[table.ByteSize];
        table.Write(span, new MetalWindowSet());
        var count = BitConverter.ToUInt64(span, MetalDeviceAddressTable.EntryBytes + 8);
        Assert.Equal(MetalDeviceAddressTable.TrapFlag, count & (MetalDeviceAddressTable.TrapFlag | MetalDeviceAddressTable.UnnamedFlag));
        Assert.Equal((20 * Mib + Mib) >> DeviceAddressPaging.PageBits,
            (count >> MetalDeviceAddressTable.GranuleShift) & MetalDeviceAddressTable.MaxTrapPage);
        Assert.Equal(2 * Chunk >> DeviceAddressPaging.PageBits, count & MetalDeviceAddressTable.PageCountMask);
        Assert.NotEqual(0UL, BitConverter.ToUInt64(span, 8) & MetalDeviceAddressTable.UnnamedFlag);

        // The chunks given back: the next cut has one entry again.
        Array.Clear(layout.Trap);
        layout.TrapVersionValue++;
        Assert.NotEqual(layout.TrapVersion, table.TrapVersion);
        table.Recut(layout);
        Assert.Equal(1, table.Count);
        Assert.Equal(0, table.TrapEntryCount);

        // Too many cuts for the table: nothing is flagged, and the overflow is reported.
        for (var chunk = first; chunk < first + 40; chunk += 2)
        {
            layout.Trap[chunk] = 1;
        }

        var small = new MetalDeviceAddressTable();
        small.Rebuild(layout, visit => visit(0x10_0000_0000, 4 * Mib), maxEntries: 8);
        Assert.True(small.TrapOverflowed);
        Assert.Equal(1, small.Count);
        Assert.Equal(0, small.TrapEntryCount);
        Assert.Equal(20, small.TrapUnflagged);
    }

    // Guest mappings onto a backing, as the registry and the alias would report them, and the
    // backing cut into windows; a window's GPU addresses sit at a distinct base per window id.
    private sealed class FakeLayout(int granuleBits) : IMetalGranuleRuns
    {
        private readonly SortedList<ulong, (ulong Size, ulong Backing)> _mappings = new();
        private readonly MetalWindowPartition _partition =
            new(18 * Gib, 1UL << granuleBits, Math.Max(Gib, 1UL << granuleBits), DeviceAddressPaging.PageSize);

        public void Map(ulong guest, ulong size, ulong backing) => _mappings.Add(guest, (size, backing));

        // The trap map: one byte per 64 KiB backing chunk.
        public byte[] Trap = [];

        public long TrapVersionValue;

        public ulong TrapChunkBytes => Trap.Length == 0 ? 0UL : 64UL * 1024;

        public int TrapChunkCount => Trap.Length;

        public long TrapVersion => TrapVersionValue;

        public int CopyTrapChunks(Span<byte> chunks)
        {
            Trap.AsSpan(0, Math.Min(Trap.Length, chunks.Length)).CopyTo(chunks);
            return Trap.Count(static value => value != 0);
        }

        public MetalWindowPartition.Placement Place(ulong start, ulong end) =>
            _partition.Place(start, end, new MetalWindowSet(), new List<MetalWindowPartition.Window>(), out _);

        public long PartitionVersion => _partition.Version;

        public (int Id, ulong End, ulong GpuAddress) WindowAt(ulong backingOffset)
        {
            var window = _partition.At(backingOffset);
            return (window.Id, window.End, ((ulong)(window.Id + 1) << 40) + (backingOffset - window.Start));
        }

        public bool TryResolveGranuleRun(
            ulong gpuVa, ulong size, ulong page, out ulong backingOffset, out ulong length, out int granule)
        {
            backingOffset = length = 0;
            granule = -1;
            var keys = _mappings.Keys;
            var low = 0;
            var high = keys.Count;
            while (low < high)
            {
                var middle = (low + high) / 2;
                if (keys[middle] <= gpuVa) low = middle + 1; else high = middle;
            }

            if (low == 0) return false;
            var index = low - 1;
            var (mappedSize, mappedBacking) = _mappings.Values[index];
            if (gpuVa - keys[index] >= mappedSize) return false;

            var backing = mappedBacking + (gpuVa - keys[index]);
            var contiguous = mappedSize - (gpuVa - keys[index]);
            // A next mapping that continues both the guest range and the backing is the same run.
            for (var next = index + 1; next < keys.Count; next++)
            {
                var previousEnd = keys[next - 1] + _mappings.Values[next - 1].Size;
                var previousBackingEnd = _mappings.Values[next - 1].Backing + _mappings.Values[next - 1].Size;
                if (keys[next] != previousEnd || _mappings.Values[next].Backing != previousBackingEnd) break;
                contiguous += _mappings.Values[next].Size;
            }

            granule = (int)(backing >> granuleBits);
            var granuleEnd = ((ulong)granule + 1) << granuleBits;
            length = Math.Min(Math.Min(size, contiguous), granuleEnd - backing) & ~(page - 1);
            backingOffset = backing;
            return length != 0;
        }
    }

    // A profile learned at 64 MiB reads back as both of its 32 MiB halves; one learned at
    // 16 MiB as the 32 MiB granules holding it. A file without a granule header (the v2
    // window-granular profile) is not trusted.
    [Fact]
    public void AReachProfileConvertsBetweenGranuleSizes()
    {
        Assert.Equal(new HashSet<ulong> { 6, 7, 20, 21 },
            MetalDeviceAddressReachProfile.Convert([3, 10], fromBits: 26, toBits: 25));
        Assert.Equal(new HashSet<ulong> { 1, 2 },
            MetalDeviceAddressReachProfile.Convert([2, 3, 4], fromBits: 24, toBits: 25));

        var directory = Path.Combine(Path.GetTempPath(), $"sharpemu-reach-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, MetalDeviceAddressReachProfile.FileName);
            File.WriteAllText(path, MetalDeviceAddressReachProfile.HeaderLine(26) + "\n3,A\n");
            var profile = MetalDeviceAddressReachProfile.Open(directory, 32 * Mib);
            Assert.NotNull(profile);
            Assert.Equal(new HashSet<ulong> { 6, 7, 20, 21 }, profile.Granules);
            // Rewritten at the granule it is read at.
            var (bits, stored) = MetalDeviceAddressReachProfile.Load(path);
            Assert.Equal(25, bits);
            Assert.Equal(new HashSet<ulong> { 6, 7, 20, 21 }, stored);

            profile.Learn([30, 6]);
            Assert.Contains(30UL, MetalDeviceAddressReachProfile.Load(path).Granules);

            File.WriteAllText(path, "3,A\n");
            Assert.Empty(MetalDeviceAddressReachProfile.Open(directory, 32 * Mib)!.Granules);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
