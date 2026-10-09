// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
using SharpEmu.HLE;
using SharpEmu.Libs.Gpu;

namespace SharpEmu.Gpu.Metal;

internal static partial class MetalVideoPresenter
{
    internal static readonly bool StorageOriginalEnrollmentWaitRequested =
        Environment.GetEnvironmentVariable("SHARPEMU_METAL_STORAGE_ORIGINAL_ENROLLMENT_WAIT") == "1";

    // A veto for the SAME cold frontend capture, never native image authority.
    // Use registry metadata only: IsGuestImageUploadKnown may otherwise inspect
    // memory when the tracker is disabled. No new snapshot/content is acquired.
    internal static bool IsStorageOriginalEnrollmentOwnerCurrent(ICpuMemory memory,
        object backend, ulong address, long session)
    {
        if (Monitor.IsEntered(_gate) || backend is not MetalGuestGpuBackend ||
            !ReferenceEquals(GuestGpu.Current, backend)) return false;
        bool current;
        lock (_gate)
            current = !_closed && _guestWorkFailure is null && _textureSourceCleanupFailure is null &&
                _typedCpuSyncCleanupFailure is null && _singletonVolumeOriginalCleanupFailure is null &&
                ReferenceEquals(_guestMemory, memory) && session == CurrentStorageCpuSeedSession &&
                !_availableGuestImages.ContainsKey(address) && !_guestImages.ContainsKey(address) &&
                !_guestImageWorkSequences.ContainsKey(address);
        // GuestGpu.Current takes its own gate; never nest it under presenter.
        return current && ReferenceEquals(GuestGpu.Current, backend);
    }
}
