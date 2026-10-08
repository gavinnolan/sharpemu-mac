// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
using System.Diagnostics;

namespace SharpEmu.HLE.Host;

/// <summary>
/// Unused ownership-contract scaffold. No production provider or fault handler
/// currently participates; a pin is not permission to restore or retry a fault.
/// </summary>
/// <remarks>
/// Construct and warm on the safe side before publishing to a native handler.
/// Close before kernel mutation, drain outside all recovery/mapping gates, then
/// retain backing until completion. A retained CLR reference does not prevent
/// explicit disposal: provider integration must enforce that separate obligation.
/// Domains never reopen; rearm/remap must create a replacement identity.
/// </remarks>
public sealed class HostRecoveryOwner
{
    private const long ClosedBit = 1L << 32;
    private static long _nextIdentity;
    private long _state; // closed bit and live pin count linearize on one CAS word
    private int _nextSlot = -1;
    private readonly Slot[] _slots;
    private struct Slot { public long Token; } // generation plus low2 lifecycle bits

    public HostRecoveryOwner(object retainedOwner, ulong address, ulong size,
        ulong nativePageSize, ulong domainEpoch, int pinCapacity = 64)
    {
        ArgumentNullException.ThrowIfNull(retainedOwner);
        if (nativePageSize == 0 || (nativePageSize & (nativePageSize - 1)) != 0)
            throw new ArgumentOutOfRangeException(nameof(nativePageSize));
        if (address == 0 || address % nativePageSize != 0)
            throw new ArgumentOutOfRangeException(nameof(address));
        if (size == 0 || size % nativePageSize != 0 || size > ulong.MaxValue - address)
            throw new ArgumentOutOfRangeException(nameof(size));
        if (domainEpoch == 0) throw new ArgumentOutOfRangeException(nameof(domainEpoch));
        if (pinCapacity is < 1 or > 4096 || (pinCapacity & (pinCapacity - 1)) != 0)
            throw new ArgumentOutOfRangeException(nameof(pinCapacity));
        _slots = new Slot[pinCapacity];
        RetainedOwner = retainedOwner;
        Address = address;
        Size = size;
        NativePageSize = nativePageSize;
        DomainEpoch = domainEpoch;
        // Safe-side setup may retry; neither signal-facing method has a loop.
        while (true)
        {
            var previous = Volatile.Read(ref _nextIdentity);
            if (previous == long.MaxValue) throw new InvalidOperationException("Owner identity exhausted.");
            if (Interlocked.CompareExchange(ref _nextIdentity, previous + 1, previous) == previous)
            { Identity = previous + 1; break; }
        }
    }

    public object RetainedOwner { get; }
    public long Identity { get; }
    public ulong Address { get; }
    public ulong Size { get; }
    public ulong NativePageSize { get; }
    public ulong DomainEpoch { get; }
    public bool IsClosed => (Volatile.Read(ref _state) & ClosedBit) != 0;
    public int ActivePins => (int)Volatile.Read(ref _state);

    /// <summary>
    /// Bounded scalar-only pin attempt. The caller must supply the current
    /// normalized frame and must not infer access from tracker/shadow state.
    /// Contention, capacity exhaustion or closure conservatively decline.
    /// </summary>
    public bool TryAcquireWritePin(ulong normalizedAddress, HostRecoveryAccess access,
        long invocation, ulong expectedDomainEpoch, out HostRecoveryPin pin)
    {
        pin = default;
        if (access != HostRecoveryAccess.Write || invocation <= 0 || expectedDomainEpoch != DomainEpoch ||
            normalizedAddress < Address || normalizedAddress - Address >= Size)
            return false;
        var page = normalizedAddress & ~(NativePageSize - 1);
        // Constructor requires whole native pages; this is never guest4K rounding.
        if (!TryReserveSlot(out var slot, out var reserved) || !TryCountReservedSlot(slot, reserved)) return false;
        pin = PublishPin(slot, reserved, normalizedAddress, page, invocation);
        return true;
    }

    private bool TryReserveSlot(out int slot, out long reserved)
    {
        reserved = 0;
        slot = Interlocked.Increment(ref _nextSlot) & (_slots.Length - 1);
        var previous = Volatile.Read(ref _slots[slot].Token);
        var generation = previous >> 2;
        if ((previous & 3) != 0 || generation >= (long.MaxValue >> 2)) return false;
        reserved = ((generation + 1) << 2) | 1;
        if (Interlocked.CompareExchange(ref _slots[slot].Token, reserved, previous) != previous) return false;
        return true;
    }

    private bool TryCountReservedSlot(int slot, long reserved)
    {
        var state = Volatile.Read(ref _state);
        if ((state & ClosedBit) != 0 ||
            Interlocked.CompareExchange(ref _state, state + 1, state) != state)
        {
            Volatile.Write(ref _slots[slot].Token, reserved & ~3L);
            return false;
        }
        return true;
    }

    private HostRecoveryPin PublishPin(int slot, long reserved, ulong normalizedAddress, ulong page, long invocation)
    {
        // Count CAS is acquisition's linearization point. Close sees the count
        // even if this publication is preempted; it cannot drain past this pin.
        var active = (reserved & ~3L) | 2;
        Volatile.Write(ref _slots[slot].Token, active);
        return new(this, slot, active, normalizedAddress, page, invocation);
    }

    /// <summary>Releases exactly one acquired generation, without waiting or allocation.</summary>
    public bool Release(in HostRecoveryPin pin)
    {
        if (!ReferenceEquals(pin.Owner, this) || (uint)pin.Slot >= (uint)_slots.Length ||
            (pin.Token & 3) != 2) return false;
        if (Interlocked.CompareExchange(ref _slots[pin.Slot].Token, (pin.Token & ~3L) | 3,
                pin.Token) != pin.Token) return false;
        Interlocked.Decrement(ref _state);
        Volatile.Write(ref _slots[pin.Slot].Token, pin.Token & ~3L);
        return true;
    }

    /// <summary>Safe-side pre-kernel revocation. Idempotent; never reopens this identity.</summary>
    public void Close() => Interlocked.Or(ref _state, ClosedBit);

    /// <summary>
    /// Safe-side bounded wait after Close. Never call in a signal handler,
    /// while owning a pin, or under a gate recovery may need. Timeout leaves
    /// the owner closed and retained; it does not authorize mutation/release.
    /// </summary>
    public bool TryDrain(TimeSpan timeout)
    {
        if (timeout < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout));
        if (!IsClosed) return false;
        var start = Stopwatch.GetTimestamp();
        while (ActivePins != 0)
        {
            if (Stopwatch.GetElapsedTime(start) >= timeout) return false;
            Thread.Sleep(1); // ordinary-thread wait only; Release does not wake/lock an event
        }
        return true;
    }
}

public enum HostRecoveryAccess { Unknown, Read, Write, Execute }

/// <summary>Non-authorizing lifetime pin; copies release only once.</summary>
public readonly struct HostRecoveryPin
{
    internal HostRecoveryPin(HostRecoveryOwner owner, int slot, long token,
        ulong address, ulong pageAddress, long invocation)
    { Owner = owner; Slot = slot; Token = token; FaultAddress = address; PageAddress = pageAddress; Invocation = invocation; }
    internal HostRecoveryOwner? Owner { get; }
    internal int Slot { get; }
    internal long Token { get; }
    public ulong FaultAddress { get; }
    public ulong PageAddress { get; }
    public long Invocation { get; }
    public long OwnerIdentity => Owner?.Identity ?? 0;
    public ulong DomainEpoch => Owner?.DomainEpoch ?? 0;
}
