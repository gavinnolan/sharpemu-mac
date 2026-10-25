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
/// <para><b>GPU-interest summary.</b> One bit per 64 KiB of guest address space below
/// <see cref="TrackerLayout.SpaceBytes"/>, set while any page in it carries a page watch in B
/// (<see cref="IPageWatchObserver"/>). A reports a managed write to B only when a bit under it is
/// set: GTA V makes ~1.2 million managed writes a second, and the GPU caches care about a few of
/// them. One word covers one 4 MiB block of the page guard, so every change of a word happens
/// under that block's lock in B. B sets a bit before the watch's protection is requested, and the
/// set is a full barrier: a managed write in A either sees the bit (and is reported) or lands
/// before B reads the bytes the watch covers.</para>
///
/// <para>Layout: the counter at 0 (<see cref="SharedCounter.BlockSize"/> bytes), the summary at
/// <see cref="SummaryOffset"/>, <see cref="SummaryWords"/> words (2 MiB, touched only where
/// watches are).</para>
/// </remarks>
public sealed unsafe class SplitSharedState : IInterestSummary
{
    public const int GroupShift = 16;
    public const int SummaryOffset = 4096;
    public const int SummaryWords = (int)(TrackerLayout.SpaceBytes >> GroupShift >> 6);
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

    /// <summary>A: whether any 64 KiB group under [address, address + size) holds a watched page.</summary>
    public bool MayInterest(ulong address, ulong size)
    {
        if (size == 0 || address >= TrackerLayout.SpaceBytes)
        {
            return false;
        }

        var end = size > TrackerLayout.SpaceBytes - address ? TrackerLayout.SpaceBytes : address + size;
        var group = address >> GroupShift;
        var last = (end - 1) >> GroupShift;
        while (group <= last)
        {
            var word = group >> 6;
            var bits = Volatile.Read(ref _summary[word]);
            var wordLast = Math.Min(last, (word << 6) | 63);
            if (bits != 0)
            {
                var low = (int)(group & 63);
                var high = (int)(wordLast & 63);
                var mask = (high == 63 ? ulong.MaxValue : (1UL << (high + 1)) - 1) & ~((1UL << low) - 1);
                if ((bits & mask) != 0)
                {
                    return true;
                }
            }

            group = wordLast + 1;
        }

        return false;
    }

    /// <summary>B: marks the 64 KiB group holding <paramref name="address"/> as interesting (a full barrier).</summary>
    public void SetGroup(ulong address)
    {
        var group = address >> GroupShift;
        Interlocked.Or(ref _summary[group >> 6], 1UL << (int)(group & 63));
    }

    /// <summary>B: the group holding <paramref name="address"/> has no watched page any more.</summary>
    public void ClearGroup(ulong address)
    {
        var group = address >> GroupShift;
        Interlocked.And(ref _summary[group >> 6], ~(1UL << (int)(group & 63)));
    }
}
