// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
namespace SharpEmu.HLE.Host;

public sealed unsafe partial class HostMappedCopyLedger
{
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

    public bool IsMappedCopyWriteWatchCurrent(HostBackingObject backing, CpuMappedCopyObservation observation) =>
        IsCurrent(backing, observation) && observation.OwnerIdentity is Capture capture &&
        capture.ReadWatch is { IsCurrent: true };


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
