// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Libs.Gpu.GpuCommands;

/// <summary>
/// The last few thousand guest writes the command processor made or deferred to the GPU:
/// labels, write-data packets, end-of-pipe values. A wait that never completes is either
/// waiting for a write that has not happened or for one that happened and was overwritten
/// before the waiter looked; this is what tells the two apart. Diagnostics only.
/// </summary>
public static class CommandProcessorWriteHistory
{
    public readonly record struct Entry(
        long Ticks, int QueueId, ulong Address, int Size, ulong Value, string Kind, bool Deferred);

    private const int Capacity = 4096;
    private static readonly Entry[] _entries = new Entry[Capacity];
    private static long _next;

    public static void Record(int queueId, ulong address, int size, ulong value, string kind, bool deferred)
    {
        var slot = Interlocked.Increment(ref _next) - 1;
        _entries[(int)(slot % Capacity)] = new Entry(
            System.Diagnostics.Stopwatch.GetTimestamp(), queueId, address, size, value, kind, deferred);
    }

    /// <summary>The recorded writes overlapping a range, oldest first.</summary>
    public static List<Entry> Find(ulong address, ulong size)
    {
        var found = new List<Entry>();
        var end = Interlocked.Read(ref _next);
        var start = Math.Max(0, end - Capacity);
        for (var index = start; index < end; index++)
        {
            var entry = _entries[(int)(index % Capacity)];
            if (entry.Size != 0 && entry.Address < address + size && address < entry.Address + (ulong)entry.Size)
            {
                found.Add(entry);
            }
        }

        return found;
    }
}
