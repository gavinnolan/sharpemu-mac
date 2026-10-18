// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics.Tracing;

namespace SharpEmu.CLI;

/// <summary>
/// SHARPEMU_ALLOC_PROFILE=&lt;seconds&gt; (0 or unset: off). Every interval, writes one
/// [ALLOC-PROFILE] line with the GC counts, total pause and allocation volume of that
/// interval, plus (SHARPEMU_ALLOC_PROFILE_TYPES=1) a histogram of what the managed heap
/// allocated, from the runtime's sampled GCAllocationTick events (one per ~100 KB, so
/// the byte figures are estimates; the type ranking is what matters).
/// </summary>
internal static class AllocationProfiler
{
    public static void StartIfRequested()
    {
        if (!int.TryParse(Environment.GetEnvironmentVariable("SHARPEMU_ALLOC_PROFILE"), out var seconds) || seconds <= 0)
        {
            return;
        }

        var listener = string.Equals(
            Environment.GetEnvironmentVariable("SHARPEMU_ALLOC_PROFILE_TYPES"), "1", StringComparison.Ordinal)
            ? new TickListener()
            : null;
        var thread = new Thread(() => Run(seconds, listener))
        {
            IsBackground = true,
            Name = "alloc-profile",
        };
        thread.Start();
    }

    private static void Run(int seconds, TickListener? listener)
    {
        var lastPause = TimeSpan.Zero;
        var lastBytes = GC.GetTotalAllocatedBytes();
        int last0 = GC.CollectionCount(0), last1 = GC.CollectionCount(1), last2 = GC.CollectionCount(2);
        while (true)
        {
            Thread.Sleep(TimeSpan.FromSeconds(seconds));
            var pause = GC.GetTotalPauseDuration();
            var bytes = GC.GetTotalAllocatedBytes();
            int g0 = GC.CollectionCount(0), g1 = GC.CollectionCount(1), g2 = GC.CollectionCount(2);
            var info = GC.GetGCMemoryInfo();
            Console.Error.WriteLine(
                $"[ALLOC-PROFILE] interval={seconds}s gc0={g0 - last0} gc1={g1 - last1} gc2={g2 - last2} " +
                $"pause_ms={(pause - lastPause).TotalMilliseconds:F0} alloc_mb={(bytes - lastBytes) / 1048576.0:F0} " +
                $"heap_mb={GC.GetTotalMemory(false) / 1048576} committed_mb={info.TotalCommittedBytes / 1048576} " +
                $"total_gc={g0} total_pause_ms={pause.TotalMilliseconds:F0} total_alloc_mb={bytes / 1048576}");
            (lastPause, lastBytes, last0, last1, last2) = (pause, bytes, g0, g1, g2);
            listener?.Dump();
        }
    }

    private sealed class TickListener : EventListener
    {
        private readonly object _lock = new();
        private Dictionary<string, long> _byType = new(StringComparer.Ordinal);
        private Dictionary<string, long> _byThread = new(StringComparer.Ordinal);

        protected override void OnEventSourceCreated(EventSource eventSource)
        {
            if (eventSource.Name == "Microsoft-Windows-DotNETRuntime")
            {
                // Keyword 0x1 = GC; Verbose is what turns on GCAllocationTick.
                EnableEvents(eventSource, EventLevel.Verbose, (EventKeywords)0x1);
            }
        }

        protected override void OnEventWritten(EventWrittenEventArgs data)
        {
            if (data.EventName is null || !data.EventName.StartsWith("GCAllocationTick", StringComparison.Ordinal) ||
                data.Payload is null)
            {
                return;
            }

            string type = "?";
            long amount = 100_000;
            for (var i = 0; i < data.Payload.Count; i++)
            {
                var name = data.PayloadNames?[i];
                if (name == "TypeName" && data.Payload[i] is string text)
                {
                    type = text;
                }
                else if (name == "AllocationAmount64" && data.Payload[i] is long big)
                {
                    amount = big;
                }
                else if (name == "AllocationAmount64" && data.Payload[i] is ulong ubig)
                {
                    amount = (long)ubig;
                }
            }

            var kind = data.Payload.Count > 1 && data.Payload[1] is uint k ? (k == 1 ? "LOH" : "SOH") : "";
            lock (_lock)
            {
                _byType[$"{type} {kind}"] = _byType.GetValueOrDefault($"{type} {kind}") + amount;
                var thread = $"os_tid={data.OSThreadId}";
                _byThread[thread] = _byThread.GetValueOrDefault(thread) + amount;
            }
        }

        public void Dump()
        {
            Dictionary<string, long> types, threads;
            lock (_lock)
            {
                (types, _byType) = (_byType, new(StringComparer.Ordinal));
                (threads, _byThread) = (_byThread, new(StringComparer.Ordinal));
            }

            var total = types.Values.Sum();
            Console.Error.WriteLine(
                "[ALLOC-PROFILE] types (est MB): " +
                string.Join("; ", types.OrderByDescending(p => p.Value).Take(14)
                    .Select(p => $"{p.Key}={p.Value / 1048576.0:F1}")) + $" total={total / 1048576.0:F0}");
            Console.Error.WriteLine(
                "[ALLOC-PROFILE] threads (est MB): " +
                string.Join("; ", threads.OrderByDescending(p => p.Value).Take(8)
                    .Select(p => $"{p.Key}={p.Value / 1048576.0:F1}")));
        }
    }
}
