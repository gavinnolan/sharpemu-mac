// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.HLE.DsTrace;

/// <summary>
/// HLE dispatch accounting: import names, the allow-list, per-thread HLEA
/// aggregates and the HLE / CPUPT records. Core's gateway wrapper calls
/// <see cref="Record"/> once per import while the hle group is armed.
/// </summary>
public static class DsHle
{
    private static readonly string[] AllowList =
    [
        "sceFiberSwitch", "sceFiberRun", "sceFiberReturnToThread",
        "sceKernelWaitEventFlag", "sceKernelSetEventFlag",
        "scePthreadCondWait", "scePthreadCondTimedwait", "pthread_cond_wait", "pthread_cond_timedwait",
        "scePthreadCondSignal", "scePthreadCondBroadcast",
        "sceKernelWaitSema", "sceKernelSignalSema",
        "sceKernelWaitEqueue",
        "sceAgcDcbSetFlip", "sceAgcDriverSubmitDcb", "sceAgcDriverSubmitAcb", "sceAgcSuspendPoint",
        "sceVideoOutSubmitFlip", "sceVideoOutWaitVblank", "sceVideoOutIsFlipPending", "sceVideoOutSetFlipRate",
        "sceKernelAprSubmitCommandBufferAndGetResult",
        "sceKernelRead",
        "sceAudioOutOutput",
    ];

    private static readonly object _gate = new();
    private static uint[] _nameIds = [];
    private static bool[] _allow = [];

    public static int Count => Volatile.Read(ref _nameIds).Length;

    /// <summary>
    /// Registers import names (export name, else NID) by import index. Re-called when the
    /// import table grows; prints the allow-list names no import matched the first time.
    /// </summary>
    public static void SetImportNames(IReadOnlyList<string?> names)
    {
        if (!DsTrace.On)
        {
            return;
        }

        lock (_gate)
        {
            if (names.Count == _nameIds.Length)
            {
                return;
            }

            var ids = new uint[names.Count];
            var allow = new bool[names.Count];
            var matched = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 0; i < names.Count; i++)
            {
                var name = names[i];
                ids[i] = DsTrace.Intern(name);
                if (name is not null && Array.IndexOf(AllowList, name) >= 0)
                {
                    allow[i] = true;
                    matched.Add(name);
                }
            }

            var first = _nameIds.Length == 0;
            Volatile.Write(ref _allow, allow);
            Volatile.Write(ref _nameIds, ids);
            if (first)
            {
                var unmatched = AllowList.Where(name => !matched.Contains(name)).ToArray();
                Console.Error.WriteLine(
                    $"[DSTRACE] hle allow-list: matched={matched.Count} unmatched={unmatched.Length}" +
                    (unmatched.Length > 0 ? " names=" + string.Join(",", unmatched) : string.Empty));
            }
        }
    }

    public static bool IsAllowed(int importIndex)
    {
        var allow = Volatile.Read(ref _allow);
        return (uint)importIndex < (uint)allow.Length && allow[importIndex];
    }

    public static uint NameId(int importIndex)
    {
        var ids = Volatile.Read(ref _nameIds);
        return (uint)importIndex < (uint)ids.Length ? ids[importIndex] : 0;
    }

    /// <summary>True on one call in SHARPEMU_DSTRACE_HLE_CPU_EVERY per thread.</summary>
    public static bool ShouldSampleCpu(DsThreadData data)
    {
        if (++data.CpuSampleCounter < DsTrace.HleCpuEvery)
        {
            return false;
        }

        data.CpuSampleCounter = 0;
        return true;
    }

    /// <summary>
    /// Accounts one dispatched import: HLEA aggregate always; HLE record when the import
    /// is allow-listed or slow; CPUPT when CPU was sampled.
    /// </summary>
    public static void Record(
        DsThreadData data, int importIndex, long t0, long t1, bool allowListed,
        bool cpuSampled, ulong cpu0, ulong cpu1,
        ulong rdi, ulong rsi, ulong rdx, ulong ra1, ulong rbp, ulong rax)
    {
        if (importIndex < 0)
        {
            return;
        }

        var wall = t1 - t0;
        var cpu = cpuSampled && cpu1 >= cpu0 ? cpu1 - cpu0 : 0UL;
        var a = data.EnsureHle(importIndex);
        var b = importIndex * DsThreadData.HleStride;
        a[b]++;
        a[b + 1] += wall;
        if (wall > a[b + 2])
        {
            a[b + 2] = wall;
        }

        var wallNs = DsTrace.TicksToNs(wall);
        if (wallNs >= 100_000)
        {
            a[b + 3]++;
            if (wallNs >= 1_000_000)
            {
                a[b + 4]++;
                if (wallNs >= 10_000_000)
                {
                    a[b + 5]++;
                }
            }
        }

        if (cpuSampled)
        {
            a[b + 6] += (long)cpu;
            a[b + 7]++;
            a[b + 8] += wall;
            DsTrace.EmitAt(t1, DsEv.CPUPT, cpu1);
        }

        if (allowListed || wall >= DsTrace.HleSlowTicks)
        {
            // RA2: one frame-pointer level above the import's caller.
            ulong ra2 = 0;
            if (rbp != 0 && (rbp & 7) == 0)
            {
                _ = DsGuestMem.TryRead64(rbp + 8, out ra2);
            }

            DsTrace.EmitAt(
                t1,
                DsEv.HLE,
                (ulong)t0,
                (ulong)wallNs,
                cpu,
                NameId(importIndex),
                rdi,
                rsi,
                rdx,
                ra1,
                ra2,
                DsTrace.LastOutcome,
                rax);
        }
    }

    // Host-sampler scratch for the 1 Hz HLEA flush.
    private static long[] _sum = [];

    /// <summary>1 Hz HLEA flush, run by the host sampler: per import, deltas summed over threads.</summary>
    internal static void Flush(long now)
    {
        var count = Count;
        var size = count * DsThreadData.HleStride;
        if (_sum.Length < size)
        {
            _sum = new long[size];
        }

        var sum = _sum;
        Array.Clear(sum, 0, size);
        var any = false;
        foreach (var data in DsTrace.AllThreadData)
        {
            var a = Volatile.Read(ref data.Hle);
            if (a.Length == 0)
            {
                continue;
            }

            var prev = data.PrevHle;
            if (prev is null || prev.Length < a.Length)
            {
                var grown = new long[a.Length];
                if (prev is not null)
                {
                    Array.Copy(prev, grown, prev.Length);
                }

                prev = data.PrevHle = grown;
            }

            var limit = Math.Min(a.Length, size);
            for (var b = 0; b < limit; b += DsThreadData.HleStride)
            {
                var calls = a[b] - prev[b];
                if (calls == 0)
                {
                    continue;
                }

                any = true;
                for (var f = 0; f < DsThreadData.HleStride; f++)
                {
                    if (f == 2)
                    {
                        var max = Interlocked.Exchange(ref a[b + 2], 0);
                        if (max > sum[b + 2])
                        {
                            sum[b + 2] = max;
                        }

                        continue;
                    }

                    var delta = a[b + f] - prev[b + f];
                    prev[b + f] += delta;
                    sum[b + f] += delta;
                }
            }
        }

        if (!any)
        {
            return;
        }

        for (var i = 0; i < count; i++)
        {
            var b = i * DsThreadData.HleStride;
            if (sum[b] == 0)
            {
                continue;
            }

            DsTrace.EmitAt(
                now,
                DsEv.HLEA,
                NameId(i),
                (ulong)sum[b],
                (ulong)DsTrace.TicksToNs(sum[b + 1]),
                (ulong)DsTrace.TicksToNs(sum[b + 2]),
                (ulong)sum[b + 3],
                (ulong)sum[b + 4],
                (ulong)sum[b + 5],
                (ulong)sum[b + 6],
                (ulong)sum[b + 7],
                (ulong)DsTrace.TicksToNs(sum[b + 8]));
        }
    }

}
