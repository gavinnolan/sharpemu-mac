// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
using System.Diagnostics;
using System.Runtime.ExceptionServices;
using SharpEmu.HLE;

namespace SharpEmu.Gpu.Metal;

internal static partial class MetalVideoPresenter
{
    private static Action<object>? _finiteStorageAppliedValidationBusyForControl;
    private static Action<object>? _finiteStorageBeforeAppliedValidationForControl;
    private static Action<object>? _finiteStorageAcquiredValidationForControl;
    // Reserved BEFORE native texture retention/dispatch. Fixed slots root the
    // exact receipt throughout completion and retain it on deadline/unknown.
    private static readonly FiniteStorageWriteback?[] _finiteStorageWritebackRoots =
        new FiniteStorageWriteback?[MaximumFiniteStorageWritebacks];

    private static void ReserveFiniteStorageWritebackRootLocked(FiniteStorageWriteback receipt)
    {
        for (var index = 0; index < _finiteStorageWritebackRoots.Length; index++)
            if (_finiteStorageWritebackRoots[index] is null)
            {
                _finiteStorageWritebackRoots[index] = receipt;
                receipt.LifetimeSlot = index;
                return;
            }
        throw new InvalidOperationException("Finite storage retained receipt inventory is exhausted before native effects.");
    }

    private static void ReleaseFiniteStorageWritebackRoot(FiniteStorageWriteback receipt)
    {
        lock (_gate)
        {
            if (receipt.LifetimeSlot < 0) return; // never registered scalar fixture
            if (receipt.LifetimeSlot >= _finiteStorageWritebackRoots.Length ||
                !ReferenceEquals(_finiteStorageWritebackRoots[receipt.LifetimeSlot], receipt))
                throw new InvalidOperationException("Finite storage exact retained receipt root was lost.");
            _finiteStorageWritebackRoots[receipt.LifetimeSlot] = null;
            receipt.LifetimeSlot = -1;
        }
    }

    private static void QuarantineFiniteStorageAppliedValidation(FiniteStorageWriteback receipt, Exception failure)
    {
        lock (_gate)
        {
            // Root already exists before any Applied effect. No failure-path
            // allocation/Exception.Data attachment can lose the only owner.
            receipt.ValidationQuarantined = true;
            receipt.Debt.Failed = true; receipt.Debt.Pending = true;
            _textureSourceCleanupFailure ??= failure;
        }
    }

    // Refusal provenance only. The provider CAS did NOT certify history/watch
    // completion; only an acquired cancel-only reservation can do that later.
    private sealed class FiniteStorageAppliedValidationBusy(CpuMappedPublicationValidationRefusal refusal) :
        InvalidOperationException("Finite canonical ORIGINAL Applied validation admission is busy.")
    { internal readonly CpuMappedPublicationValidationRefusal Refusal = refusal; }

    private sealed class FiniteStorageAppliedValidation(FiniteStorageWriteback receipt,
        StorageCpuSeedOrigin next, FiniteStoragePreencode phase, FiniteStoragePrecommitImage image)
    {
        internal readonly FiniteStorageWriteback Receipt = receipt;
        internal readonly StorageCpuSeedOrigin Next = next;
        internal readonly FiniteStoragePreencode Phase = phase;
        internal readonly FiniteStoragePrecommitImage Image = image;
        internal readonly nint CommandBuffer = receipt.CommandBuffer, Staging = receipt.Staging;
        internal readonly StorageCpuSeedOrigin? PublishedOrigin = receipt.PublishedOrigin;
        internal Exception? FirstBusy;
        internal long Started;
    }

    private static FiniteStorageAppliedValidation CaptureFiniteStorageAppliedValidation(FiniteStorageWriteback receipt,
        StorageCpuSeedOrigin next)
    {
        lock (_gate)
        {
            if (receipt.OriginalPhase is not { } phase || !ReferenceEquals(phase.Receipt, receipt) ||
                !ReferenceEquals(receipt.AppliedObservation, next.Mapping) || ReferenceEquals(next.Mapping, receipt.Origin.Mapping) ||
                !ReferenceEquals(next.Layout, receipt.Origin.Layout) || !ReferenceEquals(next.Memory, receipt.Origin.Memory) ||
                next.Session != receipt.Origin.Session || next.Observation != receipt.Origin.Observation ||
                (receipt.PublishedOrigin is not null && !ReferenceEquals(receipt.PublishedOrigin, next)))
                throw new InvalidOperationException("Finite validation cannot adopt a different Applied mapping/origin.");
            var expectedVersion = receipt.PublishedOrigin is null ? receipt.Version : checked(receipt.Version + 1);
            if (receipt.Image.ContentVersion != expectedVersion || !ReferenceEquals(receipt.Image.StorageCpuOrigin, receipt.Origin))
                throw new InvalidOperationException("Finite validation lost its exact pre-finalization native version.");
            var original = new FiniteStorageAppliedValidation(receipt, next, phase,
                CaptureFiniteStoragePrecommitImage(receipt.Descriptor.Address, receipt.Image));
            ValidateFiniteStorageAppliedOwnersLocked(original);
            return original;
        }
    }

    private static void ValidateFiniteStorageAppliedOwnersLocked(FiniteStorageAppliedValidation saved)
    {
        var receipt = saved.Receipt; var phase = saved.Phase;
        if (_closed || _guestWorkFailure is not null || _textureSourceCleanupFailure is not null ||
            _typedCpuSyncCleanupFailure is not null || !receipt.Published || !receipt.SuccessfullyCompleted ||
            !receipt.Encoded || receipt.Consumed || !phase.ComputeEncodingEnded || !phase.ReadbackAllocationStarted ||
            receipt.LifetimeSlot < 0 || receipt.LifetimeSlot >= _finiteStorageWritebackRoots.Length ||
            !ReferenceEquals(_finiteStorageWritebackRoots[receipt.LifetimeSlot], receipt) ||
            !ReferenceEquals(receipt.OriginalPhase, phase) || !ReferenceEquals(receipt.AppliedObservation, saved.Next.Mapping) ||
            !ReferenceEquals(receipt.PublishedOrigin, saved.PublishedOrigin) ||
            receipt.CommandBuffer == 0 || receipt.CommandBuffer != saved.CommandBuffer || receipt.CommandBuffer != phase.CommandBuffer ||
            receipt.Staging == 0 || receipt.Staging != saved.Staging || receipt.Texture == 0 || receipt.Texture != saved.Image.Texture ||
            !ReferenceEquals(_guestMemory, saved.Next.Memory) || saved.Next.Session != CurrentStorageCpuSeedSession ||
            receipt.Debt.Failed || !receipt.Debt.Pending || receipt.Debt.Work != receipt.Work || receipt.Work != phase.Sequence ||
            _executingGuestWorkSequence != phase.Sequence || _takenGuestWorkSequence != phase.Sequence ||
            !ReferenceEquals(_takenDepthCubeProducerWork, phase.Dispatch) || _submittingGuestQueue != phase.GuestQueue ||
            !_guestImages.TryGetValue(saved.Image.Address, out var own) || !ReferenceEquals(own, saved.Image.Image) ||
            !FiniteStoragePrecommitImageMatches(saved.Image, own) ||
            _guestImages.Count != phase.Images.Length || _selectedStorageMipOwners.Count != phase.Selected.Length ||
            _selectedStorageMipImages.Count != phase.SelectedImages.Length || _selectedNativeOriginalProofs.Count != phase.SelectedProofs.Length ||
            _finiteStorageRetiredOriginCount != phase.RetiredOrigins || _finiteRetiredDisplayOriginCount != phase.RetiredDisplays)
            throw new InvalidOperationException("Finite Applied validation ORIGINAL receipt/cohort/queue changed.");
        foreach (var image in phase.Images)
        {
            if (ReferenceEquals(image.Image, receipt.Image)) continue; // exact own phase snapshot above
            if (!_guestImages.TryGetValue(image.Address, out var current) || !ReferenceEquals(current, image.Image) ||
                !FiniteStoragePrecommitImageMatches(image, current) || image.Origin?.VolumeWatch is not null &&
                !IsSingletonVolumeNativeOwnerMetadataCurrentLocked(current, image.Origin))
                throw new InvalidOperationException("Finite Applied validation ORIGINAL sibling owner/version changed.");
        }
        foreach (var pair in phase.Selected)
            if (!_selectedStorageMipOwners.TryGetValue(pair.Key, out var current) || !ReferenceEquals(current, pair.Value))
                throw new InvalidOperationException("Finite Applied validation ORIGINAL selected owner changed.");
        foreach (var pair in phase.SelectedImages)
            if (!_selectedStorageMipImages.TryGetValue(pair.Key, out var current) || !ReferenceEquals(current, pair.Value))
                throw new InvalidOperationException("Finite Applied validation ORIGINAL selected image changed.");
        foreach (var pair in phase.SelectedProofs)
            if (!_selectedNativeOriginalProofs.TryGetValue(pair.Key, out var current) || !ReferenceEquals(current, pair.Value))
                throw new InvalidOperationException("Finite Applied validation ORIGINAL selected proof changed.");
        foreach (var image in phase.SelectedNativeImages)
            if (!FiniteStoragePrecommitImageMatches(image, image.Image))
                throw new InvalidOperationException("Finite Applied validation ORIGINAL selected native version changed.");
        foreach (var proof in phase.SelectedNativeVersions)
            if (proof.Proof.Version != proof.Version || proof.Proof.ProducerSequence != proof.Producer)
                throw new InvalidOperationException("Finite Applied validation ORIGINAL selected producer changed.");
    }

    private sealed class FiniteStorageAppliedCohortBusy(FiniteStorageAppliedValidation saved) : OriginalCanonicalDirtyBusy(
        "Finite Applied validation exact ORIGINAL cohort is busy before any finalization callback.")
    { internal readonly FiniteStorageAppliedValidation Saved = saved; }

    private static void ValidateFiniteStorageAppliedOriginals(FiniteStorageAppliedValidation saved,
        StorageWatchReservation reservation = default, bool requireCurrent = false)
    {
        if (Monitor.IsEntered(_gate)) throw new InvalidOperationException("Applied original checks require a released presenter gate.");
        lock (_gate) ValidateFiniteStorageAppliedOwnersLocked(saved);
        // Buffers were already Applied before this image. Their input registry
        // entries are consumed; validate the SAME known completed continuations.
        ValidateFiniteStorageCompletedBuffers(saved.Phase);
        // Validate every original even if a healthy watch is Busy. Such Busy
        // does NOT authorize callback/publication; acquired validation below
        // retains all existing strict original-content/currentness predicates.
        SingletonVolumeOriginalBusy? firstBusy = null;
        foreach (var image in saved.Phase.Images)
        {
            if (image.Origin?.VolumeWatch is null) continue;
            try { ValidateSingletonVolumeOriginalContentWithReservation(image.Origin, reservation, classifyOrdinaryBusy: true); }
            catch (SingletonVolumeOriginalBusy busy) { firstBusy ??= busy; }
        }
        ValidateFiniteStorageOwnOrigin(saved.Next, saved.Receipt.Descriptor);
        CpuMappedReadWatchStatus status;
        lock (_gate)
        {
            ValidateFiniteStorageAppliedOwnersLocked(saved);
            status = InspectFiniteStorageNativeCohortStatusLocked(saved.Receipt.Image, saved.Next,
                out var reason, out var offender, captureOffender: _rgbaStorageRuntimePublicationRequested,
                reservation: reservation, classifyBusy: true);
            if (status == CpuMappedReadWatchStatus.Stale)
            {
                var message = "Finite Applied validation ORIGINAL physical cohort is unavailable: " + reason;
                if (offender is { } captured) throw new FiniteStorageCohortRefusal(message, captured);
                throw new NotSupportedException(message);
            }
        }
        // Every original and physical inventory member was checked above.
        // Only these saved first-refusal types can cancel an unstarted ticket.
        if (requireCurrent && firstBusy is not null) throw firstBusy;
        if (requireCurrent && status == CpuMappedReadWatchStatus.Busy) throw new FiniteStorageAppliedCohortBusy(saved);
    }

    private static CpuMappedPublicationValidation PrepareFiniteStorageAppliedValidation(FiniteStorageWriteback receipt,
        StorageCpuSeedOrigin next)
    {
        var saved = CaptureFiniteStorageAppliedValidation(receipt, next);
        if (next.Memory is not ICpuMappedPublicationSource source)
            throw new NotSupportedException("Finite Applied validation has no participating provider.");
        _finiteStorageBeforeAppliedValidationForControl?.Invoke(receipt);
        while (true)
        {
            var result = source.TryPrepareMappedPublicationValidation(next.Mapping, out var validation, out var refusal);
            if (result == CpuMappedPublicationPreparationResult.Prepared && validation is not null)
            {
                try
                {
                    lock (_gate) ValidateFiniteStorageAppliedOwnersLocked(saved);
                    _finiteStorageAcquiredValidationForControl?.Invoke(receipt);
                    if (!validation.IsCurrent)
                        throw new InvalidOperationException("Finite Applied ORIGINAL acquired validation changed before checks.");
                    ValidateFiniteStorageAppliedOriginals(saved, new StorageWatchReservation(validation), requireCurrent: true);
                    if (!validation.IsCurrent)
                        throw new InvalidOperationException("Finite Applied ORIGINAL acquired validation changed after checks.");
                    return validation;
                }
                catch (OriginalCanonicalDirtyBusy busy) when (
                    busy is SingletonVolumeOriginalBusy volume && saved.Phase.Images.Any(image => ReferenceEquals(image.Origin, volume.Original)) ||
                    busy is FiniteStorageAppliedCohortBusy cohort && ReferenceEquals(cohort.Saved, saved))
                {
                    var firstBusy = saved.FirstBusy ??= busy;
                    if (saved.Started == 0) saved.Started = Stopwatch.GetTimestamp();
                    // No callback has been invoked. Consume THIS cancel-only
                    // ticket once; unknown release forbids wait/reprepare.
                    try { validation.Dispose(); }
                    catch (Exception cleanup)
                    {
                        QuarantineFiniteStorageAppliedValidation(receipt, firstBusy);
                        try { firstBusy.Data["FiniteStorageAppliedValidationCleanup"] = cleanup; } catch { }
                        ExceptionDispatchInfo.Capture(firstBusy).Throw();
                        throw;
                    }
                    WaitFiniteStorageAppliedValidation(saved, source);
                    continue;
                }
                catch (Exception primary)
                {
                    try { validation.Dispose(); }
                    catch (Exception cleanup)
                    {
                        QuarantineFiniteStorageAppliedValidation(receipt, primary);
                        try { primary.Data["FiniteStorageAppliedValidationCleanup"] = cleanup; } catch { }
                    }
                    throw;
                }
            }
            // Any acquired/foreign uncertain ticket is consumed before failure.
            // No catch here may repeat cancellation, an effect or finalization.
            try { validation?.Dispose(); }
            catch (Exception cleanup) { QuarantineFiniteStorageAppliedValidation(receipt, cleanup); throw; }
            if (result != CpuMappedPublicationPreparationResult.Rejected || validation is not null ||
                refusal.Kind != CpuMappedPublicationValidationRefusalKind.AdmissionBusy || refusal.RejectedAdmission <= 0)
                throw new InvalidOperationException($"Finite canonical physical-history validation was refused: result={result}.");
            saved.FirstBusy ??= new FiniteStorageAppliedValidationBusy(refusal);
            if (saved.Started == 0) saved.Started = Stopwatch.GetTimestamp();
            WaitFiniteStorageAppliedValidation(saved, source);
        }
    }

    private static void WaitFiniteStorageAppliedValidation(FiniteStorageAppliedValidation saved,
        ICpuMappedPublicationSource source)
    {
        var firstBusy = saved.FirstBusy ?? throw new InvalidOperationException("Applied validation has no saved first Busy refusal.");
        ValidateFiniteStorageAppliedOriginals(saved); // no foreign ticket after known cancellation
        if (!source.CanWaitForMappedPublicationValidation || Monitor.IsEntered(_gate))
            throw new InvalidOperationException("Finite Applied validation cannot wait under inherited or unknown gates.");
        if (Stopwatch.GetElapsedTime(saved.Started).TotalMilliseconds >= 500)
        {
            try { firstBusy.Data["FiniteStorageAppliedValidationWait"] = "500ms; Applied is never repeated; original debt remains pending"; } catch { }
            QuarantineFiniteStorageAppliedValidation(saved.Receipt, firstBusy);
            ExceptionDispatchInfo.Capture(firstBusy).Throw();
        }
        _finiteStorageAppliedValidationBusyForControl?.Invoke(saved.Receipt);
        Thread.Sleep(1);
    }
}
