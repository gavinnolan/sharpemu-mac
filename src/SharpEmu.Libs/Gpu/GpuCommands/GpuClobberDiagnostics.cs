// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Libs.Gpu.GpuCommands;

/// <summary>
/// Evidence for a late GPU write landing in command memory the guest already recycled
/// (SHARPEMU_GPU_CLOBBER_DIAG=1): when each deferred label landed, the register-table
/// address every SET_*_REG_INDIRECT packet carried when it was submitted, and a report
/// printed before a fatal ends the process. Diagnostics only; nothing runs when it is off.
/// </summary>
public static class GpuClobberDiagnostics
{
    public static readonly bool Enabled =
        Environment.GetEnvironmentVariable("SHARPEMU_GPU_CLOBBER_DIAG") == "1";

    public readonly record struct SubmittedTable(long Ticks, ulong PacketAddress, ulong TableAddress, uint Count, uint Register);

    private const int Capacity = 4096;
    private static readonly SubmittedTable[] _tables = new SubmittedTable[Capacity];
    private static long _nextTable;

    private static readonly string[] _landedKinds = BuildLandedKinds();

    private static string[] BuildLandedKinds()
    {
        var kinds = Enum.GetValues<EndOfPipeWriteKind>();
        var names = new string[kinds.Max(kind => (int)kind) + 1];
        foreach (var kind in kinds)
        {
            names[(int)kind] = "landed:" + kind;
        }

        return names;
    }

    /// <summary>The write-history kind of a deferred label once its bytes are in guest memory.</summary>
    public static string LandedKind(EndOfPipeWriteKind kind) =>
        (uint)kind < (uint)_landedKinds.Length ? _landedKinds[(int)kind] : "landed:" + kind;

    /// <summary>A register-table packet as the submit-time walk read it.</summary>
    public static void RecordSubmittedTable(ulong packetAddress, ulong tableAddress, uint count, uint register)
    {
        var slot = Interlocked.Increment(ref _nextTable) - 1;
        _tables[(int)(slot % Capacity)] = new SubmittedTable(
            System.Diagnostics.Stopwatch.GetTimestamp(), packetAddress, tableAddress, count, register);
    }

    /// <summary>The submit-time records of one packet address, newest first.</summary>
    public static List<SubmittedTable> FindSubmittedTables(ulong packetAddress, int limit)
    {
        var found = new List<SubmittedTable>();
        var end = Interlocked.Read(ref _nextTable);
        var start = Math.Max(0, end - Capacity);
        for (var index = end - 1; index >= start && found.Count < limit; index--)
        {
            var entry = _tables[(int)(index % Capacity)];
            if (entry.Ticks != 0 && entry.PacketAddress == packetAddress)
            {
                found.Add(entry);
            }
        }

        return found;
    }

    public static string AgeMilliseconds(long ticks, long now) =>
        ticks == 0
            ? "unknown"
            : ((now - ticks) * 1000.0 / System.Diagnostics.Stopwatch.Frequency)
                .ToString("F1", System.Globalization.CultureInfo.InvariantCulture);
}
