// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
using System.Diagnostics;
using System.Globalization;

namespace SharpEmu.Gpu.Metal;

/// <summary>
/// Bounded, opt-in main-presenter wall timings. A frame ends at a unique guest
/// presentation's command-buffer commit, not GPU completion or display scanout.
/// Host redraws accumulate work but never create guest frame samples.
/// </summary>
internal static partial class MetalPresentationProfile
{
    internal enum Phase { DrainGuestWork, MaterializePresentation, AcquireDrawable, EncodeAndCommit, Count }
    private const int Capacity = 4096;
    private const int Stride = (int)Phase.Count + 2;
    private static readonly bool Enabled =
        Environment.GetEnvironmentVariable("SHARPEMU_PROFILE_PERFORMANCE") == "1" ||
        Environment.GetEnvironmentVariable("SHARPEMU_PROFILE_RENDER") == "1";

    // Only the AppKit presenter thread accesses this state; command-stream
    // worker timings deliberately do not enter these counters.
    private static class Storage
    {
        internal static readonly long[] Pending = new long[(int)Phase.Count];
        internal static readonly long[] Rows = new long[Capacity * Stride];
        internal static long Count;
        internal static long PreviousTimestamp;
    }

    internal static void Begin()
    {
        if (AllocationsEnabled) BeginAllocations();
        if (!Enabled) return;
        Array.Clear(Storage.Pending);
        Array.Clear(Storage.Rows);
        Storage.Count = Storage.PreviousTimestamp = 0;
    }

    internal readonly struct Scope : IDisposable
    {
        private readonly Phase _phase;
        private readonly long _started;
        internal Scope(Phase phase) { _phase = phase; _started = Stopwatch.GetTimestamp(); }
        public void Dispose()
        {
            if (_started != 0) Storage.Pending[(int)_phase] += Stopwatch.GetTimestamp() - _started;
        }
    }

    internal static Scope Measure(Phase phase) => Enabled ? new Scope(phase) : default;

    internal static void RecordGuestCommit(long guestSequence)
    {
        if (AllocationsEnabled) RecordAllocationGuestCommit(guestSequence);
        if (!Enabled) return;
        var now = Stopwatch.GetTimestamp();
        var offset = (int)(Storage.Count % Capacity) * Stride;
        Storage.Rows[offset] = now;
        Storage.Rows[offset + 1] = Storage.PreviousTimestamp == 0 ? 0 : now - Storage.PreviousTimestamp;
        Array.Copy(Storage.Pending, 0, Storage.Rows, offset + 2, (int)Phase.Count);
        Array.Clear(Storage.Pending);
        Storage.PreviousTimestamp = now;
        Storage.Count++;
    }

    internal static void End()
    {
        if (!Enabled && !AllocationsEnabled) return;
        // A closed diagnostic output stream cannot turn window shutdown into
        // an emulator failure. Native window/overlay cleanup precedes this call.
        try
        {
            if (Enabled) WriteReport();
            if (AllocationsEnabled) WriteAllocationReport();
        }
        catch (IOException) { }
        catch (ObjectDisposedException) { }
    }

    private static void WriteReport()
    {
        var first = Math.Max(0, Storage.Count - Capacity);
        var gaps = new List<long>((int)(Storage.Count - first));
        Console.Error.WriteLine($"[PERF][METAL_PRESENT_TRACE] frequency={Stopwatch.Frequency} retained={Storage.Count - first} overwritten={first} boundary=unique_guest_command_commit phases=presenter_wall_ms gpu_execution=unmeasured scanout=unmeasured");
        for (var sequence = first; sequence < Storage.Count; sequence++)
        {
            var offset = (int)(sequence % Capacity) * Stride;
            if (Storage.Rows[offset + 1] > 0) gaps.Add(Storage.Rows[offset + 1]);
            var phases = new string[(int)Phase.Count];
            for (var phase = 0; phase < phases.Length; phase++)
                phases[phase] = $"{(Phase)phase}={Milliseconds(Storage.Rows[offset + 2 + phase])}";
            Console.Error.WriteLine($"[PERF][METAL_PRESENT_FRAME] sequence={sequence} timestamp={Storage.Rows[offset]} gap_ms={Milliseconds(Storage.Rows[offset + 1])} {string.Join(" ", phases)}");
        }
        gaps.Sort();
        if (gaps.Count != 0)
        {
            long Percentile(double p) => gaps[Math.Clamp((int)Math.Ceiling(p * gaps.Count) - 1, 0, gaps.Count - 1)];
            Console.Error.WriteLine($"[PERF][METAL_PRESENT_GAPS] samples={gaps.Count} p50_ms={Milliseconds(Percentile(.50))} p95_ms={Milliseconds(Percentile(.95))} p99_ms={Milliseconds(Percentile(.99))} max_ms={Milliseconds(gaps[^1])} scope=retained_commit_intervals_including_loads_and_movies");
        }
        // Work after the last guest commit has no frame row. Keep it explicit,
        // especially when a title stalls while the host continues redrawing.
        Console.Error.WriteLine("[PERF][METAL_PRESENT_TAIL] " + string.Join(" ", Enumerable.Range(0, (int)Phase.Count).Select(phase => $"{(Phase)phase}={Milliseconds(Storage.Pending[phase])}")));
    }

    private static string Milliseconds(long ticks) => (ticks * 1000.0 / Stopwatch.Frequency).ToString("F3", CultureInfo.InvariantCulture);
}
