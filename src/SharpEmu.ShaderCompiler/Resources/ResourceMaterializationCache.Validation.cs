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

    private ulong[] _joinedAddresses = [];
    private int[] _joinedLengths = [];
    private bool[] _joinedClean = [];
    private int[] _joinedFirst = [];
    private int[] _joinedCount = [];
    private int[] _joinedOrder = [];
    private bool[] _joinedProbed = [];

    // Joined in scratch, then copied out: it runs for every new entry. An entry rebuilt from another
    // (hint) for a moved ring keeps how its ranges join, and those arrays are shared.
    private JoinedReads JoinedReadsOf(Entry entry, JoinedReads? hint)
    {
        var rangeCount = entry.RangeAddresses.Length;
        if (_joinedAddresses.Length < rangeCount)
        {
            var capacity = Math.Max(rangeCount, _joinedAddresses.Length * 2);
            _joinedAddresses = new ulong[capacity];
            _joinedLengths = new int[capacity];
            _joinedClean = new bool[capacity];
            _joinedFirst = new int[capacity];
            _joinedCount = new int[capacity];
            _joinedOrder = new int[capacity];
            _joinedProbed = new bool[capacity];
        }

        var addresses = _joinedAddresses;
        var lengths = _joinedLengths;
        var clean = _joinedClean;
        var first = _joinedFirst;
        var count = _joinedCount;
        var reads = 0;
        var end = 0UL;
        for (var range = 0; range < rangeCount; range++)
        {
            var start = entry.RangeAddresses[range];
            var last = reads - 1;
            if (last >= 0 && start >= end && start - end <= JoinedGapBytes && (end - 1) >> PageShift == start >> PageShift)
            {
                end = Math.Max(end, start + (ulong)entry.RangeLengths[range]);
                lengths[last] = (int)(end - addresses[last]);
                clean[last] |= entry.RangeClean[range];
                count[last]++;
                continue;
            }

            addresses[reads] = start;
            lengths[reads] = entry.RangeLengths[range];
            clean[reads] = entry.RangeClean[range];
            first[reads] = range;
            count[reads] = 1;
            reads++;
            end = start + (ulong)entry.RangeLengths[range];
        }

        var probed = _joinedProbed.AsSpan(0, reads);
        probed.Clear();
        if (entry.BaseWordBuffers is not null)
        {
            foreach (var (range, _) in entry.BaseWordProbes ??= BaseWordProbesOf(entry))
            {
                for (var read = 0; read < reads; read++)
                    probed[read] |= range >= first[read] && range < first[read] + count[read];
            }
        }

        // The reads holding a base word first, each group in read order.
        var order = _joinedOrder.AsSpan(0, reads);
        var next = 0;
        for (var read = 0; read < reads; read++)
            if (probed[read])
                order[next++] = read;
        for (var read = 0; read < reads; read++)
            if (!probed[read])
                order[next++] = read;

        return new JoinedReads
        {
            Addresses = Shared(addresses.AsSpan(0, reads), hint?.Addresses),
            Lengths = Shared(lengths.AsSpan(0, reads), hint?.Lengths),
            Clean = Shared(clean.AsSpan(0, reads), hint?.Clean),
            FirstRange = Shared(first.AsSpan(0, reads), hint?.FirstRange),
            RangeCount = Shared(count.AsSpan(0, reads), hint?.RangeCount),
            Order = Shared(order, hint?.Order),
        };

        static T[] Shared<T>(Span<T> words, T[]? hint) where T : IEquatable<T> =>
            hint is not null && words.SequenceEqual(hint) ? hint : words.ToArray();
    }

    // Validate for an entry whose reads do not move: every recorded byte is compared.
    // A changed descriptor word becomes the rejection sample, as in Validate, so the variants
    // behind this one that hold the old word are refused without being read.
    private bool ValidateJoined(Entry entry, ResidentGuestBytesReader residentReader, out bool unreadable,
        out bool descriptorChanged, out RejectionSample rejection)
    {
        unreadable = false;
        descriptorChanged = false;
        rejection = default;
        if (entry.Joined is null)
        {
            entry.Joined = JoinedReadsOf(entry, entry.JoinedHint);
            entry.JoinedHint = null;
        }

        var reads = entry.Joined;
        foreach (var read in reads.Order)
        {
            var length = reads.Lengths[read];
            if (_scratch.Length < length)
                _scratch = new byte[Math.Max(length, _scratch.Length * 2)];
            var current = _scratch.AsSpan(0, length);
            var address = reads.Addresses[read];
            var joined = ReadWithProfile(residentReader, address, current, reads.Clean[read], CacheDetail.ValidateRead);
            if (!joined && reads.RangeCount[read] == 1)
            {
                unreadable = true;
                CountDiagnostic(Diagnostic.Unreadable);
                return false;
            }

            for (var range = reads.FirstRange[read]; range < reads.FirstRange[read] + reads.RangeCount[read]; range++)
            {
                var rangeLength = entry.RangeLengths[range];
                // The joined reads are derived from the ranges; a range they no longer cover is
                // not validated, so the entry misses rather than comparing the wrong bytes.
                var rangeStart = entry.RangeAddresses[range];
                if (rangeLength > length || rangeStart < address || rangeStart - address > (ulong)(length - rangeLength))
                    return false;
                var bytes = current.Slice((int)(rangeStart - address), rangeLength);
                // A gap the host refuses to read: the ranges are read on their own.
                if (!joined && !ReadWithProfile(residentReader, rangeStart, bytes, entry.RangeClean[range], CacheDetail.ValidateRead))
                {
                    unreadable = true;
                    CountDiagnostic(Diagnostic.Unreadable);
                    return false;
                }

                using var comparisonProfile = MeasureDetail(CacheDetail.ValidateCompare);
                var previous = entry.Bytes.AsSpan(entry.RangeOffsets[range], rangeLength);
                if (!bytes.SequenceEqual(previous))
                {
                    RecordChangedWords(entry, range, bytes);
                    CountDiagnostic(Diagnostic.BytesChanged);
                    for (var offset = 0; offset + sizeof(uint) <= rangeLength; offset += sizeof(uint))
                    {
                        if (!entry.WordTableOnly[(entry.RangeOffsets[range] + offset) / sizeof(uint)] &&
                            !bytes.Slice(offset, sizeof(uint)).SequenceEqual(previous.Slice(offset, sizeof(uint))))
                        {
                            descriptorChanged = true;
                            rejection = new RejectionSample(range, rangeStart, rangeLength, entry.RangeClean[range],
                                offset, System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(bytes[offset..]));
                            break;
                        }
                    }
                    return false;
                }
            }
        }

        return true;
    }
}
