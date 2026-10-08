// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
namespace SharpEmu.HLE.Host;

public sealed unsafe partial class HostMappedCopyLedger
{
    private const int DerivedReceiptCapacity = 8;
    private struct DerivedMember { internal View? View; internal long Incarnation; }
    private sealed class DerivedSlot
    {
        internal long Identity;
        internal int Count;
        internal bool Eligible;
        internal readonly DerivedMember[] Members = new DerivedMember[MaximumProtectionViews];
    }
    // Startup-only allocation. No slot/member/receipt object is allocated while
    // a provider applies a signal-entered derived protection.
    private readonly DerivedSlot[] derivedSlots = Enumerable.Range(0, DerivedReceiptCapacity).Select(_ => new DerivedSlot()).ToArray();

    public readonly struct DerivedProtectionReceipt
    {
        internal readonly HostMappedCopyLedger? Owner;
        internal readonly int Slot;
        internal readonly long Identity;
        internal DerivedProtectionReceipt(HostMappedCopyLedger owner, int slot, long identity)
        { Owner = owner; Slot = slot; Identity = identity; }
    }

    public bool BeginDerivedProtection(ulong address, ulong size, GuestPageProtection guestAccess,
        HostPageProtection derivedAccess, out DerivedProtectionReceipt receipt)
    {
        RequireGate(); receipt = default;
        if (!ValidGuestAccess(guestAccess) || !DerivedFitsGuest(guestAccess, derivedAccess)) return false;
        if (!TryBeginNativeEffect(address, size, out var effect)) return false;
        var chosen = -1;
        for (var slot = 0; slot < derivedSlots.Length; slot++)
            if (derivedSlots[slot].Identity == 0) { chosen = slot; break; }
        DerivedSlot? pending = chosen < 0 ? null : derivedSlots[chosen];
        if (pending is not null)
        {
            pending.Identity = protectionEffectSequence; pending.Count = 0; pending.Eligible = true;
            receipt = new(this, chosen, pending.Identity);
        }
        for (var index = FindAtOrBelow(effect.Address + effect.Size - 1); index >= 0; index--)
        {
            var view = views.Values[index];
            if (view.Address + view.Size <= effect.Address) break;
            Volatile.Write(ref view.CopyAvailable, 0);
            // The input is the actual guest ledger, independently of watcher
            // restrictions. An unexpected base change cannot preserve old RW.
            if (!view.GuestAccessKnown || view.Access != guestAccess)
            {
                view.GuestAccessKnown = false;
                if (pending is not null) pending.Eligible = false;
            }
            if (pending is null) continue;
            if (pending.Count == MaximumProtectionViews || nextIncarnation == long.MaxValue)
            { pending.Eligible = false; continue; }
            var incarnation = ++nextIncarnation;
            Volatile.Write(ref view.Incarnation, incarnation); // old captures stale before syscall
            pending.Members[pending.Count++] = new() { View = view, Incarnation = incarnation };
        }
        return true; // exhaustion loses copy availability, never invents kernel failure
    }

    public void CompleteDerivedProtection(DerivedProtectionReceipt receipt, bool nativeSuccess)
    {
        RequireGate();
        if (receipt.Owner is null) return; // no slot: native effect may still preserve legacy behavior
        if (!ReferenceEquals(receipt.Owner, this) || (uint)receipt.Slot >= derivedSlots.Length)
            throw new InvalidOperationException("Foreign derived protection receipt.");
        var pending = derivedSlots[receipt.Slot];
        if (receipt.Identity <= 0 || pending.Identity != receipt.Identity)
            throw new InvalidOperationException("Derived protection receipt was already consumed.");
        pending.Identity = -receipt.Identity; // consume before any release/reuse
        try
        {
            if (!nativeSuccess || !pending.Eligible || receipt.Identity != protectionEffectSequence || unknownAlias) return;
            for (var index = 0; index < pending.Count; index++)
            {
                var member = pending.Members[index]; var view = member.View!;
                if (view.Revoked != 0 || !view.GuestAccessKnown || view.Backing.IsClosing || view.Backing.IsDisposed ||
                    view.Incarnation != member.Incarnation || !views.TryGetValue(view.Address, out var current) || !ReferenceEquals(current, view)) return;
            }
            for (var index = 0; index < pending.Count; index++)
                Volatile.Write(ref pending.Members[index].View!.CopyAvailable, 1);
        }
        finally
        {
            for (var index = 0; index < pending.Count; index++) pending.Members[index] = default;
            pending.Count = 0; pending.Eligible = false; pending.Identity = 0;
        }
    }

    private static bool DerivedFitsGuest(GuestPageProtection guest, HostPageProtection derived)
    {
        if (!IsKnownProtection(derived)) return false;
        var native = AccessOf(derived);
        // Execute may imply host-readable on x86, without granting guest Read.
        var permitted = guest | ((guest & GuestPageProtection.Execute) != 0 ? GuestPageProtection.Read : 0);
        return (native & ~permitted) == 0;
    }

    public static bool IsValidDerivedProtection(GuestPageProtection guest, HostPageProtection derived) =>
        ValidGuestAccess(guest) && DerivedFitsGuest(guest, derived);
}
