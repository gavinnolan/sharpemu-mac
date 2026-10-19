// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
using SharpEmu.HLE;
using SharpEmu.Libs.Gpu;

namespace SharpEmu.Gpu.Metal;

internal static partial class MetalVideoPresenter
{
    private static Action<object>? _finiteStorageBeforePrebufferValidationForControl;
    private static Action? _finiteStoragePrebufferBusyForControl;
    private static Action<object>? _finiteStorageAfterEmptyBufferValidationForControl;

    private static void ValidateFiniteStorageBeforeBufferPublication(nint committed,
        List<FiniteStorageWriteback>? receipts, GuestMemoryBuffer[] buffers)
    {
        if (Monitor.IsEntered(_gate)) throw new InvalidOperationException("Finite pre-buffer validation requires a released presenter gate.");
        var originals = ValidateFiniteStorageBufferOriginalsAgainstImage(receipts, buffers);
        var receipt = receipts![0];
        // The caller already waited the real native CB. Require the exact
        // native identity and status here; no synthetic receipt or early flag.
        if (committed == 0 || committed != receipt.CommandBuffer || !receipt.Encoded || receipt.SuccessfullyCompleted ||
            receipt.Staging == 0 || ReadGuestCommandBufferStatus(committed) != (nint)4)
            throw new InvalidOperationException("Finite pre-buffer validation lacks its exact actual status4 producer/staging.");
        FiniteStoragePrecommit original;
        lock (_gate)
        {
            ValidateFiniteStorageReceiptOwnerLocked(receipt);
            if (_guestImages.Count > 128 || _selectedStorageMipOwners.Count > 128 || _selectedStorageMipImages.Count > 128 ||
                _selectedNativeOriginalProofs.Count > 128)
                throw new InvalidOperationException("Finite pre-buffer ORIGINAL cohort inventory exceeds its bound.");
            original = new(receipt, _guestImages.Select(pair => CaptureFiniteStoragePrecommitImage(pair.Key, pair.Value)).ToArray(),
                _selectedStorageMipOwners.ToArray(), _selectedStorageMipImages.ToArray(), _selectedNativeOriginalProofs.ToArray(),
                _finiteStorageRetiredOriginCount, _finiteRetiredDisplayOriginCount, beforeBufferPublication: true);
        }
        while (true)
        {
            try
            {
                _finiteStorageBeforePrebufferValidationForControl?.Invoke(receipt);
                var currentBuffers = ValidateFiniteStorageBufferOriginalsAgainstImage(receipts, buffers);
                if (currentBuffers.Length != originals.Length || currentBuffers.Where((buffer, index) => !ReferenceEquals(buffer, originals[index])).Any())
                    throw new InvalidOperationException("Finite pre-buffer validation cannot adopt a younger buffer original.");
                ValidateFiniteStoragePrecommit(original);
                return;
            }
            catch (OriginalCanonicalDirtyBusy busy) { WaitFiniteStoragePrecommitBusy(original, busy); }
        }
        // No buffer ticket exists in this loop. The caller prepares/commits
        // each buffer only AFTER return; all those paths remain single-shot.
    }
}
