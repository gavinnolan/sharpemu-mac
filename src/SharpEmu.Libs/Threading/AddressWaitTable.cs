// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Libs.Threading;

/// <summary>A wait keyed by a guest address (futex/umtx style).</summary>
public class AddressWaiter : GuestWaiter
{
    public AddressWaiter(ulong address, int kind = AddressWaitTable.KindAny)
    {
        Address = address;
        Kind = kind;
    }

    public ulong Address { get; }

    /// <summary>Waiter class on the same address (e.g. reader vs writer of an rwlock).</summary>
    public int Kind { get; }
}

/// <summary>
/// Address-keyed wait queues for the umtx and SyncOnAddress families (FreeBSD's umtxq
/// chains). Addresses hash onto a fixed set of buckets; each bucket is one
/// <see cref="WaitQueue"/> whose gate serialises every check-then-sleep against wakes for
/// the addresses in it, so a waiter that read the expected value under the gate cannot
/// miss a wake issued after the guest changed the value.
/// </summary>
public sealed class AddressWaitTable
{
    public const int KindAny = -1;
    private const int BucketCount = 256;

    private readonly WaitQueue[] _buckets;

    public AddressWaitTable()
    {
        _buckets = new WaitQueue[BucketCount];
        for (var i = 0; i < _buckets.Length; i++)
        {
            _buckets[i] = new WaitQueue();
        }
    }

    public static AddressWaitTable Shared { get; } = new();

    /// <summary>The bucket queue whose gate guards waits on <paramref name="address"/>.</summary>
    public WaitQueue BucketFor(ulong address)
    {
        var hash = (address >> 2) * 0x9E3779B97F4A7C15UL;
        return _buckets[(int)(hash >> 56) & (BucketCount - 1)];
    }

    /// <summary>Waiters queued on <paramref name="address"/> (caller holds its bucket's gate).</summary>
    public static int CountLocked(WaitQueue bucket, ulong address, int kind = KindAny)
    {
        var count = 0;
        for (var waiter = bucket.First; waiter is not null; waiter = waiter.NextInQueue)
        {
            if (waiter is AddressWaiter addressWaiter && Matches(addressWaiter, address, kind))
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>
    /// Satisfies up to <paramref name="count"/> waiters on <paramref name="address"/> in
    /// arrival order (caller holds the bucket's gate); returns how many.
    /// </summary>
    public static int WakeLocked(WaitQueue bucket, ulong address, int count, ref WakeBatch batch, int kind = KindAny)
    {
        var woken = 0;
        var waiter = bucket.First;
        while (waiter is not null && woken < count)
        {
            var next = waiter.NextInQueue;
            if (waiter is AddressWaiter addressWaiter &&
                Matches(addressWaiter, address, kind) &&
                addressWaiter.TryCompleteLocked(GuestWaitStatus.Satisfied))
            {
                batch.Add(addressWaiter);
                woken++;
            }

            waiter = next;
        }

        return woken;
    }

    /// <summary>Takes the bucket lock, wakes up to <paramref name="count"/> waiters and returns how many.</summary>
    public int Wake(ulong address, int count, int kind = KindAny)
    {
        if (count <= 0)
        {
            return 0;
        }

        var bucket = BucketFor(address);
        var batch = default(WakeBatch);
        int woken;
        lock (bucket.Gate)
        {
            woken = bucket.Count == 0 ? 0 : WakeLocked(bucket, address, count, ref batch, kind);
        }

        batch.WakeAll();
        return woken;
    }

    private static bool Matches(AddressWaiter waiter, ulong address, int kind) =>
        waiter.Address == address && (kind == KindAny || waiter.Kind == kind);
}
