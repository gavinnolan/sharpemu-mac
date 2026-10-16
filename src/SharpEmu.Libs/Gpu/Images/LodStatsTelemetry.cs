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
    private static long _hostRecorded;
    private static long _reports;
    private static int _lastSampledSlots;
    private static int _lastCoarseSlots;
    private static int _maxCoarseSlots;
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

            if (hostRecorded)
            {
                _hostRecorded++;
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

    /// <summary>
    /// What one report told the title: the slots that saw any sample (finest mip below 15)
    /// and the slots whose finest mip was coarser than 0. Bound-slot fallback reports only
    /// ever say mip 0 or nothing, so a run whose reports never name a coarser mip is not
    /// measuring.
    /// </summary>
    public static void NoteReportSlots(int sampledSlots, int coarseSlots)
    {
        if (!Enabled)
        {
            return;
        }

        Register();
        lock (Gate)
        {
            _reports++;
            _lastSampledSlots = sampledSlots;
            _lastCoarseSlots = coarseSlots;
            _maxCoarseSlots = Math.Max(_maxCoarseSlots, coarseSlots);
        }
    }

    /// <summary><see cref="NoteReportSlots"/> of a report as written to the guest.</summary>
    public static void NoteReport(ReadOnlySpan<byte> report)
    {
        if (!Enabled || report.Length < MipStatistics.ReportBytes)
        {
            return;
        }

        var sampled = 0;
        var coarse = 0;
        for (var slot = 0; slot < MipStatistics.SlotCount; slot++)
        {
            var high = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(
                report[((int)MipStatistics.FirstCounterOffset + slot * 8 + sizeof(uint))..]);
            var finest = (high >> 24) & 0xF;
            if (finest != MipStatistics.NoMipSampled)
            {
                sampled++;
                coarse += finest != 0 ? 1 : 0;
            }
        }

        NoteReportSlots(sampled, coarse);
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
                $"get_size_calls={_getSizeCalls} max_slots_bound={_maxSlotsBound} gpu_reports={_hostRecorded} " +
                $"reports_seen={_reports} slots_sampled={_lastSampledSlots} slots_mip_gt0={_lastCoarseSlots} " +
                $"max_slots_mip_gt0={_maxCoarseSlots}"));
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
