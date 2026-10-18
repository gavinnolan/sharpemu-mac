// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.ShaderCompiler.Resources;

public sealed partial class ResourceMaterializationCache
{
    // A hit reads every recorded range back from guest memory, and each read pays the host's
    // ownership and overlay checks however few bytes it copies. Ranges that only a short gap on a
    // page they already touch separates are read as one, and the gap's bytes are not compared. The
    // reads holding a memory-loaded base word go first, so a moved base still fails before the
    // rest is read, without the separate probe reads.
    private sealed class JoinedReads
    {
        public required ulong[] Addresses { get; init; }
        public required int[] Lengths { get; init; }
        public required bool[] Clean { get; init; }
        // The entry's ranges each read covers: FirstRange[read] and the RangeCount[read] after it.
        public required int[] FirstRange { get; init; }
        public required int[] RangeCount { get; init; }
        public required int[] Order { get; init; }
    }

    private const ulong JoinedGapBytes = 64;
    private const int PageShift = 12;

    private static JoinedReads JoinedReadsOf(Entry entry)
    {
        var addresses = new List<ulong>();
        var lengths = new List<int>();
        var clean = new List<bool>();
        var first = new List<int>();
        var count = new List<int>();
        var end = 0UL;
        for (var range = 0; range < entry.RangeAddresses.Length; range++)
        {
            var start = entry.RangeAddresses[range];
            var last = addresses.Count - 1;
            if (last >= 0 && start >= end && start - end <= JoinedGapBytes && (end - 1) >> PageShift == start >> PageShift)
            {
                end = Math.Max(end, start + (ulong)entry.RangeLengths[range]);
                lengths[last] = (int)(end - addresses[last]);
                clean[last] |= entry.RangeClean[range];
                count[last]++;
                continue;
            }

            addresses.Add(start);
            lengths.Add(entry.RangeLengths[range]);
            clean.Add(entry.RangeClean[range]);
            first.Add(range);
            count.Add(1);
            end = start + (ulong)entry.RangeLengths[range];
        }

        var probed = new bool[addresses.Count];
        if (entry.BaseWordBuffers is not null)
        {
            foreach (var (range, _) in entry.BaseWordProbes ??= BaseWordProbesOf(entry))
            {
                for (var read = 0; read < addresses.Count; read++)
                    probed[read] |= range >= first[read] && range < first[read] + count[read];
            }
        }

        var order = Enumerable.Range(0, addresses.Count).Where(read => probed[read])
            .Concat(Enumerable.Range(0, addresses.Count).Where(read => !probed[read])).ToArray();
        return new JoinedReads
        {
            Addresses = [.. addresses],
            Lengths = [.. lengths],
            Clean = [.. clean],
            FirstRange = [.. first],
            RangeCount = [.. count],
            Order = order,
        };
    }

    // Validate for an entry whose reads do not move: every recorded byte is compared.
    private bool ValidateJoined(Entry entry, ResidentGuestBytesReader residentReader, out bool unreadable)
    {
        unreadable = false;
        var reads = entry.Joined ??= JoinedReadsOf(entry);
        foreach (var read in reads.Order)
        {
            var length = reads.Lengths[read];
            if (_scratch.Length < length)
                _scratch = new byte[Math.Max(length, _scratch.Length * 2)];
            var current = _scratch.AsSpan(0, length);
            var address = reads.Addresses[read];
            var joined = residentReader(address, current, reads.Clean[read]);
            if (!joined && reads.RangeCount[read] == 1)
            {
                unreadable = true;
                return false;
            }

            for (var range = reads.FirstRange[read]; range < reads.FirstRange[read] + reads.RangeCount[read]; range++)
            {
                var rangeLength = entry.RangeLengths[range];
                var bytes = current.Slice((int)(entry.RangeAddresses[range] - address), rangeLength);
                // A gap the host refuses to read: the ranges are read on their own.
                if (!joined && !residentReader(entry.RangeAddresses[range], bytes, entry.RangeClean[range]))
                {
                    unreadable = true;
                    return false;
                }

                if (!bytes.SequenceEqual(entry.Bytes.AsSpan(entry.RangeOffsets[range], rangeLength)))
                    return false;
            }
        }

        return true;
    }
}
