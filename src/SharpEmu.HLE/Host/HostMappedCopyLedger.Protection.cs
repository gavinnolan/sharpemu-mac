// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
namespace SharpEmu.HLE.Host;

public sealed unsafe partial class HostMappedCopyLedger
{
    private const int MaximumProtectionViews = CpuMappedCopyObservation.MaximumSegments;
    private long protectionEffectSequence;

    // Trusted provider bookkeeping only, never a caller permission/native receipt.
    // A canonical provider holds Gate continuously from preparation through its
    // actual syscall and completion. No query reconstructs lost mapping identity.
    public sealed class ProtectionChange
    {
        internal readonly HostMappedCopyLedger Owner;
        internal readonly View[] Original, Successors;
        internal readonly ulong Address, Size;
        internal int State; // prepared, original revoked, consumed
        internal long EffectSequence;
        internal ProtectionChange(HostMappedCopyLedger owner, View[] original,
            View[] successors, ulong address, ulong size)
        { Owner = owner; Original = original; Successors = successors; Address = address; Size = size; }
    }

    public ProtectionChange? PrepareProtectionChange(ulong address, ulong size, HostPageProtection protection,
        GuestPageProtection? originalGuestAccess = null)
    {
        RequireGate();
        // Preserve existing 4K logical effects on Rosetta, but do not claim an
        // aligned 16K identity/rights successor from a sub-kernel-page request.
        if (unknownAlias || !HostViewMemory.IsValidRange(address, size) ||
            address % pageSize != 0 || size % pageSize != 0 || !IsKnownProtection(protection) ||
            (originalGuestAccess.HasValue && !ValidGuestAccess(originalGuestAccess.Value))) return null;
        var end = address + size;
        List<View>? originals = null;
        for (var index = FindAtOrBelow(end - 1); index >= 0; index--)
        {
            var view = views.Values[index];
            if (view.Address + view.Size <= address) break;
            if (originals?.Count == MaximumProtectionViews || view.Revoked != 0 ||
                view.Backing.IsClosing || view.Backing.IsDisposed) return null;
            (originals ??= new()).Add(view);
        }
        if (originals is null) return null;
        // At most three pieces per original. Reserve all storage/identities
        // before revocation/native effect; exhaustion only loses optional copy.
        var successorCount = 0;
        foreach (var view in originals)
        {
            successorCount++;
            if (view.Address < address) successorCount++;
            if (view.Address + view.Size > end) successorCount++;
        }
        var finalCount = views.Count - originals.Count + successorCount;
        if (finalCount > MaximumMappings || nextIncarnation > long.MaxValue - successorCount) return null;
        if (views.Capacity < finalCount) views.Capacity = finalCount;
        var successors = new View[successorCount];
        var next = 0;
        foreach (var view in originals)
        {
            var first = Math.Max(address, view.Address);
            var last = Math.Min(end, view.Address + view.Size);
            if (view.Address < first) Add(view, view.Address, first - view.Address, view.Access, view.GuestAccessKnown);
            Add(view, first, last - first, originalGuestAccess ?? AccessOf(protection), true);
            if (last < view.Address + view.Size) Add(view, last, view.Address + view.Size - last, view.Access, view.GuestAccessKnown);
        }
        return new(this, originals.ToArray(), successors, address, size);

        void Add(View old, ulong start, ulong count, GuestPageProtection access, bool known)
        {
            var piece = new View(this, old.Backing, start, count, old.Offset + start - old.Address, access, ++nextIncarnation);
            piece.GuestAccessKnown = known; piece.CopyAvailable = known ? 1 : 0;
            successors[next++] = piece;
        }
    }

    public bool BeginProtectionChange(ProtectionChange? prepared, ulong address, ulong size)
    {
        RequireGate();
        if (prepared is not null && (!ReferenceEquals(prepared.Owner, this) || prepared.State != 0 ||
            prepared.Address != address || prepared.Size != size ||
            prepared.Original.Any(view => view.Revoked != 0 ||
                !views.TryGetValue(view.Address, out var current) || !ReferenceEquals(current, view))))
            throw new InvalidOperationException("Protection preparation has no exact original mapping identities.");
        if (!TryBeginNativeEffect(address, size, out var effect)) return false;
        InvalidateProtectionCopies(effect, guestRightsUnknown: true);
        if (prepared is not null) { prepared.EffectSequence = protectionEffectSequence; prepared.State = 1; }
        return true;
    }

    public void CompleteProtectionChange(ProtectionChange? prepared, bool nativeSuccess)
    {
        RequireGate();
        if (prepared is null) return;
        if (!ReferenceEquals(prepared.Owner, this) || prepared.State != 1)
            throw new InvalidOperationException("No exact protection effect is awaiting completion.");
        prepared.State = 2; // consume before any publication/throw; never replay
        if (!nativeSuccess || prepared.EffectSequence != protectionEffectSequence) return;
        try
        {
            foreach (var old in prepared.Original)
                if (!views.TryGetValue(old.Address, out var current) || !ReferenceEquals(current, old)) return;
            foreach (var next in prepared.Successors)
            {
                // A provider must not drop Gate or reenter another effect while
                // its syscall receipt is pending. Fail closed if that contract
                // is violated rather than overwriting a competing publication.
                for (var index = FindAtOrBelow(next.Address + next.Size - 1); index >= 0; index--)
                {
                    var current = views.Values[index];
                    if (current.Address + current.Size <= next.Address) break;
                    if (Array.IndexOf(prepared.Original, current) >= 0) continue;
                    return; // never overwrite or revoke a competing identity
                }
                if (next.Backing.IsClosing || next.Backing.IsDisposed || unknownAlias) return;
            }
            foreach (var old in prepared.Original)
            { Volatile.Write(ref old.Revoked, 1); views.Remove(old.Address); NoteMappingChangedLocked(); }
            foreach (var next in prepared.Successors) PublishView(next);
        }
        catch
        {
            // No half-published optional copy coverage after bookkeeping failure.
            foreach (var next in prepared.Successors)
            {
                Volatile.Write(ref next.Revoked, 1);
                if (views.TryGetValue(next.Address, out var current) && ReferenceEquals(current, next))
                    views.Remove(next.Address); // exact rollback, never remove a newer owner
                NoteMappingChangedLocked();
            }
            throw;
        }
    }

    private static bool IsKnownProtection(HostPageProtection protection) => protection is
        HostPageProtection.NoAccess or HostPageProtection.ReadOnly or HostPageProtection.ReadWrite or
        HostPageProtection.Execute or HostPageProtection.ReadExecute or HostPageProtection.ReadWriteExecute or
        HostPageProtection.ExecuteWriteCopy;

    private static bool ValidGuestAccess(GuestPageProtection access) =>
        (access & ~(GuestPageProtection.Read | GuestPageProtection.Write | GuestPageProtection.Execute)) == 0;

    private void InvalidateProtectionCopies(HostAddressRange effect, bool guestRightsUnknown)
    {
        for (var index = FindAtOrBelow(effect.Address + effect.Size - 1); index >= 0; index--)
        {
            var view = views.Values[index];
            if (view.Address + view.Size <= effect.Address) break;
            Volatile.Write(ref view.CopyAvailable, 0);
            if (guestRightsUnknown) view.GuestAccessKnown = false;
        }
        NoteMappingChangedLocked();
    }
}
