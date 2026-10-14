// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics;

namespace SharpEmu.HLE.DsTrace;

/// <summary>
/// Host sampler thread <c>SharpEmu-DsHost</c> (§6). It arms the trace at ARM_S,
/// drives the §7 A/B schedule, flushes the per-thread aggregates once a second and
/// samples host state: CLK 4 Hz, PROC/GC 1 Hz, OTHER 0.2 Hz, TCPU 10 Hz, WCPU at
/// SHARPEMU_DSTRACE_WCPU_HZ. Mach thread_info only: no register state, safe under Rosetta.
/// </summary>
internal static unsafe class DsHost
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
        var nextClk = start;
        var nextTcpu = start;
        var nextOther = start + freq;
        InitTimebase();
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

                if (DsTrace.Host)
                {
                    if (now >= nextClk)
                    {
                        nextClk += freq / 4;
                        Clk();
                    }

                    if (now >= nextTcpu)
                    {
                        nextTcpu += freq / 10;
                        Tcpu(now);
                    }

                    if (now >= nextOther)
                    {
                        nextOther += freq * 5;
                        Other(now);
                    }

                    if (DsTrace.WcpuHz > 0 && DsTrace.Armed)
                    {
                        Wcpu(now);
                    }
                }

                if (now >= next1Hz)
                {
                    next1Hz += freq;
                    if (DsTrace.Host)
                    {
                        Proc(now, start);
                        Gc(now);
                    }

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

    // ---- CLK (4 Hz): Stopwatch ↔ guest TSC ↔ mach_absolute_time ↔ CACurrentMediaTime ----

    private static uint _tbNumer = 1;
    private static uint _tbDenom = 1;

    private static void InitTimebase()
    {
        if (DsNative.mach_timebase_info(out var info) == 0 && info.Numer != 0 && info.Denom != 0)
        {
            _tbNumer = info.Numer;
            _tbDenom = info.Denom;
        }
    }

    private static void Clk()
    {
        var reader = DsTrace.TscReader;
        var bestGap = long.MaxValue;
        long mid = 0;
        ulong tsc = 0, mat = 0, ca = 0;
        for (var i = 0; i < 3; i++)
        {
            var a = Stopwatch.GetTimestamp();
            var t = reader is null ? 0 : reader();
            var m = DsNative.mach_absolute_time();
            var c = DsNative.CACurrentMediaTime();
            var b = Stopwatch.GetTimestamp();
            if (b - a < bestGap)
            {
                bestGap = b - a;
                mid = a + (b - a) / 2;
                tsc = t;
                mat = m;
                ca = (ulong)(c * 1e9);
            }
        }

        var matNs = (ulong)((UInt128)mat * _tbNumer / _tbDenom);
        DsTrace.EmitAt(mid, DsEv.CLK, (ulong)mid, tsc, matNs, ca, (ulong)DsTrace.TicksToNs(bestGap));
    }

    // ---- PROC (1 Hz) + timebase sanity check ----

    private static ulong TbToNs(ulong ticks) =>
        TimebaseHz > 0 ? (ulong)((UInt128)ticks * 1_000_000_000UL / (ulong)TimebaseHz) : ticks;

    private static long _checkProcNs = -1;
    private static long _checkTcpuNs;
    private static long _checkTs;
    private static int _checkReports;

    private static void Proc(long now, long start)
    {
        DsNative.RusageInfoV6Data ri;
        if (DsNative.proc_pid_rusage(Environment.ProcessId, DsNative.RusageInfoV6, &ri) != 0)
        {
            return;
        }

        var loads = stackalloc double[3];
        var load1 = DsNative.getloadavg(loads, 3) > 0 ? (ulong)Math.Max(0, loads[0] * 100) : 0;
        var userNs = TbToNs(ri.ri_user_time);
        var sysNs = TbToNs(ri.ri_system_time);
        DsTrace.EmitAt(
            now,
            DsEv.PROC,
            userNs,
            sysNs,
            TbToNs(ri.ri_user_ptime),
            TbToNs(ri.ri_system_ptime),
            TbToNs(ri.ri_runnable_time),
            ri.ri_instructions,
            ri.ri_cycles,
            ri.ri_interrupt_wkups,
            ri.ri_pkg_idle_wkups,
            ri.ri_phys_footprint,
            load1,
            (ulong)_lastThreadCount);

        // ΣTCPU user+sys should match PROC user+sys within ~10 %; a ~41.67× miss
        // means the abstime-to-ns assumption (hw.tbfrequency) is wrong under Rosetta.
        var procNs = (long)(userNs + sysNs);
        if (_lastTcpuSumUs == 0 || now - start < Stopwatch.Frequency * 5)
        {
            return;
        }

        var tcpuNs = _lastTcpuSumUs * 1000;
        if (_checkProcNs < 0)
        {
            _checkProcNs = procNs;
            _checkTcpuNs = tcpuNs;
            _checkTs = now;
            return;
        }

        if (now - _checkTs < Stopwatch.Frequency * 10)
        {
            return;
        }

        var dProc = procNs - _checkProcNs;
        var dTcpu = tcpuNs - _checkTcpuNs;
        _checkProcNs = procNs;
        _checkTcpuNs = tcpuNs;
        _checkTs = now;
        if (dTcpu <= 0)
        {
            return;
        }

        var ratio = (double)dProc / dTcpu;
        var off = Math.Abs(ratio - 1.0) > 0.10;
        if (_checkReports == 0 || (off && _checkReports < 6))
        {
            _checkReports++;
            var line = FormattableString.Invariant(
                $"timebase check proc_ns={dProc} tcpu_ns={dTcpu} ratio={ratio:F3} tb_hz={TimebaseHz}{(off ? " MISMATCH" : string.Empty)}");
            Console.Error.WriteLine("[DSTRACE] " + line);
            DsTrace.Comment("# TBCHECK " + line);
        }
    }

    // ---- GC (1 Hz) ----

    private static void Gc(long now)
    {
        DsTrace.EmitAt(
            now,
            DsEv.GC,
            (ulong)GC.CollectionCount(0),
            (ulong)GC.CollectionCount(1),
            (ulong)GC.CollectionCount(2),
            (ulong)(GC.GetTotalPauseDuration().Ticks * 100),
            (ulong)GC.GetTotalAllocatedBytes(false),
            (ulong)Monitor.LockContentionCount,
            (ulong)ThreadPool.ThreadCount);
    }

    // ---- TCPU (10 Hz): every thread of the task ----

    private static readonly Dictionary<ulong, uint> _threadNames = new();
    private static int _lastThreadCount;
    private static long _lastTcpuSumUs;

    private static void Tcpu(long now)
    {
        var task = DsNative.TaskSelf;
        if (DsNative.task_threads(task, out var array, out var count) != 0)
        {
            return;
        }

        long sumUs = 0;
        var ports = (uint*)array;
        try
        {
            for (var i = 0; i < count; i++)
            {
                var port = ports[i];
                DsNative.ThreadBasicInfoData basic;
                var basicCount = DsNative.ThreadBasicInfoCount;
                if (DsNative.thread_info(port, DsNative.ThreadBasicInfo, &basic, ref basicCount) != 0)
                {
                    continue;
                }

                DsNative.ThreadIdentifierInfoData ident;
                var identCount = DsNative.ThreadIdentifierInfoCount;
                var threadId = DsNative.thread_info(port, DsNative.ThreadIdentifierInfo, &ident, ref identCount) == 0
                    ? ident.ThreadId
                    : 0;
                var userUs = (long)basic.UserSeconds * 1_000_000 + basic.UserMicroseconds;
                var sysUs = (long)basic.SystemSeconds * 1_000_000 + basic.SystemMicroseconds;
                sumUs += userUs + sysUs;
                DsTrace.EmitAt(
                    now,
                    DsEv.TCPU,
                    threadId,
                    (ulong)userUs,
                    (ulong)sysUs,
                    (ulong)basic.CpuUsage,
                    (ulong)basic.RunState,
                    (ulong)basic.Flags,
                    ThreadName(threadId),
                    port);
            }
        }
        finally
        {
            for (var i = 0; i < count; i++)
            {
                _ = DsNative.mach_port_deallocate(task, ports[i]);
            }

            _ = DsNative.vm_deallocate(task, (nuint)array, (nuint)(count * sizeof(uint)));
        }

        _lastThreadCount = (int)count;
        _lastTcpuSumUs = sumUs;
    }

    private static uint ThreadName(ulong threadId)
    {
        if (threadId == 0)
        {
            return 0;
        }

        if (_threadNames.TryGetValue(threadId, out var id))
        {
            return id;
        }

        DsNative.ProcThreadInfo info;
        string? name = null;
        if (DsNative.proc_pidinfo(
                Environment.ProcessId, DsNative.ProcPidThreadId64Info, threadId, &info, sizeof(DsNative.ProcThreadInfo)) ==
            sizeof(DsNative.ProcThreadInfo))
        {
            var length = 0;
            while (length < 64 && info.Name[length] != 0)
            {
                length++;
            }

            name = System.Text.Encoding.UTF8.GetString(info.Name, length);
        }

        // Names can be set after the first sighting; retry unnamed threads later.
        id = DsTrace.Intern(string.IsNullOrEmpty(name) ? null : name);
        if (id != 0)
        {
            _threadNames[threadId] = id;
        }

        return id;
    }

    // ---- WCPU (WCPU_HZ): CPU deltas of the registered worker threads ----

    private static readonly uint[] _wcpuPort = new uint[14];
    private static readonly long[] _wcpuPrevUs = new long[14];

    private static void Wcpu(long now)
    {
        Span<ulong> delta = stackalloc ulong[14];
        ulong running = 0;
        var any = false;
        for (var slot = 0; slot < 14; slot++)
        {
            var port = Volatile.Read(ref _workerPorts[slot]);
            if (port == 0)
            {
                continue;
            }

            any = true;

            DsNative.ThreadBasicInfoData basic;
            var basicCount = DsNative.ThreadBasicInfoCount;
            if (DsNative.thread_info(port, DsNative.ThreadBasicInfo, &basic, ref basicCount) != 0)
            {
                continue;
            }

            var totalUs = ((long)basic.UserSeconds + basic.SystemSeconds) * 1_000_000 +
                basic.UserMicroseconds + basic.SystemMicroseconds;
            if (_wcpuPort[slot] != port)
            {
                _wcpuPort[slot] = port;
                _wcpuPrevUs[slot] = totalUs;
            }

            var d = totalUs - _wcpuPrevUs[slot];
            _wcpuPrevUs[slot] = totalUs;
            delta[slot] = d <= 0 ? 0 : (ulong)Math.Min(d, uint.MaxValue);
            if (basic.RunState == DsNative.ThStateRunning)
            {
                running |= 1UL << slot;
            }
        }

        if (!any)
        {
            return;
        }

        DsTrace.EmitAt(
            now,
            DsEv.WCPU,
            delta[0] | (delta[1] << 32),
            delta[2] | (delta[3] << 32),
            delta[4] | (delta[5] << 32),
            delta[6] | (delta[7] << 32),
            delta[8] | (delta[9] << 32),
            delta[10] | (delta[11] << 32),
            delta[12] | (delta[13] << 32),
            running);
    }

    // ---- OTHER (0.2 Hz): CPU of the other processes (H11) ----

    private static int[] _pids = new int[4096];
    private static readonly Dictionary<int, ulong> _otherPrevNs = new();
    private static readonly Dictionary<int, ulong> _otherNextNs = new();

    private static void Other(long now)
    {
        int n;
        fixed (int* buffer = _pids)
        {
            n = DsNative.proc_listallpids(buffer, _pids.Length * sizeof(int));
        }

        if (n <= 0)
        {
            return;
        }

        if (n >= _pids.Length)
        {
            _pids = new int[n * 2];
            return;
        }

        var self = Environment.ProcessId;
        ulong others = 0;
        (int Pid, ulong Ns) top1 = default, top2 = default, top3 = default;
        _otherNextNs.Clear();
        DsNative.RusageInfoV6Data ri;
        for (var i = 0; i < n; i++)
        {
            var pid = _pids[i];
            if (pid <= 0 || pid == self || DsNative.proc_pid_rusage(pid, DsNative.RusageInfoV2, &ri) != 0)
            {
                continue;
            }

            var total = TbToNs(ri.ri_user_time + ri.ri_system_time);
            _otherNextNs[pid] = total;
            if (!_otherPrevNs.TryGetValue(pid, out var prev) || total < prev)
            {
                continue;
            }

            var d = total - prev;
            others += d;
            if (d > top1.Ns)
            {
                top3 = top2;
                top2 = top1;
                top1 = (pid, d);
            }
            else if (d > top2.Ns)
            {
                top3 = top2;
                top2 = (pid, d);
            }
            else if (d > top3.Ns)
            {
                top3 = (pid, d);
            }
        }

        _otherPrevNs.Clear();
        foreach (var (pid, ns) in _otherNextNs)
        {
            _otherPrevNs[pid] = ns;
        }

        DsTrace.EmitAt(
            now,
            DsEv.OTHER,
            others,
            (ulong)top1.Pid, top1.Ns, ProcessName(top1.Pid),
            (ulong)top2.Pid, top2.Ns, ProcessName(top2.Pid),
            (ulong)top3.Pid, top3.Ns, ProcessName(top3.Pid));
    }

    private static uint ProcessName(int pid)
    {
        if (pid <= 0)
        {
            return 0;
        }

        var buffer = stackalloc byte[256];
        var length = DsNative.proc_name(pid, buffer, 256);
        return length > 0 ? DsTrace.Intern(System.Text.Encoding.UTF8.GetString(buffer, length)) : 0;
    }
}
