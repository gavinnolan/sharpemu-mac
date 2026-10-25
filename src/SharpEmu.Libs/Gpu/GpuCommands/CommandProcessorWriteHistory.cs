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

    // SHARPEMU_CP_WATCH=<hex>[,<hex>...]: every recorded write overlapping one of these
    // addresses is also printed as it happens, so a label's whole history survives the ring.
    private static readonly ulong[] Watched = ParseWatched(Environment.GetEnvironmentVariable("SHARPEMU_CP_WATCH"));

    private static ulong[] ParseWatched(string? text) => string.IsNullOrWhiteSpace(text)
        ? []
        : text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(token => ulong.TryParse(
                token.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? token[2..] : token,
                System.Globalization.NumberStyles.HexNumber, null, out var address) ? address : 0)
            .Where(address => address != 0)
            .ToArray();

    public static void Record(int queueId, ulong address, int size, ulong value, string kind, bool deferred)
    {
        foreach (var watched in Watched)
        {
            if (watched >= address && watched < address + (ulong)Math.Max(size, 1))
            {
                Console.Error.WriteLine(
                    $"[GPU][CPWATCH] address=0x{address:X16} size={size} value=0x{value:X} kind={kind} " +
                    $"queue={queueId} deferred={deferred} thread={Environment.CurrentManagedThreadId}");
            }
        }

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
