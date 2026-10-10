// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Globalization;

namespace SharpEmu.HLE;

/// <summary>
/// SHARPEMU_LOG_LOAD_PROGRESS=1: a periodic line saying whether the guest is still
/// loading or has stopped.
///
/// A title that takes minutes to boot and one that is wedged waiting on something the
/// emulator never delivers look the same from outside — both sit there. They differ in
/// two things this reports: whether bytes are still arriving from the guest's files, and
/// whether each guest thread's HLE call count is still advancing. A thread whose count is
/// frozen while it sits in a named wait is the thing to fix; every thread advancing and
/// bytes still flowing means it is merely slow.
/// </summary>
public static class GuestLoadProgress
{
    public static readonly bool Enabled = string.Equals(
        Environment.GetEnvironmentVariable("SHARPEMU_LOG_LOAD_PROGRESS"), "1", StringComparison.Ordinal);

    private static long _fileReadBytes;
    private static long _fileReads;
    private static long _amprReadBytes;
    private static long _amprReads;
    private static long _amprUnknownIds;
    private static long _totalFileReadBytes;
    private static long _totalAmprReadBytes;
    private static int _quietIntervals;

    private static Func<string>? _threadSummary;
    private static Timer? _timer;
    private static readonly Lock _gate = new();

    /// <summary>Bytes a guest read() or pread() delivered.</summary>
    public static void NoteFileRead(long bytes)
    {
        if (!Enabled || bytes <= 0)
        {
            return;
        }

        Interlocked.Add(ref _fileReadBytes, bytes);
        Interlocked.Increment(ref _fileReads);
    }

    /// <summary>Bytes an AMPR command-buffer file read delivered.</summary>
    public static void NoteAmprRead(long bytes, bool unknownId = false)
    {
        if (!Enabled)
        {
            return;
        }

        if (unknownId)
        {
            Interlocked.Increment(ref _amprUnknownIds);
        }

        if (bytes > 0)
        {
            Interlocked.Add(ref _amprReadBytes, bytes);
        }

        Interlocked.Increment(ref _amprReads);
    }

    /// <summary>
    /// Supplies the per-thread summary and starts the reporter. The backend owns the
    /// thread table and its lock, so it describes itself rather than exposing them.
    /// </summary>
    public static void Start(Func<string> threadSummary)
    {
        if (!Enabled)
        {
            return;
        }

        lock (_gate)
        {
            if (_timer is not null)
            {
                return;
            }

            _threadSummary = threadSummary;
            _timer = new Timer(static _ => Report(), null, 2000, 2000);
        }
    }

    public static void Stop()
    {
        lock (_gate)
        {
            _timer?.Dispose();
            _timer = null;
        }
    }

    private static void Report()
    {
        var fileBytes = Interlocked.Exchange(ref _fileReadBytes, 0);
        var files = Interlocked.Exchange(ref _fileReads, 0);
        var amprBytes = Interlocked.Exchange(ref _amprReadBytes, 0);
        var ampr = Interlocked.Exchange(ref _amprReads, 0);
        var unknown = Interlocked.Exchange(ref _amprUnknownIds, 0);
        _totalFileReadBytes += fileBytes;
        _totalAmprReadBytes += amprBytes;
        // Consecutive intervals in which nothing arrived. One is noise; a run of them
        // with threads parked is the difference between slow and stuck.
        _quietIntervals = fileBytes + amprBytes == 0 ? _quietIntervals + 1 : 0;
        string threads;
        try
        {
            threads = _threadSummary?.Invoke() ?? string.Empty;
        }
        catch (Exception exception)
        {
            threads = $"threads=unavailable({exception.GetType().Name})";
        }

        Console.Error.WriteLine(string.Format(
            CultureInfo.InvariantCulture,
            "[LOADER][LOAD] read_kb={0} reads={1} ampr_kb={2} ampr_reads={3} ampr_unknown={4} " +
            "total_mb={5:F1} quiet={7} {6}",
            fileBytes / 1024,
            files,
            amprBytes / 1024,
            ampr,
            unknown,
            (_totalFileReadBytes + _totalAmprReadBytes) / (1024.0 * 1024.0),
            threads,
            _quietIntervals));
    }
}
