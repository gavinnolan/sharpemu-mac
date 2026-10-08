// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
using System.Runtime.CompilerServices;
using SharpEmu.HLE;
using SharpEmu.Libs.Gpu;
using SharpEmu.Libs.Gpu.Images;
using static SharpEmu.Libs.Agc.AgcBackendServices;
using Binding = SharpEmu.Libs.Agc.AgcExports.TranslatedImageBinding;

namespace SharpEmu.Gpu.Metal;

internal static partial class MetalVideoPresenter
{
    // Reuse the actual existing presenter-owner lifetime, advanced by the real
    // ReleaseSelectedStorageMipImages exit path. This base has no typed CPU seed
    // API. Mapping/provider and exact memory identity remain separate vetoes.
    internal static long CurrentNativeDepthFaceSession => Volatile.Read(ref _selectedStorageMipSession);
    // Accepted logical producer and sealed native encoder identity are distinct.
    // Encoded/Committed are NOT status4 or guest completion receipts. Sampling
    // uses later encoders in that SAME CB, or an actual successful status4
    // receipt from the original preceding batch on the SAME native queue.
    private sealed class NativeDepthFaceProducer(OffscreenGuestDraw work,
        MetalDepthFacePlacement.Observation original, long sequence, GuestQueueIdentity queue)
    {
        internal readonly OffscreenGuestDraw Work = work;
        internal readonly MetalDepthFacePlacement.Observation Original = original;
        internal readonly long Sequence = sequence;
        internal readonly GuestQueueIdentity Queue = queue;
        internal GuestImage? Image;
        internal nint Texture, NativeQueue, CommandBuffer;
        internal int Version;
        internal bool Encoded, Committed;
        internal NativeDepthBatch? Batch;
    }
    private sealed class NativeDepthBatch(nint ownedHandle)
    {
        internal nint OwnedHandle = ownedHandle;
        internal bool Submitted, Completed;
    }
    private static readonly Dictionary<nint, NativeDepthBatch> _nativeDepthBatches = [];
    private sealed record NativeDepthCubeCapture(ICpuMemory Memory, long Session,
        NativeDepthFaceProducer[] Faces);
    private static readonly ConditionalWeakTable<object, NativeDepthFaceProducer> _nativeDepthFaceWorks = new();
    private static readonly Dictionary<ulong, NativeDepthFaceProducer> _nativeDepthFaces = [];
    private static readonly ConditionalWeakTable<GuestDrawTexture, NativeDepthCubeCapture> _nativeDepthCubeCaptures = new();
    private static readonly List<NativeDepthFaceProducer> _nativeDepthUncommitted = [];

    private static NativeDepthFaceProducer? PrepareNativeDepthFaceLocked(object work, long sequence, GuestQueueIdentity queue)
    {
        if (!MetalDepthFacePlacement.Enabled || work is not OffscreenGuestDraw { DepthTarget: { } target } draw ||
            !MetalDepthFacePlacement.TryGet(target, out var original)) return null;
        if (_closed || _guestWorkFailure is not null || !ReferenceEquals(_guestMemory, original.Memory) ||
            original.Session != CurrentNativeDepthFaceSession || _nativeDepthFaces.Count >= 128 && !_nativeDepthFaces.ContainsKey(original.Address))
            throw new NotSupportedException("Metal native depth face admission is closed, changed or saturated.");
        if (draw.Targets.Length == 0 || draw.Targets.Any(color => color.Width != 1024 || color.Height != 1024) ||
            target.Width != 1024 || target.Height != 1024 || target.Address != original.Address ||
            target.HasStencil || target.MetadataClear)
            throw new NotSupportedException("Metal native cube face draw cannot expand the normalized depth attachment or write stencil/metadata.");
        MetalDepthFacePlacement.RequireOriginalCurrent(original);
        return new(draw, original, sequence, queue);
    }
    private static void PublishNativeDepthFaceLocked(NativeDepthFaceProducer? producer)
    {
        if (producer is null) return;
        if (_nativeDepthFaceWorks.TryGetValue(producer.Work, out _))
            throw new InvalidOperationException("Metal native depth face work was already accepted.");
        _nativeDepthFaces.TryGetValue(producer.Original.Address, out var previous);
        _nativeDepthFaceWorks.Add(producer.Work, producer);
        try { _nativeDepthFaces[producer.Original.Address] = producer; }
        catch
        {
            _nativeDepthFaceWorks.Remove(producer.Work);
            if (_nativeDepthFaces.TryGetValue(producer.Original.Address, out var current) && ReferenceEquals(current, producer))
            {
                if (previous is null) _nativeDepthFaces.Remove(producer.Original.Address);
                else _nativeDepthFaces[producer.Original.Address] = previous;
            }
            throw;
        }
    }

    private static void RequireNativeDepthFaceExecution(OffscreenGuestDraw work, bool successful, string stage)
    {
        if (!successful && MetalDepthFacePlacement.Enabled && _nativeDepthFaceWorks.TryGetValue(work, out _))
            throw new InvalidOperationException($"Metal admitted native depth face could not execute: {stage}.");
    }
    private static void ValidateNativeDepthFaceOwner(GuestDepthTarget target, GuestImage? existing)
    {
        if (!MetalDepthFacePlacement.TryGet(target, out var incoming)) return;
        MetalDepthFacePlacement.RequireOriginalCurrent(incoming);
        if (existing is null) return;
        if (!_nativeDepthImageOrigins.TryGetValue(existing, out var original) ||
            !ReferenceEquals(original.Memory, incoming.Memory) || original.Session != incoming.Session ||
            original.Address != incoming.Address || original.BackingAddress != incoming.BackingAddress ||
            original.Registers.ZInfo != incoming.Registers.ZInfo || original.Registers.DepthView != incoming.Registers.DepthView ||
            original.Registers.DepthSizeXy != incoming.Registers.DepthSizeXy || original.Registers.HtileBase != incoming.Registers.HtileBase)
            throw new NotSupportedException("Metal depth face cannot adopt a legacy or different original native owner.");
        MetalDepthFacePlacement.RequireOriginalCurrent(original); // never replace original with incoming stamp
    }
    private static readonly ConditionalWeakTable<GuestImage, MetalDepthFacePlacement.Observation> _nativeDepthImageOrigins = new();
    private static readonly ConditionalWeakTable<GuestImage, MetalDepthFacePlacement.DepthSpan> _nativeDepthImageSpans = new();

    private static void SealNativeDepthFace(OffscreenGuestDraw work, GuestImage image, nint commandBuffer, nint queue)
    {
        if (!_nativeDepthFaceWorks.TryGetValue(work, out var producer)) return;
        if (commandBuffer == 0 || !MetalNative.SendBool(commandBuffer, MetalNative.Selector("retainedReferences")))
            throw new NotSupportedException("Metal native depth producer requires its actual retaining command buffer.");
        MetalDepthFacePlacement.RequireOriginalCurrent(producer.Original);
        lock (_gate)
        {
            if (_closed || _guestWorkFailure is not null || producer.Encoded ||
                !ReferenceEquals(_guestMemory, producer.Original.Memory) || producer.Original.Session != CurrentNativeDepthFaceSession ||
                !_guestDepthImages.TryGetValue(producer.Original.Address, out var owner) || !ReferenceEquals(owner, image) ||
                !_nativeDepthImageOrigins.TryGetValue(image, out var original) || original.Address != producer.Original.Address ||
                commandBuffer != _batchCommandBuffer || queue == 0 || queue != _batchQueue ||
                image.Texture == 0 || !image.Initialized || image.Width != 1024 || image.Height != 1024 ||
                image.Format != MtlPixelFormat.Depth32Float || image.TextureType != MtlTextureType2D || image.Depth != 1 || image.ArrayLength != 1 ||
                _nativeDepthUncommitted.Count >= MaxPendingGuestWork + 1)
                throw new NotSupportedException("Metal native depth producer owner/session/encoder receipt changed.");
            producer.Image = image; producer.Texture = image.Texture; producer.Version = image.ContentVersion;
            if (!_nativeDepthBatches.TryGetValue(commandBuffer, out var batch))
            {
                if (_nativeDepthBatches.Count >= 128)
                    throw new NotSupportedException("Metal native depth batch retention is saturated; no completion is inferred.");
                var owned = MetalNative.Send(commandBuffer, MetalNative.Selector("retain"));
                if (owned == 0) throw new InvalidOperationException("Metal native depth batch retain failed.");
                batch = new(owned);
                try { _nativeDepthBatches.Add(commandBuffer, batch); }
                catch (Exception error) { batch.OwnedHandle = 0; ReleaseDepthObjectsPreservingFailure(error, owned); throw; }
            }
            producer.Batch = batch;
            producer.CommandBuffer = commandBuffer; producer.NativeQueue = queue; producer.Encoded = true;
            _nativeDepthUncommitted.Add(producer);
        }
    }
    private static void MarkNativeDepthFaceBatchCommitted(nint commandBuffer)
    {
        if (!MetalDepthFacePlacement.Enabled) return;
        lock (_gate)
            for (var index = _nativeDepthUncommitted.Count - 1; index >= 0; index--)
                if (_nativeDepthUncommitted[index].CommandBuffer == commandBuffer)
                {
                    _nativeDepthUncommitted[index].Committed = true;
                    _nativeDepthUncommitted[index].Batch!.Submitted = true;
                    _nativeDepthUncommitted.RemoveAt(index);
                }
    }
    // Only the real status4 wait path calls this. Synthetic status/wait overrides
    // cannot create a successful native receipt or release an actual native owner.
    private static void ObserveNativeDepthBatchCompleted(nint commandBuffer)
    {
        if (!MetalDepthFacePlacement.Enabled || _guestCommandBufferStatusOverride is not null || _guestCommandBufferWaitOverride is not null) return;
        nint owned = 0;
        lock (_gate)
            if (_nativeDepthBatches.TryGetValue(commandBuffer, out var batch))
            {
                if (!batch.Submitted) throw new InvalidOperationException("Metal native depth completion has no actual submission receipt.");
                _nativeDepthBatches.Remove(commandBuffer);
                batch.Completed = true;
                owned = batch.OwnedHandle;
                batch.OwnedHandle = 0; // consume before uncertain release; never retry by pointer
            }
        if (owned != 0) ReleaseDepthObjectsPreservingFailure(null, owned);
    }

    // Exact finite construction retains only. Removing the original association
    // and consuming both handles precedes callbacks, so reentry/uncertain release
    // cannot re-return a historical owner. Retaining CBs own already encoded use.
    private static bool ReleaseOriginalNativeDepthImage(GuestImage image, Exception? primary = null)
    {
        if (!_nativeDepthImageOrigins.Remove(image)) return false;
        _nativeDepthImageSpans.Remove(image);
        var texture = image.Texture; image.Texture = 0;
        var snapshot = image.SnapshotTexture; image.SnapshotTexture = 0;
        ReleaseDepthObjectsPreservingFailure(primary, snapshot, texture);
        return true;
    }

    // Run-only unwind, after its render callback has returned. A logical close
    // never manufactures completion: submitted/unconfirmed batches remain in a
    // bounded quarantine. Unsubmitted CB retains may be released without ever
    // committing their partial encoders. A new presenter cannot adopt old faces.
    private static void RetireNativeDepthCubeFromRenderOwner()
    {
        if (!MetalDepthFacePlacement.Enabled) return;
        List<nint> owned = [];
        lock (_gate)
        {
            if (!_closed || !ReferenceEquals(_renderOwner, Thread.CurrentThread))
                throw new InvalidOperationException("Metal native depth teardown requires the actual closed render owner.");
            var batches = _nativeDepthBatches.ToArray();
            var images = _nativeDepthImageOrigins.ToArray();
            owned.EnsureCapacity(checked(batches.Length + 2 * images.Length));
            foreach (var pair in batches)
            {
                if (pair.Value.Submitted && !pair.Value.Completed) continue; // retained unknown; never forced completed
                _nativeDepthBatches.Remove(pair.Key);
                if (pair.Value.OwnedHandle != 0) owned.Add(pair.Value.OwnedHandle);
                pair.Value.OwnedHandle = 0;
            }
            foreach (var pair in images)
            {
                var image = pair.Key;
                _nativeDepthImageOrigins.Remove(image); _nativeDepthImageSpans.Remove(image);
                if (_guestDepthImages.TryGetValue(pair.Value.Address, out var current) && ReferenceEquals(current, image))
                    _guestDepthImages.Remove(pair.Value.Address);
                if (image.SnapshotTexture != 0) owned.Add(image.SnapshotTexture);
                if (image.Texture != 0) owned.Add(image.Texture);
                image.SnapshotTexture = 0; image.Texture = 0;
            }
            _nativeDepthFaceWorks.Clear(); _nativeDepthFaces.Clear();
            _nativeDepthUncommitted.Clear(); // no native status/completion claim
        }
        Exception? failure = null;
        foreach (var handle in owned)
        {
            try { ReleaseDepthObjectsPreservingFailure(null, handle); }
            catch (Exception cleanup)
            {
                if (failure is null) failure = cleanup;
                else { try { failure.Data["Metal native depth teardown cleanup " + handle] = cleanup; } catch (Exception) { } }
            }
        }
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }

    // Must precede all CPU snapshot shortcuts. A partial matching native set
    // cannot become a CPU cube: the existing overlap rejection remains active.
    internal static bool TryCaptureNativeDepthCube(ICpuMemory memory, Binding binding, out GuestDrawTexture texture)
    {
        texture = null!;
        if (!MetalDepthFacePlacement.Enabled || !binding.Shape.DepthCompare || !binding.Shape.Cube) return false;
        var d = binding.Descriptor;
        if (d.Width != 1024 || d.Height != 1024 || d.Depth != 6 || d.Format != 2 || d.NumberType != 0 ||
            d.Type != Gen5TextureTypeCube || d.TileMode != 24 || d.Pitch != 1024 || d.ResourceMipLevels != 1 ||
            d.BaseLevel != 0 || d.LastLevel != 0 || d.BaseArray != 0 || d.DstSelect != 0x924 ||
            binding.ResourceDescriptor is not { Count: 8 } raw ||
            raw[0] != (uint)(d.Address >> 8) || raw[1] != ((uint)(d.Address >> 40) | 0xC0700000) ||
            raw[2] != 0x00FFC0FF || raw[3] != 0xB1800924 || raw[4] != 5 || raw[5] != 0x00700000 || raw[6] != 0 || raw[7] != 0) return false;
        lock (_gate)
        {
            var queue = _submittingGuestQueue ?? GuestQueueIdentity.Default;
            RejectNativeDepthCubeSiblingWritersLocked(memory, d.Address, 6 * MetalDepthFacePlacement.FaceBytes);
            var faces = new NativeDepthFaceProducer[6];
            for (var face = 0; face < 6; face++)
            {
                var address = checked(d.Address + (ulong)face * MetalDepthFacePlacement.FaceBytes);
                if (!_nativeDepthFaces.TryGetValue(address, out var producer)) return false;
                if (_closed || _guestWorkFailure is not null || !ReferenceEquals(producer.Original.Memory, memory) ||
                    producer.Original.Session != CurrentNativeDepthFaceSession || producer.Queue.Name != queue.Name || producer.Sequence <= 0)
                    throw new NotSupportedException("Metal native cube capture lost its original six accepted same-queue face producers.");
                MetalDepthFacePlacement.RequireOriginalCurrent(producer.Original);
                faces[face] = producer;
            }
            texture = new GuestDrawTexture(d.Address, 1024, 1024, 2, 0, [], IsFallback: false, IsStorage: false,
                Pitch: 1024, TileMode: 24, Sampler: ToGuestSampler(binding.SamplerDescriptor), DstSelect: d.DstSelect,
                ArrayedView: true, Type: Gen5TextureTypeCube, Depth: 1, ArrayLayers: 6,
                SourceByteCount: 6 * MetalDepthFacePlacement.FaceBytes, Descriptor: binding.ResourceDescriptor.ToArray(),
                Shape: binding.Shape, Access: binding.Access, ResourceMipLevels: 1);
            _nativeDepthCubeCaptures.Add(texture, new(memory, CurrentNativeDepthFaceSession, faces));
            return true;
        }
    }

    private static void RejectNativeDepthCubeSiblingWritersLocked(ICpuMemory memory, ulong address, ulong bytes)
    {
        void Range(ulong writer, ulong count)
        {
            if (writer != 0 && (count == 0 || count > ulong.MaxValue - writer || DepthCubeOverlap(address, bytes, writer, count)))
                throw new NotSupportedException("Metal native depth cube overlaps another accepted writer or its original extent is unknown.");
        }
        void Work(object work)
        {
            if (work is not OffscreenGuestDraw draw)
            {
                RejectDepthCubeAcceptedWriterLocked(address, bytes, work);
                return;
            }
            foreach (var target in draw.Targets)
            {
                if (target.Address == 0) continue;
                if (!MetalDepthFacePlacement.TryGetColorSpan(target, out var span) || !ReferenceEquals(span.Memory, memory))
                    Range(target.Address, 0);
                else Range(span.Address, span.Bytes);
            }
            foreach (var texture in draw.Draw.Textures)
                if (StorageImageWrites(texture)) Range(texture.Address, texture.SourceByteCount);
            foreach (var buffer in draw.Draw.GlobalMemoryBuffers)
                if (buffer.Writable && buffer.WriteBackToGuest && buffer.Length != 0)
                    Range(buffer.BaseAddress, buffer.Length > 0 ? checked((ulong)buffer.Length) : 0);
            if (draw.DepthTarget is { } depth)
            {
                if (!_nativeDepthFaceWorks.TryGetValue(work, out var producer))
                {
                    if (MetalDepthFacePlacement.TryGetDepthSpan(depth, out var span) && ReferenceEquals(span.Memory, memory))
                        Range(span.Address, span.Bytes);
                    else Range(depth.Address, 0);
                }
                else if (!ReferenceEquals(producer.Original.Memory, memory)) Range(depth.Address, 0);
                // Exact normalized depth producers supply this cube. Other known
                // face producers must be disjoint; no legacy depth owner is adopted.
                else if (DepthCubeOverlap(address, bytes, producer.Original.Address, MetalDepthFacePlacement.FaceBytes) &&
                    (producer.Original.Address < address || producer.Original.Address >= address + bytes ||
                     (producer.Original.Address - address) % MetalDepthFacePlacement.FaceBytes != 0))
                    throw new NotSupportedException("Metal native cube overlaps a differently placed accepted depth producer.");
            }
        }
        foreach (var pendingQueue in _pendingGuestWorkByQueue.Values)
            foreach (var pending in pendingQueue) Work(pending.Work);
        if (_takenGuestWorkSequence != 0)
        {
            if (_takenDepthCubeProducerWork is null) throw new NotSupportedException("Metal native cube has an unknown taken writer.");
            Work(_takenDepthCubeProducerWork);
        }
        foreach (var entry in _guestImages)
            if (entry.Value.GpuWritten)
                Range(entry.Key, _guestImageExtents.TryGetValue(entry.Key, out var extent) ? extent.ByteCount : 0);
        foreach (var entry in _guestDepthImages)
        {
            if (_nativeDepthImageOrigins.TryGetValue(entry.Value, out var face) && ReferenceEquals(face.Memory, memory) &&
                face.Address >= address && face.Address < address + bytes && (face.Address - address) % MetalDepthFacePlacement.FaceBytes == 0)
                continue; // exact retained face origin; sealed version is checked separately
            if (_nativeDepthImageSpans.TryGetValue(entry.Value, out var span) && ReferenceEquals(span.Memory, memory))
                Range(span.Address, span.Bytes);
            else Range(entry.Key, 0);
        }
    }
    private static void CopyNativeDepthCubeCapture(GuestDrawTexture source, GuestDrawTexture copy)
    {
        if (MetalDepthFacePlacement.Enabled && _nativeDepthCubeCaptures.TryGetValue(source, out var capture))
        {
            if (copy.Descriptor is not { Length: 8 } raw || source.Descriptor is not { Length: 8 } originalRaw ||
                !raw.AsSpan().SequenceEqual(originalRaw) ||
                copy.RgbaPixels.Length != 0 || source.RgbaPixels.Length != 0 ||
                (copy with { Sampler = source.Sampler, Descriptor = source.Descriptor, RgbaPixels = source.RgbaPixels }) != source)
                throw new NotSupportedException("Metal native cube copy changed its original typed face view.");
            _nativeDepthCubeCaptures.Add(copy, capture);
        }
    }

    private static bool TryAssembleNativeDepthCube(nint device, nint commandBuffer, GuestDrawTexture texture, out nint result)
    {
        result = 0;
        if (!MetalDepthFacePlacement.Enabled || !_nativeDepthCubeCaptures.TryGetValue(texture, out var capture)) return false;
        ValidateDepthComparisonTexture(texture, commandBuffer);
        if (!MetalNative.SendBool(commandBuffer, MetalNative.Selector("retainedReferences")))
            throw new NotSupportedException("Metal native depth cube consumer requires retaining command-buffer references.");
        foreach (var producer in capture.Faces)
        {
            if (producer.CommandBuffer == commandBuffer && !producer.Committed) continue; // same-CB encoder order
            nint borrowed = 0;
            lock (_gate)
            {
                if (!producer.Encoded || producer.Batch is not { Submitted: true } batch)
                    throw new NotSupportedException("Metal native depth cube face was not encoded/submitted.");
                if (!batch.Completed)
                {
                    if (_guestCommandBufferStatusOverride is not null || _guestCommandBufferWaitOverride is not null || batch.OwnedHandle == 0)
                        throw new NotSupportedException("Metal native depth cube cannot use synthetic completion or a missing original command-buffer owner.");
                    borrowed = MetalNative.Send(batch.OwnedHandle, MetalNative.Selector("retain"));
                    if (borrowed == 0) throw new InvalidOperationException("Metal original depth batch borrow failed.");
                }
            }
            if (borrowed != 0)
            {
                Exception? waitFailure = null;
                try { WaitForCommittedCommandBuffer(borrowed); }
                catch (Exception error) { waitFailure = error; }
                ReleaseDepthObjectsPreservingFailure(waitFailure, borrowed);
            }
            if (producer.Batch is not { Completed: true })
                throw new NotSupportedException("Metal native depth cube requires actual successful completion of each preceding native batch.");
        }
        foreach (var face in capture.Faces) MetalDepthFacePlacement.RequireOriginalCurrent(face.Original);
        Span<nint> handles = stackalloc nint[6]; handles.Clear();
        Span<bool> owns = stackalloc bool[6]; owns.Clear();
        Exception? failure = null;
        try
        {
            lock (_gate)
            {
                if (_closed || _guestWorkFailure is not null || !ReferenceEquals(_guestMemory, capture.Memory) || capture.Session != CurrentNativeDepthFaceSession)
                    throw new NotSupportedException("Metal native cube consumer lifetime changed.");
                RejectNativeDepthCubeSiblingWritersLocked(capture.Memory, texture.Address, texture.SourceByteCount);
                for (var face = 0; face < 6; face++)
                {
                    var producer = capture.Faces[face];
                    var image = producer.Image;
                    if (!producer.Encoded || image is null || image.Texture != producer.Texture || image.ContentVersion != producer.Version ||
                        !_guestDepthImages.TryGetValue(producer.Original.Address, out var owner) || !ReferenceEquals(owner, image) ||
                        producer.NativeQueue != _batchQueue ||
                        !(producer.CommandBuffer == commandBuffer && !producer.Committed || producer.Batch is { Completed: true }))
                        throw new NotSupportedException("Metal native cube face has no exact original ordered native producer version.");
                    handles[face] = MetalNative.Send(producer.Texture, MetalNative.Selector("retain"));
                    if (handles[face] == 0) throw new InvalidOperationException("Metal native depth cube face retain failed.");
                    owns[face] = true;
                }
            }
            result = AcquireSnapshotTexture(device, MtlPixelFormat.Depth32Float, 1024, 1024,
                (nint)UsageShaderRead, MtlTextureType2DArray, 6, 1, privateStorage: true);
            if (result == 0) throw new InvalidOperationException("Metal native depth cube array allocation failed.");
            var blit = MetalNative.Send(commandBuffer, MetalNative.Selector("blitCommandEncoder"));
            if (blit == 0) throw new InvalidOperationException("Metal native depth cube blit encoder creation failed.");
            Exception? blitFailure = null;
            try
            {
                for (var face = 0; face < 6; face++)
                    MetalNative.SendCopyTextureSlice(blit, MetalNative.Selector(
                        "copyFromTexture:sourceSlice:sourceLevel:sourceOrigin:sourceSize:toTexture:destinationSlice:destinationLevel:destinationOrigin:"),
                        handles[face], 0, 0, default, new MtlSize { Width = 1024, Height = 1024, Depth = 1 }, result, (nuint)face, 0, default);
            }
            catch (Exception error) { blitFailure = error; }
            try { MetalNative.SendVoid(blit, MetalNative.Selector("endEncoding")); }
            catch (Exception cleanup)
            {
                if (blitFailure is null) blitFailure = cleanup;
                else { try { blitFailure.Data["Metal native depth cube endEncoding cleanup"] = cleanup; } catch (Exception) { } }
            }
            if (blitFailure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(blitFailure).Throw();
            foreach (var face in capture.Faces) MetalDepthFacePlacement.RequireOriginalCurrent(face.Original);
            lock (_gate)
            {
                if (_closed || _guestWorkFailure is not null || capture.Session != CurrentNativeDepthFaceSession ||
                    !ReferenceEquals(_guestMemory, capture.Memory) || capture.Faces.Any(face => face.Image is null ||
                        face.Image.Texture != face.Texture || face.Image.ContentVersion != face.Version))
                    throw new NotSupportedException("Metal native depth cube changed during ordered assembly.");
            }
        }
        catch (Exception error) { failure = error; }
        ReleaseComputeTemporaryTextures(handles, owns, failure);
        return true;
    }
}
