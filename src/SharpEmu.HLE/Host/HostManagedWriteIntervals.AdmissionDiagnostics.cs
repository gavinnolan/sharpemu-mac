// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
namespace SharpEmu.HLE.Host;

internal sealed partial class HostManagedWriteIntervals
{
    // Only cold exclusive admission paths allocate this record, before acquiring
    // exclusion. Never points at a transaction, capture, native owner or backing.
    internal sealed class AdmissionOwner(CpuMappedAdmissionOwnerKind kind)
    {
        private static long nextIdentity;
        private static int exhausted;
        private static long AllocateIdentity()
        {
            if (Volatile.Read(ref exhausted) != 0) return 0;
            var identity = Interlocked.Increment(ref nextIdentity);
            if (identity <= 0 || identity == long.MaxValue)
            { Volatile.Write(ref exhausted, 1); return 0; }
            return identity;
        }
        internal readonly long Identity = AllocateIdentity();
        internal readonly CpuMappedAdmissionOwnerKind Kind = kind;
        internal int Thread, Phase, Outcome, SubtractionReturned;
        internal void SetPhase(CpuMappedAdmissionOwnerPhase phase)
        {
            if (Volatile.Read(ref Outcome) != (int)CpuMappedAdmissionOwnerOutcome.Unknown)
                Volatile.Write(ref Phase, (int)phase);
        }
        internal void Unknown()
        {
            Volatile.Write(ref Outcome, (int)CpuMappedAdmissionOwnerOutcome.Unknown);
            Volatile.Write(ref Phase, (int)CpuMappedAdmissionOwnerPhase.Unknown);
        }
    }
    // Mutable per-ledger CONTROL seam only. Production never assigns it.
    // It cannot supply an owner, identity, status, receipt or authority.
    private int admissionDiagnosticsForControl = -1;
    internal AdmissionOwner? CreateAdmissionOwner(CpuMappedAdmissionOwnerKind kind)
    {
        var control = Volatile.Read(ref admissionDiagnosticsForControl);
        var enabled = control < 0 ? HostPublicationDiagnostics.Enabled : control == 1;
        return enabled ? new(kind) : null;
    }
    private AdmissionOwner? admissionOwner;
    private void PublishAdmissionOwner(AdmissionOwner? owner, CpuMappedAdmissionOwnerPhase phase)
    {
        if (owner is null) return;
        // AFTER the real successful CAS. The intervening gap is unknown.
        owner.Thread = Environment.CurrentManagedThreadId;
        owner.SetPhase(phase);
        Volatile.Write(ref admissionOwner, owner);
    }
    private void DetachAdmissionOwner(AdmissionOwner owner)
    {
        owner.SetPhase(CpuMappedAdmissionOwnerPhase.Releasing);
        // Clear BEFORE the existing release atomic. No owner replacement can
        // be mistaken for this exact one; reference identities are never reused.
        if (!ReferenceEquals(Interlocked.CompareExchange(ref admissionOwner, null, owner), owner)) owner.Unknown();
    }
    internal static void CompleteAdmissionRelease(AdmissionOwner? owner)
    {
        if (owner is null) return;
        // Called only AFTER the existing caller cleanup returned. Admission
        // release is not a certificate for native ownership or guest content.
        if (Volatile.Read(ref owner.SubtractionReturned) != 0 &&
            Volatile.Read(ref owner.Outcome) != (int)CpuMappedAdmissionOwnerOutcome.Unknown)
        {
            Volatile.Write(ref owner.Outcome, (int)CpuMappedAdmissionOwnerOutcome.AdmissionReleased);
            owner.SetPhase(CpuMappedAdmissionOwnerPhase.AdmissionReleased);
        }
    }
    private static CpuMappedAdmissionOwnerDiagnostic FinishAdmissionOwnerSample(
        long admission, AdmissionOwner? before, int phase, int outcome, AdmissionOwner? after)
    {
        if ((admission & PublicationBit) == 0)
            return new(null, default, null, default, default, "not-exclusive-admission");
        if (before is null || after is null)
            return new(null, default, null, default, default, "owner-gap");
        if (!ReferenceEquals(before, after))
            return new(null, default, null, default, default, "owner-changed");
        if (before.Identity <= 0)
            return new(null, default, null, default, default, "owner-identity-unavailable");
        if (phase != Volatile.Read(ref before.Phase) || outcome != Volatile.Read(ref before.Outcome))
            return new(before.Identity, before.Kind, before.Thread, default, default, "owner-phase-changed");
        var unknown = phase == (int)CpuMappedAdmissionOwnerPhase.Unknown || outcome == (int)CpuMappedAdmissionOwnerOutcome.Unknown;
        return new(before.Identity, before.Kind, before.Thread, (CpuMappedAdmissionOwnerPhase)phase,
            (CpuMappedAdmissionOwnerOutcome)outcome, unknown ? "owner-unknown" : "diagnostic-only-no-wait-authority");
    }
}
