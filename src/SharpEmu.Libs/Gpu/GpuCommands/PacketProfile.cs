// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics;

namespace SharpEmu.Libs.Gpu.GpuCommands;

/// <summary>
/// Where the command-stream worker's own CPU goes, per packet opcode.
///
/// The shared render profile says how much of the worker is the command stream; it
/// cannot say which packets. A title that spends most of a run interpreting packets
/// — Demon's Souls measured four guest submissions per draw — needs the opcode
/// breakdown to know whether the cost is the draws, the waits, the register writes or
/// simply reading each packet out of guest memory.
///
/// Off unless SHARPEMU_GPU_PACKET_PROFILE=1, and then it costs two timestamps per
/// packet. Only the command-stream thread writes these counters.
/// </summary>
public static class PacketProfile
{
    public const int OpcodeCount = 256;

    public static readonly bool Enabled =
        Environment.GetEnvironmentVariable("SHARPEMU_GPU_PACKET_PROFILE") == "1";

    private static readonly long[] _counts = new long[OpcodeCount];
    private static readonly long[] _ticks = new long[OpcodeCount];
    private static readonly double _reportSeconds = ResolveReportSeconds();
    private static long _windowStart;
    private static long _headerReads;
    private static long _payloadReads;
    private static long _payloadDwords;
    private static long _slices;
    private static long _suspends;
    private static long _loopTicks;

    private static double ResolveReportSeconds() =>
        double.TryParse(
            Environment.GetEnvironmentVariable("SHARPEMU_GPU_PACKET_PROFILE_S"),
            System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture,
            out var seconds) && seconds > 0
            ? seconds
            : 5.0;

    /// <summary>One packet handler ran; the ticks are its own, not its reads'.</summary>
    public static void RecordPacket(uint opcode, long ticks)
    {
        if (opcode < OpcodeCount)
        {
            _counts[opcode]++;
            _ticks[opcode] += ticks;
        }
    }

    public static void RecordHeaderRead() => _headerReads++;

    private static long _wakes;
    private static long _wakesSkipped;
    private static long _retryAll;

    /// <summary>A targeted wake looked at the blocked heads: how many it woke.</summary>
    public static void RecordWakes(long woken, long skipped)
    {
        _wakes += woken;
        _wakesSkipped += skipped;
    }

    /// <summary>The fallback woke every blocked head.</summary>
    public static void RecordRetryAll() => _retryAll++;

    public static void RecordPayloadRead(uint dwords)
    {
        _payloadReads++;
        _payloadDwords += dwords;
    }

    public static void RecordSlice(long ticks, bool suspended)
    {
        _slices++;
        _loopTicks += ticks;
        if (suspended)
        {
            _suspends++;
        }
    }

    /// <summary>Prints the breakdown when the window is up; cheap to call per slice.</summary>
    public static void ReportIfDue()
    {
        if (!Enabled)
        {
            return;
        }

        var now = Stopwatch.GetTimestamp();
        if (_windowStart == 0)
        {
            _windowStart = now;
            return;
        }

        var elapsed = now - _windowStart;
        if (elapsed < _reportSeconds * Stopwatch.Frequency)
        {
            return;
        }

        _windowStart = now;
        var seconds = elapsed / (double)Stopwatch.Frequency;
        var culture = System.Globalization.CultureInfo.InvariantCulture;
        var frequency = (double)Stopwatch.Frequency;
        var builder = new System.Text.StringBuilder();
        builder.Append("[GPU][PACKETS] window_s=").Append(seconds.ToString("F1", culture));
        builder.Append(" slices=").Append(_slices);
        builder.Append(" suspends=").Append(_suspends);
        builder.Append(" loop_ms=").Append((_loopTicks * 1000.0 / frequency).ToString("F1", culture));
        builder.Append(" wakes=").Append(_wakes);
        builder.Append(" wakes_skipped=").Append(_wakesSkipped);
        builder.Append(" retry_all=").Append(_retryAll);
        builder.Append(" header_reads=").Append(_headerReads);
        builder.Append(" payload_reads=").Append(_payloadReads);
        builder.Append(" payload_dwords=").Append(_payloadDwords);

        // The opcodes that cost the most time this window, biggest first.
        Span<int> order = stackalloc int[16];
        Span<long> best = stackalloc long[16];
        var kept = 0;
        for (var opcode = 0; opcode < OpcodeCount; opcode++)
        {
            var ticks = _ticks[opcode];
            if (ticks == 0 && _counts[opcode] == 0)
            {
                continue;
            }

            var position = kept;
            while (position > 0 && best[position - 1] < ticks)
            {
                if (position < order.Length)
                {
                    best[position] = best[position - 1];
                    order[position] = order[position - 1];
                }

                position--;
            }

            if (position < order.Length)
            {
                best[position] = ticks;
                order[position] = opcode;
                if (kept < order.Length)
                {
                    kept++;
                }
            }
        }

        var totalPackets = 0L;
        var totalTicks = 0L;
        for (var opcode = 0; opcode < OpcodeCount; opcode++)
        {
            totalPackets += _counts[opcode];
            totalTicks += _ticks[opcode];
        }

        builder.Append(" packets=").Append(totalPackets);
        builder.Append(" packet_ms=").Append((totalTicks * 1000.0 / frequency).ToString("F1", culture));
        for (var index = 0; index < kept; index++)
        {
            var opcode = order[index];
            builder.Append(' ').Append(PacketOpcodeNames.Of((uint)opcode)).Append('=')
                .Append(_counts[opcode]).Append('/')
                .Append((_ticks[opcode] * 1000.0 / frequency).ToString("F1", culture)).Append("ms");
        }

        Console.Error.WriteLine(builder.ToString());
        Array.Clear(_counts);
        Array.Clear(_ticks);
        _wakes = 0;
        _wakesSkipped = 0;
        _retryAll = 0;
        _headerReads = 0;
        _payloadReads = 0;
        _payloadDwords = 0;
        _slices = 0;
        _suspends = 0;
        _loopTicks = 0;
    }
}
