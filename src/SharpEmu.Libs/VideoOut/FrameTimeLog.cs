// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics;
using System.Globalization;
using SharpEmu.Logging;

namespace SharpEmu.Libs.VideoOut;

/// <summary>
/// Opt-in per-flip timestamps for frame-pacing measurements: SHARPEMU_FRAME_TIMES=&lt;path&gt;
/// (or 1 for frame-times-&lt;timestamp&gt;.txt under the logs folder) writes one line per guest flip request, "&lt;sequence&gt; &lt;microseconds on the monotonic clock
/// since the log opened&gt;", after a header line that carries the wall-clock time of that origin
/// ("# t0_unix_us=..."), so a script can place every flip on the same timeline as its own
/// markers. Lines are buffered and flushed once a second; when the variable is unset no
/// instance exists and a flip pays one null check.
/// </summary>
internal sealed class FrameTimeLog : IDisposable
{
    public const string EnvironmentVariable = "SHARPEMU_FRAME_TIMES";

    private readonly object _gate = new();
    private readonly TextWriter _writer;
    private readonly long _originTimestamp;
    private readonly long _frequency;
    private readonly Timer? _flushTimer;
    private long _sequence;
    private bool _disposed;

    public FrameTimeLog(
        TextWriter writer,
        long originTimestamp,
        long unixOriginMicroseconds,
        long frequency,
        TimeSpan flushInterval)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(frequency);
        _writer = writer;
        _originTimestamp = originTimestamp;
        _frequency = frequency;
        _writer.Write($"# sharpemu-frame-times v1 clock=monotonic t0_unix_us={unixOriginMicroseconds.ToString(CultureInfo.InvariantCulture)}\n");
        _writer.Flush();
        if (flushInterval > TimeSpan.Zero)
        {
            _flushTimer = new Timer(_ => Flush(), null, flushInterval, flushInterval);
        }
    }

    /// <summary>The log named by <see cref="EnvironmentVariable"/>, or null when it is unset or cannot be opened.</summary>
    public static FrameTimeLog? FromEnvironment() => FromEnvironment(EnvironmentVariable, "frame-times");

    /// <summary>The log named by <paramref name="variable"/> (same format), or null.</summary>
    public static FrameTimeLog? FromEnvironment(string variable, string defaultName)
    {
        var path = SharpEmuPaths.ResolveDumpPath(
            variable, $"{defaultName}-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
        return path is null ? null : Open(path, variable);
    }

    /// <summary>A log written to <paramref name="path"/>, or null when it cannot be opened.</summary>
    public static FrameTimeLog? Open(string path, string variable)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
            var log = new FrameTimeLog(
                new StreamWriter(stream),
                Stopwatch.GetTimestamp(),
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000,
                Stopwatch.Frequency,
                TimeSpan.FromSeconds(1));
            AppDomain.CurrentDomain.ProcessExit += (_, _) => log.Dispose();
            return log;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            Console.Error.WriteLine($"[VIDEOOUT][WARN] {variable}={path} could not be opened: {exception.Message}");
            return null;
        }
    }

    public void Record() => Record(Stopwatch.GetTimestamp());

    public void Record(long timestamp)
    {
        var microseconds = (timestamp - _originTimestamp) * 1_000_000 / _frequency;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _writer.Write(++_sequence);
            _writer.Write(' ');
            _writer.Write(microseconds);
            _writer.Write('\n');
        }
    }

    public void Flush()
    {
        lock (_gate)
        {
            if (!_disposed)
            {
                _writer.Flush();
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _flushTimer?.Dispose();
            _writer.Flush();
            _writer.Dispose();
        }
    }
}

/// <summary>
/// SHARPEMU_PRESENT_TIMES=&lt;path&gt;: the same per-frame timestamps as
/// <see cref="FrameTimeLog"/>, taken where the renderer takes a guest flip off its command
/// stream instead of where the guest requests it. Under the two-process split the two differ:
/// the guest can run ahead of a busy GPU process and be held back, so its flip requests come in
/// bursts while the frames reach the screen at the renderer's own pace.
/// </summary>
public static class PresentTimeLog
{
    private static readonly bool Enabled = Environment.GetEnvironmentVariable("SHARPEMU_PRESENT_TIMES") is { Length: > 0 };

    /// <summary>Records one presented guest frame; a flag test when the log is off.</summary>
    public static void Record()
    {
        if (Enabled)
        {
            Holder.Log?.Record();
        }
    }

    // Opened on the first presented frame, by the process that presents: under the split the
    // guest process compiles the same presentation code without running it, and a log it had
    // opened (with read-only sharing) would shut the GPU process out of the file.
    private static class Holder
    {
        internal static readonly FrameTimeLog? Log;

        static Holder()
        {
            // One file per process (<path>.<pid>): under the split both processes load this code.
            var path = SharpEmuPaths.ResolveDumpPath("SHARPEMU_PRESENT_TIMES", $"present-times-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
            if (path is null)
            {
                return;
            }

            path = $"{path}.{Environment.ProcessId}";
            Log = FrameTimeLog.Open(path, "SHARPEMU_PRESENT_TIMES");
            Console.Error.WriteLine($"[VIDEOOUT] present times -> {path} opened={Log is not null}");
        }
    }
}

