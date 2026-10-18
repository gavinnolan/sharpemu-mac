// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
namespace SharpEmu.Libs.Tests.Memory.Prototype;

// Test-only design prototype. No production caller or signal path uses it.
// Its scalar bookkeeping does NOT reserve mappings or authorize native syscalls.
internal sealed class RestorationPage(ulong address, int watchers)
{
    private const long PendingBit = 1L << 32;
    private long _state = watchers;
    internal ulong Address { get; } = address;
    internal int Watchers => (int)Volatile.Read(ref _state);
    internal bool Pending => (Volatile.Read(ref _state) & PendingBit) != 0;

    internal ReleaseStep TryBegin(RestorationMembership member, out RestorationTicket ticket, Action? afterMemberClaim = null)
    {
        ticket = default;
        if (!ReferenceEquals(member.Page, this)) return ReleaseStep.Busy;
        var oldToken = Volatile.Read(ref member.Token);
        var generation = oldToken >> 2;
        if ((oldToken & 3) != 1 || generation >= (long.MaxValue >> 2)) return ReleaseStep.Busy;
        var pendingToken = ((generation + 1) << 2) | 2;
        if (Interlocked.CompareExchange(ref member.Token, pendingToken, oldToken) != oldToken) return ReleaseStep.Busy;
        afterMemberClaim?.Invoke(); // isolated deterministic scheduling seam, never a production callback
        var state = Volatile.Read(ref _state);
        var count = (int)state;
        if ((state & PendingBit) != 0 || count <= 0 ||
            Interlocked.CompareExchange(ref _state, count == 1 ? PendingBit | 1 : state - 1, state) != state)
        {
            Volatile.Write(ref member.Token, (pendingToken & ~3L) | 1);
            return ReleaseStep.Busy; // bounded CAS; no wait/spin
        }
        if (count > 1)
        {
            Volatile.Write(ref member.Token, pendingToken & ~3L);
            return ReleaseStep.SharedPageStillWatched;
        }
        ticket = new(this, member, pendingToken);
        return ReleaseStep.NativeRestoreRequired;
    }

    // The fixture reports an actual syscall result; a caller-supplied result
    // is not a permission receipt until a real mapping-owner contract exists.
    internal bool Complete(in RestorationTicket ticket, bool success, int error, Action? beforeMemberClaim = null)
    {
        if (!ReferenceEquals(ticket.Page, this) || ticket.Member is null ||
            !ReferenceEquals(ticket.Member.Page, this) || (ticket.PendingToken & 3) != 2) return false;
        beforeMemberClaim?.Invoke(); // isolated test pause before one full-token CAS
        if (Interlocked.CompareExchange(ref ticket.Member.Token, (ticket.PendingToken & ~3L) | 3,
                ticket.PendingToken) != ticket.PendingToken) return false;
        var next = success ? 0L : 1L;
        if (Interlocked.CompareExchange(ref _state, next, PendingBit | 1) != (PendingBit | 1))
            throw new InvalidOperationException("Prototype page transition lost exclusivity.");
        ticket.Member.LastError = success ? 0 : error;
        Volatile.Write(ref ticket.Member.Token, (ticket.PendingToken & ~3L) | (success ? 0L : 1L));
        return true;
    }
}

internal sealed class RestorationMembership(RestorationPage page)
{
    internal RestorationPage Page { get; } = page;
    // Generation and state publish/claim in one CAS word, preventing a stale
    // copied result from stealing the pending state of a later retry.
    internal long Token = 1; // low2 bits: watched1, pending2, settling3, released0
    internal int LastError;
    internal bool Released => (Volatile.Read(ref Token) & 3) == 0;
}

internal enum ReleaseStep { Busy, SharedPageStillWatched, NativeRestoreRequired }
internal readonly record struct RestorationTicket(RestorationPage? Page, RestorationMembership? Member, long PendingToken);
