// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Threading;

namespace SharpEmu.HLE;

/// <summary>
/// A ring of the guest byte ranges the host has written on the GPU's behalf, kept so a
/// later fault can say whether the bytes under a corrupted pointer were ones a GPU-side
/// write claimed. It lives here, and not in a backend, because the fault dumper in
/// <c>SharpEmu.Core</c> is what asks the question and nothing below it may reference a
/// graphics package.
///
/// Recording is off unless a guard asks for it: <c>SHARPEMU_METAL_V2_GUARD_WRITES=1</c>.
/// Every entry is one producer's declared destination, so an address no entry covers was
/// written by something the host did not declare — a shader store through a descriptor,
/// or not the GPU at all.
/// </summary>
public static class GuestWriteJournal
{
    private const int Capacity = 8192;

    private struct Entry
    {
        public ulong Start;
        public ulong End;
        public long Sequence;
        public string? Producer;
    }

    private static readonly Entry[] Entries = new Entry[Capacity];
    private static long _sequence;

    private static bool _enabled = string.Equals(
        Environment.GetEnvironmentVariable("SHARPEMU_METAL_V2_GUARD_WRITES"), "1", StringComparison.Ordinal);

    /// <summary>True while the journal is recording.</summary>
    public static bool Enabled => Volatile.Read(ref _enabled);

    /// <summary>
    /// Starts recording. The guard that owns the declared ranges calls this when it is
    /// constructed, so a journal armed by anything other than the environment variable
    /// still answers a fault.
    /// </summary>
    public static void Enable() => Volatile.Write(ref _enabled, true);

    /// <summary>How many ranges have been recorded since startup.</summary>
    public static long Recorded => Volatile.Read(ref _sequence);

    /// <summary>Notes that <paramref name="producer"/> declared a write of this guest range.</summary>
    public static void Record(ulong address, ulong size, string producer)
    {
        if (!Enabled || size == 0 || address > ulong.MaxValue - size)
        {
            return;
        }

        var sequence = Interlocked.Increment(ref _sequence);
        ref var entry = ref Entries[(int)((ulong)sequence % Capacity)];
        // Sequence is published last: a reader that sees it also sees the range, and a
        // torn entry is only ever an entry the ring has already overwritten.
        entry.Start = address;
        entry.End = address + size;
        entry.Producer = producer;
        Volatile.Write(ref entry.Sequence, sequence);
    }

    /// <summary>
    /// The newest recorded range covering <paramref name="address"/>. False when no
    /// declared GPU-side write owned those bytes.
    /// </summary>
    public static bool TryFind(ulong address, out ulong start, out ulong size, out string producer)
    {
        start = 0;
        size = 0;
        producer = string.Empty;
        if (!Enabled)
        {
            return false;
        }

        var newest = 0L;
        for (var index = 0; index < Capacity; index++)
        {
            ref var entry = ref Entries[index];
            var sequence = Volatile.Read(ref entry.Sequence);
            if (sequence <= newest || entry.Start > address || entry.End <= address)
            {
                continue;
            }

            newest = sequence;
            start = entry.Start;
            size = entry.End - entry.Start;
            producer = entry.Producer ?? string.Empty;
        }

        return newest != 0;
    }

    /// <summary>
    /// The newest recorded range covering <paramref name="address"/>, or null when no
    /// declared GPU-side write owned those bytes.
    /// </summary>
    public static string? Describe(ulong address) =>
        TryFind(address, out var start, out var size, out var producer)
            ? $"producer={producer} range=0x{start:X16}+0x{size:X} offset=0x{address - start:X}"
            : null;

    /// <summary>
    /// Prints one line per address a declared GPU-side write covered. Called from the
    /// fault dumper with the faulting address and every register that could be the guest
    /// structure the fault read a clobbered pointer out of.
    /// </summary>
    public static void ReportCandidates(string label, ReadOnlySpan<(string Name, ulong Value)> candidates)
    {
        if (!Enabled)
        {
            return;
        }

        var any = false;
        foreach (var (name, value) in candidates)
        {
            if (Describe(value) is not { } detail)
            {
                continue;
            }

            any = true;
            Console.Error.WriteLine($"[LOADER][INFO]   {label} {name}=0x{value:X16} {detail}");
        }

        if (!any)
        {
            Console.Error.WriteLine(
                $"[LOADER][INFO]   {label}: no declared GPU-side write covered any faulting address " +
                $"(records={Recorded}).");
        }
    }
}
