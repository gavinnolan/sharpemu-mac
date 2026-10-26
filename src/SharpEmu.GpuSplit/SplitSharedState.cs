// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.GpuRemote;
using SharpEmu.HLE.GpuMemory;

namespace SharpEmu.GpuSplit;

/// <summary>
/// The split's state in the transport's application block, which both processes read in place:
/// the page-protection acknowledgement and the GPU-interest page summary.
/// </summary>
/// <remarks>
/// <para><b>Protection acknowledgement.</b> B numbers every page protection it asks A to apply
/// (a <see cref="GpuRemoteMessage.ProtectRequest"/>), and A raises <see cref="ProtectApplied"/>
/// to a request's number once that request and every one before it is applied. B waits on it
/// before it relies on a watch it armed.</para>
///
/// <para><b>GPU-interest summary.</b> One bit per 4 KiB page of guest address space below
/// <see cref="TrackerLayout.SpaceBytes"/>, set while the page carries a page watch in B
/// (<see cref="IPageWatchObserver"/>). A reports a managed write to B only when a bit under it is
/// set: GTA V makes ~1.2 million managed writes a second, and the GPU caches care about a few of
/// them. (The first version had one bit per 64 KiB, so a write to a page nobody watches, next to
/// one somebody does, still cost a blocking call; command buffers sit next to the images they
/// draw to.) A page's watch changes under its 4 MiB block's lock in B, and one block is 16 words
/// of the summary. B sets a bit before the watch's protection is requested, and the set is a full
/// barrier: a managed write in A either sees the bit (and is reported) or lands before B reads
/// the bytes the watch covers.</para>
///
/// <para>Layout: the counter at 0 (<see cref="SharedCounter.BlockSize"/> bytes), the summary at
/// <see cref="SummaryOffset"/>, <see cref="SummaryWords"/> words (32 MiB of address space in the
/// region, touched only where watches are).</para>
/// </remarks>
public sealed unsafe class SplitSharedState : IInterestSummary
{
    public const int PageShift = 12;
    public const int SummaryOffset = 4096;
    public const int SummaryWords = (int)(TrackerLayout.SpaceBytes >> PageShift >> 6);
    public const int Bytes = SummaryOffset + SummaryWords * sizeof(ulong);

    private readonly ulong* _summary;

    public SplitSharedState(GpuRemoteChannel channel)
    {
        ArgumentNullException.ThrowIfNull(channel);
        if (channel.Application == null || channel.ApplicationBytes < Bytes)
        {
            throw new InvalidDataException(
                $"The GPU region's application block holds {channel.ApplicationBytes} bytes; the split needs {Bytes}.");
        }

        ProtectApplied = SharedCounter.InApplicationBlock(channel, 0);
        _summary = (ulong*)(channel.Application + SummaryOffset);
    }

    /// <summary>The layout of a region that holds this state.</summary>
    public static GpuRemoteLayout Layout { get; } = new() { ApplicationBytes = Bytes };

    /// <summary>The number of the last page protection A applied (with every one before it).</summary>
    public SharedCounter ProtectApplied { get; }

    /// <summary>A: whether any 4 KiB page under [address, address + size) carries a watch.</summary>
    public bool MayInterest(ulong address, ulong size)
    {
        if (size == 0 || address >= TrackerLayout.SpaceBytes)
        {
            return false;
        }

        var end = size > TrackerLayout.SpaceBytes - address ? TrackerLayout.SpaceBytes : address + size;
        var page = address >> PageShift;
        var last = (end - 1) >> PageShift;
        while (page <= last)
        {
            var word = page >> 6;
            var bits = Volatile.Read(ref _summary[word]);
            var wordLast = Math.Min(last, (word << 6) | 63);
            if (bits != 0)
            {
                var low = (int)(page & 63);
                var high = (int)(wordLast & 63);
                var mask = (high == 63 ? ulong.MaxValue : (1UL << (high + 1)) - 1) & ~((1UL << low) - 1);
                if ((bits & mask) != 0)
                {
                    return true;
                }
            }

            page = wordLast + 1;
        }

        return false;
    }

    /// <summary>B: marks the page holding <paramref name="address"/> as watched (a full barrier).</summary>
    public void SetPage(ulong address)
    {
        var page = address >> PageShift;
        Interlocked.Or(ref _summary[page >> 6], 1UL << (int)(page & 63));
    }

    /// <summary>B: the page holding <paramref name="address"/> has no watch any more.</summary>
    public void ClearPage(ulong address)
    {
        var page = address >> PageShift;
        Interlocked.And(ref _summary[page >> 6], ~(1UL << (int)(page & 63)));
    }
}
