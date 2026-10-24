// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics;
using System.Globalization;

namespace SharpEmu.Logging;

/// <summary>
/// SHARPEMU_GC_PAUSES=1: every runtime garbage collection that paused this process for 10 ms or more
/// is logged as <c>[GC-PAUSE] pid=.. unix_us=.. gen=.. pause_ms=..</c>, on the wall clock the frame-time
/// log (SHARPEMU_FRAME_TIMES) names its origin in, so a long frame can be matched with the pause in it.
/// The last collection is polled every 50 ms; one that two polls do not separate is missed.
/// </summary>
public static class GcPauseLog
{
    private const double ThresholdMilliseconds = 10;
    private static int _started;

    public static void StartFromEnvironment()
    {
        if (Environment.GetEnvironmentVariable("SHARPEMU_GC_PAUSES") != "1" ||
            Interlocked.Exchange(ref _started, 1) != 0)
        {
            return;
        }

        var thread = new Thread(Poll) { IsBackground = true, Name = "SharpEmu GC pause log" };
        thread.Start();
    }

    private static void Poll()
    {
        var pid = Environment.ProcessId;
        long lastIndex = -1;
        while (true)
        {
            Thread.Sleep(50);
            var info = GC.GetGCMemoryInfo(GCKind.Any);
            if (info.Index == lastIndex || info.Index == 0)
            {
                continue;
            }

            lastIndex = info.Index;
            var pause = 0.0;
            foreach (var duration in info.PauseDurations)
            {
                pause += duration.TotalMilliseconds;
            }

            if (pause >= ThresholdMilliseconds)
            {
                var unixMicroseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000;
                Console.Error.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"[GC-PAUSE] pid={pid} unix_us={unixMicroseconds} gen={info.Generation} pause_ms={pause:F1} " +
                    $"heap_mb={info.HeapSizeBytes / (1024 * 1024)} index={info.Index}"));
            }
        }
    }
}
