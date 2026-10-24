// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections.Concurrent;

namespace SharpEmu.HLE.GuestMemory;

/// <summary>
/// The guest process's check for guest memory the two-process split's GPU process cannot see.
/// That process sees guest memory only through the shared backing, so a GPU read of memory private
/// to this process (module code, stacks and TLS, HLE libc heap, lazily committed reservations,
/// or anything else no backing view maps) faults there. In split mode
/// (<see cref="SplitBackingLayout.IsRequested"/>) the GPU path checks what it reads here and each
/// private range is named once, so a missing case is found before the GPU process runs into it.
/// The GPU process makes the same check against the shared view table
/// (<see cref="GuestMirrorTranslator.IsFullyBacked"/>). Off, a check costs one load.
/// </summary>
public static class SplitPrivateAccess
{
    // Distinct (reader, 16 MiB region) pairs named before the rest is only counted.
    private const int MaxWarnings = 32;
    private const int RegionShift = 24;

    private static readonly bool Requested = SplitBackingLayout.IsRequested;
    private static readonly ConcurrentDictionary<(string Reader, ulong Region), byte> Warned = new();
    private static long _privateReads;
    private static int _warnings;

    /// <summary>Test override of the environment switch for checks on this thread; null reads it.</summary>
    [ThreadStatic]
    internal static bool? EnabledForTests;

    /// <summary>Test hook: every warning line this thread writes is passed here as well.</summary>
    [ThreadStatic]
    internal static Action<string>? WarningSinkForTests;

    public static bool Enabled => EnabledForTests ?? Requested;

    /// <summary>GPU reads of private guest memory seen in split mode, named or not.</summary>
    public static long PrivateReads => Interlocked.Read(ref _privateReads);

    /// <summary>
    /// Whether all of guest [address, address + size) is a view of the shared backing, through
    /// <paramref name="memory"/> or any decorator around the guest address space.
    /// </summary>
    public static bool IsBacked(object? memory, ulong address, ulong size) =>
        size != 0 && size <= ulong.MaxValue - address && ResolveBackedSpace(memory)?.IsBackedRange(address, size) == true;

    /// <summary>
    /// The GPU path is about to read guest [address, address + size). In split mode a range any part
    /// of which is not backed counts, and is named once per reader and 16 MiB region with its first
    /// private page; false then. True outside split mode, and when the memory has no backing to ask.
    /// </summary>
    public static bool CheckGpuRead(object? memory, ulong address, ulong size, string reader)
    {
        if (!Enabled || size == 0 || address == 0 || size > ulong.MaxValue - address)
        {
            return true;
        }

        var space = ResolveBackedSpace(memory);
        if (space is null || space.IsBackedRange(address, size))
        {
            return true;
        }

        Interlocked.Increment(ref _privateReads);
        if (Volatile.Read(ref _warnings) < MaxWarnings && Warned.TryAdd((reader, address >> RegionShift), 0) &&
            Interlocked.Increment(ref _warnings) <= MaxWarnings)
        {
            var line = $"[SPLIT][WARN] The GPU path reads guest memory the GPU process cannot see (not in the shared backing): " +
                $"reader={reader} address=0x{address:X16} size=0x{size:X} first_private=0x{FirstUnbacked(space, address, size):X16}" +
                (Volatile.Read(ref _warnings) == MaxWarnings ? " (further ranges are counted, not named)" : string.Empty);
            Console.Error.WriteLine(line);
            WarningSinkForTests?.Invoke(line);
        }

        return false;
    }

    // The first guest page of the range that is not backed, page by page; a range found backed
    // piece by piece (views that do not continue each other count as backed) returns its start.
    private static ulong FirstUnbacked(IGuestBackedSpace space, ulong address, ulong size)
    {
        const ulong page = GuestMemoryLayout.GuestPage;
        var end = address + size;
        for (var cursor = address; cursor < end;)
        {
            var next = Math.Min(end, (cursor & ~(page - 1)) + page);
            if (!space.IsBackedRange(cursor, next - cursor))
            {
                return cursor;
            }

            cursor = next;
        }

        return address;
    }

    private static IGuestBackedSpace? ResolveBackedSpace(object? memory)
    {
        for (var depth = 0; depth < 4 && memory is not null; depth++)
        {
            if (memory is IGuestBackedSpace space)
            {
                return space;
            }

            memory = (memory as ICpuMemoryWrapper)?.Inner;
        }

        return null;
    }
}
