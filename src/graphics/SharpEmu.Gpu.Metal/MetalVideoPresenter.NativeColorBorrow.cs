// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using SharpEmu.HLE;
using SharpEmu.Libs.Gpu;
using SharpEmu.Libs.Gpu.Abstractions;
using SharpEmu.Libs.Gpu.Images;
using static SharpEmu.Libs.Agc.AgcBackendServices;
using Binding = SharpEmu.Libs.Agc.AgcExports.TranslatedImageBinding;

namespace SharpEmu.Gpu.Metal;

internal static partial class MetalVideoPresenter
{
    // Separately opt-in. Placement alone never admits a native borrow.
    internal static bool NativeColorBorrowEnabled = Environment.GetEnvironmentVariable("SHARPEMU_METAL_NATIVE_COLOR_BORROW") == "1";
    private const int MaximumNativeColorOriginals = 128;
    private static Exception? _nativeColorCleanupFailure;
    private static readonly HashSet<NativeColorOriginal> _nativeColorOriginals = new(ReferenceEqualityComparer.Instance);
    private static readonly ConditionalWeakTable<MetalColorMipPlacement.Observation, NativeColorOriginal> _nativeColorOriginalByPlacement = new();
    private sealed class NativeColorOriginal(MetalColorMipPlacement.Observation placement,
        ICpuMappedReadWatchSource source, CpuMappedCopyObservation mapping,
        GuestImageWriteTracker.ReadSnapshot read, CpuTextureSnapshotObservation content, long session)
    {
        internal readonly MetalColorMipPlacement.Observation Placement = placement;
        internal readonly ICpuMappedReadWatchSource Source = source;
        internal readonly CpuMappedCopyObservation Mapping = mapping;
        internal readonly GuestImageWriteTracker.ReadSnapshot Read = read;
        internal readonly CpuTextureSnapshotObservation Content = content;
        internal readonly long Session = session;
        internal int References = 1; // exact original frontend preparation
        internal bool PreparationReleased, Consumed;
        // Provider scalar checks only; tracker locks stay outside presenter gate.
        internal bool ScalarCurrent => !Volatile.Read(ref Consumed) && Session == CurrentNativeDepthFaceSession &&
            Source.IsMappedCopyObservationCurrent(Mapping) && Source.IsMappedCopyWriteWatchCurrent(Mapping);
    }

    internal static void CaptureNativeColorOriginal(MetalColorMipPlacement.Observation placement)
    {
        if (!NativeColorBorrowEnabled) return;
        if (placement.Memory is not ICpuMappedReadWatchSource source)
            throw new NotSupportedException("Native selected color requires an original full-chain mapped copy with a physical write watch.");
        var session = CurrentNativeDepthFaceSession;
        // This ordinary producer establishes complete protection explicitly:
        // BeginReadSnapshot alone can skip native arming for managed coverage.
        GuestImageWriteTracker.Track(placement.PhysicalAddress, placement.ByteCount, source: "metal.native-color.original", protect: true);
        var read = GuestImageWriteTracker.BeginReadSnapshot(placement.PhysicalAddress, placement.ByteCount, source: "metal.native-color.original");
        CpuMappedCopyObservation? mapping = null;
        try
        {
            // The complete selected span is watched BEFORE its producer. These
            // discarded bytes are neither upload data nor native content authority.
            var discarded = new byte[checked((int)placement.ByteCount)];
            var result = source.TryReadMappedCopyWithWriteWatch(placement.Registers.BaseAddress,
                placement.BackingByteCount, placement.PhysicalAddress, discarded, out mapping);
            if (result != CpuMappedCopyResult.Copied || mapping is null ||
                mapping.FootprintAddress != placement.Registers.BaseAddress || mapping.FootprintByteCount != placement.BackingByteCount ||
                mapping.SourceAddress != placement.PhysicalAddress || mapping.SourceByteCount != placement.ByteCount ||
                mapping.Segments.Any(s => (s.GuestAccess & GuestPageProtection.Write) == 0))
                throw new NotSupportedException("Native color original mapping/guest-write footprint is incomplete.");
            if (!source.IsMappedCopyObservationCurrent(mapping) || !source.IsMappedCopyWriteWatchCurrent(mapping) ||
                !read.Active || !GuestImageWriteTracker.IsReadSnapshotStable(read) || session != CurrentNativeDepthFaceSession)
                throw new NotSupportedException("Native color original copy changed before its first content observation.");
            // First content token follows THIS original mapping, never a newer
            // copy. The original read generation and mapping bracket it.
            var content = ObserveCpuTextureSnapshot(placement.PhysicalAddress, placement.ByteCount, HostMemory.MappingGeneration);
            var original = new NativeColorOriginal(placement, source, mapping, read, content, session);
            RequireNativeColorOriginal(original);
            lock (_gate)
            {
                if (_closed || _guestWorkFailure is not null || _nativeColorCleanupFailure is not null ||
                    !original.ScalarCurrent || _nativeColorOriginals.Count >= MaximumNativeColorOriginals)
                    throw new NotSupportedException("Native color original inventory is closed, changed or full.");
                _nativeColorOriginalByPlacement.Add(placement, original);
                try { _nativeColorOriginals.Add(original); }
                catch { _nativeColorOriginalByPlacement.Remove(placement); throw; }
            }
            mapping = null; // exact preparation now owns watch cleanup
        }
        catch (Exception primary)
        {
            if (mapping is not null)
                try { source.ReleaseMappedCopyWriteWatch(mapping); }
                catch (Exception cleanup)
                {
                    Interlocked.CompareExchange(ref _nativeColorCleanupFailure, cleanup, null);
                    try { primary.Data["NativeColorOriginalCleanup"] = cleanup; } catch { }
                }
            throw;
        }
    }

    private static void RequireNativeColorOriginal(NativeColorOriginal original)
    {
        if (Monitor.IsEntered(_gate)) throw new InvalidOperationException("Native color tracker validation must run outside presenter gate.");
        if (!original.ScalarCurrent || !original.Content.Observed || original.Content.ByteCount != original.Placement.ByteCount || !original.Read.Active ||
            !GuestImageWriteTracker.IsReadSnapshotStable(original.Read) ||
            !GuestImageWriteTracker.TryGetProtectedObservedContentToken(original.Placement.PhysicalAddress, original.Content.ByteCount, out var token) ||
            token != original.Content.Token || !original.ScalarCurrent || !GuestImageWriteTracker.IsReadSnapshotStable(original.Read))
            throw new NotSupportedException("Native color original mapping/content/watch/session changed; no younger observation is accepted.");
    }
    private static void RetainNativeColorOriginalLocked(NativeColorOriginal original)
    {
        if (!original.ScalarCurrent || original.References <= 0 || original.References == int.MaxValue || _nativeColorCleanupFailure is not null)
            throw new NotSupportedException("Native color original ownership changed.");
        original.References++;
    }
    private static void ReleaseNativeColorOriginalLocked(NativeColorOriginal original, Exception? primary = null)
    {
        if (original.Consumed) return;
        if (original.References <= 0) throw new InvalidOperationException("Unbalanced native color watch ownership.");
        if (--original.References != 0) return;
        original.Consumed = true; // consume BEFORE optional callback, never retry unknown
        try
        {
            original.Source.ReleaseMappedCopyWriteWatch(original.Mapping);
            _nativeColorOriginals.Remove(original);
        }
        catch (Exception cleanup)
        {
            _nativeColorCleanupFailure ??= cleanup;
            if (primary is not null) { try { primary.Data["NativeColorWatchCleanup"] = cleanup; } catch { } }
            else throw;
        }
    }
    internal static void ReleasePreparedNativeColorTarget(GuestRenderTarget target,
        MetalColorMipPlacement.Observation? placement = null, Exception? primary = null)
    {
        if (placement is null && !MetalColorMipPlacement.TryGet(target, out placement!)) return;
        lock (_gate)
        {
            if (!_nativeColorOriginalByPlacement.TryGetValue(placement, out var original) || original.PreparationReleased) return;
            original.PreparationReleased = true;
            ReleaseNativeColorOriginalLocked(original, primary);
        }
    }

    private sealed class NativeColorProducer(OffscreenGuestDraw work, NativeColorOriginal original,
        long sequence, GuestQueueIdentity queue)
    {
        internal readonly OffscreenGuestDraw Work = work;
        internal readonly NativeColorOriginal Original = original;
        internal readonly long Sequence = sequence;
        internal readonly GuestQueueIdentity Queue = queue;
        internal NativeColorProducer? Predecessor;
        internal GuestImage? Image;
        internal nint Texture, CommandBuffer, NativeQueue;
        internal int Version;
        internal bool Encoded, Retired, IsLatest, OriginalReferenceOwned, CleanupUnknown;
        internal int DependentReferences; // exact sampled/predecessor queue ownership, distinct from latest
        internal NativeColorBatch? Batch;
    }
    private sealed class NativeColorBatch(nint handle)
    {
        internal nint Handle = handle;
        internal bool Submitted, Completed;
    }
    private static readonly ConditionalWeakTable<object, NativeColorProducer[]> _nativeColorWorks = new();
    private static readonly Dictionary<ulong, NativeColorProducer> _nativeColorLatest = [];
    private static readonly HashSet<NativeColorProducer> _nativeColorProducers = new(ReferenceEqualityComparer.Instance);
    private static readonly Dictionary<nint, NativeColorBatch> _nativeColorBatches = [];
    private sealed record NativeColorQueuedOwner(NativeColorOriginal[] Originals, NativeColorProducer[] Dependencies);
    private static readonly Dictionary<object, NativeColorQueuedOwner> _nativeColorQueued = new(ReferenceEqualityComparer.Instance);
    private sealed class NativeColorLease(NativeColorProducer producer)
    {
        internal readonly NativeColorProducer Producer = producer;
        internal bool PreparationReleased;
    }
    private static readonly ConditionalWeakTable<GuestDrawTexture, NativeColorLease> _nativeColorLeases = new();
    private static readonly HashSet<NativeColorLease> _nativeColorPreparations = new(ReferenceEqualityComparer.Instance);

    private static NativeColorProducer[] PrepareNativeColorAdmissionLocked(object work, long sequence, GuestQueueIdentity queue)
    {
        if (!NativeColorBorrowEnabled || work is not OffscreenGuestDraw draw) return [];
        if (draw.Targets.Length > 8 || draw.Draw.Textures.Length > 128 || draw.Draw.GlobalMemoryBuffers.Length > 64)
            throw new NotSupportedException("Native color producer resource inventory exceeds its finite bound.");
        var result = new List<NativeColorProducer>();
        foreach (var target in draw.Targets)
        {
            if (!MetalColorMipPlacement.TryGet(target, out var placement)) continue;
            if (!_nativeColorOriginalByPlacement.TryGetValue(placement, out var original) || original.PreparationReleased || !original.ScalarCurrent ||
                !ReferenceEquals(_guestMemory, placement.Memory) || _closed || _guestWorkFailure is not null || _textureSourceGraphEnabled)
                throw new NotSupportedException("Native color producer lost its ORIGINAL preparation.");
            if (result.Any(p => p.Original.Placement.PhysicalAddress == target.Address))
                throw new NotSupportedException("Native color draw has duplicate selected attachments.");
            var producer = new NativeColorProducer(draw, original, sequence, queue);
            if (_nativeColorLatest.TryGetValue(target.Address, out var predecessor))
            {
                if (predecessor.Queue.Name != queue.Name || predecessor.Retired || !predecessor.Original.ScalarCurrent)
                    throw new NotSupportedException("Native color predecessor is not the SAME original ordered producer.");
                producer.Predecessor = predecessor;
            }
            else if (_guestImages.TryGetValue(target.Address, out var old) && old.GpuWritten)
                throw new NotSupportedException("Native color cannot adopt existing GPU content without its original producer.");
            result.Add(producer);
        }
        if (_nativeColorProducers.Count + result.Count > 128)
            throw new NotSupportedException("Native color producer retention is full.");
        return result.ToArray();
    }
    private static bool RetainQueuedNativeColorLocked(object work, NativeColorProducer[] producers)
    {
        if (!NativeColorBorrowEnabled) return false;
        if (work is OffscreenGuestDraw resourceDraw && resourceDraw.Draw.Textures.Length > 128 ||
            work is ComputeGuestDispatch resourceDispatch && resourceDispatch.Textures.Length > 128)
            throw new NotSupportedException("Native color queued texture inventory exceeds its finite bound.");
        IEnumerable<GuestDrawTexture> textures = work switch
        { OffscreenGuestDraw draw => draw.Draw.Textures, ComputeGuestDispatch dispatch => dispatch.Textures, _ => [] };
        var originals = producers.Select(p => p.Original).Concat(producers.Where(p => p.Predecessor is not null).Select(p => p.Predecessor!.Original)).Concat(textures.Select(t =>
            _nativeColorLeases.TryGetValue(t, out var lease) && !lease.PreparationReleased ? lease.Producer.Original : null)
            .Where(o => o is not null).Cast<NativeColorOriginal>()).Distinct().ToArray();
        var dependencies = producers.Where(p => p.Predecessor is not null).Select(p => p.Predecessor!)
            .Concat(textures.Select(t => _nativeColorLeases.TryGetValue(t, out var lease) && !lease.PreparationReleased ? lease.Producer : null)
                .Where(p => p is not null).Cast<NativeColorProducer>()).Distinct().ToArray();
        if (originals.Length == 0) return false;
        if (originals.Length > 128 || _nativeColorQueued.Count >= MaxPendingGuestWork + 1 || _nativeColorQueued.ContainsKey(work))
            throw new NotSupportedException("Native color queued watch retention is full/duplicated.");
        if (_nativeColorCleanupFailure is not null || originals.Any(o => !o.ScalarCurrent || o.References <= 0 || o.References == int.MaxValue))
            throw new NotSupportedException("Native color queued original changed.");
        if (dependencies.Any(p => p.Retired || p.DependentReferences == int.MaxValue))
            throw new NotSupportedException("Native color queued dependency changed.");
        _nativeColorQueued.Add(work, new(originals, dependencies)); // allocate BEFORE counters
        foreach (var original in originals) original.References++; // prevalidated under SAME gate; ownership only
        foreach (var dependency in dependencies) dependency.DependentReferences++;
        return true;
    }
    private static void PublishNativeColorAdmissionLocked(object work, NativeColorProducer[] producers)
    {
        if (producers.Length == 0) return;
        if (_nativeColorCleanupFailure is not null || producers.Any(p => p.OriginalReferenceOwned || p.Original.Consumed ||
            !p.Original.ScalarCurrent || p.Original.References <= 0 || p.Original.References == int.MaxValue))
            throw new NotSupportedException("Native color producer original changed before publication.");
        if (_nativeColorWorks.TryGetValue(work, out _) || producers.Any(p =>
            _nativeColorLatest.TryGetValue(p.Original.Placement.PhysicalAddress, out var prior) &&
            (!ReferenceEquals(prior, p.Predecessor) || prior.DependentReferences <= 0)))
            throw new InvalidOperationException("Native color admission lost its pre-acquired predecessor dependency.");
        _nativeColorProducers.EnsureCapacity(_nativeColorProducers.Count + producers.Length);
        _nativeColorLatest.EnsureCapacity(_nativeColorLatest.Count + producers.Length);
        _nativeColorWorks.Add(work, producers);
        foreach (var producer in producers)
        {
            _nativeColorProducers.Add(producer);
            producer.Original.References++; // all throwing validation/allocation precedes ownership commit
            producer.OriginalReferenceOwned = true;
            producer.IsLatest = true;
            if (_nativeColorLatest.TryGetValue(producer.Original.Placement.PhysicalAddress, out var prior))
            {
                prior.IsLatest = false;
                // Admission does not mutate native pixels/version. Already
                // accepted consumers and this new producer's predecessor retain
                // the exact older receipt through their ordered execution.
                // The exact predecessor dependency was checked BEFORE commit;
                // admission cannot release an older native texture or callback.
            }
            _nativeColorLatest[producer.Original.Placement.PhysicalAddress] = producer;
        }
    }
    private static void ReleaseQueuedNativeColorLocked(object work, Exception? primary = null)
    {
        if (!_nativeColorQueued.Remove(work, out var owner)) return;
        Exception? first = primary;
        foreach (var original in owner.Originals)
            try { ReleaseNativeColorOriginalLocked(original, first); }
            catch (Exception cleanup) { first ??= cleanup; }
        foreach (var dependency in owner.Dependencies)
        {
            try
            {
                if (dependency.DependentReferences <= 0) throw new InvalidOperationException("Unbalanced native color producer dependency.");
                dependency.DependentReferences--;
                RetireNativeColorProducerLocked(dependency, first);
            }
            catch (Exception cleanup) { first ??= cleanup; }
        }
        if (primary is null && first is not null) ExceptionDispatchInfo.Capture(first).Throw();
    }
    private static void CancelQueuedNativeColorLocked(Exception? primary = null)
    {
        Exception? first = primary;
        // Removal consumes exact independent authority before optional callbacks.
        while (_nativeColorQueued.Count != 0)
        {
            var iterator = _nativeColorQueued.Keys.GetEnumerator();
            if (!iterator.MoveNext()) break;
            var work = iterator.Current; iterator.Dispose();
            try { ReleaseQueuedNativeColorLocked(work, first); } catch (Exception cleanup) { first ??= cleanup; }
        }
        foreach (var producer in _nativeColorProducers.ToArray())
            try { producer.IsLatest = false; RetireNativeColorProducerLocked(producer, first); } catch (Exception cleanup) { first ??= cleanup; }
        _nativeColorLatest.Clear();
        if (primary is null && first is not null) ExceptionDispatchInfo.Capture(first).Throw();
    }
    private static void RetireNativeColorProducerLocked(NativeColorProducer producer, Exception? primary = null)
    {
        if (producer.Retired || producer.IsLatest || producer.DependentReferences != 0) return;
        producer.Retired = true;
        Exception? first = primary;
        if (producer.OriginalReferenceOwned)
        {
            producer.OriginalReferenceOwned = false;
            try { ReleaseNativeColorOriginalLocked(producer.Original, first); }
            catch (Exception cleanup) { producer.CleanupUnknown = true; first ??= cleanup; }
        }
        // A submitted/unconfirmed encoder keeps its EXACT retain through real
        // status4. Failed releases remain charged and cannot be attempted again.
        if (!producer.CleanupUnknown && (producer.Texture == 0 || producer.Batch is { Completed: true }))
            try { ReleaseNativeColorTextureLocked(producer, first); } catch (Exception cleanup) { first ??= cleanup; }
        if (primary is null && first is not null) ExceptionDispatchInfo.Capture(first).Throw();
    }
    private static void ReleaseNativeColorTextureLocked(NativeColorProducer producer, Exception? primary = null)
    {
        if (producer.CleanupUnknown) return;
        var texture = producer.Texture; producer.Texture = 0;
        try
        {
            if (texture != 0) MetalNative.SendVoid(texture, MetalNative.Selector("release"));
            _nativeColorProducers.Remove(producer);
        }
        catch (Exception cleanup)
        {
            producer.CleanupUnknown = true; // bounded inventory retains unknown consumed owner
            _nativeColorCleanupFailure ??= cleanup;
            if (primary is null) throw;
            try { primary.Data["NativeColorTextureCleanup"] = cleanup; } catch { }
        }
    }

    private static Action? _nativeColorPredecessorValidationForControl; // default-null ordinary failure seam

    private static void ValidateNativeColorEncoderPredecessors(OffscreenGuestDraw work, GuestImage?[] images, nint commandBuffer, nint queue)
    {
        if (!_nativeColorWorks.TryGetValue(work, out var producers)) return;
        foreach (var producer in producers) RequireNativeColorOriginal(producer.Original);
        lock (_gate)
        {
            foreach (var producer in producers)
            {
                var image = images.SingleOrDefault(i => i?.ColorPhysicalView?.PhysicalAddress == producer.Original.Placement.PhysicalAddress);
                if (image is null || !producer.Original.ScalarCurrent) throw new NotSupportedException("Native color encoder lost its selected attachment.");
                if (image.Initialized || image.GpuWritten)
                {
                    var prior = producer.Predecessor;
                    if (prior is null || !prior.Encoded || !prior.Original.ScalarCurrent || !ReferenceEquals(prior.Image, image) ||
                        prior.Texture != image.Texture || prior.Version != image.ContentVersion || prior.NativeQueue != queue ||
                        !(prior.CommandBuffer == commandBuffer && prior.Batch is { Submitted: false } || prior.Batch is { Completed: true }))
                        throw new NotSupportedException("Native selected color load/partial mask has no exact ordered native predecessor.");
                }
                RequireNativeColorCohortLocked(producer);
            }
        }
    }
    private static void RequireNativeColorExecution(OffscreenGuestDraw work, bool success, string stage)
    {
        if (!success && _nativeColorWorks.TryGetValue(work, out _))
            throw new InvalidOperationException($"Admitted native color producer failed: {stage}.");
    }
    private static void RequireNativeColorCompletedWorkLocked(object work)
    {
        if (_nativeColorWorks.TryGetValue(work, out var producers) && producers.Any(p => !p.Encoded || p.Retired || p.CleanupUnknown))
            throw new NotSupportedException("Native color work cannot acknowledge an unsealed/retired producer.");
    }
    private static bool HasNativeColorProducer(OffscreenGuestDraw work) => NativeColorBorrowEnabled && _nativeColorWorks.TryGetValue(work, out _);
    private static void ValidateNativeColorTargetBeforeNative(GuestRenderTarget target)
    {
        if (!NativeColorBorrowEnabled || !MetalColorMipPlacement.TryGet(target, out var placement)) return;
        if (!_nativeColorOriginalByPlacement.TryGetValue(placement, out var original)) throw new NotSupportedException("Native color target lacks its original producer copy.");
        RequireNativeColorOriginal(original);
    }
    private static void RequireNativeColorTargetScalarLocked(GuestRenderTarget target)
    {
        if (!NativeColorBorrowEnabled || !MetalColorMipPlacement.TryGet(target, out var placement)) return;
        if (!_nativeColorOriginalByPlacement.TryGetValue(placement, out var original) || !original.ScalarCurrent ||
            !ReferenceEquals(_guestMemory, placement.Memory) || _closed || _guestWorkFailure is not null || _nativeColorCleanupFailure is not null)
            throw new NotSupportedException("Native color target original/session changed before native publication.");
    }
    private static void ValidateNativeColorOriginalsBeforeExecution(OffscreenGuestDraw work)
    {
        if (_nativeColorWorks.TryGetValue(work, out var producers)) foreach (var producer in producers) RequireNativeColorOriginal(producer.Original);
    }
    private static void ReleaseUnpublishedNativeColorImage(GuestImage image, Exception primary)
    {
        var owned = image.Texture; image.Texture = 0;
        lock (_gate)
        {
            if (image.ColorPhysicalView is { } placement && _guestImages.TryGetValue(placement.PhysicalAddress, out var current) && ReferenceEquals(current, image))
            {
                _guestImages.Remove(placement.PhysicalAddress); _guestImageExtents.Remove(placement.PhysicalAddress);
                _nativeColorCleanupFailure ??= primary; // partial publication must never be reused
            }
        }
        if (owned == 0) return;
        try { MetalNative.SendVoid(owned, MetalNative.Selector("release")); }
        catch (Exception cleanup) { Interlocked.CompareExchange(ref _nativeColorCleanupFailure, cleanup, null); try { primary.Data["NativeColorUnacceptedRetainCleanup"] = cleanup; } catch { } }
    }
    private static void SealNativeColorProducer(OffscreenGuestDraw work, GuestImage image, nint commandBuffer, nint queue)
    {
        if (!_nativeColorWorks.TryGetValue(work, out var producers)) return;
        var producer = producers.SingleOrDefault(p => p.Original.Placement.PhysicalAddress == image.ColorPhysicalView?.PhysicalAddress);
        if (producer is null) return;
        RequireNativeColorOriginal(producer.Original);
        if (commandBuffer == 0 || !MetalNative.SendBool(commandBuffer, MetalNative.Selector("retainedReferences")))
            throw new NotSupportedException("Native color requires its actual retaining command buffer.");
        lock (_gate)
        {
            if (producer.Retired || producer.Encoded || !producer.Original.ScalarCurrent || _closed || _guestWorkFailure is not null ||
                _nativeColorCleanupFailure is not null || !ReferenceEquals(_guestMemory, producer.Original.Placement.Memory) ||
                !_guestImages.TryGetValue(producer.Original.Placement.PhysicalAddress, out var owner) || !ReferenceEquals(owner, image) ||
                image.Texture == 0 || !image.Initialized || !image.GpuWritten || image.Width != 512 || image.Height != 512 ||
                image.Depth != 1 || image.ArrayLength != 1 || image.TextureType != MtlTextureType2D || image.Format != MtlPixelFormat.Rgba16Float ||
                commandBuffer != _batchCommandBuffer || queue == 0 || queue != _batchQueue)
                throw new NotSupportedException("Native color successful encoder has no exact original owner/version/queue.");
            if (!_nativeColorBatches.TryGetValue(commandBuffer, out var batch))
            {
                if (_nativeColorBatches.Count >= 128) throw new NotSupportedException("Native color batch inventory is full.");
                var handle = MetalNative.Send(commandBuffer, MetalNative.Selector("retain"));
                if (handle == 0) throw new InvalidOperationException("Native color batch retain failed.");
                batch = new(handle);
                try { _nativeColorBatches.Add(commandBuffer, batch); }
                catch (Exception primary) { ReleaseDepthObjectsPreservingFailure(primary, handle); throw; }
            }
            var texture = MetalNative.Send(image.Texture, MetalNative.Selector("retain"));
            if (texture == 0) throw new InvalidOperationException("Native color producer texture retain failed.");
            producer.Texture = texture; producer.Image = image; producer.Version = image.ContentVersion;
            producer.CommandBuffer = commandBuffer; producer.NativeQueue = queue; producer.Batch = batch; producer.Encoded = true;
        }
    }
    private static void MarkNativeColorBatchCommitted(nint commandBuffer)
    { lock (_gate) if (_nativeColorBatches.TryGetValue(commandBuffer, out var batch)) batch.Submitted = true; }
    private static void ObserveNativeColorBatchCompleted(nint commandBuffer)
    {
        if (_guestCommandBufferStatusOverride is not null || _guestCommandBufferWaitOverride is not null) return;
        nint handle = 0;
        Exception? first = null;
        lock (_gate)
        {
            if (!_nativeColorBatches.Remove(commandBuffer, out var batch)) return;
            batch.Completed = true; handle = batch.Handle; batch.Handle = 0;
            foreach (var producer in _nativeColorProducers.Where(p => ReferenceEquals(p.Batch, batch) && p.Retired).ToArray())
                try { ReleaseNativeColorTextureLocked(producer, first); } catch (Exception cleanup) { first ??= cleanup; }
        }
        if (handle != 0)
            try { ReleaseDepthObjectsPreservingFailure(null, handle); }
            catch (Exception cleanup) { Interlocked.CompareExchange(ref _nativeColorCleanupFailure, cleanup, null); first ??= cleanup; }
        if (first is not null) ExceptionDispatchInfo.Capture(first).Throw();
    }
    private static bool NativeColorPhysicalOverlap(CpuMappedCopyObservation left, ulong address, ulong bytes,
        CpuMappedCopyObservation right, ulong otherAddress, ulong otherBytes)
    {
        foreach (var a in left.Segments)
        foreach (var b in right.Segments)
        {
            if (a.BackingIdentity != b.BackingIdentity) continue;
            var aLo = Math.Max(a.CoveredAddress, address);
            var aHi = Math.Min(a.CoveredAddress + a.CoveredByteCount, address + bytes);
            var bLo = Math.Max(b.CoveredAddress, otherAddress);
            var bHi = Math.Min(b.CoveredAddress + b.CoveredByteCount, otherAddress + otherBytes);
            if (aLo >= aHi || bLo >= bHi) continue;
            var aOffset = checked(a.MappingBackingOffset + aLo - a.MappingAddress);
            var bOffset = checked(b.MappingBackingOffset + bLo - b.MappingAddress);
            if ((UInt128)aOffset < (UInt128)bOffset + bHi - bLo && (UInt128)bOffset < (UInt128)aOffset + aHi - aLo) return true;
        }
        return false;
    }
    // This is queue ordering, not a second native texture or new content
    // authority. The consumer already owns the prior receipt; the exact later
    // producer has not encoded and retains that prior as its predecessor.
    // All native handle/version/completion checks in the borrow remain strict.
    private static bool IsOrderedNativeColorSuccessorLocked(NativeColorProducer prior,
        PendingGuestWork pending, NativeColorOriginal next) =>
        InspectOrderedNativeColorSuccessorLocked(prior, pending, next, out _);

    // Literal first-refusal labels only. The predicate/query order is unchanged;
    // no formatting, sampling, authority or acceptance comes from these labels.
    private static bool InspectOrderedNativeColorSuccessorLocked(NativeColorProducer prior,
        PendingGuestWork pending, NativeColorOriginal next, out string reason)
    {
        if (_takenGuestWorkSequence == 0) { reason = "no-taken-consumer"; return false; }
        if (_takenGuestWorkSequence != _executingGuestWorkSequence) { reason = "taken-is-not-executing"; return false; }
        if (_takenGuestWorkSequence <= prior.Sequence) { reason = "consumer-not-after-prior"; return false; }
        if (pending.Sequence <= _takenGuestWorkSequence) { reason = "successor-not-after-consumer"; return false; }
        if (_submittingGuestQueue is not { } queue) { reason = "no-executing-queue"; return false; }
        if (queue.Name != prior.Queue.Name) { reason = "prior-queue-mismatch"; return false; }
        if (pending.Queue.Name != queue.Name) { reason = "successor-queue-mismatch"; return false; }
        if (_takenDepthCubeProducerWork is not { } consumer) { reason = "no-taken-work"; return false; }
        if (ReferenceEquals(consumer, pending.Work)) { reason = "successor-is-taken-consumer"; return false; }
        if (!_nativeColorQueued.TryGetValue(consumer, out var consuming)) { reason = "consumer-has-no-queued-owner"; return false; }
        if (!consuming.Dependencies.Contains(prior)) { reason = "consumer-does-not-retain-prior"; return false; }
        if (prior.Retired) { reason = "prior-retired"; return false; }
        if (prior.CleanupUnknown) { reason = "prior-cleanup-unknown"; return false; }
        if (prior.DependentReferences <= 0) { reason = "prior-has-no-dependency-charge"; return false; }
        if (!prior.OriginalReferenceOwned) { reason = "prior-lost-original-charge"; return false; }
        if (!prior.Original.ScalarCurrent) { reason = "prior-original-not-current"; return false; }
        if (!_nativeColorProducers.Contains(prior)) { reason = "prior-not-in-producer-inventory"; return false; }
        IEnumerable<GuestDrawTexture> reads = consumer switch
        { OffscreenGuestDraw draw => draw.Draw.Textures, ComputeGuestDispatch dispatch => dispatch.Textures, _ => [] };
        if (!reads.Any(texture => texture.Access == GuestImageAccess.Read && !texture.IsStorage &&
            _nativeColorLeases.TryGetValue(texture, out var lease) && ReferenceEquals(lease.Producer, prior)))
        { reason = "taken-readonly-lease-is-not-exact-prior"; return false; }
        if (!_nativeColorWorks.TryGetValue(pending.Work, out var accepted)) { reason = "pending-work-not-accepted"; return false; }
        if (!_nativeColorQueued.TryGetValue(pending.Work, out var queued)) { reason = "pending-work-has-no-queued-owner"; return false; }
        if (!queued.Dependencies.Contains(prior)) { reason = "pending-work-does-not-retain-prior"; return false; }
        if (!queued.Originals.Contains(next)) { reason = "pending-work-does-not-retain-target-original"; return false; }
        var future = accepted.SingleOrDefault(candidate => ReferenceEquals(candidate.Original, next));
        if (future is null) { reason = "accepted-producer-does-not-own-target-original"; return false; }
        if (!ReferenceEquals(future.Work, pending.Work)) { reason = "accepted-work-mismatch"; return false; }
        if (future.Sequence != pending.Sequence) { reason = "accepted-sequence-mismatch"; return false; }
        if (future.Queue != pending.Queue) { reason = "accepted-queue-mismatch"; return false; }
        if (!ReferenceEquals(future.Predecessor, prior)) { reason = "accepted-predecessor-mismatch"; return false; }
        if (future.Encoded) { reason = "successor-already-encoded"; return false; }
        if (future.Retired) { reason = "successor-retired"; return false; }
        if (future.CleanupUnknown) { reason = "successor-cleanup-unknown"; return false; }
        if (future.Texture != 0) { reason = "successor-has-native-texture"; return false; }
        if (future.CommandBuffer != 0) { reason = "successor-has-command-buffer"; return false; }
        if (future.Image is not null) { reason = "successor-has-native-image"; return false; }
        if (future.Batch is not null) { reason = "successor-has-native-batch"; return false; }
        if (!future.OriginalReferenceOwned) { reason = "successor-lost-original-charge"; return false; }
        if (!_nativeColorProducers.Contains(future)) { reason = "successor-not-in-inventory"; return false; }
        if (!next.ScalarCurrent) { reason = "successor-original-not-current"; return false; }
        var original = prior.Original;
        if (!ReferenceEquals(next.Placement.Memory, original.Placement.Memory)) { reason = "original-memory-mismatch"; return false; }
        if (!ReferenceEquals(next.Source, original.Source)) { reason = "original-source-mismatch"; return false; }
        if (next.Session != original.Session) { reason = "original-session-mismatch"; return false; }
        if (next.Placement.Registers != original.Placement.Registers) { reason = "original-registers-mismatch"; return false; }
        if (next.Placement.PhysicalAddress != original.Placement.PhysicalAddress) { reason = "original-selected-address-mismatch"; return false; }
        if (next.Placement.ByteCount != original.Placement.ByteCount) { reason = "original-selected-size-mismatch"; return false; }
        if (next.Placement.BackingByteCount != original.Placement.BackingByteCount) { reason = "original-backing-size-mismatch"; return false; }
        if (!next.Content.Observed) { reason = "successor-content-unobserved"; return false; }
        if (!original.Content.Observed) { reason = "prior-content-unobserved"; return false; }
        if (next.Content.Token != original.Content.Token) { reason = "original-content-token-mismatch"; return false; }
        if (next.Content.ByteCount != original.Content.ByteCount) { reason = "original-content-size-mismatch"; return false; }
        if (next.Mapping.FootprintAddress != original.Mapping.FootprintAddress) { reason = "original-footprint-address-mismatch"; return false; }
        if (next.Mapping.FootprintByteCount != original.Mapping.FootprintByteCount) { reason = "original-footprint-size-mismatch"; return false; }
        if (next.Mapping.SourceAddress != original.Mapping.SourceAddress) { reason = "original-source-address-mismatch"; return false; }
        if (next.Mapping.SourceByteCount != original.Mapping.SourceByteCount) { reason = "original-source-size-mismatch"; return false; }
        if (!next.Mapping.Segments.SequenceEqual(original.Mapping.Segments)) { reason = "original-segments-mismatch"; return false; }
        reason = "exact-ordered-pending-successor"; return true;
    }
    private static void RequireNativeColorCohortLocked(NativeColorProducer producer)
    {
        var original = producer.Original;
        if (_nativeColorCleanupFailure is not null || _unorderedPresentationWriteSequence != 0 ||
            _latestPresentation is { } presentation && PresentationHasUnorderedGuestWrites(presentation) ||
            _finiteStorageRetiredUnknown || _latestTextureSources.Count != 0 || _guestDepthReadAliases.Count != 0 ||
            _guestImages.Count > 128 || _guestDepthImages.Count > 128 || _selectedStorageMipOwners.Count > 128 ||
            _pendingGuestWorkCount > 128 || _pendingGuestWorkByQueue.Count > 128 || _selectedStorageMipPending.Count != 0 ||
            _selectedStorageMipOwners.Count != _selectedStorageMipImages.Count)
            throw new NotSupportedException("Native color sibling native-writer inventory is unknown, failed or unordered.");
        void Mapping(ICpuMemory memory, long session, ICpuMappedCopySource source, CpuMappedCopyObservation mapping,
            ulong address, ulong bytes)
        {
            if (!ReferenceEquals(memory, original.Placement.Memory) || session != original.Session ||
                !source.IsMappedCopyObservationCurrent(mapping) || bytes == 0 || address > ulong.MaxValue - bytes ||
                address < mapping.FootprintAddress || bytes > mapping.FootprintByteCount ||
                address - mapping.FootprintAddress > mapping.FootprintByteCount - bytes ||
                NativeColorPhysicalOverlap(original.Mapping, original.Placement.PhysicalAddress, original.Placement.ByteCount, mapping, address, bytes))
                throw new NotSupportedException("Native color sibling original physical writer is unknown, stale or overlapping.");
        }
        void Texture(GuestDrawTexture texture)
        {
            if (!StorageImageWrites(texture)) return;
            var origin = StorageCpuOrigin(texture) ?? throw new NotSupportedException("Native color sibling storage writer has no original physical owner.");
            Mapping(origin.Memory, origin.Session, origin.Source, origin.Mapping, origin.Layout.Address, origin.Layout.PhysicalBytes);
        }
        void Buffers(IEnumerable<GuestMemoryBuffer> buffers)
        {
            foreach (var buffer in buffers)
            {
                if (!buffer.Writable || !buffer.WriteBackToGuest || buffer.Length == 0) continue;
                if (!_finiteStorageBufferOrigins.TryGetValue(buffer, out var origin) || buffer.Pooled || buffer.Length <= 0 ||
                    buffer.BaseAddress != origin.Mapping.SourceAddress || (ulong)buffer.Length != origin.Mapping.SourceByteCount ||
                    buffer.Size != (ulong)buffer.Length)
                    throw new NotSupportedException("Native color writable buffer has no original complete physical span.");
                Mapping(origin.Memory, origin.Session, origin.Source, origin.Mapping, origin.Mapping.FootprintAddress, origin.Mapping.FootprintByteCount);
            }
        }
        void Work(object work, PendingGuestWork? future = null)
        {
            if (work is OffscreenGuestDraw boundedDraw && (boundedDraw.Targets.Length > 8 || boundedDraw.Draw.Textures.Length > 128 || boundedDraw.Draw.GlobalMemoryBuffers.Length > 64) ||
                work is ComputeGuestDispatch boundedDispatch && (boundedDispatch.Textures.Length > 128 || boundedDispatch.GlobalMemoryBuffers.Length > 64))
                throw new NotSupportedException("Native color sibling work inventory exceeds its finite bound.");
            if (ReferenceEquals(work, producer.Work))
            {
                // The sealed producer's own attachment alone is exempt. Other
                // storage/global/depth outputs of that draw still need originals.
                foreach (var target in producer.Work.Targets)
                    if (target.Address != original.Placement.PhysicalAddress) Target(target);
                foreach (var texture in producer.Work.Draw.Textures) Texture(texture);
                Buffers(producer.Work.Draw.GlobalMemoryBuffers);
                if (producer.Work.DepthTarget is not null) throw new NotSupportedException("Native selected color producer has an unqualified depth sibling.");
                return;
            }
            if (work is OffscreenGuestDraw draw)
            {
                foreach (var target in draw.Targets) Target(target, future);
                foreach (var texture in draw.Draw.Textures) Texture(texture);
                Buffers(draw.Draw.GlobalMemoryBuffers);
                if (draw.DepthTarget is not null) throw new NotSupportedException("Native color pending depth writer is unqualified.");
            }
            else if (work is ComputeGuestDispatch dispatch)
            { foreach (var texture in dispatch.Textures) Texture(texture); Buffers(dispatch.GlobalMemoryBuffers); }
            else
                throw new NotSupportedException("Native color has an unknown accepted sibling writer.");
        }
        void Target(GuestRenderTarget target, PendingGuestWork? future = null)
        {
            if (target.Address == 0) return; // actual transient native target has no guest/canonical range
            if (!MetalColorMipPlacement.TryGet(target, out var placement) || !_nativeColorOriginalByPlacement.TryGetValue(placement, out var sibling))
                throw new NotSupportedException("Native color sibling attachment lacks its ORIGINAL mapping.");
            if (future is { } acceptedFuture && IsOrderedNativeColorSuccessorLocked(producer, acceptedFuture, sibling)) return;
            Mapping(placement.Memory, sibling.Session, sibling.Source, sibling.Mapping, placement.PhysicalAddress, placement.ByteCount);
        }
        foreach (var queues in _pendingGuestWorkByQueue.Values) foreach (var pending in queues) Work(pending.Work, pending);
        if (_takenGuestWorkSequence != 0)
        {
            if (_takenDepthCubeProducerWork is null) throw new NotSupportedException("Native color taken writer is unknown.");
            Work(_takenDepthCubeProducerWork);
        }
        foreach (var pair in _guestImages)
        {
            if (ReferenceEquals(pair.Value, producer.Image) || pair.Key == original.Placement.PhysicalAddress &&
                _nativeColorLatest.TryGetValue(pair.Key, out var latest) && ReferenceEquals(latest, producer)) continue;
            if (_displayOriginalOwners.TryGetValue(pair.Value, out var display))
            {
                if (!IsDisplayOriginalOwnerCurrentLocked(pair.Value, display, original.Placement.Memory, original.Session))
                    throw new NotSupportedException("Native color sibling display original changed.");
                var copy = display.Copy;
                Mapping(copy.Memory, copy.Session, copy.Source, copy.Mapping, copy.Mapping.FootprintAddress, copy.Mapping.FootprintByteCount);
            }
            else if (pair.Value.StorageCpuOrigin is { } storage)
                Mapping(storage.Memory, storage.Session, storage.Source, storage.Mapping, storage.Layout.Address, storage.Layout.PhysicalBytes);
            else throw NativeColorSiblingFailure("other-native-image-original-missing", pair.Key, pair.Value);
        }
        foreach (var pair in _guestDepthImages)
        {
            if (!_nativeDepthImageOrigins.TryGetValue(pair.Value, out var depth))
                throw NativeColorSiblingFailure("depth-owner-original-missing", pair.Key, pair.Value);
            Mapping(depth.Memory, depth.Session, depth.Source, depth.Mapping, depth.BackingAddress, depth.BackingBytes);
        }
        foreach (var pair in _selectedStorageMipOwners)
        {
            if (!_selectedNativeOriginalProofs.TryGetValue(pair.Key, out var proof) ||
                !_selectedStorageMipImages.TryGetValue(pair.Key, out var image) || !ReferenceEquals(image, proof.Image) ||
                !ReferenceEquals(pair.Value, proof.Owner) || image.Texture != proof.Texture || image.ContentVersion != proof.Version)
                throw new NotSupportedException("Native color sibling selected owner has no original native version.");
            if (proof.Seed.Source is not ICpuMemory selectedMemory) throw new NotSupportedException("Native color selected source has another original memory domain.");
            Mapping(selectedMemory, pair.Value.Session, proof.Seed.Source, proof.Seed.Mapping,
                proof.Seed.Mapping.SourceAddress, proof.Seed.Mapping.SourceByteCount);
        }
        for (var index = 0; index < _finiteStorageRetiredOriginCount; index++)
        {
            var retired = _finiteStorageRetiredOrigins![index]!;
            Mapping(retired.Memory, retired.Session, retired.Source, retired.Mapping, retired.Layout.Address, retired.Layout.PhysicalBytes);
        }
        for (var index = 0; index < _finiteRetiredDisplayOriginCount; index++)
        {
            var retired = _finiteRetiredDisplayOrigins![index]!;
            Mapping(retired.Memory, retired.Session, retired.Source, retired.Mapping, retired.Mapping.FootprintAddress, retired.Mapping.FootprintByteCount);
        }
    }

    internal static bool TryCaptureNativeColorBorrow(ICpuMemory memory, Binding binding, out GuestDrawTexture texture)
    {
        try { return TryCaptureNativeColorBorrowCore(memory, binding, out texture); }
        catch (Exception failure) { TraceNativeColorRefusal(failure); throw; }
    }
    private static bool TryCaptureNativeColorBorrowCore(ICpuMemory memory, Binding binding, out GuestDrawTexture texture)
    {
        texture = null!;
        if (!NativeColorBorrowEnabled || !MetalColorMipPlacement.Enabled) return false;
        var d = binding.Descriptor;
        NativeColorProducer producer;
        lock (_gate)
        {
            if (!_nativeColorLatest.TryGetValue(d.Address, out producer!)) return false;
            if (_closed || _guestWorkFailure is not null || producer.Retired || !producer.Original.ScalarCurrent ||
                !ReferenceEquals(memory, producer.Original.Placement.Memory) || producer.Queue.Name != (_submittingGuestQueue ?? GuestQueueIdentity.Default).Name)
                throw new NotSupportedException("Native color capture lost its original accepted same-queue producer.");
        }
        if (binding.ResourceDescriptor is not { Count: 8 } raw || !TryDecodeTextureDescriptor(raw, out var decoded) || decoded != d ||
            raw[0] != (uint)(d.Address >> 8) || raw[1] != (0xC4700000u | (uint)((d.Address >> 40) & 255)) ||
            raw[2] != 0x007FC07F || raw[3] != 0x91B00FAC || raw[4] != 0 || raw[5] != 0x00700000 || raw[6] != 0 || raw[7] != 0 ||
            binding.Access != GuestImageAccess.Read || binding.IsStorage || binding.IsArrayed || binding.Dimension != 1 || binding.DynamicMip || binding.MipLevel != 0 ||
            binding.Shape.NumericClass != TextureNumericClass.Float || binding.Shape.Storage || binding.Shape.Arrayed || binding.Shape.Volume ||
            binding.Shape.Cube || binding.Shape.OneDimensional || binding.Shape.Multisampled || binding.Shape.DepthCompare || binding.Shape.Atomic ||
            binding.Shape.R128 || binding.Shape.DynamicMip)
            throw new NotSupportedException("Native selected color borrow requires the complete exact standalone sampled 512-square RGBA16Float T#.");
        Span<uint> words = stackalloc uint[8]; for (var i = 0; i < 8; i++) words[i] = raw[i];
        var request = ImageRequestBuilders.Texture(words, binding.Shape).Request.Description;
        if (request.Data.Address != producer.Original.Placement.PhysicalAddress || request.Data.Size != 0x200000 ||
            request.Extent.Width != 512 || request.Extent.Height != 512 || request.Extent.Depth != 1 || request.Pitch != 512 ||
            request.PixelFormat != GpuFormat.R16G16B16A16Sfloat || request.Resources.Levels != 1 || request.Resources.Layers != 1 ||
            request.BytesPerBlock != 8 || request.MipLayout[0].Offset != 0 || request.MipLayout[0].Size != 0x200000 ||
            request.HasMetadata || request.HasStencil || request.IsVolume || request.IsBlock)
            throw new NotSupportedException("Native selected color standalone request differs from its original normalized selected span.");
        RequireNativeColorOriginal(producer.Original);
        texture = new GuestDrawTexture(d.Address, 512, 512, d.Format, d.NumberType, [], IsStorage: false, IsFallback: false,
            Pitch: 512, TileMode: 27, Sampler: ToGuestSampler(binding.SamplerDescriptor), DstSelect: d.DstSelect,
            Type: d.Type, SourceByteCount: 0x200000, Descriptor: raw.ToArray(), Shape: binding.Shape, Access: binding.Access, ResourceMipLevels: 1);
        lock (_gate)
        {
            if (!_nativeColorLatest.TryGetValue(d.Address, out var current) || !ReferenceEquals(current, producer) || producer.Retired)
                throw new NotSupportedException("Native color producer changed during initial capture.");
            RequireNativeColorCohortLocked(producer);
            if (_nativeColorPreparations.Count >= 128) throw new NotSupportedException("Native color sampled preparation inventory is full.");
            var lease = new NativeColorLease(producer);
            _nativeColorPreparations.EnsureCapacity(_nativeColorPreparations.Count + 1);
            _nativeColorLeases.Add(texture, lease);
            try
            {
                if (producer.DependentReferences == int.MaxValue) throw new NotSupportedException("Native color sampled dependency is full.");
                RetainNativeColorOriginalLocked(producer.Original);
                producer.DependentReferences++;
                _nativeColorPreparations.Add(lease);
            }
            catch { _nativeColorLeases.Remove(texture); throw; }
        }
        return true;
    }
    private static void CopyNativeColorLease(GuestDrawTexture original, GuestDrawTexture copy)
    {
        if (!_nativeColorLeases.TryGetValue(original, out var lease)) return;
        if (lease.PreparationReleased || original.Descriptor is not { Length: 8 } raw || copy.Descriptor is not { Length: 8 } other ||
            !raw.AsSpan().SequenceEqual(other) || (copy with { Sampler = original.Sampler, Descriptor = original.Descriptor }) != original)
            throw new NotSupportedException("Native color copied binding changed its ORIGINAL sampled view.");
        _nativeColorLeases.Add(copy, lease); // SAME one-use preparation lease across frontend clones
    }
    internal static void ReleaseFailedPreparedNativeColorTextures(IReadOnlyList<GuestDrawTexture> textures, GuestDrawTexture? unreturned, Exception primary)
    {
        _ = ReleasePreparedNativeColorTextures(textures, primary);
        if (unreturned is not null) _ = ReleasePreparedNativeColorTextures([unreturned], primary);
    }
    private static Exception? ReleasePreparedNativeColorTextures(IReadOnlyList<GuestDrawTexture> textures, Exception? primary)
    {
        lock (_gate)
        {
            foreach (var texture in textures)
            {
                if (!_nativeColorLeases.TryGetValue(texture, out var lease) || lease.PreparationReleased) continue;
                lease.PreparationReleased = true;
                _nativeColorPreparations.Remove(lease);
                try { ReleaseNativeColorOriginalLocked(lease.Producer.Original, primary); }
                catch (Exception cleanup) { primary ??= cleanup; }
                try
                {
                    if (lease.Producer.DependentReferences <= 0) throw new InvalidOperationException("Unbalanced sampled color dependency.");
                    lease.Producer.DependentReferences--;
                    RetireNativeColorProducerLocked(lease.Producer, primary);
                }
                catch (Exception cleanup) { primary ??= cleanup; }
            }
        }
        return primary;
    }
    private static bool TryResolveNativeColorBorrow(nint commandBuffer, GuestDrawTexture texture, out nint handle)
    {
        try { return TryResolveNativeColorBorrowCore(commandBuffer, texture, out handle); }
        catch (Exception failure) { TraceNativeColorRefusal(failure); throw; }
    }
    private static bool TryResolveNativeColorBorrowCore(nint commandBuffer, GuestDrawTexture texture, out nint handle)
    {
        handle = 0;
        if (!_nativeColorLeases.TryGetValue(texture, out var lease)) return false;
        var producer = lease.Producer;
        RequireNativeColorOriginal(producer.Original);
        if (commandBuffer == 0 || !MetalNative.SendBool(commandBuffer, MetalNative.Selector("retainedReferences")))
            throw new NotSupportedException("Native color consumer requires its actual retaining command buffer.");
        nint wait = 0;
        lock (_gate)
        {
            if (!producer.Encoded || producer.Retired || producer.Batch is not { } batch || producer.Texture == 0)
                throw new NotSupportedException("Native color source has not sealed its exact successful encoder.");
            if (producer.CommandBuffer != commandBuffer)
            {
                if (!batch.Submitted || !batch.Completed && (batch.Handle == 0 || _guestCommandBufferStatusOverride is not null || _guestCommandBufferWaitOverride is not null))
                    throw new NotSupportedException("Native color preceding batch has no real submission/completion owner.");
                if (!batch.Completed) wait = MetalNative.Send(batch.Handle, MetalNative.Selector("retain"));
                if (!batch.Completed && wait == 0) throw new InvalidOperationException("Native color completion retain failed.");
            }
            else if (batch.Submitted) throw new NotSupportedException("Native color same-CB source cannot use an already submitted encoder.");
        }
        if (wait != 0)
        {
            Exception? failure = null;
            try { WaitForCommittedCommandBuffer(wait); }
            catch (Exception error) { failure = error; }
            ReleaseDepthObjectsPreservingFailure(failure, wait);
        }
        RequireNativeColorOriginal(producer.Original);
        lock (_gate)
        {
            var image = producer.Image;
            if (_closed || _guestWorkFailure is not null || producer.Retired || !producer.Original.ScalarCurrent ||
                image is null || image.Texture != producer.Texture || image.ContentVersion != producer.Version ||
                !_guestImages.TryGetValue(texture.Address, out var current) || !ReferenceEquals(current, image) ||
                producer.NativeQueue != _batchQueue || !ReferenceEquals(_guestMemory, producer.Original.Placement.Memory) ||
                !(producer.CommandBuffer == commandBuffer && producer.Batch is { Submitted: false } || producer.Batch is { Completed: true }))
                throw new NotSupportedException("Native color borrow lost its exact ORIGINAL image/version/session/native queue.");
            RequireNativeColorCohortLocked(producer);
            handle = MetalNative.Send(producer.Texture, MetalNative.Selector("retain"));
            if (handle == 0) throw new InvalidOperationException("Native color source retain failed.");
        }
        try
        {
            RequireNativeColorOriginal(producer.Original);
            lock (_gate)
            {
                if (producer.Retired || !producer.Original.ScalarCurrent || producer.Image?.ContentVersion != producer.Version ||
                    producer.Image.Texture != handle || _closed || _nativeColorCleanupFailure is not null)
                    throw new NotSupportedException("Native color original owner changed during retained borrow.");
            }
        }
        catch (Exception primary)
        {
            var unaccepted = handle; handle = 0;
            ReleaseDepthObjectsPreservingFailure(primary, unaccepted);
            throw;
        }
        return true; // caller owns retain until its argument binding reaches retaining CB
    }
    private static void RetireNativeColorImageLocked(GuestImage image)
    {
        foreach (var producer in _nativeColorLatest.Values.Where(p => ReferenceEquals(p.Image, image)).ToArray())
        {
            _nativeColorLatest.Remove(producer.Original.Placement.PhysicalAddress);
            producer.IsLatest = false;
            RetireNativeColorProducerLocked(producer, _guestWorkFailure);
        }
    }
    private static void RetireNativeColorFromRenderOwner()
    {
        if (!NativeColorBorrowEnabled) return;
        Exception? failure = null;
        lock (_gate)
        {
            if (!_closed || !ReferenceEquals(_renderOwner, Thread.CurrentThread))
                throw new InvalidOperationException("Native color close requires the actual closed render owner.");
            try { CancelQueuedNativeColorLocked(); } catch (Exception error) { failure = error; }
            foreach (var lease in _nativeColorPreparations.ToArray())
            {
                lease.PreparationReleased = true; _nativeColorPreparations.Remove(lease);
                try { ReleaseNativeColorOriginalLocked(lease.Producer.Original, failure); } catch (Exception error) { failure ??= error; }
                try
                {
                    if (lease.Producer.DependentReferences <= 0) throw new InvalidOperationException("Unbalanced close color dependency.");
                    lease.Producer.DependentReferences--;
                    RetireNativeColorProducerLocked(lease.Producer, failure);
                }
                catch (Exception error) { failure ??= error; }
            }
            foreach (var original in _nativeColorOriginals.ToArray())
            {
                if (original.PreparationReleased) continue;
                original.PreparationReleased = true;
                try { ReleaseNativeColorOriginalLocked(original, failure); } catch (Exception error) { failure ??= error; }
            }
            foreach (var pair in _nativeColorBatches.ToArray())
            {
                if (pair.Value.Submitted && !pair.Value.Completed) continue; // sticky owned quarantine until process exit
                var batch = pair.Value;
                _nativeColorBatches.Remove(pair.Key);
                // Unsubmitted partial work is not committed during teardown.
                foreach (var producer in _nativeColorProducers.Where(p => ReferenceEquals(p.Batch, batch) && p.Retired).ToArray())
                    try { ReleaseNativeColorTextureLocked(producer, failure); } catch (Exception error) { failure ??= error; }
                var owned = batch.Handle; batch.Handle = 0;
                if (owned != 0) try { ReleaseDepthObjectsPreservingFailure(null, owned); } catch (Exception error) { failure ??= error; }
            }
        }
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private static readonly bool _nativeColorDiagnostics = Environment.GetEnvironmentVariable("SHARPEMU_NATIVE_COLOR_DIAGNOSTICS") == "1";
    private static int _nativeColorRefusalRows;
    private sealed class NativeColorSiblingException(string reason, ulong address, GuestImage image) :
        NotSupportedException("Native selected color sibling has no original physical provenance; no CPU/address fallback is qualified.")
    {
        internal readonly string Reason = reason;
        internal readonly ulong Address = address;
        internal readonly uint Width = image.Width, Height = image.Height, Depth = image.Depth, Layers = image.ArrayLength;
        internal readonly uint Type = image.TextureType;
        internal readonly MtlPixelFormat Format = image.Format;
        internal readonly int Version = image.ContentVersion;
        internal readonly bool GpuWritten = image.GpuWritten, Initialized = image.Initialized;
    }
    private static NotSupportedException NativeColorSiblingFailure(string reason, ulong address, GuestImage image) =>
        _nativeColorDiagnostics ? new NativeColorSiblingException(reason, address, image) : new NotSupportedException("Native selected color sibling has no original physical provenance.");
    private static void TraceNativeColorRefusal(Exception failure)
    {
        if (!_nativeColorDiagnostics || Monitor.IsEntered(_gate) || failure is not NativeColorSiblingException sibling) return;
        if (Interlocked.Increment(ref _nativeColorRefusalRows) > 64) return;
        try
        {
            Console.Error.WriteLine($"[MetalNativeColorRefusal] reason={sibling.Reason} addr=0x{sibling.Address:X} shape={sibling.Width}x{sibling.Height}x{sibling.Depth} layers={sibling.Layers} native_type={sibling.Type} format={sibling.Format} version={sibling.Version} gpu_written={sibling.GpuWritten} initialized={sibling.Initialized}");
        }
        catch { } // optional logging cannot replace the original guard
    }

}
