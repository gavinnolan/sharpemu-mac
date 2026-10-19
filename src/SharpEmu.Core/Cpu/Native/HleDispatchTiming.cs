// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
using System.Collections.Concurrent;
using SharpEmu.Core.Diagnostics;

namespace SharpEmu.Core.Cpu.Native;

// Only entered by the opt-in managed import gateway, never a signal callback.
// Elapsed scopes include waits, callbacks and rendering; they are not CPU time.
// Each profiled thread also keeps its own export table, the wall time it spent
// inside top-level imports and, on sampled calls, its CPU clock - which is what
// separates an export that burns the core from one that sleeps in it.
internal static class HleDispatchTiming
{
    private const int MaximumRecordedDepth = 128;
    [ThreadStatic] private static ThreadState? _thread;
    private static long _omittedScopes;
    internal static long OmittedScopes => Interlocked.Read(ref _omittedScopes);

    // Every profiled thread, for the per-thread report. Threads only add themselves.
    internal static readonly ConcurrentQueue<ThreadState> Threads = new();

    private struct Frame
    {
        internal string? Name;
        internal long Children;
    }

    internal sealed class ExportCost
    {
        internal long Calls;
        internal long ExclusiveTicks;
        internal long CpuSamples;
        internal long SampledCpuNs;
        internal long SampledWallTicks;
        // Wall time from the end of this (top-level) import to the start of the thread's next
        // one: guest code, or the thread parked after this import blocked it cooperatively.
        internal long GapAfterTicks;
        internal long LongGapsAfter;
    }

    internal sealed class ThreadState
    {
        private readonly Frame[] _frames = new Frame[MaximumRecordedDepth];
        internal int Depth;
        internal string ThreadName = "<unnamed>";
        // Written by the owning thread only; the reporter reads them racily, which is fine for totals.
        internal readonly ConcurrentDictionary<string, ExportCost> Costs = new(StringComparer.Ordinal);
        internal long FirstTimestamp;
        internal long LastTimestamp;
        internal long TopLevelTicks;
        internal int CpuSampleCounter;
        internal ExportCost? LastTopLevel;
        internal uint MachThread;
        internal long FirstCpuNs = -1;
        /// <summary>CPU this thread used since its first import, or -1 where unavailable.</summary>
        internal long CpuSinceFirstNs()
        {
            var now = HostThreadClock.ThreadCpuNs(MachThread);
            return now >= 0 && FirstCpuNs >= 0 ? now - FirstCpuNs : -1;
        }
        internal void Push() { if (Depth < _frames.Length) _frames[Depth] = default; Depth++; }
        internal void Name(string name) { if (Depth is > 0 and <= MaximumRecordedDepth) _frames[Depth - 1].Name = name; }
        internal Elapsed Pop(int depth, long inclusive)
        {
            // An enabled gateway completes scopes in finally, once and in LIFO order.
            if (Depth != depth) throw new InvalidOperationException("Unbalanced HLE timing scope.");
            var frame = Depth <= _frames.Length ? _frames[Depth - 1] : default;
            Depth--;
            if (Depth > 0 && Depth <= _frames.Length) _frames[Depth - 1].Children += inclusive;
            else if (Depth == 0) TopLevelTicks += inclusive;
            if (depth > _frames.Length)
            {
                Interlocked.Increment(ref _omittedScopes);
                return default; // bounded deep nesting: explicitly omitted, never charged to another export
            }
            _frames[Depth] = default; // names cannot leak into a later unnamed/error dispatch
            return new(frame.Name, inclusive, Math.Max(0, inclusive - frame.Children));
        }
    }
    internal readonly record struct Elapsed(string? Name, long InclusiveTicks, long ExclusiveTicks, bool Recorded = true);
    internal readonly struct Scope(ThreadState thread, int depth, long start)
    {
        internal ThreadState Thread => thread;
        internal Elapsed Complete(long end)
        {
            thread.LastTimestamp = end;
            return thread.Pop(depth, Math.Max(0, end - start));
        }
    }
    internal static Scope Begin(long timestamp)
    {
        var thread = _thread ??= CreateThreadState(timestamp); // one bounded allocation per profiled managed thread
        if (thread.Depth == 0 && thread.LastTopLevel is { } previous)
        {
            var gap = Math.Max(0, timestamp - thread.LastTimestamp);
            previous.GapAfterTicks += gap;
            if (gap > LongGapTicks) previous.LongGapsAfter++;
            thread.LastTopLevel = null;
        }
        thread.Push(); return new(thread, thread.Depth, timestamp);
    }

    private static readonly long LongGapTicks = System.Diagnostics.Stopwatch.Frequency / 10_000; // 100 us
    internal static void NameCurrent(string name) => _thread?.Name(name);

    /// <summary>True on one call in <paramref name="every"/> on this thread (never before its first scope).</summary>
    internal static bool ShouldSampleCpu(int every)
    {
        if (every <= 0 || _thread is not { } thread || ++thread.CpuSampleCounter < every) return false;
        thread.CpuSampleCounter = 0;
        return true;
    }

    private static ThreadState CreateThreadState(long timestamp)
    {
        var state = new ThreadState
        {
            ThreadName = HostThreadClock.CurrentThreadName(),
            FirstTimestamp = timestamp,
            LastTimestamp = timestamp,
            MachThread = HostThreadClock.CurrentThread(),
        };
        state.FirstCpuNs = HostThreadClock.ThreadCpuNs(state.MachThread);
        Threads.Enqueue(state);
        return state;
    }

    /// <summary>This thread's CPU time in ns, or -1 where it is unavailable (see <see cref="HostThreadClock"/>).</summary>
    internal static long ThreadCpuNs() => HostThreadClock.ThreadCpuNs(HostThreadClock.CurrentThread());
}
