// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
using System.Runtime.InteropServices;
using SharpEmu.Gpu.Metal.V2;
using Xunit;

namespace SharpEmu.Gpu.Metal.Tests;

/// <summary>
/// A guest store into a large staged entry marks its own pages, and the next bind copies those pages alone.
/// The tests keep a guest of real bytes next to the cache's buffers, so what they check is the staging itself:
/// after a bind it holds exactly what the guest holds, however few bytes the bind copied.
/// </summary>
public sealed unsafe class MetalStagedRangePartialTests : IDisposable
{
    private const ulong Mib = 1024 * 1024;
    private const ulong Page = 4096;
    private const ulong Range = 0x10_0000_0000;
    private const ulong Size = 4 * Mib;

    // Real buffers, and a watch model that counts watchers per page the way the page guard does.
    private sealed class Backend : IStagedRangeBackend, IDisposable
    {
        private readonly Dictionary<nint, nint> _buffers = new();
        private nint _next = 0x1000;
        public readonly Dictionary<ulong, int> WatchesByPage = new();
        public int Released;

        public (nint Handle, nint Contents, ulong GpuAddress) CreateBuffer(ulong capacity)
        {
            var memory = (nint)NativeMemory.AlignedAlloc((nuint)capacity, 16384);
            _next += 0x10;
            _buffers[_next] = memory;
            return (_next, memory, (ulong)_next);
        }

        public void ReleaseBuffer(nint handle) => Released++;

        public void AddWatch(ulong address, ulong size)
        {
            for (var page = address & ~(Page - 1); page < address + size; page += Page)
            {
                WatchesByPage[page] = WatchesByPage.GetValueOrDefault(page) + 1;
            }
        }

        public void RemoveWatch(ulong address, ulong size)
        {
            for (var page = address & ~(Page - 1); page < address + size; page += Page)
            {
                var count = WatchesByPage.GetValueOrDefault(page);
                Assert.True(count > 0, $"watch removed from an unwatched page 0x{page:X}");
                if (count == 1)
                {
                    WatchesByPage.Remove(page);
                }
                else
                {
                    WatchesByPage[page] = count - 1;
                }
            }
        }

        public void Dispose()
        {
            foreach (var memory in _buffers.Values)
            {
                NativeMemory.AlignedFree((void*)memory);
            }
        }
    }

    private readonly Backend _backend = new();
    private readonly byte[] _guest;
    private long _guestBytesRead;

    public MetalStagedRangePartialTests()
    {
        _guest = new byte[Size + 2 * Page];
        new Random(7).NextBytes(_guest);
    }

    public void Dispose() => _backend.Dispose();

    private MetalStagedRangeCache Cache(bool partial = true) => new(_backend, 64 * Mib, partial);

    private void Read(ulong address, Span<byte> destination)
    {
        _guest.AsSpan((int)(address - Range), destination.Length).CopyTo(destination);
        _guestBytesRead += destination.Length;
    }

    // What MetalRenderHost.BindStagedRange does, minus the GPU: acquire, then copy what Acquire left to copy.
    private bool Bind(MetalStagedRangeCache cache, ulong address, ulong size, bool written, ulong serial,
        ulong completed, out MetalStagedRangeCache.Entry entry)
    {
        var hit = cache.Acquire(address, size, written, serial, completed, out var acquired);
        entry = acquired!;
        if (hit)
        {
            return true;
        }

        if (!cache.RefillDirty(entry, Read))
        {
            Read(address, Staging(entry, size));
            if (entry.Pristine.IsValid)
            {
                Staging(entry, size).CopyTo(new Span<byte>((void*)entry.PristineBuffer!.Contents, (int)size));
            }
        }

        return false;
    }

    private static Span<byte> Staging(MetalStagedRangeCache.Entry entry, ulong size) =>
        new((void*)entry.StagingBuffer!.Contents, (int)size);

    private ReadOnlySpan<byte> GuestBytes(ulong address, ulong size) => _guest.AsSpan((int)(address - Range), (int)size);

    private void Store(ulong address, byte value) => _guest[(int)(address - Range)] = value;

    private int WatchedPages(MetalStagedRangeCache.Entry entry)
    {
        var count = 0;
        for (var page = entry.Address & ~(Page - 1); page < entry.Address + entry.Size; page += Page)
        {
            Assert.InRange(_backend.WatchesByPage.GetValueOrDefault(page), 0, 1);
            count += _backend.WatchesByPage.ContainsKey(page) ? 1 : 0;
        }

        return count;
    }

    private static int PagesOf(ulong address, ulong size) =>
        (int)(((address + size + Page - 1) & ~(Page - 1)) - (address & ~(Page - 1))) / (int)Page;

    [Fact]
    public void AStoreIntoOnePageCopiesThatPageAndNothingElse()
    {
        var cache = Cache();
        Bind(cache, Range, Size, written: false, 1, 0, out var entry);
        Assert.Equal(PagesOf(Range, Size), WatchedPages(entry));

        // The clean pages are poisoned in the staging: a copy of them would show.
        var staging = Staging(entry, Size);
        var cleanBefore = staging.ToArray();
        var storeAt = Range + 300 * Page + 40;
        Store(storeAt, 0x5A);
        Assert.True(cache.OnCpuWrite(storeAt, 4));

        // The watch over the stored-into page is gone, so the store can land; the others stay.
        Assert.Equal(PagesOf(Range, Size) - 1, WatchedPages(entry));
        Assert.False(_backend.WatchesByPage.ContainsKey(Range + 300 * Page));
        Assert.Equal(1, entry.Stale);

        _guestBytesRead = 0;
        Assert.False(Bind(cache, Range, Size, written: false, 2, 1, out var again));
        Assert.Same(entry, again);
        Assert.Equal((long)Page, _guestBytesRead);
        Assert.True(staging.SequenceEqual(GuestBytes(Range, Size)));
        Assert.NotEqual(cleanBefore[300 * (int)Page + 40], staging[300 * (int)Page + 40]);
        Assert.Equal(PagesOf(Range, Size), WatchedPages(entry));
        Assert.Equal(0, entry.Stale);
        Assert.True(Bind(cache, Range, Size, written: false, 3, 2, out _));

        var counters = cache.PartialCounters;
        Assert.Equal((1L, (long)Page, (long)(Size - Page), 0L), counters);
    }

    [Fact]
    public void AStoreThatLandsAfterTheBindIsNotLost()
    {
        var cache = Cache();
        Bind(cache, Range, Size, written: false, 1, 0, out var entry);
        Store(Range + 5 * Page, 1);
        cache.OnCpuWrite(Range + 5 * Page, 8);
        Bind(cache, Range, Size, written: false, 2, 1, out _);

        // The page was watched again before it was copied: a second store marks it again.
        Store(Range + 5 * Page + 8, 2);
        Assert.True(cache.OnCpuWrite(Range + 5 * Page + 8, 8));
        Assert.Equal(1, entry.Stale);
        Assert.False(Bind(cache, Range, Size, written: false, 3, 2, out _));
        Assert.True(Staging(entry, Size).SequenceEqual(GuestBytes(Range, Size)));
    }

    [Fact]
    public void AnUnalignedEntryRefillsTheClippedFirstAndLastPages()
    {
        var cache = Cache();
        var address = Range + 0x100;
        var size = Size - 0x300;
        Bind(cache, address, size, written: false, 1, 0, out var entry);
        Assert.Equal(PagesOf(address, size), WatchedPages(entry));

        // A store on the first page and one on the page the entry ends in, past its last byte.
        Store(address + 8, 3);
        Store(address + size - 8, 4);
        cache.OnCpuWrite(address + 8, 4);
        cache.OnCpuWrite(address + size - 8, 4);
        Assert.Equal(PagesOf(address, size) - 2, WatchedPages(entry));

        _guestBytesRead = 0;
        Bind(cache, address, size, written: false, 2, 1, out _);
        // The first page holds Page - 0x100 of the entry; the last holds the rest up to its end.
        var lastPageBytes = (address + size) & (Page - 1);
        Assert.Equal((long)(Page - 0x100 + lastPageBytes), _guestBytesRead);
        Assert.True(Staging(entry, size).SequenceEqual(GuestBytes(address, size)));
        Assert.Equal(PagesOf(address, size), WatchedPages(entry));
    }

    [Fact]
    public void APreciseWriteMarksOnlyThePagesItsBytesLieOn()
    {
        var cache = Cache();
        Bind(cache, Range, Size, written: false, 1, 0, out var entry);
        for (var offset = 0UL; offset < 8; offset++)
        {
            Store(Range + 10 * Page - 4 + offset, (byte)(0x40 + offset));
        }

        Assert.True(cache.OnPreciseCpuWrite(Range + 10 * Page - 4, 8, backed: true));
        Assert.Equal(PagesOf(Range, Size) - 2, WatchedPages(entry));
        _guestBytesRead = 0;
        Bind(cache, Range, Size, written: false, 2, 1, out _);
        Assert.Equal((long)(2 * Page), _guestBytesRead);
        Assert.True(Staging(entry, Size).SequenceEqual(GuestBytes(Range, Size)));
    }

    [Fact]
    public void AnEntryAnInFlightSubmissionReadsKeepsItsBytesAndIsStagedWholeIntoAnotherBuffer()
    {
        var cache = Cache();
        Bind(cache, Range, Size, written: false, 5, 0, out var entry);
        var oldBuffer = entry.StagingBuffer!;
        var oldBytes = Staging(entry, Size).ToArray();

        Store(Range + 700 * Page + 9, 0xEE);
        cache.OnCpuWrite(Range + 700 * Page + 9, 4);

        // Submission 5 has not completed: the entry gets another buffer, and the old one is left as it was.
        _guestBytesRead = 0;
        Bind(cache, Range, Size, written: false, 6, 4, out _);
        Assert.NotSame(oldBuffer, entry.StagingBuffer);
        Assert.True(new ReadOnlySpan<byte>((void*)oldBuffer.Contents, (int)Size).SequenceEqual(oldBytes));
        Assert.Equal(1, cache.RetiredCount);
        Assert.Equal((long)Size, _guestBytesRead);
        Assert.True(Staging(entry, Size).SequenceEqual(GuestBytes(Range, Size)));
        Assert.Equal(0, cache.PartialCounters.Refills);
        Assert.Equal(PagesOf(Range, Size), WatchedPages(entry));
    }

    [Fact]
    public void AWrittenEntryInFlightIsStagedWhole()
    {
        var cache = Cache();
        Bind(cache, Range, Size, written: true, 5, 0, out var entry);

        Store(Range + 3 * Page, 1);
        cache.OnCpuWrite(Range + 3 * Page, 4);
        _guestBytesRead = 0;
        Bind(cache, Range, Size, written: true, 6, 4, out _);
        Assert.Equal((long)Size, _guestBytesRead);
        Assert.True(Staging(entry, Size).SequenceEqual(GuestBytes(Range, Size)));
        Assert.Equal(0, cache.PartialCounters.Refills);
    }

    [Fact]
    public void AWrittenEntryRefillsBothItsStagingAndItsPristineTwin()
    {
        var cache = Cache();
        Bind(cache, Range, Size, written: true, 1, 0, out var entry);
        Store(Range + 40 * Page + 1, 0x77);
        Store(Range + 41 * Page + 1, 0x78);
        cache.OnCpuWrite(Range + 40 * Page + 1, 1);
        cache.OnCpuWrite(Range + 41 * Page + 1, 1);

        _guestBytesRead = 0;
        Assert.False(Bind(cache, Range, Size, written: true, 2, 1, out _));
        Assert.Equal((long)(2 * Page), _guestBytesRead);
        Assert.True(Staging(entry, Size).SequenceEqual(GuestBytes(Range, Size)));
        Assert.True(new ReadOnlySpan<byte>((void*)entry.PristineBuffer!.Contents, (int)Size)
            .SequenceEqual(GuestBytes(Range, Size)));
    }

    [Fact]
    public void StoresToManyPagesStaleTheEntryWholeAndASmallerWriterIsTrackedAgainLater()
    {
        var cache = Cache();
        Bind(cache, Range, Size, written: false, 1, 0, out var entry);

        // A wholesale rewrite: past the limit the entry is staled whole and every watch is dropped.
        for (var page = 0; page < MetalStagedRangeCache.MaxDirtyPages + 1; page++)
        {
            Store(Range + (ulong)page * Page, 9);
            cache.OnCpuWrite(Range + (ulong)page * Page, 4);
        }

        Assert.Empty(_backend.WatchesByPage);
        Assert.Equal(1, cache.PartialCounters.Fallbacks);
        _guestBytesRead = 0;
        Bind(cache, Range, Size, written: false, 2, 1, out _);
        Assert.Equal((long)Size, _guestBytesRead);
        Assert.True(Staging(entry, Size).SequenceEqual(GuestBytes(Range, Size)));

        // For the next few stagings (the whole one above was the first) the first store drops the whole
        // watch, as it always did.
        for (var staging = 0; staging < MetalStagedRangeCache.PartialHoldStagings - 1; staging++)
        {
            Store(Range + Page, (byte)staging);
            cache.OnCpuWrite(Range + Page, 4);
            Assert.Empty(_backend.WatchesByPage);
            Bind(cache, Range, Size, written: false, (ulong)(3 + staging), (ulong)(2 + staging), out _);
        }

        // Then one store marks one page once more.
        Store(Range + Page, 0xAB);
        cache.OnCpuWrite(Range + Page, 4);
        Assert.Equal(PagesOf(Range, Size) - 1, WatchedPages(entry));
        _guestBytesRead = 0;
        Bind(cache, Range, Size, written: false, 20, 19, out _);
        Assert.Equal((long)Page, _guestBytesRead);
        Assert.True(Staging(entry, Size).SequenceEqual(GuestBytes(Range, Size)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void AnyOtherWriteAfterAPartialMarkStalesTheEntryWholeAndReleasesExactlyTheWatchesItHolds(int kind)
    {
        var cache = Cache();
        Bind(cache, Range, Size, written: false, 1, 0, out var entry);
        Store(Range + 9 * Page, 1);
        cache.OnCpuWrite(Range + 9 * Page, 4);
        Assert.Equal(PagesOf(Range, Size) - 1, WatchedPages(entry));

        // The host or the GPU wrote some other bytes of the entry: nothing says which pages the copy still has right.
        var other = Range + 500 * Page;
        for (var offset = 0UL; offset < 64; offset++)
        {
            Store(other + offset, 0x33);
        }

        switch (kind)
        {
            case 0:
                cache.OnHostWrite(other, 64);
                break;
            case 1:
                cache.OnGpuWrite(other, 64, MetalGpuWriteOrigin.Fill);
                break;
            default:
                cache.OnUnmap(Range, Size);
                Assert.Empty(_backend.WatchesByPage);
                return;
        }

        Assert.Empty(_backend.WatchesByPage);
        _guestBytesRead = 0;
        Bind(cache, Range, Size, written: false, 2, 1, out _);
        Assert.Equal((long)Size, _guestBytesRead);
        Assert.True(Staging(entry, Size).SequenceEqual(GuestBytes(Range, Size)));
        Assert.Equal(PagesOf(Range, Size), WatchedPages(entry));
    }

    [Fact]
    public void StoresOfAnEntryThatNeverReachedGuestMemoryAreNotKeptByAPartialRefill()
    {
        // A copy whose shader stores were lost (no writeback) differs from guest memory on pages no CPU store touched.
        var cache = Cache();
        Bind(cache, Range, Size, written: true, 1, 0, out var entry);
        entry.Dirty = true;
        var staged = new MetalStagedStorage(null!, cache);
        staged.Bind(entry, written: true);
        staged.Arm();
        staged.EndSubmission();
        Assert.False(entry.Dirty);
        Assert.Equal(1, entry.Stale);
        Assert.True(entry.NeedsWholeStage);

        // A CPU store now must not turn that into a one-page refill.
        Store(Range + 2 * Page, 5);
        cache.OnCpuWrite(Range + 2 * Page, 4);
        _guestBytesRead = 0;
        Bind(cache, Range, Size, written: true, 2, 1, out _);
        Assert.Equal((long)Size, _guestBytesRead);
    }

    [Fact]
    public void AStoreWhileTheEntryHasStoresOfItsOwnStalesItWholeOnceTheyAreWrittenBack()
    {
        var cache = Cache();
        Bind(cache, Range, Size, written: true, 1, 0, out var entry);
        entry.Dirty = true;
        Store(Range + 2 * Page, 5);
        cache.OnCpuWrite(Range + 2 * Page, 4);
        cache.OnWrittenBack(entry);
        Assert.Equal(1, entry.Stale);
        Assert.True(entry.NeedsWholeStage);
        _guestBytesRead = 0;
        Bind(cache, Range, Size, written: true, 2, 1, out _);
        Assert.Equal((long)Size, _guestBytesRead);
        Assert.True(Staging(entry, Size).SequenceEqual(GuestBytes(Range, Size)));
    }

    [Fact]
    public void AnEntryBelowTheThresholdIsStaledWholeAsBefore()
    {
        var cache = Cache();
        var size = MetalStagedRangeCache.PartialMinBytes - Page;
        Bind(cache, Range, size, written: false, 1, 0, out var entry);
        cache.OnCpuWrite(Range + 5 * Page, 4);
        Assert.Empty(_backend.WatchesByPage);
        _guestBytesRead = 0;
        Bind(cache, Range, size, written: false, 2, 1, out _);
        Assert.Equal((long)size, _guestBytesRead);
        Assert.Equal(0, cache.PartialCounters.Refills);
    }

    [Fact]
    public void WithPartialOffEveryStoreStalesTheWholeEntry()
    {
        var cache = Cache(partial: false);
        Bind(cache, Range, Size, written: false, 1, 0, out _);
        cache.OnCpuWrite(Range + 5 * Page, 4);
        Assert.Empty(_backend.WatchesByPage);
        _guestBytesRead = 0;
        Bind(cache, Range, Size, written: false, 2, 1, out _);
        Assert.Equal((long)Size, _guestBytesRead);
    }

    [Fact]
    public void ADisposedOrEvictedEntryReleasesTheWatchesOfItsCleanPagesOnly()
    {
        var cache = Cache();
        Bind(cache, Range, Size, written: false, 1, 0, out _);
        Store(Range + 9 * Page, 1);
        cache.OnCpuWrite(Range + 9 * Page, 4);
        cache.StopWatching();
        Assert.Empty(_backend.WatchesByPage);
        cache.Dispose();
    }

    // Random stores, host and GPU writes and binds, in flight or not, read-only or written, over entries
    // with unaligned edges. Whatever the sequence, a bind leaves the staging equal to the guest, and every
    // live entry holds exactly one watch on each page that no store has marked.
    [Theory]
    [InlineData(11, true)]
    [InlineData(23, true)]
    [InlineData(37, false)]
    public void RandomWritesAndBindsKeepTheStagingEqualToGuestMemory(int seed, bool partial)
    {
        var random = new Random(seed);
        var cache = Cache(partial);
        var address = Range + 0x240;
        var size = Size - 0x980;
        ulong serial = 3;
        var written = false;
        for (var step = 0; step < 600; step++)
        {
            switch (random.Next(10))
            {
                case 0:
                case 1:
                case 2:
                case 3:
                {
                    var at = address + (ulong)random.NextInt64((long)size - 16);
                    var length = (ulong)random.Next(1, 16);
                    for (var i = 0UL; i < length; i++)
                    {
                        Store(at + i, (byte)random.Next(256));
                    }

                    if (random.Next(3) == 0)
                    {
                        cache.OnPreciseCpuWrite(at, length, backed: random.Next(2) == 0);
                    }
                    else
                    {
                        cache.OnCpuWrite(at, length);
                    }

                    break;
                }
                case 4:
                {
                    // A burst: enough pages to pass the limit now and then.
                    var pages = random.Next(1, 48);
                    var first = (ulong)random.Next(0, 800);
                    for (var page = 0; page < pages; page++)
                    {
                        var at = Range + (first + (ulong)page) * Page + 17;
                        if (at - address < size)
                        {
                            Store(at, (byte)random.Next(256));
                            cache.OnCpuWrite(at, 4);
                        }
                    }

                    break;
                }
                case 5:
                {
                    var at = address + (ulong)random.NextInt64((long)size - 64);
                    Store(at, (byte)random.Next(256));
                    if (random.Next(2) == 0)
                    {
                        cache.OnHostWrite(at, 64);
                    }
                    else
                    {
                        cache.OnGpuWrite(at, 64, MetalGpuWriteOrigin.Fill);
                    }

                    break;
                }
                default:
                {
                    written = random.Next(4) == 0;
                    var completed = random.Next(3) == 0 ? serial - 2 : serial - 1;
                    Bind(cache, address, size, written, serial, completed, out var entry);
                    Assert.True(Staging(entry, size).SequenceEqual(GuestBytes(address, size)), $"step {step}: staging differs");
                    if (entry.Pristine.IsValid)
                    {
                        Assert.True(new ReadOnlySpan<byte>((void*)entry.PristineBuffer!.Contents, (int)size)
                            .SequenceEqual(GuestBytes(address, size)), $"step {step}: pristine differs");
                    }

                    serial++;
                    break;
                }
            }

            foreach (var count in _backend.WatchesByPage.Values)
            {
                Assert.Equal(1, count);
            }
        }

        if (partial)
        {
            Assert.True(cache.PartialCounters.Refills > 0);
        }
    }
}
