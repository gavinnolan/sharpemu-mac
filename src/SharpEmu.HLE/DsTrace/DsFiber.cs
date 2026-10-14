// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace SharpEmu.HLE.DsTrace;

/// <summary>
/// §4.3 fiber-switch internals. sceFiberSwitch fills the per-thread scratch, the
/// fiber gate and the transfer request add their timings, and the import dispatcher
/// emits FSW + FSJ once TryPrepareGuestContextTransfer has run.
/// </summary>
public static class DsFiber
{
    // Runtime addresses of the three WaitForCounter switch sites (RA2).
    public const ulong SiteSuspend = 0x80086d6ccUL;
    public const ulong SiteResume = 0x80086f6d4UL;
    public const ulong SiteMigrate = 0x80086f59bUL;

    /// <summary>At sceFiberSwitch entry, before FiberRunCore.</summary>
    public static void BeginSwitch(CpuContext ctx)
    {
        if (!DsTrace.Armed)
        {
            return;
        }

        var s = DsTrace.ThreadData.FiberScratch;
        s.Valid = true;
        s.EnterTs = Stopwatch.GetTimestamp();
        s.Cpu0 = DsTrace.SelfCpuNs();
        s.Ra1 = DsTrace.CurrentRa1;
        s.Rbp = ctx[CpuRegister.Rbp];
        s.Rsp = ctx[CpuRegister.Rsp];
        s.R14 = ctx[CpuRegister.R14];
        s.R15 = ctx[CpuRegister.R15];
        s.GateWaitTicks = 0;
        s.BodyTicks = 0;
        s.From = 0;
        s.To = 0;
        s.Resumed = false;
        s.ProbePath = 0xff;
        s.ProbeWlockWaitTicks = 0;
        s.ProbeMprotectTicks = 0;
        s.MprotectCount = 0;
    }

    /// <summary>sceFiberSwitch returned an error: no transfer follows.</summary>
    public static void Cancel()
    {
        DsTrace.ThreadData.FiberScratch.Valid = false;
    }

    /// <summary>Replaces <c>lock (_fiberGate)</c>'s enter; exit stays Monitor.Exit.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void EnterGate(object gate)
    {
        if (!DsTrace.Fiber)
        {
            Monitor.Enter(gate);
            return;
        }

        EnterGateTimed(gate);
    }

    private static void EnterGateTimed(object gate)
    {
        if (Monitor.TryEnter(gate))
        {
            return;
        }

        var t0 = Stopwatch.GetTimestamp();
        Monitor.Enter(gate);
        var s = DsTrace.ThreadData.FiberScratch;
        if (s.Valid)
        {
            s.GateWaitTicks += Stopwatch.GetTimestamp() - t0;
        }
    }

    /// <summary>Right before RequestCurrentContextTransfer in FiberRunCore.</summary>
    public static void NoteTransfer(ulong from, ulong to, bool resumed)
    {
        var s = DsTrace.ThreadData.FiberScratch;
        if (!s.Valid)
        {
            return;
        }

        s.From = from;
        s.To = to;
        s.Resumed = resumed;
        s.BodyTicks = Stopwatch.GetTimestamp() - s.EnterTs - s.GateWaitTicks;
    }

    /// <summary>Around the TryRead in TryValidateGuestContextTransferTarget.</summary>
    public static void BeginRipProbe()
    {
        if (DsTrace.Armed)
        {
            DsTrace.InRipProbe = true;
        }
    }

    public static void EndRipProbe(bool succeeded)
    {
        if (!DsTrace.InRipProbe)
        {
            return;
        }

        DsTrace.InRipProbe = false;
        var s = DsTrace.ThreadData.FiberScratch;
        if (succeeded && s.ProbePath == 0xff)
        {
            s.ProbePath = 1;
        }
    }

    /// <summary>The A/B branch accepted the target through IsExecutableAddress.</summary>
    public static void NoteProbePath(uint path)
    {
        if (DsTrace.Armed)
        {
            DsTrace.ThreadData.FiberScratch.ProbePath = path;
        }
    }

    /// <summary>After TryPrepareGuestContextTransfer succeeded: emits FSW + FSJ for a pending switch.</summary>
    public static void OnTransferPrepared(CpuContext ctx, long prepStarted)
    {
        var s = DsTrace.ThreadData.FiberScratch;
        if (!s.Valid)
        {
            return;
        }

        s.Valid = false;
        var now = Stopwatch.GetTimestamp();
        var cpu = DsTrace.SelfCpuNs();

        Span<ulong> ra = stackalloc ulong[4]; // RA2..RA5
        _ = DsGuestMem.ReturnChain(s.Rbp, s.Rsp, ra);
        var ra2 = ra[0];

        ulong tlsWorker = 0xff;
        if (DsGuestMem.TryRead64(ctx.FsBase, out var tcb) && tcb >= 0x10000 &&
            DsGuestMem.TryRead64(tcb - 0xe0, out var q) && (short)q < 0)
        {
            tlsWorker = q & 0x7fff;
        }

        var abPhase = DsTraceAb.RipCacheActive ? 1UL : 0UL;
        var probeCode = (s.ProbePath & 0xff) |
            ((ulong)Math.Min(s.MprotectCount, 0xffu) << 8) |
            (abPhase << 16) |
            (tlsWorker << 24);

        DsTrace.EmitAt(
            now,
            DsEv.FSW,
            (ulong)s.EnterTs,
            (ulong)DsTrace.TicksToNs(now - s.EnterTs),
            (ulong)DsTrace.TicksToNs(s.GateWaitTicks),
            (ulong)DsTrace.TicksToNs(s.BodyTicks),
            (ulong)DsTrace.TicksToNs(now - prepStarted),
            probeCode,
            (ulong)DsTrace.TicksToNs(s.ProbeWlockWaitTicks),
            (ulong)DsTrace.TicksToNs(s.ProbeMprotectTicks),
            cpu >= s.Cpu0 ? cpu - s.Cpu0 : 0,
            s.From,
            s.To,
            ra2 | (s.Resumed ? 1UL << 63 : 0),
            s.Ra1);

        ulong job = 0;
        ulong counter = 0;
        var suspend = ra2 == SiteSuspend;
        var resume = ra2 == SiteResume;
        if (suspend)
        {
            job = s.R15;
            counter = s.R14;
        }
        else if (resume || ra2 == SiteMigrate)
        {
            job = s.R14;
        }

        ulong value = 0, target = 0, cond = 0, condHi = 0, aff = 0, grp = 0, q08 = 0, q10 = 0, q58 = 0, q60 = 0;
        if (counter != 0)
        {
            value = DsGuestMem.R64(counter);
        }

        if (job != 0)
        {
            target = DsGuestMem.R64(job + 0xd0);
            cond = DsGuestMem.R32(job + 0xd8);
            condHi = DsGuestMem.R32(job + 0x148);
            aff = DsGuestMem.R32(job + 0x158);
            grp = DsGuestMem.R32(job + 0x15c);
            q08 = DsGuestMem.R64(job + 0x08);
            q10 = DsGuestMem.R64(job + 0x10);
            q58 = DsGuestMem.R64(job + 0x58);
            q60 = DsGuestMem.R64(job + 0x60);
        }

        DsTrace.EmitAt(
            now,
            DsEv.FSJ,
            job,
            counter,
            value,
            target,
            cond | (condHi << 32),
            aff | (grp << 32),
            q08,
            q10,
            q58,
            q60,
            ra[1],
            ra[2],
            ra[3]);

        if (DsTrace.Sampler)
        {
            if (suspend && counter != 0)
            {
                DsSampler.Watch(job, counter, target, (uint)cond, (uint)aff, s.EnterTs);
            }
            else if (resume && job != 0)
            {
                DsSampler.Unwatch(job);
            }
        }
    }
}
