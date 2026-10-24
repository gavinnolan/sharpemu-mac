// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.InteropServices;

namespace SharpEmu.GpuRemote;

[StructLayout(LayoutKind.Explicit, Size = SharedCounter.BlockSize)]
internal struct SharedCounterState
{
    /// <summary>Only ever raised.</summary>
    [FieldOffset(0)]
    public ulong Value;

    /// <summary>Rung after every raise; waiters sleep on it.</summary>
    [FieldOffset(64)]
    public DoorbellState Bell;
}

/// <summary>
/// A 64-bit counter in shared memory that one process raises and the other waits on: "every
/// request up to n is done". The split's page-protection acknowledgement is one (process A
/// raises it as it applies process B's requests, B waits for its own request's number).
/// </summary>
/// <remarks>
/// <para>The value only grows (<see cref="Publish"/> never lowers it), so a waiter for n is
/// done as soon as it reads anything at or past n, however many raises it slept through. A
/// raise is a CAS on the value followed by a ring of the doorbell, which costs a system call
/// only when a waiter is asleep (see <see cref="Doorbell"/>).</para>
///
/// <para>A wait sleeps in slices of at most <see cref="PeerCheckNanoseconds"/> and checks the
/// other side's state between them, so it returns soon after the peer dies even though
/// nothing rings this doorbell then (<see cref="GpuRemoteChannel.WakeAll"/> knows only the
/// transport's own doorbells).</para>
/// </remarks>
public sealed unsafe class SharedCounter
{
    /// <summary>Bytes one counter occupies: two cache lines.</summary>
    public const int BlockSize = 128;

    /// <summary>The longest a waiter sleeps before it looks at the peer's state again.</summary>
    public const long PeerCheckNanoseconds = 50_000_000;

    private readonly SharedCounterState* _state;
    private readonly uint* _peerState;

    /// <summary>
    /// A counter over <see cref="BlockSize"/> bytes at <paramref name="block"/> (128-byte
    /// aligned, zero to start with), whose waits watch <paramref name="peerState"/>.
    /// </summary>
    public SharedCounter(byte* block, uint* peerState)
    {
        if (block == null || ((nuint)block & 127) != 0)
        {
            throw new ArgumentException("A shared counter needs a 128-byte aligned block.", nameof(block));
        }

        ArgumentNullException.ThrowIfNull(peerState);
        _state = (SharedCounterState*)block;
        _peerState = peerState;
    }

    /// <summary>A counter at <paramref name="offset"/> of the channel's application block.</summary>
    public static SharedCounter InApplicationBlock(GpuRemoteChannel channel, int offset)
    {
        ArgumentNullException.ThrowIfNull(channel);
        if (channel.Application == null || offset < 0 || offset > channel.ApplicationBytes - BlockSize)
        {
            throw new ArgumentOutOfRangeException(nameof(offset), offset, "The counter does not fit the application block.");
        }

        return new SharedCounter(channel.Application + offset, channel.PeerStateAddress);
    }

    public ulong Value => Volatile.Read(ref _state->Value);

    /// <summary>Raises the value to <paramref name="value"/> (never lowers it) and wakes waiters.</summary>
    public void Publish(ulong value)
    {
        var current = Volatile.Read(ref _state->Value);
        while (current < value)
        {
            var observed = Interlocked.CompareExchange(ref _state->Value, value, current);
            if (observed == current)
            {
                break;
            }

            current = observed;
        }

        new Doorbell(&_state->Bell).Ring();
    }

    /// <summary>
    /// Waits until the value reaches <paramref name="target"/>: spins for
    /// <paramref name="spinNanoseconds"/>, then sleeps. False when
    /// <paramref name="timeoutNanoseconds"/> elapses (negative waits forever) or the other
    /// process has exited first.
    /// </summary>
    public bool WaitAtLeast(ulong target, long timeoutNanoseconds, long spinNanoseconds = SpscRing.DefaultSpinNanoseconds)
    {
        if (Volatile.Read(ref _state->Value) >= target)
        {
            return true;
        }

        var bell = new Doorbell(&_state->Bell);
        var start = RemoteNative.MonotonicNanoseconds();
        if (spinNanoseconds > 0)
        {
            var spinDeadline = start + (timeoutNanoseconds >= 0 ? Math.Min(spinNanoseconds, timeoutNanoseconds) : spinNanoseconds);
            while (RemoteNative.MonotonicNanoseconds() < spinDeadline)
            {
                var sequence = bell.Sequence;
                if (Volatile.Read(ref _state->Value) >= target)
                {
                    return true;
                }

                _ = RemoteWait.SpinWhileEqual(&_state->Bell.Sequence, sequence, spinDeadline - RemoteNative.MonotonicNanoseconds());
            }
        }

        while (true)
        {
            if (Volatile.Read(ref _state->Value) >= target)
            {
                return true;
            }

            if (Volatile.Read(ref *_peerState) == GpuRemoteChannel.PeerExited)
            {
                return Volatile.Read(ref _state->Value) >= target;
            }

            var left = RemoteWait.Remaining(start, timeoutNanoseconds);
            if (left == 0)
            {
                return false;
            }

            var ticket = bell.PrepareWait();
            if (Volatile.Read(ref _state->Value) >= target)
            {
                bell.CancelWait();
                return true;
            }

            _ = bell.CommitWait(ticket, left < 0 ? PeerCheckNanoseconds : Math.Min(left, PeerCheckNanoseconds));
        }
    }
}
