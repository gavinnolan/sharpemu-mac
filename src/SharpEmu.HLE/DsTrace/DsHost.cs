// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics;

namespace SharpEmu.HLE.DsTrace;

/// <summary>
/// Host sampler thread <c>SharpEmu-DsHost</c> (§6). It arms the trace at ARM_S,
/// drives the §7 A/B schedule and flushes the per-thread aggregates once a second.
/// </summary>
internal static class DsHost
{
    internal static readonly long TimebaseHz = DsTrace.On ? DsNative.SysctlLong("hw.tbfrequency") : 0;

    // Slot 0..12 = guest worker index, 13 = Metal GPU worker.
    private static readonly uint[] _workerPorts = new uint[14];
    private static readonly object _flushGate = new();
    private static long _abWindow = long.MinValue;

    internal static void RegisterWorkerPort(int slot, uint port)
    {
        if ((uint)slot < (uint)_workerPorts.Length)
        {
            Volatile.Write(ref _workerPorts[slot], port);
        }
    }

    internal static void StartThread()
    {
        var thread = new Thread(Main) { IsBackground = true, Name = "SharpEmu-DsHost" };
        thread.Start();
    }

    private static void Main()
    {
        var qos = SharpEmu.HLE.Host.HostThreadQualityOfService.TryApplyToCurrentThread(
            SharpEmu.HLE.Host.HostThreadQos.UserInitiated);
        DsTrace.RegisterThread(7, 0, "DsHost", qos ? 1 : 0);
        var freq = Stopwatch.Frequency;
        var periodTicks = DsTrace.WcpuHz > 0 ? Math.Max(1, freq / DsTrace.WcpuHz) : freq / 100;
        var start = DsTrace.StartTimestamp;
        var armAt = start + freq * DsTrace.ArmSeconds;
        var next1Hz = start + freq;
        if (DsTraceAb.Enabled)
        {
            DsTrace.Emit(DsEv.AB, 0, unchecked((ulong)-1L));
        }

        var next = Stopwatch.GetTimestamp();
        try
        {
            while (!DsTrace.Closed)
            {
                var now = Stopwatch.GetTimestamp();
                if (!DsTrace.Armed && now >= armAt)
                {
                    DsTrace.Arm();
                    Console.Error.WriteLine($"[DSTRACE] armed t={(double)(now - start) / freq:F1}s");
                }

                if (DsTraceAb.Enabled)
                {
                    StepAb(now, start, freq);
                }

                if (now >= next1Hz)
                {
                    next1Hz += freq;
                    Flush(now);
                }

                next += periodTicks;
                var after = Stopwatch.GetTimestamp();
                if (next <= after)
                {
                    next = after;
                    continue;
                }

                DsNative.SleepNs(DsTrace.TicksToNs(next - after));
            }
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"[DSTRACE] host sampler failed: {exception.GetType().Name}: {exception.Message}");
        }
    }

    // §7: from AB_START_S, flip every AB_RIPCACHE seconds: OFF, ON, OFF, ON …
    private static void StepAb(long now, long start, long freq)
    {
        var abStart = start + freq * DsTrace.AbStartSeconds;
        if (now < abStart)
        {
            return;
        }

        var window = (now - abStart) / (freq * DsTrace.AbRipCacheSeconds);
        if (window == _abWindow)
        {
            return;
        }

        _abWindow = window;
        var on = (window & 1) == 1;
        DsTraceAb.RipCacheActive = on;
        DsTrace.EmitAt(now, DsEv.AB, on ? 1UL : 0UL, (ulong)window);
    }

    private static void Flush(long now)
    {
        lock (_flushGate)
        {
            if (!DsTrace.Armed)
            {
                return;
            }

            if (DsTrace.Hle)
            {
                DsHle.Flush(now);
            }

            if (DsTrace.Mem)
            {
                DsLock.Flush(now);
            }

            if (DsTrace.Sched)
            {
                DsSched.FlushPump(now);
            }
        }
    }

    /// <summary>Last partial second of aggregates, from DsTrace.Shutdown.</summary>
    internal static void FinalFlush() => Flush(Stopwatch.GetTimestamp());
}
