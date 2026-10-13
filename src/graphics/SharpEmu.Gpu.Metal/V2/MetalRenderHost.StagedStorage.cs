// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.Scheduling;

namespace SharpEmu.Gpu.Metal.V2;

internal sealed partial class MetalRenderHost
{
    private MetalStagedStorage? _stagedStorageCopies;

    private MetalStagedStorage StagedStorage => _stagedStorageCopies ??= new MetalStagedStorage(_guestMemory);

    private bool _encodingStagedStores;

    /// <summary>
    /// The buffer a storage binding reads and writes: its window when one covers the
    /// range, otherwise a staged copy that sees the GPU's earlier writes and whose stores
    /// reach guest memory (<see cref="MetalStagedStorage"/>).
    /// </summary>
    private MetalGuestRange ResolveStorageBuffer(ulong address, ulong size, bool written)
    {
        var staged = StagedStorage;
        if (staged.TryShare(address, size, written, out var shared))
        {
            return shared;
        }

        OrderAfterStagedStores(address, size);
        if (_guestMemory.TryResolveInPlace(address, size, out var window))
        {
            return window;
        }

        var staging = _guestMemory.Stage(address, size);
        if (!staging.IsValid)
        {
            return staging;
        }

        // The CPU staged what guest memory holds now. Bytes the GPU has yet to write
        // there are copied in by the GPU, behind that work.
        var gathered = _gpuWrites.PendingSerial(address, size, CompletedSerial) != 0;
        if (!written && !gathered)
        {
            return staging;
        }

        MetalGuestRange pristine;
        if (gathered)
        {
            pristine = _guestMemory.Allocate(staging.Size, 256, out _);
            var context = ((IMetalImageCacheOwner)this).BeginTransfer(MetalTransferCause.GuestCopy);
            staged.EncodeGather(context.CommandBuffer, address, staging, pristine);
            context.ProfileEncoded($"stage storage bytes=0x{staging.Size:X}");
        }
        else
        {
            pristine = _guestMemory.Duplicate(staging);
        }

        if (!pristine.IsValid)
        {
            throw GpuFatal.Fatal(
                $"A staged storage buffer has no pristine copy: address=0x{address:X16} size=0x{staging.Size:X}.");
        }

        staged.Add(address, size, staging, pristine, written);
        return staging;
    }

    /// <summary>
    /// A binding over bytes a staged copy has recorded stores into is recorded after those
    /// stores reach guest memory: ending the open encoder writes them back.
    /// </summary>
    private void OrderAfterStagedStores(ulong address, ulong size)
    {
        if (_stagedStorageCopies is { Count: > 0 } staged && staged.HasRecordedStoresOver(address, size))
        {
            EndEncoders(PassEndCause.Other);
        }
    }

    /// <summary>
    /// Writes every recorded store of a staged copy back into guest memory. Called where
    /// no encoder is open, like the deferred label copies, so the copies run after the
    /// work that stored and before whatever is recorded next.
    /// </summary>
    private void EncodeStagedStores()
    {
        if (_encodingStagedStores || _stagedStorageCopies is not { Count: > 0 } staged || !staged.HasRecordedStores)
        {
            return;
        }

        _encodingStagedStores = true;
        try
        {
            var written = staged.EncodeWritebacks(EnsureCommandBuffer());
            NoteRecordedWork();
            foreach (var (address, size) in written)
            {
                NoteGuestMemoryWrite(address, size, MetalGpuWriteOrigin.StorageBuffer);
            }

            staged.MarkWrittenBack();
        }
        finally
        {
            _encodingStagedStores = false;
        }
    }
}
