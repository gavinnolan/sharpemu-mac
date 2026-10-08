// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using SharpEmu.Libs.Gpu;

namespace SharpEmu.Gpu.Metal;

internal static partial class MetalVideoPresenter
{
    // Private accepted-transfer receipts. A completed work retains its consumed
    // receipt; late release never rediscovers authority by array identity.
    private sealed class GuestPayloadReceipt(byte[] data)
    {
        public readonly byte[] Data = data;
        public GuestIndexBufferLease? IndexLease;
        public int References;
        public bool Returned;
    }
    private sealed class GuestPayloadOwner(object[] keys, GuestPayloadReceipt[] receipts, GuestIndexBufferLease?[] leases)
    {
        public readonly object[] Keys = keys;
        public readonly GuestPayloadReceipt[] Receipts = receipts;
        public readonly GuestIndexBufferLease?[] Leases = leases;
        public readonly GuestIndexBufferLease?[] PreviousLeases = receipts.Select(static receipt => receipt.IndexLease).ToArray();
        public int RegisteredKeys;
        public bool Accepted, Released;
    }
    private static readonly ConditionalWeakTable<object, GuestPayloadOwner> _guestPayloadOwners = new();
    private static readonly Dictionary<byte[], GuestPayloadReceipt> _activeGuestPayloadReceipts = new(ReferenceEqualityComparer.Instance);
    private static readonly HashSet<GuestPayloadOwner> _activeGuestPayloadOwners = new();

    // Caller holds _gate. Preparation changes no ownership; all allocations and
    // lease validation happen before successful queue/presentation publication.
    private static GuestPayloadOwner PrepareGuestPayloadOwnerLocked(object work)
    {
        var draw = work switch { OffscreenGuestDraw offscreen => offscreen.Draw, Presentation presentation => presentation.TranslatedDraw, TranslatedGuestDraw direct => direct, _ => null };
        object[] keys = draw is not null && !ReferenceEquals(draw, work) ? [work, draw] : [work];
        foreach (var key in keys)
            if (_guestPayloadOwners.TryGetValue(key, out _))
                throw new InvalidOperationException("Metal payload ownership cannot be transferred twice for the same work.");
        var arrays = new Dictionary<byte[], GuestIndexBufferLease?>(ReferenceEqualityComparer.Instance);
        void Add(byte[] data, GuestIndexBufferLease? lease = null)
        {
            if (lease is not null && !lease.Owns(data)) throw new InvalidOperationException("Metal index descriptor has mismatched lease/data identity.");
            if (lease is { Returned: true }) throw new InvalidOperationException("Metal cannot accept an already-returned index lease.");
            if (arrays.TryGetValue(data, out var previous) && previous is not null && lease is not null && !ReferenceEquals(previous, lease))
                throw new InvalidOperationException("Metal shared payload array has conflicting index leases.");
            arrays[data] = previous ?? lease;
        }
        if (work is ComputeGuestDispatch compute)
            foreach (var buffer in compute.GlobalMemoryBuffers) { if (buffer.Pooled) Add(buffer.Data); }
        if (draw is not null)
        {
            foreach (var buffer in draw.GlobalMemoryBuffers) { if (buffer.Pooled) Add(buffer.Data); }
            foreach (var buffer in draw.VertexBuffers) { if (buffer.Pooled) Add(buffer.Data); }
            if (draw.IndexBuffer is { Pooled: true } indexBuffer) Add(indexBuffer.Data, indexBuffer.Lease);
        }
        var receipts = new GuestPayloadReceipt[arrays.Count];
        var leases = new GuestIndexBufferLease?[arrays.Count];
        var index = 0;
        foreach (var pair in arrays)
        {
            var receipt = _activeGuestPayloadReceipts.TryGetValue(pair.Key, out var active) ? active : new(pair.Key);
            if (receipt.Returned || receipt.References == int.MaxValue ||
                receipt.IndexLease is { Returned: true } ||
                receipt.IndexLease is { } existing && pair.Value is { } candidate && !ReferenceEquals(existing, candidate))
                throw new InvalidOperationException("Metal payload receipt has stale or conflicting return authority.");
            receipts[index] = receipt; leases[index++] = pair.Value;
        }
        _activeGuestPayloadReceipts.EnsureCapacity(_activeGuestPayloadReceipts.Count + receipts.Length);
        _activeGuestPayloadOwners.EnsureCapacity(_activeGuestPayloadOwners.Count + 1);
        return new(keys, receipts, leases);
    }
    private static void RegisterGuestPayloadOwnerLocked(GuestPayloadOwner owner)
    {
        foreach (var key in owner.Keys)
        {
            _guestPayloadOwners.Add(key, owner);
            owner.RegisteredKeys++;
        }
    }
    private static void CommitGuestPayloadOwnerLocked(GuestPayloadOwner owner)
    {
        for (var index = 0; index < owner.Receipts.Length; index++)
        {
            var receipt = owner.Receipts[index];
            _activeGuestPayloadReceipts[receipt.Data] = receipt;
            receipt.IndexLease ??= owner.Leases[index];
            receipt.References++;
        }
        owner.Accepted = true;
        _activeGuestPayloadOwners.Add(owner);
    }
    private static void RollbackGuestPayloadOwnerLocked(GuestPayloadOwner owner)
    {
        if (owner.Accepted)
        {
            for (var index = 0; index < owner.Receipts.Length; index++)
            {
                var receipt = owner.Receipts[index];
                if (--receipt.References == 0) _activeGuestPayloadReceipts.Remove(receipt.Data);
                receipt.IndexLease = owner.PreviousLeases[index];
            }
            _activeGuestPayloadOwners.Remove(owner); owner.Accepted = false;
        }
        for (var index = 0; index < owner.RegisteredKeys; index++) _guestPayloadOwners.Remove(owner.Keys[index]);
        owner.RegisteredKeys = 0;
        // Failed admission leaves caller-owned arrays rented. Never return here.
    }
    private static void ReleaseGuestPayloadOwnerLocked(GuestPayloadOwner owner)
    {
        if (!owner.Accepted || owner.Released) return;
        owner.Released = true;
        _activeGuestPayloadOwners.Remove(owner);
        ReleaseGuestPayloadReceiptsLocked(owner.Receipts);
    }
    private static void ReleaseGuestPayloadReceiptsLocked(GuestPayloadReceipt[] receipts)
    {
        Exception? failure = null;
        foreach (var receipt in receipts)
        {
            if (--receipt.References != 0) continue;
            // Consume before pool return (which may throw after removing its
            // lease). No retry can steal a later rent of this same array.
            receipt.Returned = true;
            _activeGuestPayloadReceipts.Remove(receipt.Data);
            try
            {
                if (receipt.IndexLease is { } lease)
                {
                    if (!lease.TryReturn()) throw new InvalidOperationException("Metal accepted index return authority was consumed externally.");
                }
                else GuestDataPool.Shared.Return(receipt.Data);
                _textureSourceAfterCanceledPayloadReturn?.Invoke();
            }
            catch (Exception exception) { failure ??= exception; }
        }
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
    private static void ReleaseGuestPayloadWork(object work)
    {
        lock (_gate)
            if (_guestPayloadOwners.TryGetValue(work, out var owner)) ReleaseGuestPayloadOwnerLocked(owner);
    }
    private static void ReleaseAllGuestPayloadOwnersLocked()
    {
        // Inventory only accepted live owners, including taken work and latest
        // presentation. Completed CWT history is never return authority.
        var owners = _activeGuestPayloadOwners.ToArray();
        Exception? failure = null;
        foreach (var owner in owners)
            try { ReleaseGuestPayloadOwnerLocked(owner); } catch (Exception exception) { failure ??= exception; }
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
    private static void PublishGuestPresentationLocked(Presentation presentation)
    {
        if (!_textureSourceGraphEnabled) { _latestPresentation = presentation; return; }
        if (_closed || _guestWorkFailure is not null)
            throw new InvalidOperationException("Metal cannot publish a presentation after close or failure.");
        var owner = PrepareGuestPayloadOwnerLocked(presentation);
        try { RegisterGuestPayloadOwnerLocked(owner); CommitGuestPayloadOwnerLocked(owner); }
        catch { RollbackGuestPayloadOwnerLocked(owner); throw; }
        var previous = _latestPresentation;
        // Acquire the replacement first: shared arrays remain rented between
        // presentations and repeated-frame rendering retains its CPU snapshot.
        _latestPresentation = presentation;
        if (previous is not null)
            try { ReleaseGuestPayloadWork(previous); }
            catch (Exception exception) { LatchTextureSourceDrainFailureLocked(exception); throw; }
    }

}
