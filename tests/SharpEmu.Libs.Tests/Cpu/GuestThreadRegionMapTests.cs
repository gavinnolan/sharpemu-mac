// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Core.Cpu.Native;
using SharpEmu.Core.Loader;
using SharpEmu.Core.Memory;
using Xunit;

namespace SharpEmu.Libs.Tests.Cpu;

public sealed class GuestThreadRegionMapTests
{
    private const ulong BaseAddress = 0x6FFF_E000_0000UL;
    private const ulong RegionSize = 0x0020_0000UL;

    [Fact]
    public void ConcurrentMapsNeverHandOutTheSameSlot()
    {
        // A thread being created and another thread mapping its first nested-callback
        // stack both pick a slot. Remapping an existing range does not fail, so two
        // callers that both saw a slot free would share (and zero) one live stack.
        const int callers = 16;
        var memory = new SlowRegionMemory();
        var bases = new ulong[callers];
        using var start = new Barrier(callers);
        var threads = Enumerable.Range(0, callers).Select(index => new Thread(() =>
        {
            start.SignalAndWait();
            Assert.True(DirectExecutionBackend.TryMapGuestThreadRegionForTest(
                memory, BaseAddress, RegionSize, out bases[index]));
        })).ToArray();

        foreach (var thread in threads)
        {
            thread.Start();
        }
        foreach (var thread in threads)
        {
            thread.Join();
        }

        Assert.Equal(callers, bases.Distinct().Count());
    }

    // Like PhysicalVirtualMemory, mapping over an existing range succeeds. The delay
    // between the caller's free-slot check and the region becoming visible stands in
    // for the host allocation and zero-fill.
    private sealed class SlowRegionMemory : IVirtualMemory
    {
        private readonly object _gate = new();
        private readonly List<VirtualMemoryRegion> _regions = [];

        public void Map(ulong virtualAddress, ulong memorySize, ulong fileOffset, ReadOnlySpan<byte> fileData, ProgramHeaderFlags protection)
        {
            Thread.Sleep(5);
            lock (_gate)
            {
                _regions.Add(new VirtualMemoryRegion(virtualAddress, memorySize, fileOffset, 0, protection));
            }
        }

        public IReadOnlyList<VirtualMemoryRegion> SnapshotRegions()
        {
            lock (_gate)
            {
                return _regions.ToArray();
            }
        }

        public void Clear()
        {
        }

        public bool IsBackedView(ulong address) => false;

        public bool TryRead(ulong virtualAddress, Span<byte> destination) => false;

        public bool TryWrite(ulong virtualAddress, ReadOnlySpan<byte> source) => false;
    }
}
