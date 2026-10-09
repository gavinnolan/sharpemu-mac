// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
using System.Diagnostics;
using System.Runtime.ExceptionServices;
using SharpEmu.HLE;

namespace SharpEmu.Gpu.Metal;

internal static partial class MetalVideoPresenter
{
    // Default-null race controls; callbacks never supply content, admission,
    // completion, mapping or native ownership and run outside presenter gates.
    private static Action<object>? _finiteStorageCompletedPrecommitForControl;
    private static Action<bool>? _finiteStorageBeforePrecommitValidationForControl;
    private static Action? _finiteStoragePrecommitBusyForControl;
    private const int FiniteStoragePrecommitBusyMilliseconds = 500;

    private sealed record FiniteStoragePrecommitImage(ulong Address, GuestImage Image,
        StorageCpuSeedOrigin? Origin, MetalStorageCpuLayout? Layout, nint Texture, int Version,
        uint Width, uint Height, uint Depth, uint ArrayLength, uint TextureType, MtlPixelFormat Format,
        bool Initialized, bool GpuWritten);

    // Immutable original registry identities, NOT a content/native lease.
    // Strict provider/token/physical-cohort predicates are still required each
    // tick. A replacement or new owner is never enrolled into this continuation.
    private sealed class FiniteStoragePrecommit(FiniteStorageWriteback receipt,
        FiniteStoragePrecommitImage[] images,
        KeyValuePair<SelectedStorageMipKey, SelectedStorageMipAssociation>[] selected,
        KeyValuePair<SelectedStorageMipKey, GuestImage>[] selectedImages,
        KeyValuePair<SelectedStorageMipKey, SelectedNativeOriginalProof>[] selectedProofs,
        int retiredOrigins, int retiredDisplays)
    {
        internal readonly FiniteStorageWriteback Receipt = receipt;
        internal readonly nint CommandBuffer = receipt.CommandBuffer, Staging = receipt.Staging;
        internal readonly FiniteStoragePrecommitImage[] SelectedNativeImages = selectedImages
            .Select(pair => CaptureFiniteStoragePrecommitImage(pair.Key.Resource.Address, pair.Value)).ToArray();
        internal readonly (SelectedNativeOriginalProof Proof, int Version, long Producer)[] SelectedNativeVersions = selectedProofs
            .Select(pair => (pair.Value, pair.Value.Version, pair.Value.ProducerSequence)).ToArray();
        internal readonly FiniteStoragePrecommitImage[] Images = images;
        internal readonly KeyValuePair<SelectedStorageMipKey, SelectedStorageMipAssociation>[] Selected = selected;
        internal readonly KeyValuePair<SelectedStorageMipKey, GuestImage>[] SelectedImages = selectedImages;
        internal readonly KeyValuePair<SelectedStorageMipKey, SelectedNativeOriginalProof>[] SelectedProofs = selectedProofs;
        internal readonly int RetiredOrigins = retiredOrigins, RetiredDisplays = retiredDisplays;
        internal OriginalCanonicalDirtyBusy? FirstBusy;
        internal long BusyStarted;
    }

    private sealed class FiniteStoragePrecommitCohortBusy(FiniteStoragePrecommit original) : OriginalCanonicalDirtyBusy(
        "Metal finite storage ORIGINAL native cohort is busy before publication effects.")
    {
        internal readonly FiniteStoragePrecommit Original = original;
    }

    private static FiniteStoragePrecommit CaptureFiniteStoragePrecommit(FiniteStorageWriteback receipt)
    {
        lock (_gate)
        {
            ValidateFiniteStorageReceiptOwnerLocked(receipt);
            if (!receipt.SuccessfullyCompleted || !receipt.Encoded || receipt.CommandBuffer == 0 || receipt.Staging == 0 ||
                _guestImages.Count > 128 || _selectedStorageMipOwners.Count > 128 || _selectedStorageMipImages.Count > 128 ||
                _selectedNativeOriginalProofs.Count > 128)
                throw new InvalidOperationException("Metal finite storage precommit requires its bounded completed producer receipt.");
            return new(receipt, _guestImages.Select(pair => CaptureFiniteStoragePrecommitImage(pair.Key, pair.Value)).ToArray(),
                _selectedStorageMipOwners.ToArray(), _selectedStorageMipImages.ToArray(), _selectedNativeOriginalProofs.ToArray(),
                _finiteStorageRetiredOriginCount, _finiteRetiredDisplayOriginCount);
        }
    }

    private static FiniteStoragePrecommitImage CaptureFiniteStoragePrecommitImage(ulong address, GuestImage image) =>
        new(address, image, image.StorageCpuOrigin, image.StorageCpuLayout, image.Texture, image.ContentVersion,
            image.Width, image.Height, image.Depth, image.ArrayLength, image.TextureType, image.Format,
            image.Initialized, image.GpuWritten);

    private static void ValidateFiniteStoragePrecommitOwnersLocked(FiniteStoragePrecommit original)
    {
        ValidateFiniteStorageReceiptOwnerLocked(original.Receipt);
        if (!original.Receipt.SuccessfullyCompleted || !original.Receipt.Encoded || original.Receipt.CommandBuffer == 0 ||
            original.Receipt.Staging == 0 || original.Receipt.Staging != original.Staging ||
            original.Receipt.CommandBuffer != original.CommandBuffer || _guestImages.Count != original.Images.Length ||
            _selectedStorageMipOwners.Count != original.Selected.Length || _selectedStorageMipImages.Count != original.SelectedImages.Length ||
            _selectedNativeOriginalProofs.Count != original.SelectedProofs.Length ||
            _finiteStorageRetiredOriginCount != original.RetiredOrigins || _finiteRetiredDisplayOriginCount != original.RetiredDisplays)
            throw new InvalidOperationException("Metal finite storage precommit ORIGINAL cohort inventory changed.");
        foreach (var saved in original.Images)
        {
            if (!_guestImages.TryGetValue(saved.Address, out var image) || !ReferenceEquals(image, saved.Image) ||
                !FiniteStoragePrecommitImageMatches(saved, image))
                throw new InvalidOperationException("Metal finite storage precommit ORIGINAL native owner/version changed.");
            if (saved.Origin?.VolumeWatch is not null && !IsSingletonVolumeNativeOwnerMetadataCurrentLocked(image, saved.Origin))
                throw new InvalidOperationException("Metal finite storage precommit ORIGINAL volume watch owner changed.");
        }
        foreach (var pair in original.Selected)
            if (!_selectedStorageMipOwners.TryGetValue(pair.Key, out var value) || !ReferenceEquals(value, pair.Value))
                throw new InvalidOperationException("Metal finite storage precommit ORIGINAL selected owner changed.");
        foreach (var pair in original.SelectedImages)
            if (!_selectedStorageMipImages.TryGetValue(pair.Key, out var value) || !ReferenceEquals(value, pair.Value))
                throw new InvalidOperationException("Metal finite storage precommit ORIGINAL selected image changed.");
        foreach (var pair in original.SelectedProofs)
            if (!_selectedNativeOriginalProofs.TryGetValue(pair.Key, out var value) || !ReferenceEquals(value, pair.Value))
                throw new InvalidOperationException("Metal finite storage precommit ORIGINAL selected proof changed.");
        foreach (var image in original.SelectedNativeImages)
            if (!FiniteStoragePrecommitImageMatches(image, image.Image))
                throw new InvalidOperationException("Metal finite storage precommit ORIGINAL selected native version changed.");
        foreach (var saved in original.SelectedNativeVersions)
            if (saved.Proof.Version != saved.Version || saved.Proof.ProducerSequence != saved.Producer)
                throw new InvalidOperationException("Metal finite storage precommit ORIGINAL selected producer changed.");
    }

    private static bool FiniteStoragePrecommitImageMatches(FiniteStoragePrecommitImage saved, GuestImage image) =>
        ReferenceEquals(image.StorageCpuOrigin, saved.Origin) && ReferenceEquals(image.StorageCpuLayout, saved.Layout) &&
        image.Texture == saved.Texture && image.ContentVersion == saved.Version && image.Width == saved.Width &&
        image.Height == saved.Height && image.Depth == saved.Depth && image.ArrayLength == saved.ArrayLength &&
        image.TextureType == saved.TextureType && image.Format == saved.Format && image.Initialized == saved.Initialized &&
        image.GpuWritten == saved.GpuWritten;

    private static void ValidateFiniteStoragePrecommit(FiniteStoragePrecommit original, StorageWatchReservation reservation = default)
    {
        if (Monitor.IsEntered(_gate)) throw new InvalidOperationException("Finite storage precommit content validation requires a released presenter gate.");
        lock (_gate) ValidateFiniteStoragePrecommitOwnersLocked(original);
        SingletonVolumeOriginalBusy? busy = null;
        // Check ALL retained originals, even when the first is transiently Busy.
        // A stale/overlapping/unknown second owner must not hide behind the first.
        foreach (var image in original.Images)
        {
            if (image.Origin?.VolumeWatch is null) continue;
            try { ValidateSingletonVolumeOriginalContentWithReservation(image.Origin, reservation, classifyOrdinaryBusy: true); }
            catch (SingletonVolumeOriginalBusy refusal) { busy ??= refusal; }
        }
        ValidateFiniteStorageOwnOrigin(original.Receipt.Origin, original.Receipt.Descriptor);
        CpuMappedReadWatchStatus status;
        string reason;
        FiniteStorageCohortOffender? offender;
        lock (_gate)
        {
            ValidateFiniteStoragePrecommitOwnersLocked(original);
            status = InspectFiniteStorageNativeCohortStatusLocked(original.Receipt.Image, original.Receipt.Origin,
                out reason, out offender, captureOffender: _rgbaStorageRuntimePublicationRequested, reservation, classifyBusy: true);
        }
        if (status == CpuMappedReadWatchStatus.Stale)
        {
            var message = "Metal finite canonical publication native cohort is unavailable: " + reason;
            if (offender is { } captured) throw new FiniteStorageCohortRefusal(message, captured);
            throw new NotSupportedException(message);
        }
        if (busy is not null) throw busy;
        if (status == CpuMappedReadWatchStatus.Busy) throw new FiniteStoragePrecommitCohortBusy(original);
    }

    private static void WaitFiniteStoragePrecommitBusy(FiniteStoragePrecommit original, OriginalCanonicalDirtyBusy busy)
    {
        if (Monitor.IsEntered(_gate)) throw new InvalidOperationException("Finite storage precommit Busy wait requires a released presenter gate.");
        lock (_gate)
        {
            ValidateFiniteStoragePrecommitOwnersLocked(original);
            if (busy is SingletonVolumeOriginalBusy volume)
            {
                if (!original.Images.Any(image => ReferenceEquals(image.Origin, volume.Original)))
                    throw new InvalidOperationException("Finite storage precommit Busy cannot adopt a different ORIGINAL volume.");
            }
            else if (busy is not FiniteStoragePrecommitCohortBusy cohort || !ReferenceEquals(cohort.Original, original))
                throw new InvalidOperationException("Finite storage precommit cannot retry an unrelated Busy reason.");
        }
        original.FirstBusy ??= busy;
        if (original.BusyStarted == 0) original.BusyStarted = Stopwatch.GetTimestamp();
        if (Stopwatch.GetElapsedTime(original.BusyStarted).TotalMilliseconds >= FiniteStoragePrecommitBusyMilliseconds)
        {
            try { original.FirstBusy.Data["FiniteStoragePrecommitWait"] = "500ms bound; ORIGINAL GPU debt remains pending; TryCommit not called"; } catch { }
            ExceptionDispatchInfo.Capture(original.FirstBusy).Throw();
        }
        _finiteStoragePrecommitBusyForControl?.Invoke();
        Thread.Sleep(1); // all original objects retained, no presenter/provider/PVM gate held
    }

    private static CpuMappedPublication PrepareFiniteStoragePrecommit(FiniteStoragePrecommit original,
        ICpuMappedPublicationSource publisher, CpuMappedWriteRun[] runs)
    {
        while (true)
        {
            try
            {
                _finiteStorageBeforePrecommitValidationForControl?.Invoke(false);
                ValidateFiniteStoragePrecommit(original);
            }
            catch (OriginalCanonicalDirtyBusy busy)
            {
                WaitFiniteStoragePrecommitBusy(original, busy);
                continue;
            }
            CpuMappedPublication? transaction = null;
            try
            {
                var prepared = publisher.TryPrepareMappedPublication(original.Receipt.Origin.Mapping, runs, out transaction);
                if (prepared != CpuMappedPublicationPreparationResult.Prepared || transaction is null)
                    throw new InvalidOperationException($"Metal finite storage canonical publication preparation was refused: result={prepared}; GPU debt remains pending.");
                _finiteStorageBeforePrecommitValidationForControl?.Invoke(true);
                try { ValidateFiniteStoragePrecommit(original, new StorageWatchReservation(transaction)); }
                catch (OriginalCanonicalDirtyBusy busy)
                {
                    // This exact ticket has NEVER called TryCommit. Consume it
                    // before cancellation; unknown release must stop, not retry.
                    var cancelled = transaction; transaction = null;
                    try { cancelled.Dispose(); }
                    catch (Exception cleanup)
                    {
                        lock (_gate) { _textureSourceCleanupFailure ??= cleanup; original.Receipt.Debt.Failed = true; }
                        try { busy.Data["FiniteStoragePrecommitCancellation"] = cleanup; } catch { }
                        ExceptionDispatchInfo.Capture(busy).Throw();
                    }
                    WaitFiniteStoragePrecommitBusy(original, busy);
                    continue;
                }
                var accepted = transaction; transaction = null;
                return accepted;
            }
            catch (Exception failure)
            {
                CancelFiniteStoragePublication(transaction, failure);
                throw;
            }
        }
    }
}
