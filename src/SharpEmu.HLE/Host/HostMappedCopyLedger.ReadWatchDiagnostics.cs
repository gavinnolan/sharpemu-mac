// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
namespace SharpEmu.HLE.Host;

public sealed unsafe partial class HostMappedCopyLedger
{
    // Same original Boolean mapping/watch guard order. In particular this
    // does NOT invoke Status or perform its later admission classification.
    public bool IsMappedCopyWriteWatchCurrent(HostBackingObject backing,
        CpuMappedCopyObservation observation, out CpuMappedReadWatchDiagnostic diagnostic)
    {
        diagnostic = default;
        if (!IsCurrentForReadWatchDiagnostic(backing, observation, out var mappingReason))
        {
            diagnostic = new() { MappingGuard = mappingReason, MappingCheck = "before-watch", BooleanCurrent = false };
            return ReadWatchDeclined("mapping-not-current", observation);
        }
        if (observation.OwnerIdentity is not Capture capture)
        { diagnostic = new() { MappingGuard = "capture-missing", BooleanCurrent = false }; return ReadWatchDeclined("capture-missing", observation); }
        if (capture.ReadWatch is not { } watch)
        { diagnostic = new() { WatchGuard = "watch-missing", BooleanCurrent = false }; return ReadWatchDeclined("watch-missing", observation); }
        var current = watch.TryIsCurrent(out var reason, out var primaryAdmission);
        diagnostic = new() { WatchGuard = reason, PrimaryAdmission = primaryAdmission >= 0 ? primaryAdmission : null, BooleanCurrent = current };
        return current || ReadWatchDeclined(reason, observation, primaryAdmission);
    }

    // Selected ONLY by the optional diagnostic overload. Same short-circuit
    // reads as IsCurrent, without a second mapping query or late re-resolution.
    private bool IsCurrentForReadWatchDiagnostic(HostBackingObject backing, CpuMappedCopyObservation observation,
        out string reason)
    {
        reason = "observation-null"; if (observation is null) return false;
        reason = "capture-missing"; if (observation.OwnerIdentity is not Capture captured) return false;
        reason = "capture-ledger"; if (!ReferenceEquals(captured.Owner, this)) return false;
        reason = "ledger-unknown-alias"; if (Volatile.Read(ref unknownAlias)) return false;
        reason = "backing-closing"; if (backing.IsClosing) return false;
        reason = "backing-disposed"; if (backing.IsDisposed) return false;
        for (var index = 0; index < captured.States.Length; index++)
        {
            var view = captured.States[index];
            reason = "view-backing"; if (!ReferenceEquals(view.Backing, backing)) return false;
            reason = "view-revoked"; if (Volatile.Read(ref view.Revoked) != 0) return false;
            reason = "view-copy-unavailable"; if (Volatile.Read(ref view.CopyAvailable) == 0) return false;
            reason = "view-incarnation"; if (Volatile.Read(ref view.Incarnation) != captured.Incarnations[index]) return false;
        }
        reason = "current"; return true;
    }

    public CpuMappedReadWatchStatus GetMappedCopyWriteWatchStatus(HostBackingObject backing,
        CpuMappedCopyObservation observation, out CpuMappedReadWatchDiagnostic diagnostic)
    {
        diagnostic = default;
        if (!IsCurrentForReadWatchDiagnostic(backing, observation, out var reason))
        { diagnostic = new() { MappingGuard = reason, MappingCheck = "before-watch", ProviderStatus = CpuMappedReadWatchStatus.Stale }; return CpuMappedReadWatchStatus.Stale; }
        if (observation.OwnerIdentity is not Capture capture)
        { diagnostic = new() { MappingGuard = "capture-missing", MappingCheck = "after-first-mapping", ProviderStatus = CpuMappedReadWatchStatus.Stale }; return CpuMappedReadWatchStatus.Stale; }
        if (capture.ReadWatch is not { } watch)
        { diagnostic = new() { WatchGuard = "watch-missing", ProviderStatus = CpuMappedReadWatchStatus.Stale }; return CpuMappedReadWatchStatus.Stale; }
        var status = watch.GetStatusWithDiagnostic(out diagnostic);
        if (IsCurrentForReadWatchDiagnostic(backing, observation, out reason)) return status;
        diagnostic = diagnostic with { MappingGuard = reason, MappingCheck = "after-watch", ProviderStatus = CpuMappedReadWatchStatus.Stale };
        return CpuMappedReadWatchStatus.Stale;
    }
}
