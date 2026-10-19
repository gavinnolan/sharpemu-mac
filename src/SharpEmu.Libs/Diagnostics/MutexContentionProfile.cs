// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace SharpEmu.Libs.Diagnostics;

/// <summary>
/// SHARPEMU_PERF_MUTEX=1: guest mutex contention by (mutex, waiting thread, owner when the
/// waiter queued), with the time waiters spent queued. Reported every 15 s, cumulative.
/// </summary>
internal static partial class MutexContentionProfile
{
    internal static readonly bool Enabled =
        Environment.GetEnvironmentVariable("SHARPEMU_PERF_MUTEX") == "1";

    private sealed class Entry
    {
        internal long Count;
        internal long Ticks;
        internal long MaxTicks;
    }

    private static readonly ConcurrentDictionary<(ulong Mutex, string Waiter, string Owner), Entry> _entries = new();
    private static long _nextReport = Stopwatch.GetTimestamp() + Stopwatch.Frequency * 15;
    private static int _reporting;

    [ThreadStatic] private static string? _threadName;

    internal static string CurrentThreadName() => _threadName ??= ResolveThreadName();

    internal static void Record(ulong mutexAddress, string? owner, long startTimestamp)
    {
        if (startTimestamp == 0) return;
        var now = Stopwatch.GetTimestamp();
        var ticks = Math.Max(0, now - startTimestamp);
        var entry = _entries.GetOrAdd((mutexAddress, CurrentThreadName(), owner ?? "?"), static _ => new Entry());
        Interlocked.Increment(ref entry.Count);
        Interlocked.Add(ref entry.Ticks, ticks);
        long max;
        while (ticks > (max = Volatile.Read(ref entry.MaxTicks)) &&
               Interlocked.CompareExchange(ref entry.MaxTicks, ticks, max) != max)
        {
        }

        if (now >= Volatile.Read(ref _nextReport) && Interlocked.Exchange(ref _reporting, 1) == 0)
        {
            try
            {
                Volatile.Write(ref _nextReport, now + Stopwatch.Frequency * 15);
                Report();
            }
            finally
            {
                Volatile.Write(ref _reporting, 0);
            }
        }
    }

    private static void Report()
    {
        var frequency = (double)Stopwatch.Frequency;
        var snapshot = new List<KeyValuePair<(ulong Mutex, string Waiter, string Owner), Entry>>();
        foreach (var pair in _entries)
        {
            snapshot.Add(pair);
        }

        var top = snapshot
            .OrderByDescending(pair => Volatile.Read(ref pair.Value.Ticks))
            .Take(16)
            .Select(pair =>
                $"0x{pair.Key.Mutex:X} waiter={pair.Key.Waiter} owner={pair.Key.Owner} " +
                $"n={pair.Value.Count} wait_s={pair.Value.Ticks / frequency:F2} " +
                $"avg_us={pair.Value.Ticks / frequency * 1e6 / Math.Max(1, pair.Value.Count):F1} " +
                $"max_ms={pair.Value.MaxTicks / frequency * 1e3:F1}");
        Console.Error.WriteLine($"[PERF][MUTEX] contended: {string.Join(" | ", top)}");
    }

    private static unsafe string ResolveThreadName()
    {
        var managed = Thread.CurrentThread.Name;
        if (!string.IsNullOrEmpty(managed)) return managed;
        if (OperatingSystem.IsMacOS())
        {
            var buffer = stackalloc byte[64];
            if (pthread_getname_np(pthread_self(), buffer, 64) == 0 && buffer[0] != 0)
                return Marshal.PtrToStringUTF8((nint)buffer) ?? "<unnamed>";
        }

        return $"<tid {Environment.CurrentManagedThreadId}>";
    }

    [LibraryImport("libSystem.dylib")]
    private static partial nint pthread_self();

    [LibraryImport("libSystem.dylib")]
    private static unsafe partial int pthread_getname_np(nint thread, byte* name, nuint length);
}
