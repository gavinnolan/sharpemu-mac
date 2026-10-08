// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
using System.Globalization;

namespace SharpEmu.Gpu.Metal;

internal static partial class MetalPresentationProfile
{
    internal static readonly bool AllocationsEnabled =
        Environment.GetEnvironmentVariable("SHARPEMU_PROFILE_ALLOCATIONS") == "1";
    internal enum DrainWorkKind { Other, Draw, Compute }
    private enum AllocationCounter { Bytes, Calls, Work, Draws, Compute, Failed, Discarded, Count }
    private const int AllocationStride = (int)AllocationCounter.Count + 1;

    // Initialized only by an enabled presenter session. Separate rows preserve
    // the existing METAL_PRESENT_* schema and support allocation-only capture.
    private static class AllocationStorage
    {
        // Prevent beforefieldinit from permitting eager storage initialization
        // on a disabled branch merely because the method mentions these fields.
        static AllocationStorage() { }
        internal static readonly long[] Pending = new long[(int)AllocationCounter.Count];
        internal static readonly long[] Rows = new long[Capacity * AllocationStride];
        internal static long Count, Started, Epoch;
        internal static int OwnerThread, Depth;
        internal static bool InvalidScope;
    }

    private static void BeginAllocations()
    {
        Array.Clear(AllocationStorage.Pending);
        Array.Clear(AllocationStorage.Rows);
        AllocationStorage.Count = AllocationStorage.Started = 0;
        AllocationStorage.Depth = 0;
        AllocationStorage.Epoch++;
        AllocationStorage.InvalidScope = false;
        AllocationStorage.OwnerThread = Environment.CurrentManagedThreadId;
    }

    internal readonly struct DrainAllocationScope(bool accepted, bool outer, long epoch, int depth)
    {
        internal void Finish(bool completed)
        {
            if (!AllocationsEnabled) return;
            if (!accepted || Environment.CurrentManagedThreadId != AllocationStorage.OwnerThread)
            {
                Interlocked.Increment(ref AllocationStorage.Pending[(int)AllocationCounter.Discarded]);
                if (accepted) Volatile.Write(ref AllocationStorage.InvalidScope, true);
                return; // current-thread counters cannot measure another thread
            }
            if (epoch != AllocationStorage.Epoch || depth != AllocationStorage.Depth ||
                Volatile.Read(ref AllocationStorage.InvalidScope))
            {
                Interlocked.Increment(ref AllocationStorage.Pending[(int)AllocationCounter.Discarded]);
                Volatile.Write(ref AllocationStorage.InvalidScope, true);
                return;
            }
            if (outer)
            {
                // Capture before formatting/export and before any instrumentation
                // bookkeeping. Nested drains are included once by this delta.
                var bytes = GC.GetAllocatedBytesForCurrentThread() - AllocationStorage.Started;
                if (bytes >= 0) AllocationStorage.Pending[(int)AllocationCounter.Bytes] += bytes;
                else Interlocked.Increment(ref AllocationStorage.Pending[(int)AllocationCounter.Discarded]);
            }
            if (!completed) AllocationStorage.Pending[(int)AllocationCounter.Failed]++;
            AllocationStorage.Depth--;
        }
    }

    internal static DrainAllocationScope MeasureDrainAllocations()
    {
        if (!AllocationsEnabled) return default; // no counter/storage/thread read
        if (Environment.CurrentManagedThreadId != AllocationStorage.OwnerThread) return default;
        if (Volatile.Read(ref AllocationStorage.InvalidScope))
        {
            AllocationStorage.Depth = 0;
            AllocationStorage.InvalidScope = false;
        }
        var outer = AllocationStorage.Depth++ == 0;
        if (outer) AllocationStorage.Epoch++;
        AllocationStorage.Pending[(int)AllocationCounter.Calls]++;
        if (outer) AllocationStorage.Started = GC.GetAllocatedBytesForCurrentThread();
        return new(true, outer, AllocationStorage.Epoch, AllocationStorage.Depth);
    }

    internal static void RecordCompletedDrainWork(DrainWorkKind kind)
    {
        if (!AllocationsEnabled) return;
        if (Environment.CurrentManagedThreadId != AllocationStorage.OwnerThread || AllocationStorage.Depth == 0)
        {
            Interlocked.Increment(ref AllocationStorage.Pending[(int)AllocationCounter.Discarded]);
            return;
        }
        AllocationStorage.Pending[(int)AllocationCounter.Work]++;
        if (kind == DrainWorkKind.Draw) AllocationStorage.Pending[(int)AllocationCounter.Draws]++;
        else if (kind == DrainWorkKind.Compute) AllocationStorage.Pending[(int)AllocationCounter.Compute]++;
    }

    private static void RecordAllocationGuestCommit(long guestSequence)
    {
        if (Environment.CurrentManagedThreadId != AllocationStorage.OwnerThread)
        {
            Interlocked.Increment(ref AllocationStorage.Pending[(int)AllocationCounter.Discarded]);
            return;
        }
        var offset = (int)(AllocationStorage.Count % Capacity) * AllocationStride;
        AllocationStorage.Rows[offset] = guestSequence;
        Array.Copy(AllocationStorage.Pending, 0, AllocationStorage.Rows, offset + 1, (int)AllocationCounter.Count);
        // The last counter also receives rejected foreign-thread scopes. Exchange
        // it separately: copying then clearing it would lose concurrent increments.
        AllocationStorage.Rows[offset + 1 + (int)AllocationCounter.Discarded] =
            Interlocked.Exchange(ref AllocationStorage.Pending[(int)AllocationCounter.Discarded], 0);
        Array.Clear(AllocationStorage.Pending, 0, (int)AllocationCounter.Discarded);
        AllocationStorage.Count++;
    }

    private static void WriteAllocationReport()
    {
        var first = Math.Max(0, AllocationStorage.Count - Capacity);
        Console.Error.WriteLine($"[PERF][METAL_DRAIN_ALLOC_TRACE] schema=1 retained={AllocationStorage.Count - first} overwritten={first} scope=current_presenter_thread_allocated_bytes excludes=other_threads_gc_pauses_cpu_self_gpu work_boundary=acknowledged_work_including_noops");
        for (var sequence = first; sequence < AllocationStorage.Count; sequence++)
        {
            var offset = (int)(sequence % Capacity) * AllocationStride;
            Console.Error.WriteLine($"[PERF][METAL_DRAIN_ALLOC_FRAME] sequence={sequence} guest_sequence={AllocationStorage.Rows[offset]} {AllocationFields(AllocationStorage.Rows, offset + 1)}");
        }
        Console.Error.WriteLine($"[PERF][METAL_DRAIN_ALLOC_TAIL] scope=since_last_guest_commit {AllocationFields(AllocationStorage.Pending, 0)}");
    }

    private static string AllocationFields(long[] values, int offset)
    {
        string Value(AllocationCounter counter) =>
            (counter == AllocationCounter.Discarded
                ? Interlocked.Read(ref values[offset + (int)counter])
                : values[offset + (int)counter]).ToString(CultureInfo.InvariantCulture);
        return $"allocated_bytes={Value(AllocationCounter.Bytes)} drain_calls={Value(AllocationCounter.Calls)} completed_work={Value(AllocationCounter.Work)} completed_draws={Value(AllocationCounter.Draws)} completed_compute={Value(AllocationCounter.Compute)} failed_drains={Value(AllocationCounter.Failed)} discarded_scopes={Value(AllocationCounter.Discarded)}";
    }
}
