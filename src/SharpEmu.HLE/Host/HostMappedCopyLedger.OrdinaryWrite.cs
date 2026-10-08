// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
namespace SharpEmu.HLE.Host;

public sealed unsafe partial class HostMappedCopyLedger
{
    // ORIGINAL provider records only, captured before the first ordinary write
    // admission. This is no copy/content/publication or permission lease.
    public sealed class OrdinaryWriteIdentity
    {
        private readonly HostMappedCopyLedger owner;
        private readonly HostBackingObject backing;
        private readonly View[] states;
        private readonly long[] incarnations;
        internal OrdinaryWriteIdentity(HostMappedCopyLedger owner, HostBackingObject backing, View[] states)
        {
            this.owner = owner; this.backing = backing; this.states = states;
            incarnations = new long[states.Length];
            for (var index = 0; index < states.Length; index++) incarnations[index] = states[index].Incarnation;
        }
        public bool IsCurrent
        {
            get
            {
                if (Volatile.Read(ref owner.unknownAlias) || backing.IsClosing || backing.IsDisposed) return false;
                for (var index = 0; index < states.Length; index++)
                {
                    var view = states[index];
                    if (!ReferenceEquals(view.Backing, backing) || Volatile.Read(ref view.Revoked) != 0 ||
                        Volatile.Read(ref view.CopyAvailable) == 0 || !Volatile.Read(ref view.GuestAccessKnown) ||
                        (view.Access & GuestPageProtection.Write) == 0 ||
                        Volatile.Read(ref view.Incarnation) != incarnations[index]) return false;
                }
                return true;
            }
        }
    }

    public OrdinaryWriteIdentity? CaptureOrdinaryWriteIdentity(HostBackingObject backing, ulong address, ulong size)
    {
        if (Monitor.IsEntered(gate)) return null; // no hidden recursive provider caller
        if (!HostViewMemory.IsValidRange(address, size)) return null;
        lock (gate)
        {
            if (unknownAlias || !aliases.ContainsKey(backing) || backing.IsClosing || backing.IsDisposed) return null;
            var selected = new List<View>();
            var cursor = address; var end = address + size;
            while (cursor < end)
            {
                if (selected.Count >= CpuMappedCopyObservation.MaximumSegments) return null;
                var index = FindAtOrBelow(cursor);
                if (index < 0) return null;
                var view = views.Values[index];
                if (!ReferenceEquals(view.Backing, backing) || view.Revoked != 0 || view.CopyAvailable == 0 ||
                    !view.GuestAccessKnown || (view.Access & GuestPageProtection.Write) == 0 ||
                    cursor < view.Address || cursor - view.Address >= view.Size) return null;
                var count = Math.Min(end - cursor, view.Size - (cursor - view.Address));
                var offset = view.Offset + cursor - view.Address;
                if (offset > backing.Size || count > backing.Size - offset) return null;
                selected.Add(view); cursor += count;
            }
            return new(this, backing, selected.ToArray());
        }
    }
}
