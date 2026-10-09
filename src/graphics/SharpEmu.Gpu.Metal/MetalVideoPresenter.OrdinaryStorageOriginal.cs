// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using SharpEmu.HLE;
using SharpEmu.Libs.Gpu;
using Binding = SharpEmu.Libs.Agc.AgcExports.TranslatedImageBinding;

namespace SharpEmu.Gpu.Metal;

internal static partial class MetalVideoPresenter
{
    private static readonly bool _r32NativeOriginalRequested = Environment.GetEnvironmentVariable("SHARPEMU_METAL_R32_NATIVE_ORIGINAL") == "1";
    private const int MaximumOrdinaryStorageOriginals = 128;
    private static bool _ordinaryStorageOriginalUnknown;
    private static Exception? _ordinaryStorageOriginalCleanupFailure;
    private static readonly Dictionary<ulong, OrdinaryStorageOriginal> _ordinaryStorageOriginalByAddress = [];
    private static readonly ConditionalWeakTable<GuestDrawTexture, OrdinaryStorageOriginal> _ordinaryStorageOriginals = new();
    private static readonly ConditionalWeakTable<object, OrdinaryStorageProducer[]> _ordinaryStorageProducers = new();

    private sealed class OrdinaryStorageOriginal(MetalStoragePhysicalPlacement.Observation physical,
        ICpuMappedReadWatchSource? watched, GuestImageWriteTracker.ReadSnapshot read, CpuTextureSnapshotObservation content)
    {
        internal readonly MetalStoragePhysicalPlacement.Observation Physical = physical;
        internal readonly ICpuMappedReadWatchSource? Watched = watched;
        internal readonly GuestImageWriteTracker.ReadSnapshot Read = read;
        internal readonly CpuTextureSnapshotObservation Content = content;
        internal OrdinaryStorageProducer? LatestAccepted, LatestEncoded;
        internal GuestImage? Image;
        internal bool WatchConsumed, PreparationFailed;
    }
    private sealed class OrdinaryStorageProducer(OrdinaryStorageOriginal original, ComputeGuestDispatch work, long sequence, GuestQueueIdentity queue)
    {
        internal readonly OrdinaryStorageOriginal Original = original;
        internal readonly ComputeGuestDispatch Work = work;
        internal readonly long Sequence = sequence;
        internal readonly GuestQueueIdentity Queue = queue;
        internal GuestImage? Image;
        internal nint Texture, CommandBuffer, NativeQueue;
        internal int Version;
        internal bool Encoded, RetainsConsumed;
        internal OrdinaryStorageProducer? PreviousAccepted;
    }

    // Frontend BEFORE any initial generic CPU read and BEFORE queue admission.
    // Full write geometry is qualified by the independent normalized helper;
    // a discarded mapping probe is never the image seed or native pixels.
    internal static object? PrepareOrdinaryStorageOriginal(ICpuMemory memory, Binding binding)
    {
        if (!_r32NativeOriginalRequested || TextureSourceGraphEnabled || !binding.IsStorage ||
            (binding.Access & GuestImageAccess.Write) == 0 || binding.MipLevel != 0 || binding.Descriptor.BaseLevel != 0) return null;
        var d = binding.Descriptor;
        var prototype = new GuestDrawTexture(d.Address, d.Width, d.Height, d.Format, d.NumberType, [], IsFallback: false, IsStorage: true,
            MipLevel: binding.MipLevel, ResourceMipLevels: d.ResourceMipLevels, Pitch: d.Pitch, Depth: d.Depth,
            Type: d.Type, BaseMipLevel: d.BaseLevel, TileMode: d.TileMode, DstSelect: d.DstSelect,
            ArrayedView: binding.IsArrayed, Descriptor: binding.ResourceDescriptor?.ToArray(), Shape: binding.Shape, Access: binding.Access);
        try { _ = MetalStoragePhysicalPlacement.Describe(prototype); }
        catch (NotSupportedException) { lock (_gate) _ordinaryStorageOriginalUnknown = true; return null; }
        var session = CurrentSelectedStorageMipSeedSession;
        lock (_gate)
        {
            if (_ordinaryStorageOriginalByAddress.TryGetValue(d.Address, out var existing))
            {
                RequireOrdinaryStoragePhysicalCurrentLocked(existing, memory, prototype, session);
                return existing;
            }
            if (_guestImages.ContainsKey(d.Address) || _availableGuestImages.ContainsKey(d.Address) ||
                _guestImageWorkSequences.ContainsKey(d.Address))
                throw new NotSupportedException("Ordinary storage cannot acquire a younger original mapping for an existing or accepted image.");
        }
        ICpuMappedReadWatchSource? watched = null;
        CpuMappedCopyObservation? mapping = null;
        GuestImageWriteTracker.ReadSnapshot read = default;
        CpuTextureSnapshotObservation content = default;
        Exception? primary = null;
        try
        {
            MetalStoragePhysicalPlacement.Observation physical;
            if (IsOrdinaryR32MipZero(prototype))
            {
                watched = memory as ICpuMappedReadWatchSource ?? throw new NotSupportedException("Native R32 producer requires its ORIGINAL mapped physical read watch.");
                var placement = MetalStoragePhysicalPlacement.Describe(prototype);
                GuestImageWriteTracker.Track(placement.Address, placement.Bytes, source: "metal.r32-native.original", protect: true);
                read = GuestImageWriteTracker.BeginReadSnapshot(placement.Address, placement.Bytes, source: "metal.r32-native.original");
                // The actual selected write footprint is watched in full. These
                // discarded bytes do not authorize native-produced content.
                var discarded = new byte[checked((int)placement.Bytes)];
                var result = watched.TryReadMappedCopyWithWriteWatch(placement.Allocation, placement.AllocationBytes,
                    placement.Address, discarded, out mapping);
                if (result != CpuMappedCopyResult.Copied || mapping is null || mapping.FootprintAddress != placement.Allocation ||
                    mapping.FootprintByteCount != placement.AllocationBytes || mapping.SourceAddress != placement.Address ||
                    mapping.SourceByteCount != placement.Bytes || !read.Active || !GuestImageWriteTracker.IsReadSnapshotStable(read) ||
                    !watched.IsMappedCopyObservationCurrent(mapping) || !watched.IsMappedCopyWriteWatchCurrent(mapping))
                    throw new NotSupportedException("Native R32 first producer ORIGINAL full allocation/watch/read observation was refused.");
                content = ObserveCpuTextureSnapshot(placement.Address, placement.Bytes, HostMemory.MappingGeneration);
                physical = new(memory, watched, mapping, prototype, placement.Address, placement.Bytes, session);
                _ = MetalStoragePhysicalPlacement.WriteSpans(physical);
            }
            else physical = MetalStoragePhysicalPlacement.Capture(memory, prototype, session);
            var original = new OrdinaryStorageOriginal(physical, watched, read, content);
            RequireOrdinaryStorageOriginalContent(original);
            lock (_gate)
            {
                if (_closed || _guestWorkFailure is not null || _ordinaryStorageOriginalCleanupFailure is not null ||
                    !ReferenceEquals(_guestMemory, memory) || session != CurrentSelectedStorageMipSeedSession ||
                    _ordinaryStorageOriginalByAddress.Count >= MaximumOrdinaryStorageOriginals ||
                    _ordinaryStorageOriginalByAddress.ContainsKey(d.Address) || _guestImages.ContainsKey(d.Address) ||
                    _availableGuestImages.ContainsKey(d.Address))
                    throw new NotSupportedException("Ordinary storage original installation changed or exceeded its bound.");
                _ordinaryStorageOriginalByAddress.Add(d.Address, original);
                mapping = null; // bounded original registry owns the exact watch until retirement
            }
            return original;
        }
        catch (Exception failure) { primary = failure; throw; }
        finally { ReleaseOriginalSelectedWriteWatch(watched, mapping, primary); }
    }

    private static bool IsOrdinaryR32MipZero(GuestDrawTexture texture) => texture.Type == 9 && texture.Format == 4 &&
        texture.NumberType == 7 && texture.Width == 512 && texture.Height == 256 && texture.Pitch == 512 &&
        texture.TileMode == 9 && texture.ResourceMipLevels == 9 && texture.MipLevel == 0 && texture.BaseMipLevel == 0;

    private static bool SameOrdinaryStorageView(GuestDrawTexture a, GuestDrawTexture b) =>
        a.Address == b.Address && a.Width == b.Width && a.Height == b.Height && a.Depth == b.Depth &&
        a.Format == b.Format && a.NumberType == b.NumberType && a.Type == b.Type && a.Pitch == b.Pitch &&
        a.TileMode == b.TileMode && a.ResourceMipLevels == b.ResourceMipLevels && a.MipLevel == b.MipLevel &&
        a.BaseMipLevel == b.BaseMipLevel && a.DstSelect == b.DstSelect && a.ArrayedView == b.ArrayedView &&
        a.ArrayLayers == b.ArrayLayers && a.IsStorage == b.IsStorage && a.Shape == b.Shape &&
        a.Descriptor is { Length: 8 } x && b.Descriptor is { Length: 8 } y && x.AsSpan().SequenceEqual(y);

    internal static void AttachOrdinaryStorageOriginal(object? capture, GuestDrawTexture texture)
    {
        if (capture is not OrdinaryStorageOriginal original) return;
        lock (_gate)
        {
            RequireOrdinaryStoragePhysicalCurrentLocked(original, original.Physical.Memory, texture, original.Physical.Session);
            if (_ordinaryStorageOriginals.TryGetValue(texture, out var existing))
            { if (!ReferenceEquals(existing, original)) throw new NotSupportedException("Ordinary storage DTO already carries another ORIGINAL."); }
            else _ordinaryStorageOriginals.Add(texture, original);
        }
        RequireOrdinaryStorageOriginalContent(original);
    }
    private static void CopyOrdinaryStorageOriginal(GuestDrawTexture source, GuestDrawTexture copy)
    {
        if (!_ordinaryStorageOriginals.TryGetValue(source, out var original)) return;
        if (!SameOrdinaryStorageView(source, copy)) throw new NotSupportedException("Ordinary storage copy changed its ORIGINAL physical view.");
        _ordinaryStorageOriginals.Add(copy, original);
    }
    private static void RequireOrdinaryStoragePhysicalCurrentLocked(OrdinaryStorageOriginal original, ICpuMemory memory,
        GuestDrawTexture texture, long session)
    {
        if (_closed || _guestWorkFailure is not null || _ordinaryStorageOriginalCleanupFailure is not null ||
            original.WatchConsumed || original.PreparationFailed || !ReferenceEquals(_guestMemory, memory) || !ReferenceEquals(original.Physical.Memory, memory) ||
            original.Physical.Session != session || session != CurrentSelectedStorageMipSeedSession ||
            !SameOrdinaryStorageView(original.Physical.Original, texture) ||
            !original.Physical.Source.IsMappedCopyObservationCurrent(original.Physical.Mapping))
            throw new NotSupportedException("Ordinary storage lost its ORIGINAL physical mapping/view/session.");
    }
    private static void RequireOrdinaryStorageOriginalContent(OrdinaryStorageOriginal original)
    {
        if (Monitor.IsEntered(_gate)) throw new InvalidOperationException("Ordinary R32 ORIGINAL content checks require a released presenter gate.");
        if (original.Watched is not { } source) return; // mapping-only other images confer no content authority
        if (original.WatchConsumed || original.Physical.Session != CurrentSelectedStorageMipSeedSession ||
            !original.Content.Observed || original.Content.ByteCount != original.Physical.WriteBytes || !original.Read.Active ||
            !GuestImageWriteTracker.IsReadSnapshotStable(original.Read) ||
            !source.IsMappedCopyObservationCurrent(original.Physical.Mapping) || !source.IsMappedCopyWriteWatchCurrent(original.Physical.Mapping) ||
            !GuestImageWriteTracker.TryGetProtectedObservedContentToken(original.Physical.WriteAddress, original.Physical.WriteBytes, out var token) ||
            token != original.Content.Token || !GuestImageWriteTracker.IsReadSnapshotStable(original.Read) ||
            !source.IsMappedCopyObservationCurrent(original.Physical.Mapping) || !source.IsMappedCopyWriteWatchCurrent(original.Physical.Mapping))
            throw new NotSupportedException("Native R32 producer lost its ORIGINAL mapping/read/protected token/watch.");
    }

    private static OrdinaryStorageProducer[] PrepareOrdinaryStorageProducersLocked(object work, long sequence, GuestQueueIdentity queue)
    {
        if (!_r32NativeOriginalRequested || work is not ComputeGuestDispatch dispatch) return [];
        var producers = new List<OrdinaryStorageProducer>();
        var seen = new HashSet<OrdinaryStorageOriginal>(ReferenceEqualityComparer.Instance);
        if (_ordinaryStorageNativeReceipts.Count >= 256) throw new NotSupportedException("Original native storage receipt inventory is full.");
        foreach (var texture in dispatch.Textures)
        {
            if (!StorageImageWrites(texture) || _selectedStorageMipAssociations.TryGetValue(texture, out _)) continue;
            if (!_ordinaryStorageOriginals.TryGetValue(texture, out var original)) { _ordinaryStorageOriginalUnknown = true; continue; }
            RequireOrdinaryStoragePhysicalCurrentLocked(original, original.Physical.Memory, texture, original.Physical.Session);
            if (seen.Add(original)) producers.Add(new(original, dispatch, sequence, queue));
        }
        var result = producers.ToArray();
        if (_ordinaryStorageNativeReceipts.Count + result.Length > 256) throw new NotSupportedException("Original native storage receipt inventory is full.");
        _ordinaryStorageNativeReceipts.EnsureCapacity(_ordinaryStorageNativeReceipts.Count + result.Length);
        // Reserve the exact work association BEFORE queue insertion. The later
        // commit only installs nonthrowing LatestAccepted/reference metadata.
        _ordinaryStorageProducers.Add(work, result);
        return result;
    }
    private static void PublishOrdinaryStorageProducersLocked(object work, OrdinaryStorageProducer[] producers)
    {
        if (producers.Length == 0) return;
        foreach (var producer in producers)
        { producer.PreviousAccepted = producer.Original.LatestAccepted; _ordinaryStorageNativeReceipts.Add(producer); producer.Original.LatestAccepted = producer; }
    }

    private static void BindOrdinaryStorageNativeOriginal(GuestDrawTexture descriptor, GuestImage image)
    {
        if (!_r32NativeOriginalRequested || !_ordinaryStorageOriginals.TryGetValue(descriptor, out var original)) return;
        lock (_gate)
        {
            RequireOrdinaryStoragePhysicalCurrentLocked(original, original.Physical.Memory, descriptor, original.Physical.Session);
            if (image.OrdinaryStorageOriginal is not null && !ReferenceEquals(image.OrdinaryStorageOriginal, original) ||
                image.OrdinaryStorageOriginal is null && (image.GpuWritten || original.Image is not null))
                throw new NotSupportedException("Native storage cannot adopt a younger physical original on an existing GPU owner.");
            image.OrdinaryStorageOriginal = original; original.Image = image;
        }
    }

    private static void RecordOrdinaryStorageEncodedProducers(ComputeGuestDispatch dispatch, nint commandBuffer, nint queue)
    {
        if (!_r32NativeOriginalRequested || !_ordinaryStorageProducers.TryGetValue(dispatch, out var producers) || producers.Length == 0) return;
        if (commandBuffer == 0 || queue == 0 || !MetalNative.SendBool(commandBuffer, MetalNative.Selector("retainedReferences")))
            throw new NotSupportedException("Native storage original producer requires its real retaining command buffer.");
        foreach (var producer in producers)
        {
            lock (_gate)
            {
            if (producer.Encoded || producer.Sequence != _executingGuestWorkSequence ||
                _takenGuestWorkSequence != producer.Sequence || !ReferenceEquals(_takenDepthCubeProducerWork, dispatch) ||
                _submittingGuestQueue != producer.Queue || queue != _batchQueue ||
                (_batchOpen ? commandBuffer != _batchCommandBuffer : commandBuffer != _lastFlushedCommandBuffer) ||
                !_guestImages.TryGetValue(producer.Original.Physical.Original.Address, out var image) ||
                !ReferenceEquals(image, producer.Original.Image) || !ReferenceEquals(image.OrdinaryStorageOriginal, producer.Original) ||
                image.Texture == 0 || !image.Initialized || !image.GpuWritten || image.ContentVersion <= 1)
                throw new NotSupportedException("Native storage encoded writer lacks its exact accepted original owner/version.");
            producer.Image = image;
            try
            {
                producer.Texture = MetalNative.Send(image.Texture, MetalNative.Selector("retain"));
                if (producer.Texture == 0) throw new NotSupportedException("Native storage cannot retain its exact producer texture.");
                producer.CommandBuffer = MetalNative.Send(commandBuffer, MetalNative.Selector("retain"));
                if (producer.CommandBuffer == 0) throw new NotSupportedException("Native storage cannot retain its exact producer command buffer.");
                producer.Version = image.ContentVersion; producer.NativeQueue = queue; producer.Encoded = true;
                producer.Original.LatestEncoded = producer;
            }
            catch (Exception primary)
            {
                try { ReleaseOrdinaryStorageNativeReceipt(producer); }
                catch (Exception cleanup) { try { primary.Data["OrdinaryStorageProducerRetainCleanup"] = cleanup; } catch { } }
                throw;
            }
            }
        }
        PruneOrdinaryStorageNativeReceipts();
    }

    private static readonly HashSet<OrdinaryStorageProducer> _ordinaryStorageNativeReceipts = new(ReferenceEqualityComparer.Instance);
    private static void PruneOrdinaryStorageNativeReceipts()
    {
        OrdinaryStorageProducer[] retired;
        lock (_gate) retired = _ordinaryStorageNativeReceipts.Where(producer => producer.Encoded && !producer.RetainsConsumed && producer.CommandBuffer != 0 &&
            !ReferenceEquals(producer.Original.LatestEncoded, producer) && !ReferenceEquals(producer.Original.LatestAccepted, producer) &&
            !_nativeR32Snapshots.Any(lease => ReferenceEquals(lease.Producer, producer))).ToArray();
        foreach (var producer in retired)
        {
            // A pending original native CB stays retained and charged. Native
            // status4 is required before releasing a completed old receipt.
            if (MetalNative.Send(producer.CommandBuffer, MetalNative.Selector("status")) != (nint)4) continue;
            ReleaseOrdinaryStorageNativeReceipt(producer);
        }
    }
    private static void ReleaseOrdinaryStorageNativeReceipt(OrdinaryStorageProducer producer)
    {
        if (producer.RetainsConsumed) return;
        producer.RetainsConsumed = true;
        var texture = producer.Texture; var commandBuffer = producer.CommandBuffer;
        producer.Texture = producer.CommandBuffer = 0;
        Exception? failure = null;
        ReleaseFiniteStorageHandle(commandBuffer, ref failure); ReleaseFiniteStorageHandle(texture, ref failure);
        lock (_gate)
        {
            if (failure is null) _ordinaryStorageNativeReceipts.Remove(producer);
            else _ordinaryStorageOriginalCleanupFailure ??= failure; // unknown remains charged; no release retry
        }
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
    private static void RollbackOrdinaryStorageProducersLocked(object work, OrdinaryStorageProducer[] producers)
    {
        _ordinaryStorageProducers.Remove(work);
        foreach (var producer in producers)
        {
            if (ReferenceEquals(producer.Original.LatestAccepted, producer)) producer.Original.LatestAccepted = producer.PreviousAccepted;
            if (!producer.Encoded && producer.Texture == 0 && producer.CommandBuffer == 0) _ordinaryStorageNativeReceipts.Remove(producer);
            else _ordinaryStorageOriginalUnknown = true; // no retry of a partially encoded admission
        }
    }

    internal static void AbandonPreparedOrdinaryStorageOriginal(object? capture)
    {
        if (capture is not OrdinaryStorageOriginal original) return;
        lock (_gate)
        {
            if (original.LatestAccepted is not null) return; // an older accepted owner remains independently owned
            original.PreparationFailed = true;
            _ordinaryStorageOriginalUnknown = true;
            // Exact watch remains charged in the bounded registry until actual
            // retirement; failed preparation never opens a younger capture.
        }
    }

    private static void RetireOrdinaryStorageNativeOriginals()
    {
        OrdinaryStorageProducer[] producers;
        OrdinaryStorageOriginal[] originals;
        Exception? first = null;
        var complete = true;
        lock (_gate)
        {
            try { CancelQueuedNativeR32SnapshotsLocked(); } catch (Exception failure) { first = failure; complete = false; }
            foreach (var lease in _nativeR32Snapshots.ToArray())
                if (!lease.PreparationReleased)
                {
                    lease.PreparationReleased = true;
                    try { ReleaseNativeR32SnapshotLocked(lease, first); } catch (Exception failure) { first ??= failure; complete = false; }
                }
            if (_nativeR32Snapshots.Count != 0 || _ordinaryStorageOriginalCleanupFailure is not null)
            {
                complete = false;
                first ??= _ordinaryStorageOriginalCleanupFailure ?? new InvalidOperationException("Unknown native R32 snapshot lifetime retains every exact producer/backing/watch owner.");
                _ordinaryStorageOriginalCleanupFailure ??= first;
            }
            producers = _ordinaryStorageNativeReceipts.ToArray();
            originals = _ordinaryStorageOriginalByAddress.Values.ToArray();
        }
        // A private copy may still read the original producer resource. A
        // known producer status4 alone never retires that unknown dependency.
        if (!complete) ExceptionDispatchInfo.Capture(first!).Throw();
        foreach (var producer in producers)
            try
            {
                if (producer.RetainsConsumed) { complete = false; continue; }
                if (producer.CommandBuffer != 0) WaitForCommittedGuestBufferWriteback(producer.CommandBuffer);
                ReleaseOrdinaryStorageNativeReceipt(producer);
            }
            catch (Exception failure) { first ??= failure; complete = false; }
        // Watch/backing metadata never retires past unknown GPU completion.
        if (complete)
        foreach (var original in originals)
        {
            if (original.Watched is null || original.WatchConsumed) continue;
            original.WatchConsumed = true;
            try { original.Watched.ReleaseMappedCopyWriteWatch(original.Physical.Mapping); }
            catch (Exception failure) { first ??= failure; complete = false; }
        }
        lock (_gate)
        {
            if (complete && _nativeR32Snapshots.Count == 0) _ordinaryStorageOriginalByAddress.Clear();
            else _ordinaryStorageOriginalCleanupFailure ??= first ?? new InvalidOperationException("Ordinary storage native originals remain quarantined.");
        }
        if (first is not null) ExceptionDispatchInfo.Capture(first).Throw();
    }
}
