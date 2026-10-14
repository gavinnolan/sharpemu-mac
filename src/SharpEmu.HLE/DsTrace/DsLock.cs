// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace SharpEmu.HLE.DsTrace;

/// <summary>
/// Timed lock wrappers for the §4.5 memory-system sites. With the mem group off or
/// not yet armed each wrapper is exactly the original lock call. The armed fast path
/// adds one TryEnter(0) attempt and one timestamp.
///
/// Site ids: 1 TryRead read lock, 2 TryRead exclusive write lock, 3 TryWriteCore read
/// lock, 4 TryWriteCore exclusive write lock, 5 TryProtect read lock, 6
/// SharedBackingViews.TryWriteBackingCore monitor, 7 SharedBackingViews.ClearCore
/// monitor, 8 temporary read mprotect calls (wait fields carry mprotect ns), 9 TryRead
/// single-view hit/miss, 10 TryWriteCore single-view hit/miss (count = calls,
/// slow_count = misses).
/// </summary>
public static class DsLock
{
    private const int S = DsThreadData.LockStride;

    private static bool Active
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => DsTrace.Mem && DsTrace.Armed;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static long EnterRead(ReaderWriterLockSlim gate, int site)
    {
        if (!Active)
        {
            gate.EnterReadLock();
            return 0;
        }

        return EnterReadTimed(gate, site);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void ExitRead(ReaderWriterLockSlim gate, int site, long acquired)
    {
        gate.ExitReadLock();
        if (DsTrace.Mem && acquired != 0)
        {
            Hold(site, acquired);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static long EnterWrite(ReaderWriterLockSlim gate, int site)
    {
        if (!Active)
        {
            gate.EnterWriteLock();
            return 0;
        }

        return EnterWriteTimed(gate, site);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void ExitWrite(ReaderWriterLockSlim gate, int site, long acquired)
    {
        gate.ExitWriteLock();
        if (DsTrace.Mem && acquired != 0)
        {
            Hold(site, acquired);
        }
    }

    /// <summary>Replaces <c>lock (o) { … }</c> with <c>t = EnterMonitor(o, site); try { … } finally { ExitMonitor(o, site, t); }</c>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static long EnterMonitor(object gate, int site)
    {
        if (!Active)
        {
            Monitor.Enter(gate);
            return 0;
        }

        return EnterMonitorTimed(gate, site);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void ExitMonitor(object gate, int site, long acquired)
    {
        Monitor.Exit(gate);
        if (DsTrace.Mem && acquired != 0)
        {
            Hold(site, acquired);
        }
    }

    /// <summary>Hit/miss counter (sites 9, 10).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Count(int site, bool hit)
    {
        if (Active)
        {
            CountSlow(site, hit);
        }
    }

    /// <summary>Start of a timed host mprotect (site 8); returns 0 when inactive.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static long ProtectBegin() => Active ? Stopwatch.GetTimestamp() : 0;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void ProtectEnd(long started)
    {
        if (DsTrace.Mem && started != 0)
        {
            ProtectEndSlow(started);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static long EnterReadTimed(ReaderWriterLockSlim gate, int site)
    {
        var a = DsTrace.ThreadData.Lock;
        var b = site * S;
        if (gate.TryEnterReadLock(0))
        {
            a[b]++;
            return Stopwatch.GetTimestamp();
        }

        var t0 = Stopwatch.GetTimestamp();
        gate.EnterReadLock();
        var t1 = Stopwatch.GetTimestamp();
        Wait(a, site, t1 - t0);
        return t1;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static long EnterWriteTimed(ReaderWriterLockSlim gate, int site)
    {
        var data = DsTrace.ThreadData;
        var a = data.Lock;
        var b = site * S;
        if (DsTrace.InRipProbe)
        {
            data.FiberScratch.ProbePath = 2;
        }

        if (gate.TryEnterWriteLock(0))
        {
            a[b]++;
            return Stopwatch.GetTimestamp();
        }

        var t0 = Stopwatch.GetTimestamp();
        gate.EnterWriteLock();
        var t1 = Stopwatch.GetTimestamp();
        Wait(a, site, t1 - t0);
        if (DsTrace.InRipProbe)
        {
            data.FiberScratch.ProbeWlockWaitTicks += t1 - t0;
        }

        return t1;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static long EnterMonitorTimed(object gate, int site)
    {
        var a = DsTrace.ThreadData.Lock;
        var b = site * S;
        if (Monitor.TryEnter(gate))
        {
            a[b]++;
            return Stopwatch.GetTimestamp();
        }

        var t0 = Stopwatch.GetTimestamp();
        Monitor.Enter(gate);
        var t1 = Stopwatch.GetTimestamp();
        Wait(a, site, t1 - t0);
        return t1;
    }

    private static void Wait(long[] a, int site, long wait)
    {
        var b = site * S;
        a[b]++;
        a[b + 1]++;
        a[b + 2] += wait;
        if (wait > a[b + 3])
        {
            a[b + 3] = wait;
        }

        if (wait >= DsTrace.MlwTicks)
        {
            Mlw(site, wait, 0);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Hold(int site, long acquired)
    {
        var hold = Stopwatch.GetTimestamp() - acquired;
        var a = DsTrace.ThreadData.Lock;
        var b = site * S;
        a[b + 4] += hold;
        if (hold > a[b + 5])
        {
            a[b + 5] = hold;
        }

        if (hold >= DsTrace.MlwTicks)
        {
            Mlw(site, 0, hold);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void CountSlow(int site, bool hit)
    {
        var data = DsTrace.ThreadData;
        var a = data.Lock;
        var b = site * S;
        a[b]++;
        if (!hit)
        {
            a[b + 1]++;
        }
        else if (site == 9 && DsTrace.InRipProbe)
        {
            data.FiberScratch.ProbePath = 0;
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ProtectEndSlow(long started)
    {
        var ticks = Stopwatch.GetTimestamp() - started;
        var data = DsTrace.ThreadData;
        var a = data.Lock;
        const int b = 8 * S;
        a[b]++;
        a[b + 2] += ticks;
        if (ticks > a[b + 3])
        {
            a[b + 3] = ticks;
        }

        if (DsTrace.InRipProbe)
        {
            data.FiberScratch.ProbeMprotectTicks += ticks;
            data.FiberScratch.MprotectCount++;
        }
    }

    private static void Mlw(int site, long waitTicks, long holdTicks)
    {
        DsTrace.Emit(
            DsEv.MLW,
            (ulong)site,
            (ulong)DsTrace.TicksToNs(waitTicks),
            (ulong)DsTrace.TicksToNs(holdTicks),
            DsHle.NameId(DsTrace.CurrentImport),
            DsTrace.ThreadClass,
            DsTrace.InRipProbe ? 1UL : 0UL);
    }

    /// <summary>1 Hz MLA flush, run by the host sampler.</summary>
    internal static void Flush(long now)
    {
        foreach (var data in DsTrace.AllThreadData)
        {
            var a = data.Lock;
            var prev = data.PrevLock ??= new long[a.Length];
            for (var site = 1; site < DsThreadData.LockSites; site++)
            {
                var b = site * S;
                var count = a[b] - prev[b];
                if (count == 0)
                {
                    continue;
                }

                var slow = a[b + 1] - prev[b + 1];
                var waitSum = a[b + 2] - prev[b + 2];
                var holdSum = a[b + 4] - prev[b + 4];
                prev[b] += count;
                prev[b + 1] += slow;
                prev[b + 2] += waitSum;
                prev[b + 4] += holdSum;
                var waitMax = Interlocked.Exchange(ref a[b + 3], 0);
                var holdMax = Interlocked.Exchange(ref a[b + 5], 0);
                DsTrace.EmitAt(
                    now,
                    DsEv.MLA,
                    (ulong)site,
                    (ulong)data.Tid,
                    (ulong)count,
                    (ulong)slow,
                    (ulong)DsTrace.TicksToNs(waitSum),
                    (ulong)DsTrace.TicksToNs(waitMax),
                    (ulong)DsTrace.TicksToNs(holdSum),
                    (ulong)DsTrace.TicksToNs(holdMax));
            }
        }
    }
}
