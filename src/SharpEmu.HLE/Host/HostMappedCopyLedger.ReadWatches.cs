// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
namespace SharpEmu.HLE.Host;

public sealed unsafe partial class HostMappedCopyLedger
{
    private sealed class ReadWatchAdmissionWait(Func<bool> originalStillCurrent)
    {
        internal readonly Func<bool> OriginalStillCurrent = originalStillCurrent;
        internal readonly long Deadline = Environment.TickCount64 + 500;
        internal CpuMappedCopyDiagnostics.Failure FirstRefusal;
    }

    // This capability excludes ONLY the calling-thread provider gate. Known
    // wrappers must additionally exclude their inherited gates and closure.
    public bool CanWaitForMappedReadWatchStatus => !Monitor.IsEntered(gate);

    public bool CanWaitForMappedReadWatchEnrollment => !Monitor.IsEntered(gate);

    public CpuMappedCopyResult TryReadMappedCopyWithWriteWatchAdmissionWait(HostBackingObject backing,
        ulong footprintAddress, ulong footprintByteCount, ulong sourceAddress, Span<byte> destination,
        Func<bool> originalStillCurrent, out CpuMappedCopyObservation? observation)
    {
        ArgumentNullException.ThrowIfNull(originalStillCurrent);
        observation = null;
        if (!CanWaitForMappedReadWatchEnrollment || !originalStillCurrent() ||
            !CanWaitForMappedReadWatchEnrollment) return CpuMappedCopyResult.Rejected;
        return TryReadMappedCopyCore(backing, footprintAddress, footprintByteCount, sourceAddress,
            destination, true, out observation, new ReadWatchAdmissionWait(originalStillCurrent));
    }
    private sealed class ReadWatchOwnership(HostMappedCopyLedger owner)
    {
        internal HostManagedWriteIntervals.ReadWatch? Watch;
        internal int Charged;
        internal void Release()
        {
            Watch?.Release();
            if (Interlocked.Exchange(ref Charged, 0) == 1) Interlocked.Decrement(ref owner.readWatchCount);
        }
        ~ReadWatchOwnership() => Release(); // Scalar bookkeeping only, no gate/native callback.
    }

    public CpuMappedCopyResult TryReadMappedCopyWithWriteWatch(HostBackingObject backing,
        ulong footprintAddress, ulong footprintByteCount, ulong sourceAddress,
        Span<byte> destination, out CpuMappedCopyObservation? observation) =>
        TryReadMappedCopyCore(backing, footprintAddress, footprintByteCount, sourceAddress, destination, true, out observation);

    public bool IsMappedCopyWriteWatchCurrent(HostBackingObject backing, CpuMappedCopyObservation observation)
    {
        // Keep the original short-circuit query order. Failure samples below
        // describe this refusal; they cannot authorize or refresh a watch.
        if (!IsCurrent(backing, observation)) return ReadWatchDeclined("mapping-not-current", observation);
        if (observation.OwnerIdentity is not Capture capture) return ReadWatchDeclined("capture-missing", observation);
        if (capture.ReadWatch is not { } watch) return ReadWatchDeclined("watch-missing", observation);
        if (!watch.TryIsCurrent(out var reason, out var primaryAdmission))
            return ReadWatchDeclined(reason, observation, primaryAdmission);
        return true;
    }

    public CpuMappedReadWatchStatus GetMappedCopyWriteWatchStatus(HostBackingObject backing, CpuMappedCopyObservation observation)
    {
        if (!IsCurrent(backing, observation) || observation.OwnerIdentity is not Capture capture ||
            capture.ReadWatch is not { } watch) return CpuMappedReadWatchStatus.Stale;
        var status = watch.Status;
        return IsCurrent(backing, observation) ? status : CpuMappedReadWatchStatus.Stale;
    }

    private static bool ReadWatchDeclined(string reason, CpuMappedCopyObservation observation, long primaryAdmission = -1)
    {
        if (HostPublicationDiagnostics.Enabled)
        {
            var capture = observation?.OwnerIdentity as Capture;
            capture?.ReadWatch?.TraceDecline(reason, capture.WriterRevision, primaryAdmission,
                observation.SourceAddress, observation.SourceByteCount);
            if (capture?.ReadWatch is null)
                HostPublicationDiagnostics.Declined("read-watch", reason, capture?.WriterRevision ?? -1, -1, -1, -1);
        }
        return false;
    }


    // No gate, allocation, native query or current-View reconstruction. The
    // caller checks its exact prepared ticket state on both sides of this read.
    private bool IsOriginalDisjointReadWatchCurrent(HostBackingObject backing,
        CpuMappedCopyObservation reservationOriginal, HostManagedWriteIntervals writers,
        ReadOnlySpan<HostManagedWriteIntervals.PhysicalRun> reservedRuns, CpuMappedCopyObservation watchedOriginal)
    {
        if (!IsCurrent(backing, reservationOriginal) || !IsCurrent(backing, watchedOriginal) ||
            watchedOriginal.OwnerIdentity is not Capture capture || !ReferenceEquals(capture.Owner, this) ||
            capture.ReadWatch is not { } watch || !watch.IsUnrevokedDuringPublication(writers)) return false;
        foreach (var watched in watch.Runs)
            foreach (var reserved in reservedRuns)
                if (Overlaps(new(watched.Offset, watched.Count), new(reserved.Offset, reserved.Count))) return false;
        return watch.IsUnrevokedDuringPublication(writers) && IsCurrent(backing, reservationOriginal) &&
            IsCurrent(backing, watchedOriginal);
    }

    public void ReleaseMappedCopyWriteWatch(CpuMappedCopyObservation observation)
    {
        if (observation?.OwnerIdentity is Capture capture && ReferenceEquals(capture.Owner, this)) capture.ReleaseWatch();
    }
}
