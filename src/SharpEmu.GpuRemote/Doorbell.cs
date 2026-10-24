// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace SharpEmu.GpuRemote;

/// <summary>The shared words of one <see cref="Doorbell"/>.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct DoorbellState
{
    /// <summary>Bumped by every ring; the word sleepers wait on.</summary>
    public uint Sequence;

    /// <summary>How many waiters are between <see cref="Doorbell.PrepareWait"/> and the end of their wait.</summary>
    public uint Sleepers;
}

/// <summary>
/// An event count over two words in the shared region: the cheap "wake the other side" signal
/// behind every ring and call table.
/// </summary>
/// <remarks>
/// <para>A waiter takes a ticket (the current sequence), registers as a sleeper, re-checks its
/// condition, and only then blocks until the sequence moves past the ticket. A ringer publishes
/// its data, bumps the sequence and makes the wake system call only when someone is registered,
/// so a ring with nobody asleep is one atomic add and one load - no system call.</para>
///
/// <para>Ordering across the processes (and across x86-64 TSO under Rosetta vs. arm64): the
/// ringer's data store, its atomic add on <see cref="DoorbellState.Sequence"/> and its load of
/// <see cref="DoorbellState.Sleepers"/> are ordered by the add, a full barrier on both
/// architectures (lock xadd, which Rosetta translates to an acquire-release atomic; LDADDAL or
/// an exclusive pair plus DMB natively). The waiter's register (an atomic add on Sleepers) is
/// likewise a full barrier before its re-check. Either the ringer sees the sleeper and wakes it,
/// or the waiter's re-check sees the data, and if the add lands between the ticket and the
/// block the kernel's compare fails and the wait returns at once.</para>
/// </remarks>
public readonly unsafe struct Doorbell
{
    private readonly DoorbellState* _state;

    internal Doorbell(DoorbellState* state)
    {
        _state = state;
    }

    /// <summary>Publishes a signal: wakes every registered sleeper, if any.</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public void Ring()
    {
        Interlocked.Increment(ref _state->Sequence);
        if (Volatile.Read(ref _state->Sleepers) != 0)
        {
            RemoteWait.WakeAll(&_state->Sequence);
        }
    }

    /// <summary>
    /// Bumps the sequence and wakes every waiter regardless of registration; used when one side
    /// is going away and every wait must notice.
    /// </summary>
    public void WakeAll()
    {
        Interlocked.Increment(ref _state->Sequence);
        RemoteWait.WakeAll(&_state->Sequence);
    }

    /// <summary>
    /// First half of a wait: returns the ticket and registers this thread as a sleeper. The
    /// caller re-checks its condition, then calls <see cref="CancelWait"/> or <see cref="CommitWait"/>.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public uint PrepareWait()
    {
        var ticket = Volatile.Read(ref _state->Sequence);
        Interlocked.Increment(ref _state->Sleepers);
        return ticket;
    }

    /// <summary>Withdraws a <see cref="PrepareWait"/> whose condition turned out to hold already.</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public void CancelWait()
    {
        Interlocked.Decrement(ref _state->Sleepers);
    }

    /// <summary>
    /// Second half of a wait: blocks until a ring after the ticket, or the timeout. Returns false
    /// only on timeout (see <see cref="RemoteWait.Wait"/>).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public bool CommitWait(uint ticket, long timeoutNanoseconds)
    {
        var woken = RemoteWait.Wait(&_state->Sequence, ticket, timeoutNanoseconds);
        Interlocked.Decrement(ref _state->Sleepers);
        return woken;
    }

    /// <summary>Current sequence; diagnostics only.</summary>
    public uint Sequence => Volatile.Read(ref _state->Sequence);
}
