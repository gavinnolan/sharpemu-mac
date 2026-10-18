// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics;
using System.Globalization;

namespace SharpEmu.Libs.VideoOut;

/// <summary>
/// Opt-in per-flip timestamps for frame-pacing measurements: SHARPEMU_FRAME_TIMES=&lt;path&gt;
/// writes one line per guest flip request, "&lt;sequence&gt; &lt;microseconds on the monotonic clock
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
    public static FrameTimeLog? FromEnvironment()
    {
        var path = Environment.GetEnvironmentVariable(EnvironmentVariable);
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
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
            Console.Error.WriteLine($"[VIDEOOUT][WARN] {EnvironmentVariable}={path} could not be opened: {exception.Message}");
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
