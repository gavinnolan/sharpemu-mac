// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace SharpEmu.HLE.DsTrace;

/// <summary>Scheduler events: GF, BRA, WAKE, PUMPA counters and USLA/USLP.</summary>
public static class DsSched
{
    /// <summary>One GUEST_FLOW event mirrored as GF (same values as the existing TraceEvent).</summary>
    public static void Gf(
        int kind, ulong guest, string? name, string? reason, string? wakeKey, string? nid,
        ulong arg0, ulong arg1, long deadline)
    {
        DsTrace.Emit(
            DsEv.GF,
            (ulong)kind,
            guest,
            DsTrace.Intern(name),
            DsTrace.Intern(reason),
            DsTrace.Intern(wakeKey),
            DsTrace.Intern(nid),
            arg0,
            arg1,
            unchecked((ulong)deadline));
    }

    /// <summary>A blocking import consumed the current thread's block (BRA).</summary>
    public static void BlockingReturn(ulong guest, uint nidStr, ulong ra1, ulong rbp, ulong rsp)
    {
        Span<ulong> ra = stackalloc ulong[2];
        _ = DsGuestMem.ReturnChain(rbp, rsp, ra);
        DsTrace.Emit(DsEv.BRA, guest, nidStr, ra1, ra[0], ra[1]);
    }

    public static void Wake(
        long enterTs, long gateAcquiredTs, string wakeKey, int scanned, int woken, ulong firstWoken, bool pumped)
    {
        var now = Stopwatch.GetTimestamp();
        DsTrace.EmitAt(
            now,
            DsEv.WAKE,
            (ulong)enterTs,
            (ulong)DsTrace.TicksToNs(gateAcquiredTs - enterTs),
            (ulong)DsTrace.TicksToNs(now - enterTs),
            DsTrace.Intern(wakeKey),
            (ulong)scanned,
            (ulong)woken,
            firstWoken,
            pumped ? 1UL : 0UL);
    }

    // ---- PUMPA: per-thread monotonic counters, summed by the host sampler ----

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void PumpCall(long wakeExpiredTicks)
    {
        var p = DsTrace.ThreadData.Pump;
        p[0]++;
        p[4] += wakeExpiredTicks;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void PumpCasLost() => DsTrace.ThreadData.Pump[1]++;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void PumpCapHit() => DsTrace.ThreadData.Pump[2]++;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void PumpClaimed() => DsTrace.ThreadData.Pump[3]++;

    internal static void FlushPump(long now)
    {
        Span<long> sum = stackalloc long[DsThreadData.PumpFields];
        sum.Clear();
        foreach (var data in DsTrace.AllThreadData)
        {
            var p = data.Pump;
            var prev = data.PrevPump ??= new long[p.Length];
            for (var i = 0; i < p.Length; i++)
            {
                var delta = p[i] - prev[i];
                prev[i] += delta;
                sum[i] += delta;
            }
        }

        if (sum[0] == 0)
        {
            return;
        }

        DsTrace.EmitAt(
            now,
            DsEv.PUMPA,
            (ulong)sum[0],
            (ulong)sum[1],
            (ulong)sum[2],
            (ulong)sum[3],
            (ulong)DsTrace.TicksToNs(sum[4]));
    }

    // ---- USLA / USLP (sceKernelUsleep) ----

    internal sealed class UsleepAgg
    {
        public long Count;
        public long ReqUsSum;
        public long ActualTicksSum;
        public long PumpTicksSum;
        public long Sleep0Count;
    }

    /// <summary>
    /// One sceKernelUsleep call. Aggregated per RA1 on the calling thread and flushed
    /// by that thread when its current second changes; USLP for long calls.
    /// branch: 0 yield, 1 sleep(0), 2 long sleep.
    /// </summary>
    public static void Usleep(CpuContext ctx, ulong requestUs, long enterTs, long pumpTicks, int branch)
    {
        var now = Stopwatch.GetTimestamp();
        var actual = now - enterTs;
        var ra1 = DsTrace.CurrentRa1;
        var data = DsTrace.ThreadData;
        var second = (now - DsTrace.StartTimestamp) / Stopwatch.Frequency;
        var map = data.Usla ??= new Dictionary<ulong, UsleepAgg>();
        if (second != data.UslaSecond)
        {
            FlushUsla(data, now);
            data.UslaSecond = second;
        }

        if (!map.TryGetValue(ra1, out var agg))
        {
            agg = new UsleepAgg();
            map[ra1] = agg;
        }

        agg.Count++;
        agg.ReqUsSum += (long)Math.Min(requestUs, (ulong)long.MaxValue);
        agg.ActualTicksSum += actual;
        agg.PumpTicksSum += pumpTicks;
        if (branch == 1)
        {
            agg.Sleep0Count++;
        }

        var actualNs = DsTrace.TicksToNs(actual);
        if (actualNs >= 200_000 || requestUs >= 1000)
        {
            ulong ra2 = 0;
            var rbp = ctx[CpuRegister.Rbp];
            if (rbp != 0 && (rbp & 7) == 0)
            {
                _ = DsGuestMem.TryRead64(rbp + 8, out ra2);
            }

            DsTrace.EmitAt(
                now,
                DsEv.USLP,
                ra1,
                ra2,
                requestUs,
                (ulong)actualNs,
                (ulong)DsTrace.TicksToNs(pumpTicks),
                (ulong)branch);
        }
    }

    private static void FlushUsla(DsThreadData data, long now)
    {
        var map = data.Usla;
        if (map is null || map.Count == 0)
        {
            return;
        }

        foreach (var (ra1, agg) in map)
        {
            if (agg.Count == 0)
            {
                continue;
            }

            DsTrace.EmitAt(
                now,
                DsEv.USLA,
                ra1,
                (ulong)agg.Count,
                (ulong)agg.ReqUsSum,
                (ulong)DsTrace.TicksToNs(agg.ActualTicksSum),
                (ulong)DsTrace.TicksToNs(agg.PumpTicksSum),
                (ulong)agg.Sleep0Count);
            agg.Count = 0;
            agg.ReqUsSum = 0;
            agg.ActualTicksSum = 0;
            agg.PumpTicksSum = 0;
            agg.Sleep0Count = 0;
        }
    }
}
