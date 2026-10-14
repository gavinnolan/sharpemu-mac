// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections.Concurrent;

namespace SharpEmu.HLE.DsTrace;

/// <summary>Guest-state sampler <c>SharpEmu-DsSampler</c> (§5).</summary>
internal static class DsSampler
{
    internal readonly record struct WatchMessage(
        bool Add, ulong Job, ulong Counter, ulong Target, uint Cond, uint Affinity, long SuspendTs);

    private static readonly ConcurrentQueue<WatchMessage>? _watchQueue = DsTrace.Sampler ? new() : null;
    private static volatile bool _running;

    /// <summary>FSJ hand-off: a job suspended on a counter (§4.3).</summary>
    internal static void Watch(ulong job, ulong counter, ulong target, uint cond, uint affinity, long suspendTs)
    {
        if (_running)
        {
            _watchQueue!.Enqueue(new WatchMessage(true, job, counter, target, cond, affinity, suspendTs));
        }
    }

    /// <summary>FSJ hand-off: the job resumed.</summary>
    internal static void Unwatch(ulong job)
    {
        if (_running)
        {
            _watchQueue!.Enqueue(new WatchMessage(false, job, 0, 0, 0, 0, 0));
        }
    }

    internal static void StartThread()
    {
        // The sampler loop lands in a follow-up commit.
    }
}
