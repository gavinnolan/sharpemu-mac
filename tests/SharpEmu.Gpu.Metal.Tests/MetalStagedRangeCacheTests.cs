// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
using SharpEmu.Gpu.Metal.V2;
using Xunit;

namespace SharpEmu.Gpu.Metal.Tests;

/// <summary>
/// The rules a staged range cache keeps without a GPU: what makes an entry stale, what
/// never is dropped, and when a buffer may be written again.
/// </summary>
public sealed class MetalStagedRangeCacheTests
{
    private const ulong Mib = 1024 * 1024;
    private const ulong Range = 0x10_0000_0000;
    private const ulong Size = 512 * 1024;

    private sealed class FakeBackend : IStagedRangeBackend
    {
        public int Created;
        public int Released;
        public readonly List<(ulong Address, ulong Size)> Watches = new();
        private nint _next = 0x1000;

        public (nint Handle, nint Contents, ulong GpuAddress) CreateBuffer(ulong capacity)
        {
            Created++;
            _next += 0x10;
            return (_next, 0, (ulong)_next);
        }

        public void ReleaseBuffer(nint handle) => Released++;

        public void AddWatch(ulong address, ulong size) => Watches.Add((address, size));

        public void RemoveWatch(ulong address, ulong size) => Assert.True(Watches.Remove((address, size)));
    }

    private static MetalStagedRangeCache Cache(FakeBackend backend, ulong budgetMiB = 64) =>
        new(backend, budgetMiB * Mib);

    [Fact]
    public void ASecondBindOfTheSameRangeHitsAndTakesNoBuffer()
    {
        var backend = new FakeBackend();
        var cache = Cache(backend);
        Assert.False(cache.Acquire(Range, Size, written: false, 1, 0, out var first));
        Assert.True(cache.Acquire(Range, Size, written: false, 2, 1, out var second));
        Assert.Same(first, second);
        Assert.Equal(1, backend.Created);
        Assert.Single(backend.Watches);
    }

    [Fact]
    public void ARangeBelowTheThresholdIsNotCached()
    {
        var cache = Cache(new FakeBackend());
        Assert.False(cache.Acquire(Range, 4096, written: false, 1, 0, out var entry));
        Assert.Null(entry);
    }

    [Fact]
    public void EveryKindOfWriteMarksOverlappingEntriesStaleAndDropsTheirWatch()
    {
        foreach (var write in new Action<MetalStagedRangeCache>[]
        {
            c => Assert.True(c.OnCpuWrite(Range + 100, 8)),
            c => c.OnPreciseCpuWrite(Range + 100, 8, backed: true),
            c => c.OnHostWrite(Range + 100, 8),
            c => c.OnGpuWrite(Range + 100, 8, MetalGpuWriteOrigin.Fill),
        })
        {
            var backend = new FakeBackend();
            var cache = Cache(backend);
            cache.Acquire(Range, Size, written: false, 1, 0, out var entry);
            write(cache);
            Assert.Equal(1, entry!.Stale);
            Assert.Empty(backend.Watches);
            Assert.False(cache.Acquire(Range, Size, written: false, 2, 2, out _));
            Assert.Single(backend.Watches);
        }
    }

    [Fact]
    public void AWriteBesideAnEntryLeavesItCurrentButAFaultOnItsPageDoesNot()
    {
        var backend = new FakeBackend();
        var cache = Cache(backend);
        cache.Acquire(Range + 100, Size, written: false, 1, 0, out var entry);
        cache.OnHostWrite(Range, 100);
        cache.OnPreciseCpuWrite(Range + 100 + Size, 4, backed: true);
        Assert.Equal(0, entry!.Stale);
        // A fault is page-sized: the store can only land once the page's watch is gone.
        Assert.True(cache.OnCpuWrite(Range + 100 + Size, 4));
        Assert.Equal(1, entry.Stale);
    }

    [Fact]
    public void AnEntryWithItsOwnStoresIsKeptUntilTheyAreWrittenBack()
    {
        var backend = new FakeBackend();
        var cache = Cache(backend);
        cache.Acquire(Range, Size, written: true, 1, 0, out var entry);
        entry!.Dirty = true;
        cache.OnGpuWrite(Range, 16, MetalGpuWriteOrigin.Fill);
        cache.OnCpuWrite(Range, 8);
        Assert.Equal(0, entry.Stale);
        Assert.True(cache.Acquire(Range, Size, written: true, 2, 1, out _));
        cache.OnWrittenBack(entry);
        Assert.Equal(1, entry.Stale);
        Assert.False(cache.Acquire(Range, Size, written: false, 3, 3, out _));
    }

    [Fact]
    public void AnEntrysOwnStoresAndWritebackDoNotStaleIt()
    {
        var cache = Cache(new FakeBackend());
        cache.Acquire(Range, Size, written: true, 1, 0, out var entry);
        entry!.Dirty = true;
        cache.OnGpuWrite(Range, Size, MetalGpuWriteOrigin.StorageBuffer);
        cache.MarkSelfWriteback(entry);
        cache.OnGpuWrite(Range, Size, MetalGpuWriteOrigin.StorageBuffer);
        cache.OnWrittenBack(entry);
        Assert.Equal(0, entry.Stale);
        Assert.True(cache.Acquire(Range, Size, written: false, 2, 1, out _));
    }

    [Fact]
    public void ABufferAnInFlightSubmissionReadsIsNeverRewritten()
    {
        var backend = new FakeBackend();
        var cache = Cache(backend);
        cache.Acquire(Range, Size, written: false, 5, 0, out var entry);
        var before = entry!.Staging.Buffer;
        cache.OnHostWrite(Range, 8);

        // Submission 5 has not completed: the restaged entry gets another buffer.
        cache.Acquire(Range, Size, written: false, 6, 4, out _);
        Assert.NotEqual(before, entry.Staging.Buffer);
        Assert.Equal(1, cache.RetiredCount);

        // Once 5 completes its buffer is reusable: submission 6's restage retires, 7's reuses.
        cache.OnHostWrite(Range, 8);
        cache.Acquire(Range, Size, written: false, 7, 6, out _);
        Assert.Equal(2, backend.Created);
    }

    [Fact]
    public void OverBudgetTheLeastRecentlyUsedIdleEntryGoesAndNothingInFlightDoes()
    {
        var backend = new FakeBackend();
        var cache = Cache(backend, budgetMiB: 2);
        cache.Acquire(Range, Size, written: false, 1, 0, out var oldest);
        cache.Acquire(Range + 8 * Mib, Size, written: false, 2, 0, out var newer);

        // Both are still in flight: the cache is full and says so.
        Assert.False(cache.Acquire(Range + 16 * Mib, Size, written: false, 3, 0, out var refused));
        Assert.Null(refused);

        // With submission 1 done, the oldest goes.
        Assert.False(cache.Acquire(Range + 16 * Mib, Size, written: false, 3, 1, out var third));
        Assert.NotNull(third);
        Assert.True(oldest!.Removed);
        Assert.False(newer!.Removed);
        Assert.Equal(1, cache.TakeCounters().Evictions);
    }

    [Fact]
    public void ADirtyEntryIsNotEvicted()
    {
        var cache = Cache(new FakeBackend(), budgetMiB: 2);
        cache.Acquire(Range, Size, written: true, 1, 0, out var dirty);
        dirty!.Dirty = true;
        Assert.False(cache.Acquire(Range + 8 * Mib, Size, written: false, 2, 5, out var other));
        Assert.Null(other);
        Assert.False(dirty.Removed);
    }

    [Fact]
    public void UnmapDropsEntriesWithTheirWatchAndRetiresTheirBuffersBySerial()
    {
        var backend = new FakeBackend();
        var cache = Cache(backend);
        cache.Acquire(Range, Size, written: true, 4, 0, out var entry);
        entry!.Dirty = true;
        cache.OnUnmap(Range, Size);
        Assert.True(entry.Removed);
        Assert.Empty(backend.Watches);
        Assert.Equal(0, cache.EntryCount);
        Assert.Equal(2, cache.RetiredCount);

        // A new entry cannot take those buffers until submission 4 completes.
        cache.Acquire(Range, Size, written: false, 5, 3, out _);
        Assert.Equal(3, backend.Created);
    }

    [Fact]
    public void AReadOnlyEntryIsRestagedBeforeAWrittenBindingUsesIt()
    {
        var cache = Cache(new FakeBackend());
        cache.Acquire(Range, Size, written: false, 1, 0, out var entry);
        Assert.False(entry!.Pristine.IsValid);
        Assert.False(cache.Acquire(Range, Size, written: true, 2, 1, out _));
        Assert.True(entry.Pristine.IsValid);
    }
}
