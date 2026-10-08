// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
namespace SharpEmu.Core.Cpu.Native;

// Only entered by the opt-in managed import gateway, never a signal callback.
// Elapsed scopes include waits, callbacks and rendering; they are not CPU time.
internal static class HleDispatchTiming
{
    private const int MaximumRecordedDepth = 128;
    [ThreadStatic] private static ThreadState? _thread;
    private static long _omittedScopes;
    internal static long OmittedScopes => Interlocked.Read(ref _omittedScopes);

    private struct Frame
    {
        internal string? Name;
        internal long Children;
    }
    internal sealed class ThreadState
    {
        private readonly Frame[] _frames = new Frame[MaximumRecordedDepth];
        internal int Depth;
        internal void Push() { if (Depth < _frames.Length) _frames[Depth] = default; Depth++; }
        internal void Name(string name) { if (Depth is > 0 and <= MaximumRecordedDepth) _frames[Depth - 1].Name = name; }
        internal Elapsed Pop(int depth, long inclusive)
        {
            // An enabled gateway completes scopes in finally, once and in LIFO order.
            if (Depth != depth) throw new InvalidOperationException("Unbalanced HLE timing scope.");
            var frame = Depth <= _frames.Length ? _frames[Depth - 1] : default;
            Depth--;
            if (Depth > 0 && Depth <= _frames.Length) _frames[Depth - 1].Children += inclusive;
            if (depth > _frames.Length)
            {
                Interlocked.Increment(ref _omittedScopes);
                return default; // bounded deep nesting: explicitly omitted, never charged to another export
            }
            _frames[Depth] = default; // names cannot leak into a later unnamed/error dispatch
            return new(frame.Name, inclusive, Math.Max(0, inclusive - frame.Children));
        }
    }
    internal readonly record struct Elapsed(string? Name, long InclusiveTicks, long ExclusiveTicks);
    internal readonly struct Scope(ThreadState thread, int depth, long start)
    {
        internal Elapsed Complete(long end) => thread.Pop(depth, Math.Max(0, end - start));
    }
    internal static Scope Begin(long timestamp)
    {
        var thread = _thread ??= new ThreadState(); // one bounded allocation per profiled managed thread
        thread.Push(); return new(thread, thread.Depth, timestamp);
    }
    internal static void NameCurrent(string name) => _thread?.Name(name);
}
