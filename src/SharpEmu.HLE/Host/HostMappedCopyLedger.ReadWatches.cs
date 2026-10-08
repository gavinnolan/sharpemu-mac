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

    public void ReleaseMappedCopyWriteWatch(CpuMappedCopyObservation observation)
    {
        if (observation?.OwnerIdentity is Capture capture && ReferenceEquals(capture.Owner, this)) capture.ReleaseWatch();
    }
}
