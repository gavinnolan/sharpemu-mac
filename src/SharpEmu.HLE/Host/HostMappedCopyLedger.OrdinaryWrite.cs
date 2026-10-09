// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
namespace SharpEmu.HLE.Host;

public sealed unsafe partial class HostMappedCopyLedger
{
    // ORIGINAL provider records only, captured before the first ordinary write
    // admission. Single-view identities are values: no arrays, object allocation
    // or boxing. Multiview spans alone retain bounded original arrays.
    // This is no copy/content/publication or permission lease.
    public readonly struct OrdinaryWriteIdentity
    {
        private readonly HostMappedCopyLedger? owner;
        private readonly HostBackingObject? backing;
        private readonly View? single;
        private readonly long singleIncarnation;
        private readonly View[]? states;
        private readonly long[]? incarnations;
        internal OrdinaryWriteIdentity(HostMappedCopyLedger owner, HostBackingObject backing, View single)
        {
            this.owner = owner; this.backing = backing; this.single = single;
            singleIncarnation = single.Incarnation; states = null; incarnations = null;
        }
        internal OrdinaryWriteIdentity(HostMappedCopyLedger owner, HostBackingObject backing, View[] states)
        {
            this.owner = owner; this.backing = backing; this.states = states;
            single = null; singleIncarnation = 0;
            incarnations = new long[states.Length];
            for (var index = 0; index < states.Length; index++) incarnations[index] = states[index].Incarnation;
        }
        public bool IsCurrent
        {
            get
            {
                if (owner is null || backing is null || Volatile.Read(ref owner.unknownAlias) ||
                    backing.IsClosing || backing.IsDisposed) return false;
                if (single is not null) return IsOriginalViewCurrent(single, singleIncarnation);
                if (states is null || incarnations is null || states.Length == 0) return false;
                for (var index = 0; index < states.Length; index++)
                    if (!IsOriginalViewCurrent(states[index], incarnations[index])) return false;
                return true;
            }
        }
        private bool IsOriginalViewCurrent(View view, long incarnation) =>
            ReferenceEquals(view.Owner, owner) && ReferenceEquals(view.Backing, backing) &&
            Volatile.Read(ref view.Revoked) == 0 && Volatile.Read(ref view.CopyAvailable) != 0 &&
            Volatile.Read(ref view.GuestAccessKnown) && (view.Access & GuestPageProtection.Write) != 0 &&
            Volatile.Read(ref view.Incarnation) == incarnation;
    }

    public OrdinaryWriteIdentity? CaptureOrdinaryWriteIdentity(HostBackingObject backing, ulong address, ulong size)
        => CaptureOrdinaryWriteIdentity(backing, address, size, out _);

    public OrdinaryWriteIdentity? CaptureOrdinaryWriteIdentity(HostBackingObject backing, ulong address, ulong size,
        out string? firstRefusal)
    {
        firstRefusal = null;
        if (Monitor.IsEntered(gate)) { firstRefusal = "inherited-provider-gate"; return null; }
        if (!HostViewMemory.IsValidRange(address, size)) { firstRefusal = "invalid-original-range"; return null; }
        lock (gate)
        {
            firstRefusal = unknownAlias ? "unknown-alias"
                : !aliases.ContainsKey(backing) ? "backing-not-enrolled"
                : backing.IsClosing ? "backing-closing"
                : backing.IsDisposed ? "backing-disposed" : null;
            if (firstRefusal is not null) return null;
            var index = FindAtOrBelow(address);
            if (index < 0) { firstRefusal = "original-view-missing"; return null; }
            var first = views.Values[index];
            if (!TryResolveOriginalWriteView(backing, first, address, size, out var firstCount, out firstRefusal)) return null;
            if (firstCount == size) return new(this, backing, first);
            // Allocate only after proving that the original request actually
            // crosses the first View. Never substitute a younger record later.
            var selected = new List<View> { first };
            var cursor = address + firstCount; var end = address + size;
            while (cursor < end)
            {
                if (selected.Count >= CpuMappedCopyObservation.MaximumSegments) { firstRefusal = "original-view-limit"; return null; }
                index = FindAtOrBelow(cursor);
                if (index < 0) { firstRefusal = "original-view-missing"; return null; }
                var view = views.Values[index];
                if (!TryResolveOriginalWriteView(backing, view, cursor, end - cursor, out var count, out firstRefusal)) return null;
                selected.Add(view); cursor += count;
            }
            return new(this, backing, selected.ToArray());
        }
    }

    private static bool TryResolveOriginalWriteView(HostBackingObject backing, View view,
        ulong cursor, ulong remaining, out ulong count, out string? firstRefusal)
    {
        count = 0;
        firstRefusal = !ReferenceEquals(view.Backing, backing) ? "original-view-foreign-backing"
            : view.Revoked != 0 ? "original-view-revoked"
            : view.CopyAvailable == 0 ? "original-view-copy-unavailable"
            : !view.GuestAccessKnown ? "original-guest-access-unknown"
            : (view.Access & GuestPageProtection.Write) == 0 ? "original-guest-write-denied"
            : cursor < view.Address ? "original-view-start-gap"
            : cursor - view.Address >= view.Size ? "original-view-end-gap" : null;
        if (firstRefusal is not null) return false;
        count = Math.Min(remaining, view.Size - (cursor - view.Address));
        var offset = view.Offset + cursor - view.Address;
        firstRefusal = offset > backing.Size ? "original-backing-offset-outside"
            : count > backing.Size - offset ? "original-backing-length-outside" : null;
        return firstRefusal is null;
    }
}
