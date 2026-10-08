// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
namespace SharpEmu.Gpu.Metal;

// Producer payload accounting, not allocated bytes, GPU dispatches or freshness.
// Flags are process-start settings. Storage is touched only when profiling is on.
internal static class MetalVertexPrefixProfile
{
    internal static readonly bool Enabled = Environment.GetEnvironmentVariable("SHARPEMU_PROFILE_PERFORMANCE") == "1";
    internal static readonly bool PrefixEnabled = Environment.GetEnvironmentVariable("SHARPEMU_METAL_VERTEX_PREFIX") != "0";
    static MetalVertexPrefixProfile() { }
    private static class Storage
    {
        static Storage() { }
        internal static readonly Aggregate Value = new();
    }
    private static Action<string>? _reportSinkOverride = null;

    internal static void WarmUp() { if (Enabled) _ = Storage.Value; }
    internal static CopyScope BeginCopy(bool packetEligible) => Enabled ? new(Storage.Value, packetEligible) : default;
    internal static Totals Snapshot() => Enabled ? Storage.Value.Snapshot() : default;

    // Independent, tiny instrumentation gate: no native calls, GPU gates,
    // formatting, callbacks or allocation while held. End obtains one coherent
    // snapshot; producer updates after that boundary remain outside the report.
    internal sealed class Aggregate
    {
        private readonly object _gate = new();
        private long _attempted, _packetEligible, _qualified, _copied, _applied, _full, _actual, _failed, _active, _rejectedBytes;
        private bool _overflow;
        internal bool Begin(bool eligible)
        {
            lock (_gate)
            {
                try
                {
                    var attempted = checked(_attempted + 1);
                    var packet = checked(_packetEligible + (eligible ? 1 : 0));
                    var active = checked(_active + 1);
                    _attempted = attempted; _packetEligible = packet; _active = active;
                    return true;
                }
                catch (OverflowException) { _overflow = true; return false; }
            }
        }
        internal void Finish(bool qualified, bool copied, bool applied, long full, long actual, bool bytesKnown)
        {
            lock (_gate)
            {
                if (_active <= 0) { _overflow = true; return; }
                _active--;
                try
                {
                    var nextQualified = checked(_qualified + (qualified ? 1 : 0));
                    var nextCopied = checked(_copied + (copied ? 1 : 0));
                    var nextApplied = checked(_applied + (copied && applied ? 1 : 0));
                    var nextFailed = checked(_failed + (copied ? 0 : 1));
                    var nextRejected = checked(_rejectedBytes + (copied && !bytesKnown ? 1 : 0));
                    var nextFull = checked(_full + (copied && bytesKnown ? full : 0));
                    var nextActual = checked(_actual + (copied && bytesKnown ? actual : 0));
                    _qualified = nextQualified; _copied = nextCopied; _applied = nextApplied;
                    _failed = nextFailed; _rejectedBytes = nextRejected; _full = nextFull; _actual = nextActual;
                }
                catch (OverflowException) { _overflow = true; }
            }
        }
        internal Totals Snapshot()
        {
            lock (_gate) return new(_attempted, _packetEligible, _qualified, _copied, _applied, _full, _actual,
                _failed, _active, _rejectedBytes, _overflow);
        }
    }

    internal readonly record struct Totals(long Attempted, long PacketEligible, long Qualified, long Copied,
        long Applied, long FullBytes, long ActualBytes, long Failed, long Active, long RejectedBytes, bool Overflow)
    {
        internal bool CoverageComplete => !Overflow && Active == 0 && Failed == 0 && RejectedBytes == 0;
    }

    internal struct CopyScope
    {
        private Aggregate? _aggregate;
        private long _full, _actual;
        private bool _qualified, _copied, _applied, _unknownBytes;
        internal CopyScope(Aggregate aggregate, bool packetEligible)
        {
            this = default;
            if (aggregate.Begin(packetEligible)) _aggregate = aggregate;
        }
        internal void Qualify() { if (_aggregate is not null) _qualified = true; }
        internal void Stream(ulong requested, int actual)
        {
            if (_aggregate is null || _unknownBytes) return;
            if (actual < 0 || requested > long.MaxValue) { _unknownBytes = true; return; }
            try { _full = checked(_full + (long)requested); _actual = checked(_actual + actual); }
            catch (OverflowException) { _unknownBytes = true; }
        }
        internal void Complete(bool applied)
        {
            if (_aggregate is null) return;
            _copied = true; _applied = applied;
        }
        internal void Finish()
        {
            if (_aggregate is not { } aggregate) return;
            _aggregate = null; // This local finally consumes its scope once.
            aggregate.Finish(_qualified, _copied, _applied, _full, _actual, !_unknownBytes);
        }
    }

    internal static void EndReport()
    {
        if (!Enabled) return;
        try
        {
            var totals = Snapshot(); // Never format or call a sink under the gate.
            var qualified = PrefixEnabled ? totals.Qualified.ToString(System.Globalization.CultureInfo.InvariantCulture) : "unknown";
            var line = $"[PERF][METAL_VERTEX_PREFIX] schema=1 scope=process_producer_payload policy={(PrefixEnabled ? "on" : "off")} " +
                $"attempted={totals.Attempted} packet_eligible={totals.PacketEligible} qualified={qualified} copied={totals.Copied} applied={totals.Applied} " +
                $"full_requested_bytes={totals.FullBytes} actual_copied_bytes={totals.ActualBytes} failed_capture_scopes={totals.Failed} " +
                $"active_capture_scopes={totals.Active} rejected_byte_samples={totals.RejectedBytes} overflow={totals.Overflow} coverage_complete={totals.CoverageComplete} " +
                "bytes_boundary=successful_copy_vertex_buffers excludes=index_bytes_allocations_gpu_dispatch_freshness snapshot=coherent_end_boundary";
            if (_reportSinkOverride is { } sink) sink(line);
            else Console.Error.WriteLine(line);
        }
        catch { /* Optional telemetry must not replace a guest/shutdown failure. */ }
    }
}
