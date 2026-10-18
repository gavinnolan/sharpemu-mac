// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Globalization;
using System.Text;
using SharpEmu.Logging;

namespace SharpEmu.Libs.Gpu.Images;

/// <summary>
/// SHARPEMU_LOG_MEMORY=1: what the title does with AGC LOD statistics. Answers whether a game
/// that asks for them gives a buffer large enough for counters (<see cref="MipStatistics.ReportBytes"/>
/// bytes; sceAgcDcbGetLodStatsGetSize answers 0x10 + 4 * counters, which is smaller), so whether
/// the report it reads back ever carries any. Prints on the "[MEM] lod" line.
/// </summary>
public static class LodStatsTelemetry
{
    private static readonly object Gate = new();
    private static readonly MemoryCounter Sampled = MemoryTelemetry.Counter("lod.sampled_bindings");
    private static readonly Dictionary<uint, long> BufferSizes = new();
    private static readonly Dictionary<uint, uint> GetSizeAnswers = new();
    private static readonly Dictionary<uint, long> Masks = new();
    private static readonly List<string> First = new();
    private static long _packets;
    private static long _withCounters;
    private static long _getSizeCalls;
    private static long _emits;
    private static int _maxSlotsBound;
    private static int _registered;

    public static bool Enabled => MemoryTelemetry.Enabled;

    public static void NoteSampledBinding() => Sampled.Add();

    /// <summary>Executed GET_LOD_STATS packet: the guest buffer size, destination and whether counters were written.</summary>
    public static void NotePacket(uint bufferSize, ulong destination, bool hostRecorded, int slotsBound)
    {
        if (!Enabled)
        {
            return;
        }

        Register();
        var withCounters = hostRecorded || bufferSize >= MipStatistics.ReportBytes;
        lock (Gate)
        {
            _packets++;
            if (withCounters)
            {
                _withCounters++;
            }

            BufferSizes[bufferSize] = BufferSizes.GetValueOrDefault(bufferSize) + 1;
            _maxSlotsBound = Math.Max(_maxSlotsBound, slotsBound);
            if (First.Count < 8)
            {
                First.Add(string.Create(CultureInfo.InvariantCulture,
                    $"size={bufferSize} dst=0x{destination:X} counters_written={withCounters} slots_bound={slotsBound}"));
            }
        }
    }

    /// <summary>sceAgcDcbGetLodStatsGetSize(counterCount) and what it answered.</summary>
    public static void NoteGetSize(uint counterCount, uint answer)
    {
        if (!Enabled)
        {
            return;
        }

        Register();
        lock (Gate)
        {
            _getSizeCalls++;
            if (GetSizeAnswers.Count < 16)
            {
                GetSizeAnswers[counterCount] = answer;
            }
        }
    }

    /// <summary>sceAgcDcbGetLodStats emission: the counter mask the title selected.</summary>
    public static void NoteEmit(uint counterMask)
    {
        if (!Enabled)
        {
            return;
        }

        Register();
        lock (Gate)
        {
            _emits++;
            Masks[counterMask] = Masks.GetValueOrDefault(counterMask) + 1;
        }
    }

    private static void Register()
    {
        if (Interlocked.Exchange(ref _registered, 1) == 0)
        {
            MemoryTelemetry.Register("lod", Describe);
        }
    }

    private static string Describe()
    {
        lock (Gate)
        {
            var text = new StringBuilder(string.Create(CultureInfo.InvariantCulture,
                $"report_bytes_needed={MipStatistics.ReportBytes} emits={_emits} packets={_packets} " +
                $"counters_written={_withCounters} without_counters={_packets - _withCounters} " +
                $"get_size_calls={_getSizeCalls} max_slots_bound={_maxSlotsBound}"));
            text.Append(" buffer_sizes={")
                .Append(string.Join(',', BufferSizes.OrderBy(pair => pair.Key).Select(pair => $"{pair.Key}:{pair.Value}")))
                .Append("} get_size={")
                .Append(string.Join(',', GetSizeAnswers.OrderBy(pair => pair.Key).Select(pair => $"{pair.Key}->{pair.Value}")))
                .Append("} masks={")
                .Append(string.Join(',', Masks.OrderBy(pair => pair.Key).Select(pair => $"0x{pair.Key:X2}:{pair.Value}")))
                .Append("} first=[").Append(string.Join("; ", First)).Append(']');
            return text.ToString();
        }
    }
}
