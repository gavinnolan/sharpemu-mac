// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Gpu.Scheduling;

namespace SharpEmu.Gpu.Metal.V2;

/// <summary>
/// Checks that a GPU-side write to guest memory touches only the bytes its producer
/// declared. Guest memory is the GPU's memory on this backend, so a retile with the wrong
/// pitch, a fill sized from a padded footprint or a blit to the wrong window offset does
/// not fault — it silently rewrites whatever the guest's own allocator had put next door,
/// and the title dies later inside its own code on a clobbered pointer.
///
/// For every declared write the guard snapshots the <see cref="MarginBytes"/> immediately
/// before and after the range, and once the GPU has finished that submission it compares
/// them again. A byte that changed, and that no other declared range covers, names the
/// producer that ran off its end.
///
/// Off unless <c>SHARPEMU_METAL_V2_GUARD_WRITES=1</c>: it costs two 4 KiB reads per
/// declared write and holds them until the submission completes.
/// </summary>
internal sealed class MetalGuestWriteGuard
{
    /// <summary>How many bytes either side of a declared range are watched.</summary>
    public const int MarginBytes = 4096;

    // Enough slots that a submission's worth of declared writes all stay watched; past
    // this the oldest is verified early (its submission may still be running, so an
    // early check can only miss a violation, never invent one).
    private const int Slots = 2048;
    private const int MaxReports = 48;

    private sealed class Slot
    {
        public bool InUse;

        // The range stays readable after the watch is released, so another producer's
        // check can still see that these bytes were declared; cleared only on reuse.
        public bool Declared;
        public ulong Start;
        public ulong End;
        public ulong Serial;
        public string Producer = string.Empty;
        public ulong BeforeAddress;
        public int BeforeLength;
        public ulong AfterAddress;
        public int AfterLength;
        public readonly byte[] Before = new byte[MarginBytes];
        public readonly byte[] After = new byte[MarginBytes];
    }

    private readonly ICpuMemory _memory;
    private readonly Slot[] _slots = new Slot[Slots];
    private readonly byte[] _current = new byte[MarginBytes];
    private int _cursor;
    private int _live;
    private long _captures;
    private long _violations;
    private long _overlaps;
    private long _unreadable;
    private long _evicted;
    private int _reports;
    private int _unmappedReports;

    public MetalGuestWriteGuard(ICpuMemory memory)
    {
        _memory = memory;
        // The journal is what a later fault asks which producer owned a clobbered byte.
        GuestWriteJournal.Enable();
        for (var index = 0; index < _slots.Length; index++)
        {
            _slots[index] = new Slot();
        }
    }

    /// <summary>True while the guard is watching declared writes.</summary>
    public static bool Enabled { get; } = string.Equals(
        Environment.GetEnvironmentVariable("SHARPEMU_METAL_V2_GUARD_WRITES"), "1", StringComparison.Ordinal);

    public long Captures => _captures;

    public long Violations => _violations;

    public long Overlaps => _overlaps;

    public long Unreadable => _unreadable;

    public long Evicted => _evicted;

    public int Live => _live;

    /// <summary>
    /// Watches the margins of a write <paramref name="serial"/> will perform. The caller
    /// has not written anything yet, which is what makes the snapshot the "before" state.
    /// </summary>
    public void Capture(ulong address, ulong size, ulong serial, string producer) =>
        CaptureSlot(address, size, serial, producer);

    /// <summary>
    /// Watches the margins of a write the host is about to perform itself and returns the
    /// handle <see cref="End"/> checks, or -1 when there is nothing to watch. A host write
    /// is finished when it returns, so it needs no submission to wait for.
    /// </summary>
    public int Begin(ulong address, ulong size, string producer)
    {
        var slot = CaptureSlot(address, size, serial: 0, producer);
        return slot is null ? -1 : Array.IndexOf(_slots, slot);
    }

    /// <summary>Checks and releases the watch <see cref="Begin"/> handed out.</summary>
    public void End(int handle)
    {
        if (handle < 0)
        {
            return;
        }

        var slot = _slots[handle];
        Check(slot);
        Release(slot);
    }

    private Slot? CaptureSlot(ulong address, ulong size, ulong serial, string producer)
    {
        if (size == 0 || address > ulong.MaxValue - size)
        {
            return null;
        }

        GuestWriteJournal.Record(address, size, producer);
        var slot = Take();
        slot.InUse = true;
        slot.Declared = true;
        slot.Start = address;
        slot.End = address + size;
        slot.Serial = serial;
        slot.Producer = producer;
        slot.BeforeAddress = address >= MarginBytes ? address - MarginBytes : 0;
        slot.BeforeLength = address >= MarginBytes ? MarginBytes : (int)address;
        slot.AfterAddress = slot.End;
        slot.AfterLength = slot.End > ulong.MaxValue - MarginBytes ? 0 : MarginBytes;
        // A declared range that is not wholly mapped guest memory is already wrong: the
        // producer sized it from something other than what the guest owns.
        if (!_memory.CanRead(address, size) && ++_unmappedReports <= MaxReports)
        {
            Console.Error.WriteLine(
                $"[METAL-V2][GUARD] A declared GPU-side write is not entirely mapped guest memory: " +
                $"producer={producer} declared=0x{address:X16}+0x{size:X} serial={serial} " +
                $"{_memory.DescribeReadRange(address, size)}");
        }

        slot.BeforeLength = Read(slot.BeforeAddress, slot.Before.AsSpan(0, slot.BeforeLength));
        slot.AfterLength = Read(slot.AfterAddress, slot.After.AsSpan(0, slot.AfterLength));
        _captures++;
        if (slot.BeforeLength == 0 && slot.AfterLength == 0)
        {
            // Nothing readable either side: the range sits alone in its mapping.
            Release(slot);
            return null;
        }

        return slot;
    }

    /// <summary>Verifies and releases every watch the GPU has finished.</summary>
    public void Verify(ulong completedSerial)
    {
        foreach (var slot in _slots)
        {
            if (slot.InUse && slot.Serial <= completedSerial)
            {
                Check(slot);
                Release(slot);
            }
        }
    }

    /// <summary>Verifies and releases everything, for a drain or a shutdown.</summary>
    public void VerifyAll()
    {
        foreach (var slot in _slots)
        {
            if (slot.InUse)
            {
                Check(slot);
                Release(slot);
            }
        }
    }

    /// <summary>The guard's own counters, for the periodic report.</summary>
    public string Report() =>
        $"captures={_captures} live={_live} violations={_violations} overlaps={_overlaps} " +
        $"unreadable={_unreadable} unmapped={_unmappedReports} evicted={_evicted}";

    private Slot Take()
    {
        for (var attempt = 0; attempt < _slots.Length; attempt++)
        {
            var slot = _slots[_cursor];
            _cursor = _cursor + 1 == _slots.Length ? 0 : _cursor + 1;
            if (!slot.InUse)
            {
                _live++;
                return slot;
            }
        }

        // Full: check the one the cursor is on now and reuse it.
        var oldest = _slots[_cursor];
        Check(oldest);
        _evicted++;
        oldest.InUse = false;
        return oldest;
    }

    private void Release(Slot slot)
    {
        if (slot.InUse)
        {
            slot.InUse = false;
            _live--;
        }
    }

    private int Read(ulong address, Span<byte> destination)
    {
        if (destination.Length == 0)
        {
            return 0;
        }

        if (_memory.TryRead(address, destination))
        {
            return destination.Length;
        }

        _unreadable++;
        return 0;
    }

    // True when a declared range other than this slot's covers the byte. A released slot
    // keeps its range until the slot is reused, so a producer whose own watch has already
    // been checked still accounts for the bytes it declared in the same submission.
    private bool OwnedByAnother(Slot slot, ulong address)
    {
        foreach (var other in _slots)
        {
            if (!ReferenceEquals(other, slot) && other.Declared && other.Start <= address && address < other.End)
            {
                return true;
            }
        }

        return false;
    }

    private void Check(Slot slot)
    {
        CheckSide(slot, before: true);
        CheckSide(slot, before: false);
    }

    private void CheckSide(Slot slot, bool before)
    {
        var length = before ? slot.BeforeLength : slot.AfterLength;
        if (length == 0)
        {
            return;
        }

        var address = before ? slot.BeforeAddress : slot.AfterAddress;
        var snapshot = (before ? slot.Before : slot.After).AsSpan(0, length);
        var current = _current.AsSpan(0, length);
        if (!_memory.TryRead(address, current))
        {
            return;
        }

        for (var index = 0; index < length; index++)
        {
            if (snapshot[index] == current[index])
            {
                continue;
            }

            var faulted = address + (ulong)index;
            // Another producer may own these bytes; its own margin check covers them.
            if (OwnedByAnother(slot, faulted))
            {
                _overlaps++;
                return;
            }

            _violations++;
            if (++_reports <= MaxReports)
            {
                Console.Error.WriteLine(
                    $"[METAL-V2][GUARD] A GPU-side write changed guest bytes outside the range it declared: " +
                    $"producer={slot.Producer} declared=0x{slot.Start:X16}+0x{slot.End - slot.Start:X} " +
                    $"serial={slot.Serial} side={(before ? "before" : "after")} " +
                    $"violation=0x{faulted:X16} distance=0x{(before ? slot.Start - faulted : faulted - slot.End):X} " +
                    $"was=0x{Qword(snapshot, index):X16} now=0x{Qword(current, index):X16}.");
            }

            return;
        }
    }

    // The eight bytes from the first difference, which is what names a poison pattern.
    private static ulong Qword(ReadOnlySpan<byte> bytes, int index)
    {
        var value = 0UL;
        for (var offset = 0; offset < 8 && index + offset < bytes.Length; offset++)
        {
            value |= (ulong)bytes[index + offset] << (offset * 8);
        }

        return value;
    }
}
