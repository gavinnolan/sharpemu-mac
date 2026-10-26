// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Xunit;

namespace SharpEmu.Gpu.Metal.LeakTests;

// Count live allocation ranges, as heap(1) does. On translated macOS 27.2 the
// malloc_zone_statistics block counter grows by 1-3 per flush even when heap's
// live nodes decrease; it can also wrap through uint.MaxValue. Those counters
// cannot distinguish a leaked command buffer from allocator bookkeeping drift.
internal static unsafe class NativeHeap
{
    internal struct Counts
    {
        public long Blocks;
        public ulong Bytes;
    }

    public static Counts Sample()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        nint* zones;
        uint count;
        var task = mach_task_self();
        Check(malloc_get_all_zones(task, &Read, &zones, &count));
        Counts result = default;
        for (var index = 0; index < count; index++)
            Enumerate(task, zones[index], &result);
        return result;
    }

    internal static Counts CountZone(nint zone)
    {
        Counts result = default;
        Enumerate(mach_task_self(), zone, &result);
        return result;
    }

    private static void Enumerate(uint task, nint zone, Counts* result)
    {
        // malloc/malloc.h's stable zone prefix: two reserved pointers, eight
        // callbacks/name pointers, batch_malloc and batch_free, then introspect.
        // The first introspection function is enumerator(task, context, mask,
        // zone, reader, recorder). All pointers are in this process.
        var introspect = *(nint**)(zone + 12 * IntPtr.Size);
        if (introspect == null || introspect[0] == 0)
            throw new InvalidOperationException("Malloc zone has no allocation enumerator.");
        var enumerate = (delegate* unmanaged[Cdecl]<uint, void*, uint, nuint,
            delegate* unmanaged[Cdecl]<uint, nuint, nuint, void**, int>,
            delegate* unmanaged[Cdecl]<uint, void*, uint, Range*, uint, void>, int>)introspect[0];
        Check(enumerate(task, result, 1, (nuint)zone, &Read, &Record)); // MALLOC_PTR_IN_USE_RANGE_TYPE
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Range
    {
        public nuint Address;
        public nuint Size;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int Read(uint task, nuint address, nuint size, void** memory)
    {
        *memory = (void*)address;
        return 0;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void Record(uint task, void* context, uint type, Range* ranges, uint count)
    {
        var result = (Counts*)context;
        result->Blocks += count;
        for (var index = 0; index < count; index++) result->Bytes += ranges[index].Size;
    }

    private static void Check(int error)
    {
        if (error != 0) throw new InvalidOperationException($"Malloc range enumeration failed: {error}.");
    }

    [DllImport("/usr/lib/libSystem.dylib")]
    private static extern uint mach_task_self();

    [DllImport("/usr/lib/libSystem.dylib")]
    private static extern int malloc_get_all_zones(uint task,
        delegate* unmanaged[Cdecl]<uint, nuint, nuint, void**, int> reader, nint** zones, uint* count);
}

public sealed unsafe class NativeHeapTests
{
    [Fact]
    public void EnumerationCountsLiveBlocksAndTheirReleaseInAnIsolatedZone()
    {
        if (!OperatingSystem.IsMacOS()) return;
        var zone = malloc_create_zone(0, 0);
        Assert.NotEqual(0, zone);
        nint* allocations = stackalloc nint[32];
        var allocated = 0;
        try
        {
            var before = NativeHeap.CountZone(zone);
            for (; allocated < 32; allocated++)
            {
                allocations[allocated] = malloc_zone_malloc(zone, 137);
                Assert.NotEqual(0, allocations[allocated]);
            }
            var live = NativeHeap.CountZone(zone);
            Assert.Equal(before.Blocks + 32, live.Blocks);
            Assert.True(live.Bytes >= before.Bytes + 32 * 137);
            while (allocated > 0) malloc_zone_free(zone, allocations[--allocated]);
            var freed = NativeHeap.CountZone(zone);
            Assert.Equal(before.Blocks, freed.Blocks);
            Assert.Equal(before.Bytes, freed.Bytes);
        }
        finally
        {
            while (allocated > 0) malloc_zone_free(zone, allocations[--allocated]);
            malloc_destroy_zone(zone);
        }
    }

    [DllImport("/usr/lib/libSystem.dylib")]
    private static extern nint malloc_create_zone(nuint size, uint flags);
    [DllImport("/usr/lib/libSystem.dylib")]
    private static extern nint malloc_zone_malloc(nint zone, nuint size);
    [DllImport("/usr/lib/libSystem.dylib")]
    private static extern void malloc_zone_free(nint zone, nint pointer);
    [DllImport("/usr/lib/libSystem.dylib")]
    private static extern void malloc_destroy_zone(nint zone);
}
