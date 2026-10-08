// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
namespace SharpEmu.HLE.Host;

internal sealed partial class HostManagedWriteIntervals
{
    private ReadWatch?[]? readWatches; // Allocated only by the optional cold watched-copy path.
    private int activeReadWatches;
    internal sealed class ReadWatch(HostManagedWriteIntervals owner, PhysicalRun[] runs)
    {
        internal readonly PhysicalRun[] Runs = runs;
        internal int Slot = -1, Revoked, Released;
        internal bool IsCurrent => Volatile.Read(ref Released) == 0 && Volatile.Read(ref Revoked) == 0 &&
            Volatile.Read(ref owner.coverageIncomplete) == 0 && Volatile.Read(ref owner.admission) == 0 &&
            Volatile.Read(ref Revoked) == 0 && Volatile.Read(ref Released) == 0;
        internal void Release()
        {
            if (Interlocked.Exchange(ref Released, 1) != 0) return;
            if (Slot >= 0 && Interlocked.CompareExchange(ref owner.readWatches![Slot], null, this) == this)
                Interlocked.Decrement(ref owner.activeReadWatches);
        }
    }

    internal ReadWatch? TryCreateReadWatch(PhysicalRun[] runs)
    {
        if (runs.Length is 0 or > CpuMappedCopyObservation.MaximumSegments) return null;
        var watch = new ReadWatch(this, runs); // Cold preparation only, never writer path.
        var installed = Volatile.Read(ref readWatches);
        if (installed is null)
        {
            var candidate = new ReadWatch?[Capacity];
            installed = Interlocked.CompareExchange(ref readWatches, candidate, null) ?? candidate;
        }
        if (Volatile.Read(ref coverageIncomplete) != 0 ||
            Interlocked.CompareExchange(ref admission, PublicationBit, 0) != 0) return null;
        try
        {
            if (Volatile.Read(ref coverageIncomplete) != 0) return null;
            for (var index = 0; index < installed.Length; index++)
            {
                watch.Slot = index;
                if (Interlocked.CompareExchange(ref installed[index], watch, null) is null)
                { Interlocked.Increment(ref activeReadWatches); return watch; }
            }
            return null; // Finite watch capacity; never synthesize a current receipt.
        }
        finally { ReleasePublication(); }
    }

    private void InvalidateReadWatches(ulong offset, ulong count, bool unknown = false)
    {
        if (Volatile.Read(ref activeReadWatches) == 0) return;
        var installed = Volatile.Read(ref readWatches)!;
        for (var index = 0; index < installed.Length; index++)
        {
            var watch = Volatile.Read(ref installed[index]);
            if (watch is null || Volatile.Read(ref watch.Released) != 0) continue;
            foreach (var run in watch.Runs)
                if (unknown || offset < run.Offset + run.Count && run.Offset < offset + count)
                { Volatile.Write(ref watch.Revoked, 1); break; }
        }
    }
}
