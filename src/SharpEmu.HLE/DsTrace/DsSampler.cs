// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;

namespace SharpEmu.HLE.DsTrace;

/// <summary>
/// Guest-state sampler <c>SharpEmu-DsSampler</c>. Polls Demon's Souls' job
/// system at SHARPEMU_DSTRACE_SAMPLER_HZ (default 1000) once armed and emits
/// edge-triggered records. All guest reads go through <see cref="DsGuestMem"/>
/// (ICpuMemory.TryRead unless SHARPEMU_DSTRACE_RAW_READS=1). The roots are
/// title-specific: on any other title they never validate and sampling stays off.
/// </summary>
internal static class DsSampler
{
    internal readonly record struct WatchMessage(
        bool Add, ulong Job, ulong Counter, ulong Target, uint Cond, uint Affinity, long SuspendTs);

    private static readonly ConcurrentQueue<WatchMessage>? _watchQueue = DsTrace.Sampler ? new() : null;
    private static volatile bool _running;

    /// <summary>FSJ hand-off: a job suspended on a counter.</summary>
    internal static void Watch(ulong job, ulong counter, ulong target, uint cond, uint affinity, long suspendTs)
    {
        if (_running)
        {
            _watchQueue!.Enqueue(new WatchMessage(true, job, counter, target, cond, affinity, suspendTs));
        }
    }

    /// <summary>FSJ hand-off: the job resumed.</summary>
    internal static void Unwatch(ulong job)
    {
        if (_running)
        {
            _watchQueue!.Enqueue(new WatchMessage(false, job, 0, 0, 0, 0, 0));
        }
    }

    internal static void StartThread()
    {
        _running = true;
        var thread = new Thread(Main) { IsBackground = true, Name = "SharpEmu-DsSampler" };
        thread.Start();
    }

    // ---- guest layout (runtime addresses) ----

    private const ulong JobSystemRootPtr = 0x8030fd5b0UL;
    private const ulong RenderThreadPtrPtr = 0x803181450UL;
    private const ulong RenderContextPtr = 0x8031810c0UL;
    private const ulong DisplayPtr = 0x803123450UL;
    private const int Workers = 13;
    private const int Groups = 8;
    private const ulong GroupStride = 0x9220;

    // One bulk read of the job-system struct S covers every per-tick field:
    // 0xba0 worker table (cur, fiber) × 13, 0xec8/0xed0/0xef0 pools, 0xef8 and
    // 0xf78 locks, 0xf00 lastJobTsc × 13, 0xf80 mailbox × 13.
    private const ulong SBlockStart = 0xba0;
    private const int SBlockLength = 0xfe8 - 0xba0;
    private const int JobBlockLength = 55 * 8; // JD: 5 × 11 qwords (covers 0x190 and the 0x158/0x15c fields)

    private static ulong _s, _g, _rtpp, _ctx, _disp;
    private static bool _resolved;
    private static bool _gaveUp;
    private static long _armedTs;
    private static long _nextRootTry;

    // Edge-trigger state.
    private static ulong _lastState = ulong.MaxValue;
    private static ulong _lastRt;
    private static readonly ulong[] _lastSfc = new ulong[8];
    private static bool _sfcSeen;
    private static readonly ulong[] _lastCur = new ulong[Workers];
    private static bool _swkSeen;
    private static uint _lastEf8 = uint.MaxValue, _lastF78 = uint.MaxValue;
    private static readonly uint[] _lastSfm = [uint.MaxValue, 0, 0, 0];
    private static ulong _idleMask;

    private static readonly Dictionary<ulong, WatchMessage> _watches = new();
    private static readonly List<ulong> _watchRemove = new();
    private static readonly HashSet<(ulong, ulong)> _jdSeen = new();

    // SSTAT: gap histogram in 10 µs buckets up to 100 ms.
    private const int GapBuckets = 10_000;
    private static readonly int[] _gapHist = new int[GapBuckets + 1];

    private static void Main()
    {
        var qos = SharpEmu.HLE.Host.HostThreadQualityOfService.TryApplyToCurrentThread(
            SharpEmu.HLE.Host.HostThreadQos.UserInitiated);
        DsTrace.RegisterThread(7, 0, "DsSampler", qos ? 1 : 0);
        var freq = Stopwatch.Frequency;
        var period = Math.Max(1, freq / DsTrace.SamplerHz);
        var next = Stopwatch.GetTimestamp();
        var lastTickStart = 0L;
        var nextSecond = next + freq;
        var next10ms = next;
        var next100ms = next;
        long ticks = 0, maxGap = 0, costSum = 0;
        try
        {
            while (!DsTrace.Closed)
            {
                var tickStart = Stopwatch.GetTimestamp();
                if (lastTickStart != 0)
                {
                    var gap = tickStart - lastTickStart;
                    maxGap = Math.Max(maxGap, gap);
                    _gapHist[Math.Min(GapBuckets, DsTrace.TicksToNs(gap) / 10_000)]++;
                }

                lastTickStart = tickStart;
                if (DsTrace.Armed)
                {
                    Tick(tickStart, ref next10ms, ref next100ms);
                }

                var tickEnd = Stopwatch.GetTimestamp();
                ticks++;
                costSum += tickEnd - tickStart;
                if (tickEnd >= nextSecond)
                {
                    nextSecond += freq;
                    DsTrace.EmitAt(
                        tickEnd,
                        DsEv.SSTAT,
                        (ulong)ticks,
                        (ulong)DsTrace.TicksToNs(maxGap),
                        (ulong)GapP99Ns(),
                        (ulong)(ticks > 0 ? DsTrace.TicksToNs(costSum) / ticks : 0));
                    ticks = 0;
                    maxGap = 0;
                    costSum = 0;
                    Array.Clear(_gapHist);
                }

                next += period;
                if (next <= tickEnd)
                {
                    next = tickEnd;
                    continue;
                }

                DsNative.SleepNs(DsTrace.TicksToNs(next - tickEnd));
            }
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"[DSTRACE] guest sampler failed: {exception.GetType().Name}: {exception.Message}");
        }
        finally
        {
            _running = false;
        }
    }

    private static long GapP99Ns()
    {
        long total = 0;
        foreach (var count in _gapHist)
        {
            total += count;
        }

        if (total == 0)
        {
            return 0;
        }

        var threshold = total - total / 100;
        long seen = 0;
        for (var i = 0; i < _gapHist.Length; i++)
        {
            seen += _gapHist[i];
            if (seen >= threshold)
            {
                return (i + 1) * 10_000L;
            }
        }

        return GapBuckets * 10_000L;
    }

    private static void Tick(long now, ref long next10ms, ref long next100ms)
    {
        if (_armedTs == 0)
        {
            _armedTs = now;
            _nextRootTry = now;
        }

        if (!_resolved)
        {
            DrainWatchesDiscard();
            if (_gaveUp || now < _nextRootTry)
            {
                return;
            }

            _nextRootTry = now + Stopwatch.Frequency;
            if (!TryResolveRoots(now))
            {
                if (now - _armedTs >= Stopwatch.Frequency * 60)
                {
                    _gaveUp = true;
                    Console.Error.WriteLine("[DSTRACE] guest sampler: roots did not validate within 60 s; guest sampling off");
                }

                return;
            }
        }

        Span<byte> sBlock = stackalloc byte[SBlockLength];
        var haveS = DsGuestMem.TryReadBlock(_s + SBlockStart, sBlock);
        if (haveS)
        {
            SampleWorkers(now, sBlock);
            SampleLocks(now, sBlock);
        }

        SampleRenderThread(now);
        SampleFence(now);
        EvaluateWatches(now);

        if (now >= next10ms)
        {
            next10ms = now + Stopwatch.Frequency / 100;
            SampleCounts(now);
        }

        if (now >= next100ms)
        {
            next100ms = now + Stopwatch.Frequency / 10;
            SampleGroups(now);
            if (haveS)
            {
                DsTrace.EmitAt(now, DsEv.SPOOL, S64(sBlock, 0xec8), S64(sBlock, 0xef0), S64(sBlock, 0xed0));
            }

            _ctx = DsGuestMem.R64(RenderContextPtr);
            _disp = DsGuestMem.R64(DisplayPtr);
            if (_rtpp == 0)
            {
                _rtpp = DsGuestMem.R64(RenderThreadPtrPtr);
            }

            SampleFrameMode(now);
        }
    }

    private static ulong S64(ReadOnlySpan<byte> block, ulong offset) =>
        MemoryMarshal.Read<ulong>(block[(int)(offset - SBlockStart)..]);

    private static uint S32(ReadOnlySpan<byte> block, ulong offset) =>
        MemoryMarshal.Read<uint>(block[(int)(offset - SBlockStart)..]);

    private static bool TryResolveRoots(long now)
    {
        var j = DsGuestMem.R64(JobSystemRootPtr);
        var s = j != 0 ? DsGuestMem.R64(j + 8) : 0;
        var sb90 = s != 0 ? DsGuestMem.R32(s + 0xb90) : 0;
        var sff8 = s != 0 ? DsGuestMem.R32(s + 0xff8) : 0;
        var valid = s != 0 && sb90 == Workers && sff8 == Groups;
        var g = valid ? DsGuestMem.R64(s + 0x1000) : 0;
        var rtpp = DsGuestMem.R64(RenderThreadPtrPtr);
        var ctx = DsGuestMem.R64(RenderContextPtr);
        var disp = DsGuestMem.R64(DisplayPtr);
        ulong ok = (j != 0 ? 1UL : 0) | (valid ? 2UL : 0) | (g != 0 ? 4UL : 0) |
            (rtpp != 0 ? 8UL : 0) | (ctx != 0 ? 16UL : 0) | (disp != 0 ? 32UL : 0);
        DsTrace.EmitAt(
            now,
            DsEv.ROOTS,
            j,
            s,
            g,
            s != 0 ? DsGuestMem.R32(s + 0x48) : 0,
            s != 0 ? DsGuestMem.R32(s + 0x4c) : 0,
            sb90,
            rtpp,
            ctx,
            disp,
            ok);
        if (!valid)
        {
            return false;
        }

        (_s, _g, _rtpp, _ctx, _disp) = (s, g, rtpp, ctx, disp);
        _resolved = true;
        Console.Error.WriteLine($"[DSTRACE] guest sampler roots: S=0x{s:X} G=0x{g:X} rtpp=0x{rtpp:X} ctx=0x{ctx:X} disp=0x{disp:X}");
        return true;
    }

    // SWK, JD, idle mask.
    private static void SampleWorkers(long now, ReadOnlySpan<byte> sBlock)
    {
        ulong idle = 0;
        Span<byte> job = stackalloc byte[JobBlockLength];
        for (var w = 0; w < Workers; w++)
        {
            var cur = S64(sBlock, 0xba0 + (ulong)(16 * w));
            if (cur == 0)
            {
                idle |= 1UL << w;
            }

            if (_swkSeen && cur == _lastCur[w])
            {
                continue;
            }

            _lastCur[w] = cur;
            ulong q08 = 0, q10 = 0, q58 = 0, q60 = 0, flagsAff = 0;
            var haveDump = cur != 0 && DsGuestMem.TryReadBlock(cur, job);
            var haveJob = haveDump || (cur != 0 && DsGuestMem.TryReadBlock(cur, job[..0x160]));
            if (haveJob)
            {
                q08 = MemoryMarshal.Read<ulong>(job[0x08..]);
                q10 = MemoryMarshal.Read<ulong>(job[0x10..]);
                q58 = MemoryMarshal.Read<ulong>(job[0x58..]);
                q60 = MemoryMarshal.Read<ulong>(job[0x60..]);
                flagsAff = MemoryMarshal.Read<uint>(job[0x148..]) |
                    ((ulong)MemoryMarshal.Read<uint>(job[0x158..]) << 32);
            }

            DsTrace.EmitAt(
                now,
                DsEv.SWK,
                (ulong)w,
                cur,
                S64(sBlock, 0xba8 + (ulong)(16 * w)),
                q08,
                q10,
                q58,
                q60,
                flagsAff,
                S64(sBlock, 0xf00 + (ulong)(8 * w)),
                S64(sBlock, 0xf80 + (ulong)(8 * w)));

            if (haveDump && _jdSeen.Count < 4096 && _jdSeen.Add((q08, q10)))
            {
                for (var chunk = 0; chunk < 5; chunk++)
                {
                    var q = MemoryMarshal.Cast<byte, ulong>(job.Slice(chunk * 88, 88));
                    DsTrace.EmitAt(
                        now, DsEv.JD, cur, (ulong)chunk,
                        q[0], q[1], q[2], q[3], q[4], q[5], q[6], q[7], q[8], q[9], q[10]);
                }
            }
        }

        _swkSeen = true;
        _idleMask = idle;
    }

    private static void SampleLocks(long now, ReadOnlySpan<byte> sBlock)
    {
        var ef8 = S32(sBlock, 0xef8);
        var f78 = S32(sBlock, 0xf78);
        if (ef8 == _lastEf8 && f78 == _lastF78)
        {
            return;
        }

        _lastEf8 = ef8;
        _lastF78 = f78;
        DsTrace.EmitAt(now, DsEv.SLK, ef8, f78);
    }

    private static void SampleRenderThread(long now)
    {
        if (_rtpp == 0)
        {
            return;
        }

        var rt = DsGuestMem.R64(_rtpp);
        if (rt == 0 || !DsGuestMem.TryRead64(rt + 8, out var state))
        {
            return;
        }

        if (state == _lastState && rt == _lastRt)
        {
            return;
        }

        DsTrace.EmitAt(now, DsEv.SRT, _lastState, state, rt, DsGuestMem.R32(rt + 0x10));
        _lastState = state;
        _lastRt = rt;
    }

    private static void SampleFence(long now)
    {
        var ctx = _ctx;
        if (ctx == 0)
        {
            return;
        }

        Span<ulong> v = stackalloc ulong[8];
        v[0] = DsGuestMem.R32(ctx + 0x918);
        v[1] = DsGuestMem.R32(ctx + 0xc);
        var lp0 = DsGuestMem.R64(ctx + 0x8e8);
        var lp1 = DsGuestMem.R64(ctx + 0x8f0);
        v[2] = lp0 != 0 ? DsGuestMem.R64(lp0) : 0;
        v[3] = lp1 != 0 ? DsGuestMem.R64(lp1) : 0;
        v[4] = DsGuestMem.R64(ctx + 0x8f8);
        v[5] = DsGuestMem.R64(ctx + 0x900);
        v[6] = lp0;
        v[7] = lp1;
        if (_sfcSeen && v.SequenceEqual(_lastSfc))
        {
            return;
        }

        _sfcSeen = true;
        v.CopyTo(_lastSfc);
        DsTrace.EmitAt(now, DsEv.SFC, v[0], v[1], v[2], v[3], v[4], v[5], v[6], v[7]);
    }

    private static void DrainWatchesDiscard()
    {
        while (_watchQueue!.TryDequeue(out _))
        {
        }
    }

    // CSAT: first tick a watched job's counter condition holds.
    private static void EvaluateWatches(long now)
    {
        var queue = _watchQueue!;
        while (queue.TryDequeue(out var message))
        {
            if (message.Add)
            {
                _watches[message.Job] = message;
            }
            else
            {
                _ = _watches.Remove(message.Job);
            }
        }

        if (_watches.Count == 0)
        {
            return;
        }

        var expiry = now - Stopwatch.Frequency * 10;
        var evaluated = 0;
        _watchRemove.Clear();
        foreach (var (job, watch) in _watches)
        {
            if (watch.SuspendTs < expiry)
            {
                _watchRemove.Add(job);
                continue;
            }

            if (evaluated++ >= 512 || !DsGuestMem.TryRead64(watch.Counter, out var raw))
            {
                continue;
            }

            var value = (long)raw;
            var target = (long)watch.Target;
            var satisfied = watch.Cond switch
            {
                0 => value == target,
                1 => value <= target,
                2 => value < target,
                3 => value >= target,
                4 => value > target,
                _ => false,
            };
            if (!satisfied)
            {
                continue;
            }

            var idle = _idleMask;
            DsTrace.EmitAt(
                now,
                DsEv.CSAT,
                job,
                watch.Counter,
                raw,
                watch.Target,
                watch.Cond,
                (ulong)watch.SuspendTs,
                (ulong)BitOperations.PopCount(idle & watch.Affinity),
                idle);
            _watchRemove.Add(job);
        }

        foreach (var job in _watchRemove)
        {
            _ = _watches.Remove(job);
        }
    }

    // SCNT (100 Hz): per-worker counts of groups 2 and 3, 4 × u16 per field.
    private static void SampleCounts(long now)
    {
        if (_g == 0)
        {
            return;
        }

        Span<ulong> packed = stackalloc ulong[8];
        packed.Clear();
        Span<byte> counts = stackalloc byte[Workers * 4];
        for (var gi = 0; gi < 2; gi++)
        {
            var g = (ulong)(gi + 2);
            if (!DsGuestMem.TryReadBlock(_g + g * GroupStride + 0x91e0, counts))
            {
                return;
            }

            var c = MemoryMarshal.Cast<byte, uint>(counts);
            for (var w = 0; w < Workers; w++)
            {
                packed[gi * 4 + w / 4] |= (ulong)(c[w] & 0xffff) << (16 * (w % 4));
            }
        }

        DsTrace.EmitAt(now, DsEv.SCNT, packed[0], packed[1], packed[2], packed[3], packed[4], packed[5], packed[6], packed[7]);
    }

    // SGRP (10 Hz): per group, count sum and non-null slots of the waiting / external lists.
    private static void SampleGroups(long now)
    {
        if (_g == 0)
        {
            return;
        }

        Span<byte> counts = stackalloc byte[Workers * 4];
        Span<byte> list = stackalloc byte[256 * 8];
        for (var g = 0UL; g < Groups; g++)
        {
            var baseAddress = _g + g * GroupStride;
            ulong sum = 0;
            if (DsGuestMem.TryReadBlock(baseAddress + 0x91e0, counts))
            {
                foreach (var c in MemoryMarshal.Cast<byte, uint>(counts))
                {
                    sum += c;
                }
            }

            var waiting = DsGuestMem.TryReadBlock(baseAddress + 0x85e0, list) ? CountNonNull(list) : 0UL;
            var external = DsGuestMem.TryReadBlock(baseAddress + 0x79e0, list) ? CountNonNull(list) : 0UL;
            DsTrace.EmitAt(now, DsEv.SGRP, g, sum, waiting, external);
        }
    }

    private static ulong CountNonNull(ReadOnlySpan<byte> list)
    {
        ulong n = 0;
        foreach (var q in MemoryMarshal.Cast<byte, ulong>(list))
        {
            if (q != 0)
            {
                n++;
            }
        }

        return n;
    }

    // SFM (edge-triggered): frame mode and target rate of the display object.
    private static void SampleFrameMode(long now)
    {
        if (_disp == 0)
        {
            return;
        }

        Span<byte> block = stackalloc byte[16];
        if (!DsGuestMem.TryReadBlock(_disp + 0x28, block))
        {
            return;
        }

        var hz = MemoryMarshal.Read<uint>(block);
        var r2c = MemoryMarshal.Read<uint>(block[4..]);
        var mode = MemoryMarshal.Read<uint>(block[8..]);
        var r34 = MemoryMarshal.Read<uint>(block[12..]);
        if (mode == _lastSfm[0] && r34 == _lastSfm[1] && r2c == _lastSfm[2] && hz == _lastSfm[3])
        {
            return;
        }

        (_lastSfm[0], _lastSfm[1], _lastSfm[2], _lastSfm[3]) = (mode, r34, r2c, hz);
        DsTrace.EmitAt(now, DsEv.SFM, mode, r34, r2c, hz);
    }
}
