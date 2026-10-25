// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace SharpEmu.GpuRemote;

/// <summary>
/// The shared control block of one ring: the two positions and two doorbells, each on its own
/// 128-byte line (the Apple-silicon cache line) so the producer's and consumer's writes never
/// share one. The data area follows it.
/// </summary>
[StructLayout(LayoutKind.Explicit, Size = Size)]
internal struct RingControl
{
    public const int Size = 512;

    /// <summary>Byte position one past the last published record; written only by the producer.</summary>
    [FieldOffset(0)]
    public long Tail;

    /// <summary>Byte position of the first unconsumed record; written only by the consumer.</summary>
    [FieldOffset(128)]
    public long Head;

    /// <summary>Rung by the producer after publishing; the consumer sleeps on it.</summary>
    [FieldOffset(256)]
    public DoorbellState DataBell;

    /// <summary>Rung by the consumer after freeing space; a producer facing a full ring sleeps on it.</summary>
    [FieldOffset(384)]
    public DoorbellState SpaceBell;
}

[StructLayout(LayoutKind.Sequential)]
internal struct RecordHeader
{
    public uint Length;
    public uint Type;
}

/// <summary>
/// Single-producer/single-consumer byte ring of variable-length records in the shared region.
/// </summary>
/// <remarks>
/// <para>A record is an 8-byte header (payload length, type) and the payload, padded to 8 bytes.
/// Positions are 64-bit byte counts that only grow (they would take centuries to wrap), masked
/// by the power-of-two capacity to index the data area. A record never wraps: when it does not
/// fit before the end of the area the producer first writes a padding record covering the rest,
/// and payloads are capped at half the capacity so a record always fits in an empty ring.</para>
///
/// <para>Ordering: the producer writes the record bytes, then publishes <see cref="RingControl.Tail"/>
/// with a release store; the consumer reads Tail with an acquire load before touching the bytes,
/// and gives the space back with a release store of <see cref="RingControl.Head"/>, which the
/// producer acquires before reusing it. <see cref="Volatile"/> compiles to STLR/LDAR on arm64; on
/// x86-64 it is a plain move, which is enough because x86-64 stores and loads are already
/// release and acquire - and Rosetta runs translated code with the hardware in TSO mode, so that
/// guarantee holds for process A too. The doorbells carry their own full barriers.</para>
///
/// <para>Exactly one thread may use the <see cref="RingWriter"/> and one the
/// <see cref="RingReader"/> at a time; the ring takes no locks.</para>
/// </remarks>
public static class SpscRing
{
    public const int RecordHeaderSize = 8;
    public const int MinimumCapacity = 4096;

    /// <summary>The record type reserved for padding; never handed to a reader.</summary>
    public const uint PaddingType = uint.MaxValue;

    /// <summary>Default busy-wait before a reader or writer sleeps.</summary>
    public const long DefaultSpinNanoseconds = 20_000;

    /// <summary>Bytes a ring of <paramref name="capacity"/> data bytes occupies, control block included.</summary>
    public static int BlockSize(int capacity)
    {
        ValidateCapacity(capacity);
        return RingControl.Size + capacity;
    }

    internal static void ValidateCapacity(int capacity)
    {
        if (capacity < MinimumCapacity || !BitOperations.IsPow2(capacity))
        {
            throw new ArgumentOutOfRangeException(nameof(capacity), capacity, $"A ring capacity is a power of two of at least {MinimumCapacity}.");
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int RecordSize(int payloadLength) => (RecordHeaderSize + payloadLength + 7) & ~7;
}

/// <summary>The producer end of an <see cref="SpscRing"/>.</summary>
public sealed unsafe class RingWriter
{
    private readonly RingControl* _control;
    private readonly byte* _data;
    private readonly int _capacity;
    private readonly uint* _peerState;
    private long _tail;
    private long _headCache;
    private long _pendingTail = -1;

    internal RingWriter(byte* block, int capacity, uint* peerState)
    {
        SpscRing.ValidateCapacity(capacity);
        _control = (RingControl*)block;
        _data = block + RingControl.Size;
        _capacity = capacity;
        _peerState = peerState;
        _tail = Volatile.Read(ref _control->Tail);
        _headCache = Volatile.Read(ref _control->Head);
    }

    public int Capacity => _capacity;

    /// <summary>The largest payload one record may carry.</summary>
    public int MaxPayloadLength => _capacity / 2 - SpscRing.RecordHeaderSize;

    /// <summary>Bytes currently free, as last observed.</summary>
    public long FreeBytes => _capacity - (_tail - Volatile.Read(ref _control->Head));

    /// <summary>
    /// Reserves a record of <paramref name="length"/> payload bytes and returns its payload span
    /// to fill in place; <see cref="EndWrite"/> publishes it. False when the ring has no room.
    /// </summary>
    public bool TryBeginWrite(uint type, int length, out Span<byte> payload)
    {
        if (_pendingTail >= 0)
        {
            throw new InvalidOperationException("The previous record was not published with EndWrite.");
        }

        if (type == SpscRing.PaddingType)
        {
            throw new ArgumentOutOfRangeException(nameof(type), "The padding record type is reserved.");
        }

        if ((uint)length > (uint)MaxPayloadLength)
        {
            throw new ArgumentOutOfRangeException(nameof(length), length, $"A record payload is at most {MaxPayloadLength} bytes.");
        }

        var size = SpscRing.RecordSize(length);
        var offset = (int)(_tail & (_capacity - 1));
        var toEnd = _capacity - offset;
        var padding = size > toEnd ? toEnd : 0;
        if (padding + size > _capacity - (_tail - _headCache))
        {
            _headCache = Volatile.Read(ref _control->Head);
            if (padding + size > _capacity - (_tail - _headCache))
            {
                payload = default;
                return false;
            }
        }

        if (padding != 0)
        {
            var filler = (RecordHeader*)(_data + offset);
            filler->Length = (uint)(padding - SpscRing.RecordHeaderSize);
            filler->Type = SpscRing.PaddingType;
            _tail += padding;
            offset = 0;
        }

        var header = (RecordHeader*)(_data + offset);
        header->Length = (uint)length;
        header->Type = type;
        _pendingTail = _tail + size;
        payload = new Span<byte>(_data + offset + SpscRing.RecordHeaderSize, length);
        return true;
    }

    /// <summary>Publishes the record reserved by <see cref="TryBeginWrite"/> and rings the reader.</summary>
    public void EndWrite()
    {
        if (_pendingTail < 0)
        {
            throw new InvalidOperationException("No record is being written.");
        }

        _tail = _pendingTail;
        _pendingTail = -1;
        Volatile.Write(ref _control->Tail, _tail);
        new Doorbell(&_control->DataBell).Ring();
    }

    public bool TryWrite(uint type, ReadOnlySpan<byte> payload)
    {
        if (!TryBeginWrite(type, payload.Length, out var destination))
        {
            return false;
        }

        payload.CopyTo(destination);
        EndWrite();
        return true;
    }

    public bool TryWrite<T>(uint type, in T value)
        where T : unmanaged =>
        TryWrite(type, MemoryMarshal.AsBytes(new ReadOnlySpan<T>(in value)));

    /// <summary>
    /// Writes a record, sleeping while the ring is full. False when the timeout elapses
    /// (negative waits forever) or the reading process has exited.
    /// </summary>
    public bool Write(uint type, ReadOnlySpan<byte> payload, long timeoutNanoseconds, long spinNanoseconds = SpscRing.DefaultSpinNanoseconds)
    {
        if (TryWrite(type, payload))
        {
            return true;
        }

        var spaceBell = new Doorbell(&_control->SpaceBell);
        var start = RemoteNative.MonotonicNanoseconds();
        var spinDeadline = start + spinNanoseconds;
        while (true)
        {
            if (TryWrite(type, payload))
            {
                return true;
            }

            if (Volatile.Read(ref *_peerState) == GpuRemoteChannel.PeerExited)
            {
                return false;
            }

            var left = RemoteWait.Remaining(start, timeoutNanoseconds);
            if (left == 0)
            {
                return false;
            }

            if (RemoteNative.MonotonicNanoseconds() < spinDeadline)
            {
                RemoteWait.Pause();
                continue;
            }

            var ticket = spaceBell.PrepareWait();
            if (TryWrite(type, payload))
            {
                spaceBell.CancelWait();
                return true;
            }

            _ = spaceBell.CommitWait(ticket, left);
        }
    }

    public bool Write<T>(uint type, in T value, long timeoutNanoseconds)
        where T : unmanaged =>
        Write(type, MemoryMarshal.AsBytes(new ReadOnlySpan<T>(in value)), timeoutNanoseconds);
}

/// <summary>The consumer end of an <see cref="SpscRing"/>.</summary>
public sealed unsafe class RingReader
{
    private readonly RingControl* _control;
    private readonly byte* _data;
    private readonly int _capacity;
    private readonly uint* _peerState;
    private long _head;
    private long _tailCache;
    private long _pendingHead = -1;

    internal RingReader(byte* block, int capacity, uint* peerState)
    {
        SpscRing.ValidateCapacity(capacity);
        _control = (RingControl*)block;
        _data = block + RingControl.Size;
        _capacity = capacity;
        _peerState = peerState;
        _head = Volatile.Read(ref _control->Head);
        _tailCache = Volatile.Read(ref _control->Tail);
    }

    public int Capacity => _capacity;

    /// <summary>True when a published record is waiting (padding aside).</summary>
    public bool HasData
    {
        get
        {
            if (_head != _tailCache)
            {
                return true;
            }

            _tailCache = Volatile.Read(ref _control->Tail);
            return _head != _tailCache;
        }
    }

    /// <summary>
    /// Returns the oldest unconsumed record without consuming it; the payload points into the
    /// ring and stays valid until <see cref="Advance"/>. Calling again before Advance returns the
    /// same record.
    /// </summary>
    public bool TryRead(out uint type, out ReadOnlySpan<byte> payload)
    {
        while (true)
        {
            if (!HasData)
            {
                type = 0;
                payload = default;
                return false;
            }

            var offset = (int)(_head & (_capacity - 1));
            var header = (RecordHeader*)(_data + offset);
            var length = header->Length;
            if (header->Type == SpscRing.PaddingType)
            {
                // Padding is always published together with the record after it, so the ring
                // cannot run dry here; the skip reaches the shared Head with the next Advance.
                _head += _capacity - offset;
                continue;
            }

            var size = SpscRing.RecordSize((int)Math.Min(length, (uint)_capacity));
            if (length > (uint)(_capacity / 2 - SpscRing.RecordHeaderSize) || size > _capacity - offset)
            {
                throw new InvalidDataException($"Corrupt ring record at position {_head}: type {header->Type}, length {length}.");
            }

            type = header->Type;
            payload = new ReadOnlySpan<byte>(_data + offset + SpscRing.RecordHeaderSize, (int)length);
            _pendingHead = _head + size;
            return true;
        }
    }

    /// <summary>Reads the payload of the record <see cref="TryRead"/> returned as a <typeparamref name="T"/>.</summary>
    public static T Read<T>(ReadOnlySpan<byte> payload)
        where T : unmanaged
    {
        if (payload.Length < sizeof(T))
        {
            throw new InvalidDataException($"A {typeof(T).Name} record needs {sizeof(T)} bytes, got {payload.Length}.");
        }

        return MemoryMarshal.Read<T>(payload);
    }

    /// <summary>Consumes the record last returned by <see cref="TryRead"/> and gives its space back.</summary>
    public void Advance()
    {
        if (_pendingHead < 0)
        {
            throw new InvalidOperationException("No record was read.");
        }

        _head = _pendingHead;
        _pendingHead = -1;
        Volatile.Write(ref _control->Head, _head);
        new Doorbell(&_control->SpaceBell).Ring();
    }

    /// <summary>
    /// Waits until a record is available: spins for <paramref name="spinNanoseconds"/>, then
    /// sleeps on the data doorbell. False when the timeout elapses (negative waits forever), the
    /// writing process exited with nothing left to read, or a <see cref="GpuRemoteChannel.WakeAll"/>
    /// (a stop request) or interrupted wait woke it with nothing to read - callers loop on their
    /// own conditions.
    /// </summary>
    public bool WaitForData(long timeoutNanoseconds, long spinNanoseconds = SpscRing.DefaultSpinNanoseconds)
    {
        if (HasData)
        {
            return true;
        }

        var dataBell = new Doorbell(&_control->DataBell);
        var start = RemoteNative.MonotonicNanoseconds();
        if (spinNanoseconds > 0)
        {
            // Spinning on the doorbell sequence, which the producer bumps right after publishing
            // Tail, keeps the spin to one word and one line.
            var sequence = dataBell.Sequence;
            if (HasData)
            {
                return true;
            }

            var spin = timeoutNanoseconds >= 0 ? Math.Min(spinNanoseconds, timeoutNanoseconds) : spinNanoseconds;
            if (RemoteWait.SpinWhileEqual(&_control->DataBell.Sequence, sequence, spin) && HasData)
            {
                return true;
            }
        }

        while (true)
        {
            if (HasData)
            {
                return true;
            }

            if (Volatile.Read(ref *_peerState) == GpuRemoteChannel.PeerExited)
            {
                return HasData;
            }

            var left = RemoteWait.Remaining(start, timeoutNanoseconds);
            if (left == 0)
            {
                return false;
            }

            var ticket = dataBell.PrepareWait();
            if (HasData || Volatile.Read(ref *_peerState) == GpuRemoteChannel.PeerExited)
            {
                dataBell.CancelWait();
                continue;
            }

            // Only the producer's publish and a WakeAll move the data doorbell, so a wake that
            // brings no data is a stop request (or an interrupted wait): hand it to the caller.
            if (dataBell.CommitWait(ticket, left) && !HasData)
            {
                return false;
            }
        }
    }
}
