// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using System.Diagnostics;
using System.Threading;

namespace SharpEmu.Libs.Kernel;

/// <summary>
/// Schedules a callback at a host <see cref="Stopwatch"/> timestamp. Timer
/// knotes (EVFILT_TIMER, HR timers) only depend on this seam, so the shared
/// guest timer service can replace <see cref="HostEqueueTimerScheduler"/>
/// through <see cref="KernelEventQueueCompatExports.TimerScheduler"/>.
/// </summary>
internal interface IEqueueTimerScheduler
{
    /// <summary>
    /// Runs <paramref name="callback"/> once at or after
    /// <paramref name="dueTimestamp"/>; disposing the result cancels it.
    /// </summary>
    IDisposable Schedule(long dueTimestamp, Action<object?> callback, object? state);
}

/// <summary>
/// Host timers: a precise sleep on a pool thread for sub-1.2 ms deadlines
/// (System.Threading.Timer cannot resolve them), a threading timer otherwise.
/// </summary>
internal sealed class HostEqueueTimerScheduler : IEqueueTimerScheduler
{
    private static readonly long PreciseThresholdTicks = Stopwatch.Frequency * 12 / 10_000;

    public static HostEqueueTimerScheduler Instance { get; } = new();

    private sealed class Entry : IDisposable
    {
        public required Action<object?> Callback { get; init; }
        public required object? State { get; init; }
        public required long DueTimestamp { get; init; }
        public Timer? Timer;
        private int _state;

        public void Fire()
        {
            if (Interlocked.CompareExchange(ref _state, 1, 0) == 0)
            {
                Timer?.Dispose();
                Callback(State);
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _state, 1) == 0)
            {
                Timer?.Dispose();
            }
        }
    }

    public IDisposable Schedule(long dueTimestamp, Action<object?> callback, object? state)
    {
        var entry = new Entry { Callback = callback, State = state, DueTimestamp = dueTimestamp };
        var remaining = dueTimestamp - Stopwatch.GetTimestamp();
        if (remaining < PreciseThresholdTicks)
        {
            ThreadPool.UnsafeQueueUserWorkItem(
                static entry =>
                {
                    HostTiming.SleepUntil(entry.DueTimestamp);
                    entry.Fire();
                },
                entry,
                preferLocal: false);
            return entry;
        }

        var dueTime = TimeSpan.FromSeconds((double)remaining / Stopwatch.Frequency);
        lock (entry)
        {
            entry.Timer = new Timer(
                static state =>
                {
                    var entry = (Entry)state!;
                    // The threading timer may fire early by a fraction of a tick.
                    HostTiming.SleepUntil(entry.DueTimestamp);
                    entry.Fire();
                },
                entry,
                Timeout.InfiniteTimeSpan,
                Timeout.InfiniteTimeSpan);
            entry.Timer.Change(dueTime, Timeout.InfiniteTimeSpan);
        }

        return entry;
    }
}

public static partial class KernelEventQueueCompatExports
{
    private const uint NoteSeconds = 0x01;
    private const uint NoteMilliseconds = 0x02;
    private const uint NoteMicroseconds = 0x04;
    private const uint NoteNanoseconds = 0x08;

    private static IEqueueTimerScheduler _timerScheduler = HostEqueueTimerScheduler.Instance;

    /// <summary>
    /// The scheduler every timer knote arms. Integration point for the guest
    /// timer service; replaced only before guest code runs (tests, startup).
    /// </summary>
    internal static IEqueueTimerScheduler TimerScheduler
    {
        get => Volatile.Read(ref _timerScheduler);
        set => Volatile.Write(ref _timerScheduler, value ?? HostEqueueTimerScheduler.Instance);
    }

    // One armed expiry of a timer knote. The knote holds the current arm in
    // TimerToken; a firing arm that is no longer current is ignored.
    private sealed class TimerArm : IDisposable
    {
        public required Knote Knote { get; init; }
        public IDisposable? Handle;
        private int _cancelled;

        public bool Cancelled => Volatile.Read(ref _cancelled) != 0;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _cancelled, 1) == 0)
            {
                Interlocked.Exchange(ref Handle, null)?.Dispose();
            }
        }
    }

    [SysAbiExport(
        Nid = "R74tt43xP6k",
        ExportName = "sceKernelAddHRTimerEvent",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int KernelAddHrTimerEvent(CpuContext ctx)
    {
        var handle = ctx[CpuRegister.Rdi];
        var id = unchecked((int)ctx[CpuRegister.Rsi]);
        var timespecAddress = ctx[CpuRegister.Rdx];
        var userData = ctx[CpuRegister.Rcx];

        if (timespecAddress == 0 ||
            !ctx.TryReadUInt64(timespecAddress, out var secondsBits) ||
            !ctx.TryReadUInt64(timespecAddress + sizeof(ulong), out var nanosecondsBits))
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        }

        var seconds = unchecked((long)secondsBits);
        var nanoseconds = unchecked((long)nanosecondsBits);
        if (seconds < 0 || nanoseconds < 0 || nanoseconds >= 1_000_000_000 ||
            seconds > (long.MaxValue - nanoseconds) / 1_000_000_000)
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
        }

        var totalNanoseconds = seconds * 1_000_000_000 + nanoseconds;
        if (!AddTimerKnote(
                handle,
                unchecked((uint)id),
                KernelEventFilterHrTimer,
                userData,
                KernelEventFlagOneShot | KernelEventFlagClear,
                NanosecondsToTicks(totalNanoseconds),
                periodic: false))
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND;
        }

        TraceEventQueue(ctx, "add_hrtimer", handle, $"id={id} ns={totalNanoseconds} user_data=0x{userData:X16}");
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "J+LF6LwObXU",
        ExportName = "sceKernelDeleteHRTimerEvent",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int KernelDeleteHrTimerEvent(CpuContext ctx)
    {
        var handle = ctx[CpuRegister.Rdi];
        var id = unchecked((int)ctx[CpuRegister.Rsi]);
        var deleted = DeleteRegisteredEvent(handle, unchecked((uint)id), KernelEventFilterHrTimer);
        TraceEventQueue(ctx, "delete_hrtimer", handle, $"id={id}");
        return deleted
            ? (int)OrbisGen2Result.ORBIS_GEN2_OK
            : (int)OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND;
    }

    /// <summary>
    /// sceKernelAddTimerEvent(eq, id, usec, udata): a periodic EVFILT_TIMER knote.
    /// It is implicitly EV_CLEAR and reports the expirations since the last
    /// delivery in data. Re-adding restarts the period.
    /// </summary>
    [SysAbiExport(
        Nid = "57ZK+ODEXWY",
        ExportName = "sceKernelAddTimerEvent",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int KernelAddTimerEvent(CpuContext ctx)
    {
        var handle = ctx[CpuRegister.Rdi];
        var id = unchecked((int)ctx[CpuRegister.Rsi]);
        var microseconds = unchecked((uint)ctx[CpuRegister.Rdx]);
        var userData = ctx[CpuRegister.Rcx];
        if (!AddTimerKnote(
                handle,
                unchecked((uint)id),
                KernelEventFilterTimer,
                userData,
                KernelEventFlagClear,
                NanosecondsToTicks(microseconds * 1000L),
                periodic: true))
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND;
        }

        TraceEventQueue(ctx, "add_timer", handle, $"id={id} usec={microseconds} user_data=0x{userData:X16}");
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "YWQFUyXIVdU",
        ExportName = "sceKernelDeleteTimerEvent",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int KernelDeleteTimerEvent(CpuContext ctx)
    {
        var handle = ctx[CpuRegister.Rdi];
        var id = unchecked((int)ctx[CpuRegister.Rsi]);
        var deleted = DeleteRegisteredEvent(handle, unchecked((uint)id), KernelEventFilterTimer);
        TraceEventQueue(ctx, "delete_timer", handle, $"id={id}");
        return deleted
            ? (int)OrbisGen2Result.ORBIS_GEN2_OK
            : (int)OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND;
    }

    private static long NanosecondsToTicks(long nanoseconds)
    {
        if (nanoseconds <= 0)
        {
            return 0;
        }

        var ticks = (Int128)nanoseconds * Stopwatch.Frequency / 1_000_000_000;
        return ticks > long.MaxValue / 4 ? long.MaxValue / 4 : Math.Max(1L, (long)ticks);
    }

    // kevent EVFILT_TIMER data: the period in the unit selected by fflags (ms by default).
    private static long TimerDataToNanoseconds(ulong data, uint fflags)
    {
        var value = (long)Math.Min(data, (ulong)long.MaxValue);
        Int128 nanoseconds = (fflags & (NoteSeconds | NoteMilliseconds | NoteMicroseconds | NoteNanoseconds)) switch
        {
            NoteSeconds => (Int128)value * 1_000_000_000,
            NoteMicroseconds => (Int128)value * 1000,
            NoteNanoseconds => value,
            _ => (Int128)value * 1_000_000,
        };
        return nanoseconds > long.MaxValue ? long.MaxValue : (long)nanoseconds;
    }

    private static bool AddTimerKnote(
        ulong handle,
        ulong ident,
        short filter,
        ulong userData,
        ushort flags,
        long periodTicks,
        bool periodic,
        uint sfflags = 0,
        ulong sdata = 0)
    {
        if (!TryGetLiveEventQueue(handle, out var state))
        {
            return false;
        }

        IDisposable? previous;
        lock (state.Gate)
        {
            if (state.Deleted)
            {
                return false;
            }

            var knote = AttachOrUpdateLocked(state, ident, filter, userData, flags, sfflags, sdata, out var created);
            if (!created && filter == KernelEventFilterHrTimer)
            {
                // Re-arming an HR timer discards an undelivered expiry.
                DeactivateLocked(state, knote);
                ResetPendingLocked(knote);
            }

            previous = knote.TimerToken as IDisposable;
            knote.TimerToken = null;
            knote.TimerPeriodTicks = periodic ? periodTicks : -1;
            ArmTimerLocked(state, knote, System.Diagnostics.Stopwatch.GetTimestamp() + periodTicks);
        }

        previous?.Dispose();
        Signal(state);
        return true;
    }

    // A periodic knote with a zero period is always expired: it is activated now
    // and again every time a delivery clears it (see OnClearedLocked).
    private static void ArmTimerLocked(Kqueue state, Knote knote, long dueTimestamp)
    {
        if (knote.TimerPeriodTicks == 0)
        {
            knote.Data = SaturatingIncrement(knote.Data);
            ActivateLocked(state, knote);
            return;
        }

        knote.TimerNextDue = dueTimestamp;
        var arm = new TimerArm { Knote = knote };
        knote.TimerToken = arm;
        arm.Handle = TimerScheduler.Schedule(dueTimestamp, static s => FireTimer((TimerArm)s!), arm);
        if (arm.Cancelled)
        {
            Interlocked.Exchange(ref arm.Handle, null)?.Dispose();
        }
    }

    private static void FireTimer(TimerArm arm)
    {
        var knote = arm.Knote;
        var state = knote.Owner;
        lock (state.Gate)
        {
            if (arm.Cancelled || knote.Dropped || state.Deleted || !ReferenceEquals(knote.TimerToken, arm))
            {
                return;
            }

            knote.TimerToken = null;
            var expirations = 1UL;
            var now = System.Diagnostics.Stopwatch.GetTimestamp();
            if (knote.TimerPeriodTicks > 0 && (knote.Flags & KernelEventFlagOneShot) == 0)
            {
                // Drift-free: the next expiry is a whole number of periods after
                // the previous one; periods missed while the host lagged count.
                var next = knote.TimerNextDue + knote.TimerPeriodTicks;
                if (now >= next)
                {
                    var missed = (now - knote.TimerNextDue) / knote.TimerPeriodTicks;
                    expirations += (ulong)missed;
                    next = knote.TimerNextDue + ((missed + 1) * knote.TimerPeriodTicks);
                }

                ArmTimerLocked(state, knote, next);
            }

            knote.Data = knote.Filter == KernelEventFilterHrTimer
                ? 1
                : SaturatingAdd(knote.Data, expirations);
            ActivateLocked(state, knote);
        }

        Signal(state, _logEqueue ? $"source=timer ident=0x{knote.Ident:X16} filter={knote.Filter}" : null);
    }

    private static ulong SaturatingAdd(ulong left, ulong right) =>
        left > ulong.MaxValue - right ? ulong.MaxValue : left + right;

    private static ulong SaturatingIncrement(ulong value) => SaturatingAdd(value, 1);

    // Called after an EV_CLEAR delivery reset the knote.
    private static void OnClearedLocked(Kqueue state, Knote knote)
    {
        if (knote.Filter == KernelEventFilterUser)
        {
            // filt_usertouch(EVENT_PROCESS) with EV_CLEAR forgets fflags and data.
            knote.SFflags = 0;
            knote.SData = 0;
            return;
        }

        if (knote.Filter == KernelEventFilterTimer && knote.TimerPeriodTicks == 0 && !knote.Disabled)
        {
            knote.Data = 1;
            knote.Triggered = true;
            if (!knote.Queued)
            {
                state.Active.AddLast(knote);
                knote.Queued = true;
            }
        }
    }
}
