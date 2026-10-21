// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.HLE.DsTrace;

/// <summary>
/// Per-thread DSTRACE aggregates. Only the owning thread writes the counters, and
/// they only ever grow (max fields excepted), so the host sampler reads them
/// without atomics and emits per-second deltas: the hot path never takes a shared
/// cache line. The max fields are reset by the sampler with Interlocked.Exchange;
/// a racing owner update can be lost, which only affects that one second's max.
/// </summary>
public sealed class DsThreadData
{
    internal const int LockSites = 16;
    internal const int LockStride = 8;   // count, slow, wait_sum, wait_max, hold_sum, hold_max (ticks)
    internal const int HleStride = 9;    // count, wall_sum, wall_max, ge100us, ge1ms, ge10ms, cpu_sum, cpu_n, wall_of_cpu_sampled
    internal const int PumpFields = 5;   // calls, cas_lost, cap_hit, claimed, wake_expired_ticks

    internal DsThreadData(int tid) => Tid = tid;

    public int Tid { get; }

    internal readonly long[] Lock = new long[LockSites * LockStride];
    internal long[] Hle = [];
    internal readonly long[] Pump = new long[PumpFields];
    internal int CpuSampleCounter;

    /// <summary>Scratch state of the fiber switch in flight on this thread.</summary>
    public readonly DsFiberScratch FiberScratch = new();

    // Host-sampler side: the values at the previous 1 Hz flush.
    internal long[]? PrevLock;
    internal long[]? PrevHle;
    internal long[]? PrevPump;

    // sceKernelUsleep aggregates, owner-flushed (USLA).
    internal long UslaSecond;
    internal Dictionary<ulong, DsSched.UsleepAgg>? Usla;

    internal long[] EnsureHle(int importIndex)
    {
        var hle = Hle;
        var need = (importIndex + 1) * HleStride;
        if (need <= hle.Length)
        {
            return hle;
        }

        var size = Math.Max(need, DsHle.Count * HleStride);
        var grown = new long[size];
        Array.Copy(hle, grown, hle.Length);
        Volatile.Write(ref Hle, grown);
        return grown;
    }
}

/// <summary>Fields of the sceFiberSwitch in flight; FSW/FSJ are emitted from them at the context transfer.</summary>
public sealed class DsFiberScratch
{
    public bool Valid;
    public long EnterTs;
    public ulong Cpu0;
    public long GateWaitTicks;
    public long BodyTicks;
    public ulong From;
    public ulong To;
    public bool Resumed;
    public ulong Ra1;
    public ulong Rbp;
    public ulong Rsp;
    public ulong R14;
    public ulong R15;

    // Filled by the lock wrappers while DsTrace.InRipProbe is set.
    public uint ProbePath = 0xff;
    public long ProbeWlockWaitTicks;
    public long ProbeMprotectTicks;
    public uint MprotectCount;
}
