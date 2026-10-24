// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace SharpEmu.GpuRemote;

/// <summary>Outcome of a <see cref="SyncCallTable.Call(uint, byte*, int, byte*, int, out int, out int, long, long)"/>.</summary>
public enum SyncCallStatus
{
    /// <summary>The server answered; the response and its status code are valid.</summary>
    Completed = 0,

    /// <summary>No answer within the timeout. The slot is handed to the server, which frees it when it gets there.</summary>
    TimedOut = 1,

    /// <summary>The serving process has exited.</summary>
    PeerExited = 2,

    /// <summary>The request does not fit a slot's payload.</summary>
    RequestTooLarge = 3,

    /// <summary>Every slot stayed busy for the whole timeout.</summary>
    NoFreeSlot = 4,
}

/// <summary>
/// Serves one request on the server side: reads <paramref name="request"/>, writes up to
/// <c>response.Length</c> bytes into <paramref name="response"/>, and returns how many it wrote.
/// <paramref name="status"/> is handed back to the caller as is.
/// </summary>
public delegate int SyncCallHandler(uint opcode, ReadOnlySpan<byte> request, Span<byte> response, out int status);

[StructLayout(LayoutKind.Explicit, Size = Size)]
internal struct SyncControl
{
    public const int Size = 512;

    /// <summary>Bit i set: slot i holds a posted request the server has not picked up yet.</summary>
    [FieldOffset(0)]
    public ulong Pending;

    /// <summary>Rung by callers after posting; the server's thread sleeps on it.</summary>
    [FieldOffset(128)]
    public DoorbellState ServiceBell;

    /// <summary>Rung whenever a slot is freed; callers that found every slot busy sleep on it.</summary>
    [FieldOffset(256)]
    public DoorbellState FreeBell;
}

[StructLayout(LayoutKind.Explicit, Size = Size)]
internal struct SyncSlotHeader
{
    public const int Size = 128;

    /// <summary>A <see cref="SyncSlotState"/>; also the word a caller sleeps on for its answer.</summary>
    [FieldOffset(0)]
    public uint State;

    /// <summary>Non-zero while the caller may be asleep on <see cref="State"/>.</summary>
    [FieldOffset(4)]
    public uint Sleepers;

    [FieldOffset(8)]
    public uint Opcode;

    [FieldOffset(12)]
    public int Status;

    [FieldOffset(16)]
    public uint RequestLength;

    [FieldOffset(20)]
    public uint ResponseLength;

    /// <summary>Per-table call counter, for diagnostics when a slot is inspected.</summary>
    [FieldOffset(24)]
    public ulong CallId;
}

internal static class SyncSlotState
{
    public const uint Free = 0;
    public const uint Claimed = 1;
    public const uint Posted = 2;
    public const uint Serving = 3;
    public const uint Completed = 4;

    /// <summary>The caller gave up; whoever sees this next (the server) frees the slot.</summary>
    public const uint Abandoned = 5;
}

/// <summary>
/// Synchronous request/response slots in the shared region: process A posts a request into a
/// free slot and waits for B to answer in place. Built for the CPU-write-fault slow path
/// (crossing 8 of the split plan), so the calling side is async-signal-safe.
/// </summary>
/// <remarks>
/// <para><b>What may run inside a signal handler.</b> Only
/// <see cref="Call(uint, byte*, int, byte*, int, out int, out int, long, long)"/> (and the span
/// overload, which only pins stack or caller memory). It touches shared memory through atomics,
/// a counter field of this already-constructed object, and at most the system calls of
/// <see cref="RemoteWait"/> made through pointers resolved at start-up (<c>__ulock_wait2</c>
/// with a timeout, <c>__ulock_wake</c>, <c>clock_gettime_nsec_np</c>, <c>nanosleep</c>). It
/// allocates nothing, takes no lock, throws nothing (every failure is a
/// <see cref="SyncCallStatus"/>), and leaves <c>errno</c> alone. Claiming a slot is a CAS, so a
/// nested call from a signal that interrupted another call on the same thread just takes a
/// different slot. The call methods are AggressiveOptimization (no tiering, so no re-JIT in
/// signal context) and <see cref="PrepareForSignalContext"/> compiles them up front; one real
/// call on an ordinary thread before the handler is armed is still the safest warm-up (the
/// handshake does one). Everything else in the transport - rings, the server side, process
/// management - is ordinary-thread code.</para>
///
/// <para><b>Protocol.</b> A slot moves Free → Claimed (caller CAS) → Posted (caller, then sets
/// its <see cref="SyncControl.Pending"/> bit and rings the service doorbell) → Serving (server
/// CAS) → Completed (server CAS) → Free (caller, after copying the answer). A caller that times
/// out CASes the slot to Abandoned instead; whichever CAS the server then loses tells it to free
/// the slot itself. A caller blocks on the slot's own state word after registering in
/// <see cref="SyncSlotHeader.Sleepers"/>, so the server's completion costs a system call only
/// when the caller really went to sleep (the CAS to Completed is the full barrier between the
/// state store and the sleeper check, the caller's exchange on Sleepers the one before its
/// re-check).</para>
/// </remarks>
public sealed unsafe class SyncCallTable
{
    public const int MaxSlots = 64;

    /// <summary>Default busy-wait for an answer before the caller sleeps.</summary>
    public const long DefaultSpinNanoseconds = 50_000;

    private readonly SyncControl* _control;
    private readonly byte* _slots;
    private readonly int _slotCount;
    private readonly int _payloadCapacity;
    private readonly int _stride;
    private readonly uint* _peerState;
    private int _claimCursor;
    private long _callCounter;

    internal SyncCallTable(byte* block, int slotCount, int payloadCapacity, uint* peerState)
    {
        ValidateLayout(slotCount, payloadCapacity);
        _control = (SyncControl*)block;
        _slots = block + SyncControl.Size;
        _slotCount = slotCount;
        _payloadCapacity = payloadCapacity;
        _stride = SyncSlotHeader.Size + payloadCapacity;
        _peerState = peerState;
    }

    public int SlotCount => _slotCount;

    /// <summary>The most request (and response) bytes one call can carry.</summary>
    public int PayloadCapacity => _payloadCapacity;

    /// <summary>Bytes a table occupies in the region.</summary>
    public static int BlockSize(int slotCount, int payloadCapacity)
    {
        ValidateLayout(slotCount, payloadCapacity);
        return SyncControl.Size + slotCount * (SyncSlotHeader.Size + payloadCapacity);
    }

    /// <summary>
    /// Makes one synchronous call: posts <paramref name="requestLength"/> bytes under
    /// <paramref name="opcode"/> and waits up to <paramref name="timeoutNanoseconds"/> (negative:
    /// no limit) for the answer, spinning for <paramref name="spinNanoseconds"/> before sleeping.
    /// Async-signal-safe; see the type remarks.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public SyncCallStatus Call(
        uint opcode,
        byte* request,
        int requestLength,
        byte* response,
        int responseCapacity,
        out int responseLength,
        out int status,
        long timeoutNanoseconds,
        long spinNanoseconds = DefaultSpinNanoseconds)
    {
        responseLength = 0;
        status = 0;
        if ((uint)requestLength > (uint)_payloadCapacity)
        {
            return SyncCallStatus.RequestTooLarge;
        }

        var start = RemoteNative.MonotonicNanoseconds();
        var index = Claim(start, timeoutNanoseconds, out var claimStatus);
        if (index < 0)
        {
            return claimStatus;
        }

        var slot = Slot(index);
        var payload = (byte*)slot + SyncSlotHeader.Size;
        slot->Opcode = opcode;
        slot->RequestLength = (uint)requestLength;
        slot->ResponseLength = 0;
        slot->Status = 0;
        slot->CallId = (ulong)Interlocked.Increment(ref _callCounter);
        for (var i = 0; i < requestLength; i++)
        {
            payload[i] = request[i];
        }

        Volatile.Write(ref slot->State, SyncSlotState.Posted);
        Interlocked.Or(ref _control->Pending, 1UL << index);
        new Doorbell(&_control->ServiceBell).Ring();

        var outcome = AwaitAnswer(slot, start, timeoutNanoseconds, spinNanoseconds);
        if (outcome != SyncCallStatus.Completed)
        {
            return outcome;
        }

        var length = (int)Math.Min(slot->ResponseLength, (uint)Math.Min(responseCapacity, _payloadCapacity));
        for (var i = 0; i < length; i++)
        {
            response[i] = payload[i];
        }

        responseLength = length;
        status = slot->Status;
        Volatile.Write(ref slot->State, SyncSlotState.Free);
        new Doorbell(&_control->FreeBell).Ring();
        return SyncCallStatus.Completed;
    }

    /// <summary>Span form of <see cref="Call(uint, byte*, int, byte*, int, out int, out int, long, long)"/>; equally signal-safe.</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public SyncCallStatus Call(
        uint opcode,
        ReadOnlySpan<byte> request,
        Span<byte> response,
        out int responseLength,
        out int status,
        long timeoutNanoseconds,
        long spinNanoseconds = DefaultSpinNanoseconds)
    {
        fixed (byte* requestBytes = request)
        fixed (byte* responseBytes = response)
        {
            return Call(opcode, requestBytes, request.Length, responseBytes, response.Length, out responseLength, out status, timeoutNanoseconds, spinNanoseconds);
        }
    }

    /// <summary>
    /// Compiles the caller-side methods now, on an ordinary thread, so a first call from a
    /// signal handler never has to JIT.
    /// </summary>
    public static void PrepareForSignalContext()
    {
        // Static constructors first: they resolve the native entry points and read the
        // environment, neither of which may happen on a signal's first call.
        RuntimeHelpers.RunClassConstructor(typeof(RemoteNative).TypeHandle);
        RuntimeHelpers.RunClassConstructor(typeof(RemoteWait).TypeHandle);
        const BindingFlags All = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
        foreach (var method in typeof(SyncCallTable).GetMethods(All))
        {
            if (method.Name is nameof(Call) or nameof(Claim) or nameof(TryClaimAny) or nameof(AwaitAnswer) or nameof(Slot))
            {
                RuntimeHelpers.PrepareMethod(method.MethodHandle);
            }
        }

        foreach (var method in typeof(RemoteWait).GetMethods(All).Concat(typeof(Doorbell).GetMethods(All)))
        {
            RuntimeHelpers.PrepareMethod(method.MethodHandle);
        }

        RuntimeHelpers.PrepareMethod(typeof(RemoteNative).GetMethod(nameof(RemoteNative.MonotonicNanoseconds))!.MethodHandle);
    }

    /// <summary>
    /// Server side: serves every request posted so far with <paramref name="handler"/> and
    /// returns how many it answered.
    /// </summary>
    public int ServePending(SyncCallHandler handler)
    {
        var pending = Interlocked.Exchange(ref _control->Pending, 0);
        var served = 0;
        while (pending != 0)
        {
            var index = BitOperations.TrailingZeroCount(pending);
            pending &= pending - 1;
            var slot = Slot(index);
            var observed = Interlocked.CompareExchange(ref slot->State, SyncSlotState.Serving, SyncSlotState.Posted);
            if (observed != SyncSlotState.Posted)
            {
                if (observed == SyncSlotState.Abandoned)
                {
                    Release(slot);
                }

                continue;
            }

            var payload = (byte*)slot + SyncSlotHeader.Size;
            var requestLength = (int)Math.Min(slot->RequestLength, (uint)_payloadCapacity);
            int length;
            int status;
            try
            {
                length = handler(slot->Opcode, new ReadOnlySpan<byte>(payload, requestLength), new Span<byte>(payload, _payloadCapacity), out status);
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine($"[GPU-REMOTE][ERROR] Sync call {slot->Opcode} failed: {exception}");
                length = 0;
                status = int.MinValue;
            }

            slot->ResponseLength = (uint)Math.Clamp(length, 0, _payloadCapacity);
            slot->Status = status;
            observed = Interlocked.CompareExchange(ref slot->State, SyncSlotState.Completed, SyncSlotState.Serving);
            if (observed == SyncSlotState.Serving)
            {
                if (Volatile.Read(ref slot->Sleepers) != 0)
                {
                    RemoteWait.WakeAll(&slot->State);
                }
            }
            else if (observed == SyncSlotState.Abandoned)
            {
                Release(slot);
            }

            served++;
        }

        return served;
    }

    /// <summary>
    /// Server side: waits until a request is pending. False when the timeout elapses (negative
    /// waits forever), the calling process has exited, or a wake-up brought nothing (a stop
    /// request); callers loop on their own conditions.
    /// </summary>
    public bool WaitForRequests(long timeoutNanoseconds, long spinNanoseconds = SpscRing.DefaultSpinNanoseconds)
    {
        if (Volatile.Read(ref _control->Pending) != 0)
        {
            return true;
        }

        var serviceBell = new Doorbell(&_control->ServiceBell);
        var start = RemoteNative.MonotonicNanoseconds();
        if (spinNanoseconds > 0)
        {
            var sequence = serviceBell.Sequence;
            if (Volatile.Read(ref _control->Pending) != 0)
            {
                return true;
            }

            var spin = timeoutNanoseconds >= 0 ? Math.Min(spinNanoseconds, timeoutNanoseconds) : spinNanoseconds;
            if (RemoteWait.SpinWhileEqual(&_control->ServiceBell.Sequence, sequence, spin) && Volatile.Read(ref _control->Pending) != 0)
            {
                return true;
            }
        }

        while (true)
        {
            if (Volatile.Read(ref _control->Pending) != 0)
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

            var ticket = serviceBell.PrepareWait();
            if (Volatile.Read(ref _control->Pending) != 0 || Volatile.Read(ref *_peerState) == GpuRemoteChannel.PeerExited)
            {
                serviceBell.CancelWait();
                continue;
            }

            // As in RingReader.WaitForData: a wake with nothing pending is a stop request.
            if (serviceBell.CommitWait(ticket, left) && Volatile.Read(ref _control->Pending) == 0)
            {
                return false;
            }
        }
    }

    /// <summary>Wakes every waiter of this table in both processes; used when a side goes away.</summary>
    internal void WakeAll()
    {
        new Doorbell(&_control->ServiceBell).WakeAll();
        new Doorbell(&_control->FreeBell).WakeAll();
        for (var i = 0; i < _slotCount; i++)
        {
            RemoteWait.WakeAll(&Slot(i)->State);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private int Claim(long start, long timeoutNanoseconds, out SyncCallStatus status)
    {
        var freeBell = new Doorbell(&_control->FreeBell);
        while (true)
        {
            if (Volatile.Read(ref *_peerState) == GpuRemoteChannel.PeerExited)
            {
                status = SyncCallStatus.PeerExited;
                return -1;
            }

            var index = TryClaimAny();
            if (index >= 0)
            {
                status = SyncCallStatus.Completed;
                return index;
            }

            var left = RemoteWait.Remaining(start, timeoutNanoseconds);
            if (left == 0)
            {
                status = SyncCallStatus.NoFreeSlot;
                return -1;
            }

            var ticket = freeBell.PrepareWait();
            index = TryClaimAny();
            if (index >= 0)
            {
                freeBell.CancelWait();
                status = SyncCallStatus.Completed;
                return index;
            }

            _ = freeBell.CommitWait(ticket, left);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private int TryClaimAny()
    {
        // A rolling start spreads concurrent callers over the slots instead of having all of
        // them fight over slot 0.
        var first = (int)((uint)Interlocked.Increment(ref _claimCursor) % (uint)_slotCount);
        for (var probe = 0; probe < _slotCount; probe++)
        {
            var index = first + probe;
            if (index >= _slotCount)
            {
                index -= _slotCount;
            }

            var slot = Slot(index);
            if (Volatile.Read(ref slot->State) == SyncSlotState.Free &&
                Interlocked.CompareExchange(ref slot->State, SyncSlotState.Claimed, SyncSlotState.Free) == SyncSlotState.Free)
            {
                return index;
            }
        }

        return -1;
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private SyncCallStatus AwaitAnswer(SyncSlotHeader* slot, long start, long timeoutNanoseconds, long spinNanoseconds)
    {
        if (spinNanoseconds > 0)
        {
            var spin = timeoutNanoseconds >= 0 ? Math.Min(spinNanoseconds, timeoutNanoseconds) : spinNanoseconds;
            var deadline = start + spin;
            do
            {
                // Posted -> Serving is not the answer; keep spinning through it.
                var state = Volatile.Read(ref slot->State);
                if (state == SyncSlotState.Completed)
                {
                    return SyncCallStatus.Completed;
                }

                if (RemoteWait.SpinWhileEqual(&slot->State, state, deadline - RemoteNative.MonotonicNanoseconds()) &&
                    Volatile.Read(ref slot->State) == SyncSlotState.Completed)
                {
                    return SyncCallStatus.Completed;
                }
            }
            while (RemoteNative.MonotonicNanoseconds() < deadline);
        }

        while (true)
        {
            var state = Volatile.Read(ref slot->State);
            if (state == SyncSlotState.Completed)
            {
                return SyncCallStatus.Completed;
            }

            var peerExited = Volatile.Read(ref *_peerState) == GpuRemoteChannel.PeerExited;
            var left = RemoteWait.Remaining(start, timeoutNanoseconds);
            if (peerExited || left == 0)
            {
                // Hand the slot over; if the answer landed meanwhile, take it after all.
                if (Interlocked.CompareExchange(ref slot->State, SyncSlotState.Abandoned, state) == state)
                {
                    return peerExited ? SyncCallStatus.PeerExited : SyncCallStatus.TimedOut;
                }

                continue;
            }

            Interlocked.Exchange(ref slot->Sleepers, 1);
            state = Volatile.Read(ref slot->State);
            if (state != SyncSlotState.Completed)
            {
                _ = RemoteWait.Wait(&slot->State, state, left);
            }

            Volatile.Write(ref slot->Sleepers, 0);
        }
    }

    private void Release(SyncSlotHeader* slot)
    {
        Volatile.Write(ref slot->State, SyncSlotState.Free);
        new Doorbell(&_control->FreeBell).Ring();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private SyncSlotHeader* Slot(int index) => (SyncSlotHeader*)(_slots + (nint)index * _stride);

    private static void ValidateLayout(int slotCount, int payloadCapacity)
    {
        if (slotCount < 1 || slotCount > MaxSlots)
        {
            throw new ArgumentOutOfRangeException(nameof(slotCount), slotCount, $"A call table has 1 to {MaxSlots} slots.");
        }

        // Whole cache lines, so every slot header starts a line of its own.
        if (payloadCapacity < 128 || payloadCapacity % 128 != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(payloadCapacity), payloadCapacity, "A slot payload is a positive multiple of 128 bytes.");
        }
    }
}
