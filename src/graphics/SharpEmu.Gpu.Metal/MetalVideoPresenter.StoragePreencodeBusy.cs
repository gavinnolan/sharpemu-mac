// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
using System.Diagnostics;
using System.Runtime.ExceptionServices;
using SharpEmu.HLE;
using SharpEmu.Libs.Gpu;

namespace SharpEmu.Gpu.Metal;

internal static partial class MetalVideoPresenter
{
    // Ordinary race controls only. Neither callback supplies admission,
    // content, mapping, ownership or completion; both run outside the gate.
    private static Action<object>? _finiteStorageBeforePreencodeValidationForControl;
    private static Action? _finiteStoragePreencodeBusyForControl;
    private const int FiniteStoragePreencodeBusyMilliseconds = 500;

    // This is NOT a completed producer or a publication ticket. Every original
    // is frozen before the first check and before creating the compute encoder.
    private sealed class FiniteStoragePreencode(ComputeGuestDispatch dispatch, FiniteStorageWriteback receipt,
        nint commandBuffer, nint queue, long sequence, GuestQueueIdentity guestQueue,
        FiniteStoragePrecommitImage[] images,
        KeyValuePair<SelectedStorageMipKey, SelectedStorageMipAssociation>[] selected,
        KeyValuePair<SelectedStorageMipKey, GuestImage>[] selectedImages,
        KeyValuePair<SelectedStorageMipKey, SelectedNativeOriginalProof>[] selectedProofs,
        FiniteStorageBufferOrigin[] buffers, FiniteStorageBufferPublication[] bufferPublications,
        int retiredOrigins, int retiredDisplays)
    {
        internal readonly ComputeGuestDispatch Dispatch = dispatch;
        internal readonly FiniteStorageWriteback Receipt = receipt;
        internal readonly nint CommandBuffer = commandBuffer, Queue = queue;
        internal readonly long Sequence = sequence;
        internal readonly GuestQueueIdentity GuestQueue = guestQueue;
        internal readonly FiniteStoragePrecommitImage[] Images = images;
        internal readonly KeyValuePair<SelectedStorageMipKey, SelectedStorageMipAssociation>[] Selected = selected;
        internal readonly KeyValuePair<SelectedStorageMipKey, GuestImage>[] SelectedImages = selectedImages;
        internal readonly KeyValuePair<SelectedStorageMipKey, SelectedNativeOriginalProof>[] SelectedProofs = selectedProofs;
        internal readonly FiniteStoragePrecommitImage[] SelectedNativeImages = selectedImages
            .Select(pair => CaptureFiniteStoragePrecommitImage(pair.Key.Resource.Address, pair.Value)).ToArray();
        internal readonly (SelectedNativeOriginalProof Proof, int Version, long Producer)[] SelectedNativeVersions = selectedProofs
            .Select(pair => (pair.Value, pair.Value.Version, pair.Value.ProducerSequence)).ToArray();
        internal readonly FiniteStorageBufferOrigin[] Buffers = buffers;
        internal readonly FiniteStorageBufferPublication[] BufferPublications = bufferPublications;
        internal readonly int RetiredOrigins = retiredOrigins, RetiredDisplays = retiredDisplays;
        internal OriginalCanonicalDirtyBusy? FirstBusy;
        internal long BusyStarted;
        internal bool ComputeEncodingEnded, ReadbackAllocationStarted;
        internal OriginalCanonicalDirtyBusy? FirstReadbackBusy;
        internal long ReadbackBusyStarted;
    }

    private sealed class FiniteStoragePreencodeCohortBusy(FiniteStoragePreencode original) : OriginalCanonicalDirtyBusy(
        "Metal finite storage ORIGINAL healthy native cohort is busy before compute encoding.")
    { internal readonly FiniteStoragePreencode Original = original; }

    private static FiniteStoragePreencode CaptureFiniteStoragePreencode(ComputeGuestDispatch dispatch,
        nint commandBuffer, nint queue, List<FiniteStorageWriteback>? receipts)
    {
        var buffers = ValidateFiniteStorageBufferOriginalsAgainstImage(receipts, dispatch.GlobalMemoryBuffers);
        // Capture provider-bound input slots before the presenter gate. The
        // constructor only stores these exact originals; later checks stay strict.
        var bufferPublications = CaptureFiniteStorageBufferPublications(dispatch.GlobalMemoryBuffers, buffers);
        var receipt = receipts![0];
        lock (_gate)
        {
            ValidateFiniteStorageReceiptOwnerLocked(receipt);
            if (receipt.Encoded || receipt.SuccessfullyCompleted || receipt.CommandBuffer != 0 || receipt.Staging != 0 ||
                commandBuffer == 0 || queue == 0 || !_batchOpen || _batchCommandBuffer != commandBuffer || _batchQueue != queue ||
                _executingGuestWorkSequence == 0 || _executingGuestWorkSequence != _takenGuestWorkSequence ||
                receipt.Work != _executingGuestWorkSequence ||
                !ReferenceEquals(_takenDepthCubeProducerWork, dispatch) || _submittingGuestQueue is not { } guestQueue ||
                _guestImages.Count > 128 || _selectedStorageMipOwners.Count > 128 || _selectedStorageMipImages.Count > 128 ||
                _selectedNativeOriginalProofs.Count > 128)
                throw new InvalidOperationException("Finite storage pre-encode continuation lost its unencoded exact taken receipt/batch.");
            return new(dispatch, receipt, commandBuffer, queue, _executingGuestWorkSequence, guestQueue,
                _guestImages.Select(pair => CaptureFiniteStoragePrecommitImage(pair.Key, pair.Value)).ToArray(),
                _selectedStorageMipOwners.ToArray(), _selectedStorageMipImages.ToArray(), _selectedNativeOriginalProofs.ToArray(),
                buffers, bufferPublications, _finiteStorageRetiredOriginCount, _finiteRetiredDisplayOriginCount);
        }
    }

    private static void ValidateFiniteStoragePreencodeOwnersLocked(FiniteStoragePreencode original) =>
        ValidateFiniteStoragePhaseOwnersLocked(original, afterCompute: false);

    private static void ValidateFiniteStoragePhaseOwnersLocked(FiniteStoragePreencode original, bool afterCompute, nint allocatedStaging = 0)
    {
        var receipt = original.Receipt;
        ValidateFiniteStorageReceiptOwnerLocked(receipt);
        if (original.ComputeEncodingEnded != afterCompute || original.ReadbackAllocationStarted != (allocatedStaging != 0) ||
            receipt.Encoded || receipt.SuccessfullyCompleted ||
            (allocatedStaging == 0 ? receipt.CommandBuffer != 0 || receipt.Staging != 0 :
                !afterCompute || receipt.CommandBuffer != original.CommandBuffer || receipt.Staging != allocatedStaging) ||
            !_batchOpen || _batchCommandBuffer != original.CommandBuffer || _batchQueue != original.Queue ||
            _executingGuestWorkSequence != original.Sequence || _takenGuestWorkSequence != original.Sequence ||
            receipt.Work != original.Sequence ||
            !ReferenceEquals(_takenDepthCubeProducerWork, original.Dispatch) || _submittingGuestQueue != original.GuestQueue ||
            _guestImages.Count != original.Images.Length || _selectedStorageMipOwners.Count != original.Selected.Length ||
            _selectedStorageMipImages.Count != original.SelectedImages.Length || _selectedNativeOriginalProofs.Count != original.SelectedProofs.Length ||
            _finiteStorageRetiredOriginCount != original.RetiredOrigins || _finiteRetiredDisplayOriginCount != original.RetiredDisplays)
            throw new InvalidOperationException("Finite storage pre-encode ORIGINAL receipt/cohort/queue changed.");
        foreach (var saved in original.Images)
            ValidateFiniteStoragePreencodeSavedImageLocked(saved);
        foreach (var pair in original.Selected)
            if (!_selectedStorageMipOwners.TryGetValue(pair.Key, out var value) || !ReferenceEquals(value, pair.Value))
                throw new InvalidOperationException("Finite storage pre-encode ORIGINAL selected owner changed.");
        foreach (var pair in original.SelectedImages)
            if (!_selectedStorageMipImages.TryGetValue(pair.Key, out var value) || !ReferenceEquals(value, pair.Value))
                throw new InvalidOperationException("Finite storage pre-encode ORIGINAL selected image changed.");
        foreach (var pair in original.SelectedProofs)
            if (!_selectedNativeOriginalProofs.TryGetValue(pair.Key, out var value) || !ReferenceEquals(value, pair.Value))
                throw new InvalidOperationException("Finite storage pre-encode ORIGINAL selected proof changed.");
        foreach (var saved in original.SelectedNativeImages)
            if (!FiniteStoragePrecommitImageMatches(saved, saved.Image))
                throw new InvalidOperationException("Finite storage pre-encode ORIGINAL selected native version changed.");
        foreach (var saved in original.SelectedNativeVersions)
            if (saved.Proof.Version != saved.Version || saved.Proof.ProducerSequence != saved.Producer)
                throw new InvalidOperationException("Finite storage pre-encode ORIGINAL selected producer changed.");
    }

    private readonly record struct FiniteStoragePreencodeOwnerDiagnostic(ulong Address, string Branch, string Guard,
        ulong? SavedScalar = null, ulong? ActualScalar = null);

    private sealed class FiniteStoragePreencodeOwnerRefusal(FiniteStoragePreencodeOwnerDiagnostic first)
        : InvalidOperationException("Finite storage pre-encode ORIGINAL native owner/version changed.")
    {
        internal readonly FiniteStoragePreencodeOwnerDiagnostic First = first;
    }

    private static void ValidateFiniteStoragePreencodeSavedImageLocked(FiniteStoragePrecommitImage saved) =>
        ValidateFiniteStoragePreencodeSavedImageWithDiagnosticsLocked(saved, _volumeOwnerDeclineDiagnostics);

    private static void ValidateFiniteStoragePreencodeSavedImageWithDiagnosticsLocked(FiniteStoragePrecommitImage saved, bool captureFirstRefusal)
    {
        if (!captureFirstRefusal)
        {
            // Cached-off retains the original predicate, reads and exception.
            if (!_guestImages.TryGetValue(saved.Address, out var image) || !ReferenceEquals(image, saved.Image) ||
                !FiniteStoragePrecommitImageMatches(saved, image) || saved.Origin?.VolumeWatch is not null &&
                !IsSingletonVolumeNativeOwnerMetadataCurrentLocked(image, saved.Origin))
                throw new InvalidOperationException("Finite storage pre-encode ORIGINAL native owner/version changed.");
            return;
        }
        if (InspectFiniteStoragePreencodeSavedImageLocked(saved) is { } first)
            throw new FiniteStoragePreencodeOwnerRefusal(first);
    }

    private static FiniteStoragePreencodeOwnerDiagnostic? InspectFiniteStoragePreencodeSavedImageLocked(FiniteStoragePrecommitImage saved)
    {
        // Alternative to ONE original check. Each reached field is sampled
        // once in the old AND/OR order; no later tuple/watch/status resampling.
        if (!_guestImages.TryGetValue(saved.Address, out var image)) return new(saved.Address, "registry", "missing");
        if (!ReferenceEquals(image, saved.Image)) return new(saved.Address, "registry", "image-identity");
        if (!ReferenceEquals(image.StorageCpuOrigin, saved.Origin)) return new(saved.Address, "tuple", "image-original");
        if (!ReferenceEquals(image.StorageCpuLayout, saved.Layout)) return new(saved.Address, "tuple", "layout");
        var texture = image.Texture;
        if (texture != saved.Texture) return new(saved.Address, "tuple", "texture", (ulong)(nuint)saved.Texture, (ulong)(nuint)texture);
        var version = image.ContentVersion;
        if (version != saved.Version) return new(saved.Address, "tuple", "version", unchecked((ulong)saved.Version), unchecked((ulong)version));
        var width = image.Width;
        if (width != saved.Width) return new(saved.Address, "tuple", "width", saved.Width, width);
        var height = image.Height;
        if (height != saved.Height) return new(saved.Address, "tuple", "height", saved.Height, height);
        var depth = image.Depth;
        if (depth != saved.Depth) return new(saved.Address, "tuple", "depth", saved.Depth, depth);
        var layers = image.ArrayLength;
        if (layers != saved.ArrayLength) return new(saved.Address, "tuple", "layers", saved.ArrayLength, layers);
        var type = image.TextureType;
        if (type != saved.TextureType) return new(saved.Address, "tuple", "type", saved.TextureType, type);
        var format = image.Format;
        if (format != saved.Format) return new(saved.Address, "tuple", "format", (ulong)saved.Format, (ulong)format);
        var initialized = image.Initialized;
        if (initialized != saved.Initialized) return new(saved.Address, "tuple", "initialized", saved.Initialized ? 1ul : 0ul, initialized ? 1ul : 0ul);
        var written = image.GpuWritten;
        if (written != saved.GpuWritten) return new(saved.Address, "tuple", "gpu-written", saved.GpuWritten ? 1ul : 0ul, written ? 1ul : 0ul);
        if (saved.Origin?.VolumeWatch is not null && SingletonVolumeNativeMetadataGuard(image, saved.Origin) is { } guard)
            return new(saved.Address, "volume-metadata", guard);
        return null;
    }

    private static void ValidateFiniteStoragePreencode(FiniteStoragePreencode original) =>
        ValidateFiniteStoragePhaseOriginal(original, afterCompute: false);

    private static void ValidateFiniteStoragePhaseOriginal(FiniteStoragePreencode original, bool afterCompute, nint allocatedStaging = 0)
    {
        if (Monitor.IsEntered(_gate)) throw new InvalidOperationException("Finite storage pre-encode content checks require a released presenter gate.");
        lock (_gate) ValidateFiniteStoragePhaseOwnersLocked(original, afterCompute, allocatedStaging);
        var buffers = ValidateFiniteStorageBufferOriginalsAgainstImage([original.Receipt], original.Dispatch.GlobalMemoryBuffers);
        if (buffers.Length != original.Buffers.Length || buffers.Where((buffer, index) => !ReferenceEquals(buffer, original.Buffers[index])).Any())
            throw new InvalidOperationException("Finite storage pre-encode cannot adopt a younger writable buffer original.");
        SingletonVolumeOriginalBusy? busy = null;
        foreach (var image in original.Images)
        {
            if (image.Origin?.VolumeWatch is null) continue;
            try { ValidateSingletonVolumeOriginalContentWithReservation(image.Origin, default, classifyOrdinaryBusy: true); }
            catch (SingletonVolumeOriginalBusy refusal) { busy ??= refusal; }
        }
        ValidateFiniteStorageOwnOrigin(original.Receipt.Origin, original.Receipt.Descriptor);
        CpuMappedReadWatchStatus status;
        string reason;
        FiniteStorageCohortOffender? offender;
        lock (_gate)
        {
            ValidateFiniteStoragePhaseOwnersLocked(original, afterCompute, allocatedStaging);
            status = InspectFiniteStorageNativeCohortStatusLocked(original.Receipt.Image, original.Receipt.Origin,
                out reason, out offender, captureOffender: _rgbaStorageRuntimePublicationRequested, classifyBusy: true);
        }
        if (status == CpuMappedReadWatchStatus.Stale)
        {
            var message = "Finite storage pre-encode native cohort is unavailable: " + reason;
            if (offender is { } captured) throw new FiniteStorageCohortRefusal(message, captured);
            throw new NotSupportedException(message);
        }
        // Once allocation has begun, an independently classified healthy Busy
        // cannot replay allocation or dispatch. All original mapping/read/token,
        // native identity and physical-inventory checks above remain mandatory.
        // Real completion/prebuffer/precommit checks still precede publication.
        if (busy is not null && allocatedStaging == 0) throw busy;
        if (status == CpuMappedReadWatchStatus.Busy && allocatedStaging == 0)
        {
            if (afterCompute) throw new FiniteStorageReadbackCohortBusy(original);
            throw new FiniteStoragePreencodeCohortBusy(original);
        }
    }

    private static FiniteStoragePreencode ValidateFiniteStorageBeforeEncoding(ComputeGuestDispatch dispatch, nint commandBuffer,
        nint queue, List<FiniteStorageWriteback>? receipts)
    {
        var original = CaptureFiniteStoragePreencode(dispatch, commandBuffer, queue, receipts);
        while (true)
        {
            try
            {
                _finiteStorageBeforePreencodeValidationForControl?.Invoke(original.Receipt);
                ValidateFiniteStoragePreencode(original);
                return original;
            }
            catch (OriginalCanonicalDirtyBusy busy)
            {
                WaitFiniteStoragePreencodeBusy(original, busy);
            }
        }
    }

    private static void WaitFiniteStoragePreencodeBusy(FiniteStoragePreencode original, OriginalCanonicalDirtyBusy busy)
    {
        if (Monitor.IsEntered(_gate)) throw new InvalidOperationException("Finite storage pre-encode Busy wait cannot hold the presenter gate.");
        lock (_gate)
        {
            ValidateFiniteStoragePreencodeOwnersLocked(original);
            if (busy is SingletonVolumeOriginalBusy volume)
            {
                if (!original.Images.Any(image => ReferenceEquals(image.Origin, volume.Original)))
                    throw new InvalidOperationException("Pre-encode Busy cannot adopt another original volume.");
            }
            else if (busy is not FiniteStoragePreencodeCohortBusy cohort || !ReferenceEquals(cohort.Original, original))
                throw new InvalidOperationException("Pre-encode continuation cannot retry an unrelated Busy reason.");
        }
        original.FirstBusy ??= busy;
        if (original.BusyStarted == 0) original.BusyStarted = Stopwatch.GetTimestamp();
        if (Stopwatch.GetElapsedTime(original.BusyStarted).TotalMilliseconds >= FiniteStoragePreencodeBusyMilliseconds)
        {
            try { original.FirstBusy.Data["FiniteStoragePreencodeWait"] = "500ms; same ORIGINAL retained receipt; compute encoder/dispatch not created"; } catch { }
            ExceptionDispatchInfo.Capture(original.FirstBusy).Throw();
        }
        _finiteStoragePreencodeBusyForControl?.Invoke();
        Thread.Sleep(1);
    }
}
