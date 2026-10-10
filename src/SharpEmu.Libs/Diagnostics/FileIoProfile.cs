// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;

namespace SharpEmu.Libs.Diagnostics;

/// <summary>
/// Opt-in aggregate profile of the HLE file-I/O surface (SHARPEMU_PROFILE_FILEIO=1).
/// Boot-time loading in file-heavy titles is dominated by a few guest exports
/// called tens or hundreds of thousands of times, so a per-call trace is useless
/// (it changes the thing being measured). This records one fixed-size counter set
/// per operation plus a bounded per-path table, and a background reporter prints
/// the totals every few seconds.
/// </summary>
/// <remarks>
/// Disabled is the hot path: <see cref="Enabled"/> is a static readonly bool, so
/// the JIT folds every <c>if (!Enabled) return</c> guard away and an uninstrumented
/// run pays nothing beyond the (elided) branch.
/// </remarks>
internal static class FileIoProfile
{
    internal enum Op
    {
        Open,
        Close,
        Read,
        Pread,
        Write,
        Pwrite,
        Lseek,
        Stat,
        Fstat,
        Getdents,
        Unlink,
        ResolvePath,
        HostStat,
        AprResolveIds,
        AprFileSize,
        AprFileStat,
        AmprReadFileCmd,
        AmprSubmit,
        AmprHostRead,
        AmprHostOpen,
        Fopen,
        Fread,
        Fseek,
    }

    private const int OpCount = (int)Op.Fseek + 1;

    // A guest path table is only useful while it stays small enough to print and
    // cheap enough to probe; a dump with 223k files would otherwise grow one entry
    // per asset. Past the cap, calls still land in the per-op totals.
    private const int MaxTrackedPaths = 16384;

    internal static readonly bool Enabled = string.Equals(
        Environment.GetEnvironmentVariable("SHARPEMU_PROFILE_FILEIO"),
        "1",
        StringComparison.Ordinal);

    private static readonly double TicksToMs = 1000.0 / Stopwatch.Frequency;

    // One object per op: separate allocations keep the Interlocked targets off
    // each other's cache lines without hand-written padding.
    private sealed class OpCounters
    {
        public long Calls;
        public long Ticks;
        public long MaxTicks;
        public long Bytes;
        public long Failures;
        public long ReportedCalls;
        public long ReportedTicks;
    }

    private sealed class PathCounters
    {
        public long Calls;
        public long Ticks;
        public long Bytes;
    }

    private static readonly OpCounters[] _ops = CreateCounters();
    private static readonly ConcurrentDictionary<string, PathCounters> _paths =
        new(StringComparer.Ordinal);
    private static int _pathCount;
    private static int _reporterStarted;

    private static OpCounters[] CreateCounters()
    {
        var counters = new OpCounters[OpCount];
        for (var i = 0; i < counters.Length; i++)
        {
            counters[i] = new OpCounters();
        }

        return counters;
    }

    /// <summary>Timestamp to hand back to <see cref="End"/>; 0 when disabled.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static long Begin()
    {
        if (!Enabled)
        {
            return 0;
        }

        EnsureReporter();
        return Stopwatch.GetTimestamp();
    }

    /// <summary>
    /// Records one completed operation. <paramref name="started"/> of 0 means the
    /// profile is off (or the caller never began), and the call is dropped.
    /// </summary>
    internal static void End(
        Op op,
        long started,
        long bytes = 0,
        string? path = null,
        bool failed = false)
    {
        if (!Enabled || started == 0)
        {
            return;
        }

        var elapsed = Stopwatch.GetTimestamp() - started;
        if (elapsed < 0)
        {
            elapsed = 0;
        }

        var counters = _ops[(int)op];
        Interlocked.Increment(ref counters.Calls);
        Interlocked.Add(ref counters.Ticks, elapsed);
        Interlocked.Add(ref counters.Bytes, bytes);
        if (failed)
        {
            Interlocked.Increment(ref counters.Failures);
        }

        // Racy max: a lost update only understates the worst call, which never
        // justifies a lock on a path this hot.
        if (elapsed > Volatile.Read(ref counters.MaxTicks))
        {
            Volatile.Write(ref counters.MaxTicks, elapsed);
        }

        if (path is { Length: > 0 })
        {
            RecordPath(path, elapsed, bytes);
        }
    }

    // Exports with many early returns cannot thread an out-parameter through
    // every one of them without obscuring the logic they implement. They instead
    // note the guest path once, where they read it, and the wrapper's
    // EndNoted picks it up on the way out.
    [ThreadStatic]
    private static string? _notedPath;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void NotePath(string? path)
    {
        if (Enabled)
        {
            _notedPath = path;
        }
    }

    /// <summary>
    /// Completes an operation using the path its body noted via
    /// <see cref="NotePath"/>. The note is consumed, so a body that returned
    /// before reaching its NotePath cannot inherit the previous call's path.
    /// </summary>
    internal static void EndNoted(Op op, long started, long bytes = 0, bool failed = false)
    {
        if (!Enabled)
        {
            return;
        }

        var path = _notedPath;
        _notedPath = null;
        End(op, started, bytes, path, failed);
    }

    /// <summary>Records an operation whose cost is not worth timing (count only).</summary>
    internal static void Count(Op op, long bytes = 0)
    {
        if (!Enabled)
        {
            return;
        }

        EnsureReporter();
        var counters = _ops[(int)op];
        Interlocked.Increment(ref counters.Calls);
        Interlocked.Add(ref counters.Bytes, bytes);
    }

    private static void RecordPath(string path, long elapsed, long bytes)
    {
        if (!_paths.TryGetValue(path, out var counters))
        {
            if (Volatile.Read(ref _pathCount) >= MaxTrackedPaths)
            {
                return;
            }

            counters = _paths.GetOrAdd(path, static _ => new PathCounters());
            Interlocked.Increment(ref _pathCount);
        }

        Interlocked.Increment(ref counters.Calls);
        Interlocked.Add(ref counters.Ticks, elapsed);
        Interlocked.Add(ref counters.Bytes, bytes);
    }

    private static void EnsureReporter()
    {
        if (Volatile.Read(ref _reporterStarted) != 0 ||
            Interlocked.Exchange(ref _reporterStarted, 1) != 0)
        {
            return;
        }

        var intervalSeconds = 5;
        if (int.TryParse(
                Environment.GetEnvironmentVariable("SHARPEMU_PROFILE_FILEIO_INTERVAL"),
                out var configured) &&
            configured is > 0 and <= 600)
        {
            intervalSeconds = configured;
        }

        var reporter = new Thread(() => ReportLoop(intervalSeconds))
        {
            IsBackground = true,
            Name = "fileio-profile",
        };
        reporter.Start();
    }

    private static void ReportLoop(int intervalSeconds)
    {
        var started = Stopwatch.StartNew();
        while (true)
        {
            Thread.Sleep(intervalSeconds * 1000);
            try
            {
                Console.Error.Write(FormatReport(started.Elapsed));
                Console.Error.Flush();
            }
            catch (IOException)
            {
                // stderr closed during shutdown; the profile is diagnostic only.
                return;
            }
        }
    }

    /// <summary>Renders the current totals. Exposed for tests.</summary>
    internal static string FormatReport(TimeSpan uptime)
    {
        var builder = new StringBuilder(4096);
        var totalCalls = 0L;
        var totalTicks = 0L;
        for (var i = 0; i < _ops.Length; i++)
        {
            totalCalls += Volatile.Read(ref _ops[i].Calls);
            totalTicks += Volatile.Read(ref _ops[i].Ticks);
        }

        builder.Append("[FILEIO] t=").Append(uptime.TotalSeconds.ToString("F1"))
            .Append("s calls=").Append(totalCalls)
            .Append(" total_ms=").Append((totalTicks * TicksToMs).ToString("F1"))
            .AppendLine(" (nested ops double-count)");

        for (var i = 0; i < _ops.Length; i++)
        {
            var counters = _ops[i];
            var calls = Volatile.Read(ref counters.Calls);
            if (calls == 0)
            {
                continue;
            }

            var ticks = Volatile.Read(ref counters.Ticks);
            var windowCalls = calls - counters.ReportedCalls;
            var windowMs = (ticks - counters.ReportedTicks) * TicksToMs;
            counters.ReportedCalls = calls;
            counters.ReportedTicks = ticks;

            builder.Append("[FILEIO]   ").Append(((Op)i).ToString().ToLowerInvariant().PadRight(16))
                .Append(" calls=").Append(calls)
                .Append(" total_ms=").Append((ticks * TicksToMs).ToString("F1"))
                .Append(" max_ms=").Append((Volatile.Read(ref counters.MaxTicks) * TicksToMs).ToString("F2"))
                .Append(" avg_us=").Append(calls == 0 ? "0" : (ticks * TicksToMs * 1000.0 / calls).ToString("F1"))
                .Append(" mib=").Append((Volatile.Read(ref counters.Bytes) / (1024.0 * 1024.0)).ToString("F1"))
                .Append(" fail=").Append(Volatile.Read(ref counters.Failures))
                .Append(" +calls=").Append(windowCalls)
                .Append(" +ms=").Append(windowMs.ToString("F1"))
                .AppendLine();
        }

        AppendTopPaths(builder, "by_ms", static p => p.Value.Ticks);
        AppendTopPaths(builder, "by_calls", static p => p.Value.Calls);
        return builder.ToString();
    }

    private static void AppendTopPaths(
        StringBuilder builder,
        string label,
        Func<KeyValuePair<string, PathCounters>, long> rank)
    {
        if (_paths.IsEmpty)
        {
            return;
        }

        var top = _paths.ToArray();
        Array.Sort(top, (left, right) => rank(right).CompareTo(rank(left)));
        var shown = Math.Min(10, top.Length);
        for (var i = 0; i < shown; i++)
        {
            var entry = top[i];
            builder.Append("[FILEIO]   top:").Append(label).Append(' ')
                .Append("calls=").Append(Volatile.Read(ref entry.Value.Calls))
                .Append(" ms=").Append((Volatile.Read(ref entry.Value.Ticks) * TicksToMs).ToString("F1"))
                .Append(" mib=").Append((Volatile.Read(ref entry.Value.Bytes) / (1024.0 * 1024.0)).ToString("F1"))
                .Append(" path='").Append(entry.Key).AppendLine("'");
        }

        builder.Append("[FILEIO]   tracked_paths=").Append(top.Length)
            .Append(" capped=").AppendLine(top.Length >= MaxTrackedPaths ? "1" : "0");
    }

    /// <summary>Test hook: drop all accumulated state.</summary>
    internal static void ResetForTests()
    {
        for (var i = 0; i < _ops.Length; i++)
        {
            _ops[i] = new OpCounters();
        }

        _paths.Clear();
        Volatile.Write(ref _pathCount, 0);
    }
}
