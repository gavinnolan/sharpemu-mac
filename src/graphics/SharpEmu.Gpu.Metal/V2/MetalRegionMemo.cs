// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Gpu.Metal.V2;

/// <summary>
/// The ring ranges one submission already filled, found again by content: an argument buffer or
/// an uploaded table that comes out byte for byte the same as one an earlier draw of the same
/// command buffer wrote is the same range, so the draw binds it instead of reserving, clearing
/// and filling a new one.
/// <para>
/// A range is only reused by the submission that wrote it. The ring a range lives in belongs to
/// that submission until it completes (<see cref="MetalGuestMemory.Allocate"/> stamps it), and a
/// later submission may find the ring recycled and its bytes overwritten, so a range an earlier
/// serial wrote is never handed out again. Within one submission nothing rewrites a range: the
/// bytes the GPU reads when the command buffer runs are the bytes the draw recorded.
/// </para>
/// <para>One thread uses an instance: the one recording draws.</para>
/// </summary>
internal sealed class MetalRegionMemo
{
    private struct Slot
    {
        public ulong Serial;
        public MetalGuestRange Range;
        public byte[]? Bytes;
        public int Length;
    }

    private readonly Slot[] _slots;
    private int _next;

    public MetalRegionMemo(int slots)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(slots, 1);
        _slots = new Slot[slots];
    }

    /// <summary>Ranges handed back instead of a new one.</summary>
    public long Hits { get; private set; }

    /// <summary>Lookups that found nothing for the submission.</summary>
    public long Misses { get; private set; }

    /// <summary>The range this submission already holds these bytes in, if it does.</summary>
    public bool TryFind(ReadOnlySpan<byte> bytes, ulong serial, out MetalGuestRange range)
    {
        var slots = _slots;
        for (var index = 0; index < slots.Length; index++)
        {
            ref var slot = ref slots[index];
            if (slot.Bytes is { } held && slot.Serial == serial && slot.Length == bytes.Length &&
                held.AsSpan(0, slot.Length).SequenceEqual(bytes))
            {
                range = slot.Range;
                Hits++;
                return true;
            }
        }

        range = default;
        Misses++;
        return false;
    }

    /// <summary>Remembers a range this submission just filled with these bytes.</summary>
    public void Add(ReadOnlySpan<byte> bytes, ulong serial, in MetalGuestRange range)
    {
        ref var slot = ref _slots[_next];
        _next = (_next + 1) % _slots.Length;
        if (slot.Bytes is null || slot.Bytes.Length < bytes.Length)
        {
            slot.Bytes = new byte[bytes.Length];
        }

        bytes.CopyTo(slot.Bytes);
        slot.Length = bytes.Length;
        slot.Serial = serial;
        slot.Range = range;
    }

    /// <summary>Forgets everything (a new device or a disposed ring pool).</summary>
    public void Clear()
    {
        for (var index = 0; index < _slots.Length; index++)
        {
            _slots[index].Bytes = null;
            _slots[index].Length = 0;
        }
    }
}
