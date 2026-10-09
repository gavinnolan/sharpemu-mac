// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using SharpEmu.HLE;
using SharpEmu.Libs.Gpu;
using static SharpEmu.Libs.Agc.AgcBackendServices;
using Binding = SharpEmu.Libs.Agc.AgcExports.TranslatedImageBinding;

namespace SharpEmu.Gpu.Metal;

internal static partial class MetalVideoPresenter
{
    private sealed class NativeR32Snapshot(OrdinaryStorageProducer producer, GuestDrawTexture descriptor, GuestQueueIdentity queue)
    {
        internal readonly OrdinaryStorageProducer Producer = producer;
        internal readonly GuestDrawTexture Descriptor = descriptor;
        internal readonly GuestQueueIdentity Queue = queue;
        internal int References = 1; // exact frontend preparation, independent queued owners below
        internal long CopySequence;
        internal nint Texture, CommandBuffer;
        internal bool PreparationReleased, Consumed, Sealed, CompletionUnknown;
    }
    private static Action<nint>? _nativeR32AfterCopyCommit; // default-null actual native failure fixture seam
    private static readonly HashSet<NativeR32Snapshot> _nativeR32Snapshots = new(ReferenceEqualityComparer.Instance);
    private static readonly ConditionalWeakTable<GuestDrawTexture, NativeR32Snapshot> _nativeR32Samples = new();
    private static readonly ConditionalWeakTable<Action, NativeR32Snapshot> _nativeR32CopyActions = new();
    private static readonly Dictionary<object, NativeR32Snapshot[]> _nativeR32Queued = new(ReferenceEqualityComparer.Instance);

    internal static bool TryPrepareNativeR32Sample(ICpuMemory memory, Binding binding, out GuestDrawTexture texture)
    {
        texture = null!;
        if (!_r32NativeOriginalRequested || binding.IsStorage || binding.Descriptor.Format != 4 || binding.Descriptor.NumberType != 7 ||
            binding.Descriptor.Width != 512 || binding.Descriptor.Height != 256 || binding.Descriptor.TileMode != 9 ||
            binding.Descriptor.ResourceMipLevels != 9 || binding.Descriptor.BaseLevel != 0 || binding.Descriptor.LastLevel != 0) return false;
        MetalTextureSnapshots.RequireR32SampledMipZero(binding);
        OrdinaryStorageProducer producer;
        var queue = _submittingGuestQueue ?? GuestQueueIdentity.Default;
        lock (_gate)
        {
            if (!_ordinaryStorageOriginalByAddress.TryGetValue(binding.Descriptor.Address, out var original))
            {
                if (_guestImages.ContainsKey(binding.Descriptor.Address) || _guestImageWorkSequences.ContainsKey(binding.Descriptor.Address))
                    throw new NotSupportedException("Native R32 cannot reconstruct a missing ORIGINAL producer mapping from a current address.");
                return false; // unchanged CPU-owned route still has its own strict empty-inventory proof
            }
            producer = original.LatestAccepted ?? throw new NotSupportedException("Native R32 has no exact accepted original storage producer.");
            if (producer.Queue.Name != queue.Name || original.Physical.Original.Descriptor is not { Length: 8 } raw ||
                binding.ResourceDescriptor is not { Count: 8 } sampled || !raw.AsSpan().SequenceEqual(sampled.ToArray()) ||
                !ReferenceEquals(original.Physical.Memory, memory))
                throw new NotSupportedException("Native R32 sampled view differs from its exact original producer descriptor/queue/domain.");
        }
        void PreparationStillCurrent(long? waitDeadline)
        {
            EnterOriginalWatchMetadataGate(waitDeadline);
            try
            {
                if (!_ordinaryStorageOriginalByAddress.TryGetValue(binding.Descriptor.Address, out var current) ||
                    !ReferenceEquals(current, producer.Original) || !ReferenceEquals(current.LatestAccepted, producer) ||
                    producer.Queue.Name != queue.Name || (_submittingGuestQueue ?? GuestQueueIdentity.Default).Name != queue.Name ||
                    current.Physical.Original.Descriptor is not { Length: 8 } raw ||
                    binding.ResourceDescriptor is not { Count: 8 } sampled || !raw.AsSpan().SequenceEqual(sampled.ToArray()) ||
                    !ReferenceEquals(current.Physical.Memory, memory))
                    throw new NotSupportedException("Native R32 Busy preparation lost its exact original producer/descriptor/queue/domain.");
                RequireOrdinaryStoragePhysicalCurrentLocked(current, memory, current.Physical.Original, current.Physical.Session);
            }
            finally { Monitor.Exit(_gate); }
        }
        RequireOrdinaryStorageOriginalContent(producer.Original, PreparationStillCurrent);
        var descriptor = new GuestDrawTexture(binding.Descriptor.Address, 512, 256, 4, 7, [], false, false,
            ResourceMipLevels: 9, Pitch: 512, Type: 9, TileMode: 9, DstSelect: 0x924, SourceByteCount: 0xC0000,
            Sampler: ToGuestSampler(binding.SamplerDescriptor), Descriptor: binding.ResourceDescriptor!.ToArray(), Shape: binding.Shape, Access: binding.Access);
        var lease = new NativeR32Snapshot(producer, descriptor, queue);
        Action copy = () => ExecuteNativeR32Snapshot(lease);
        lock (_gate)
        {
            RequireNativeR32PhysicalCohortLocked(lease);
            if (_nativeR32Snapshots.Count >= 128) throw new NotSupportedException("Native R32 immutable snapshot inventory is full.");
            _nativeR32Snapshots.Add(lease);
            _nativeR32CopyActions.Add(copy, lease);
            try
            {
                lease.CopySequence = EnqueueGuestWorkLocked(new OrderedGuestAction(copy, "original-native-r32-immutable-copy"));
                if (lease.CopySequence <= producer.Sequence || lease.CopySequence <= 0)
                    throw new NotSupportedException("Native R32 copy was not accepted after its exact producer.");
                _nativeR32Samples.Add(descriptor, lease);
            }
            catch (Exception primary)
            {
                lease.PreparationReleased = true;
                ReleaseNativeR32SnapshotLocked(lease, primary);
                throw;
            }
        }
        texture = descriptor;
        return true;
    }

    private static void RequireNativeR32PhysicalCohortLocked(NativeR32Snapshot lease, object? incoming = null, bool copying = false,
        SelectedNativeOriginalProof? constructing = null)
    {
        var original = lease.Producer.Original;
        RequireOrdinaryStoragePhysicalCurrentLocked(original, original.Physical.Memory, original.Physical.Original, original.Physical.Session);
        if (TextureSourceGraphEnabled || _ordinaryStorageOriginalUnknown || _finiteStorageRetiredUnknown ||
            _finiteStorageRetiredOriginCount != 0 || _finiteRetiredDisplayOriginCount != 0 || _latestTextureSources.Count != 0 ||
            _guestDepthImages.Count != 0 || _guestDepthReadAliases.Count != 0 || _unorderedPresentationWriteSequence != 0 ||
            _latestPresentation is { } presentation && PresentationHasUnorderedGuestWrites(presentation) ||
            _guestImages.Count > 128 || _ordinaryStorageOriginalByAddress.Count > 128 || _selectedStorageMipOwners.Count > 128 ||
            (!lease.Sealed && !ReferenceEquals(original.LatestAccepted, lease.Producer)))
            throw new NotSupportedException("Native R32 original native/writer inventory is incomplete, changed or unordered.");
        var readSpans = MetalStoragePhysicalPlacement.WriteSpans(original.Physical);
        void Other(OrdinaryStorageOriginal other)
        {
            RequireOrdinaryStoragePhysicalCurrentLocked(other, original.Physical.Memory, other.Physical.Original, original.Physical.Session);
            if (ReferenceEquals(other, original)) return;
            if (MetalStoragePhysicalPlacement.WriteSpans(other.Physical).Any(write => readSpans.Any(read => MetalStoragePhysicalPlacement.Overlap(read, write))))
                throw new NotSupportedException("Native R32 sampled original physically overlaps another ordinary storage writer.");
        }
        foreach (var other in _ordinaryStorageOriginalByAddress.Values) Other(other); // also retained/retired originals; no debt erased
        foreach (var pair in _guestImages)
        {
            var image = pair.Value;
            if (image.OrdinaryStorageOriginal is not { } owner || !ReferenceEquals(owner.Image, image) || image.Texture == 0 || !image.Initialized ||
                image.TextureType != MtlTextureType2D || image.Depth != 1 || image.ArrayLength != 1 ||
                image.Width != owner.Physical.Original.Width || image.Height != owner.Physical.Original.Height)
                throw new NotSupportedException("Native R32 ordinary image lacks its exact ORIGINAL physical/native owner.");
            Other(owner);
            if (image.GpuWritten && (owner.LatestEncoded is not { Encoded: true } encoded || !ReferenceEquals(encoded.Image, image) ||
                image.Texture != encoded.Texture || image.ContentVersion != encoded.Version || encoded.NativeQueue == 0))
                throw new NotSupportedException("Native R32 ordinary image lacks its exact last encoded storage writer/version.");
        }
        foreach (var owner in _selectedStorageMipOwners.Values) Selected(owner);
        if (_selectedStorageMipImages.Keys.Any(key => !_selectedStorageMipOwners.ContainsKey(key)) ||
            _selectedStorageMipPending.Keys.Any(key => !_selectedStorageMipOwners.ContainsKey(key)) ||
            _selectedNativeOriginalProofs.Keys.Any(key => !_selectedStorageMipOwners.ContainsKey(key)) ||
            _selectedStorageMipNativeQueues.Keys.Any(key => !_selectedStorageMipOwners.ContainsKey(key)))
            throw new NotSupportedException("Native R32 selected native inventory is orphaned.");
        foreach (var pending in _pendingGuestWorkByQueue.Values.SelectMany(static values => values)) Work(pending.Work);
        if (_takenGuestWorkSequence != 0)
        {
            if (_takenDepthCubeProducerWork is not { } taken) throw new NotSupportedException("Native R32 taken writer has no exact original DTO.");
            Work(taken);
        }
        if (incoming is not null) Work(incoming);
        if (copying && (!lease.Producer.Encoded || lease.Producer.Image is not { } source ||
            !ReferenceEquals(original.LatestEncoded, lease.Producer) || !ReferenceEquals(source.OrdinaryStorageOriginal, original) ||
            !_guestImages.TryGetValue(original.Physical.Original.Address, out var current) || !ReferenceEquals(current, source) ||
            source.Texture != lease.Producer.Texture || source.ContentVersion != lease.Producer.Version || source.Format != MtlPixelFormat.R32Float ||
            lease.Producer.NativeQueue != _commandQueue || lease.Producer.CommandBuffer == 0))
            throw new NotSupportedException("Native R32 copy lost its exact original encoded producer/resource/version/native queue.");

        void Selected(SelectedStorageMipAssociation owner)
        {
            if (!IsR32MipOneOriginal(owner, original.Physical.Memory, original.Physical.Session, out var seed, constructing) ||
                !seed.Source.IsMappedCopyObservationCurrent(seed.Mapping) ||
                SourcePhysicalSpans(seed.Mapping).Any(write => readSpans.Any(read => read.BackingIdentity == write.Backing &&
                    (UInt128)read.Offset < (UInt128)write.Offset + write.Bytes && (UInt128)write.Offset < (UInt128)read.Offset + read.ByteCount)))
                throw new NotSupportedException("Native R32 selected writer is missing, stale or physically overlaps the original mip-zero span.");
        }
        void Work(object work)
        {
            if (work is OrderedGuestAction ordered && _nativeR32CopyActions.TryGetValue(ordered.Action, out var copy) &&
                ReferenceEquals(copy.Producer.Original.Physical.Memory, original.Physical.Memory)) return;
            if (work is GlobalDataShareFill or GlobalDataShareCopyFromGuest) return;
            if (work is not ComputeGuestDispatch dispatch || dispatch.GlobalMemoryBuffers.Any(buffer => buffer.Writable && buffer.WriteBackToGuest))
                throw new NotSupportedException("Native R32 pending/taken/incoming guest writer has no complete original physical proof.");
            foreach (var texture in dispatch.Textures)
            {
                if (!StorageImageWrites(texture)) continue;
                if (_selectedStorageMipAssociations.TryGetValue(texture, out var selected)) Selected(selected);
                else if (_ordinaryStorageOriginals.TryGetValue(texture, out var ordinary)) Other(ordinary);
                else throw new NotSupportedException("Native R32 pending/taken ordinary writer lacks its ORIGINAL mapping.");
            }
        }
    }

    private static bool TryAllowNativeR32SelectedConstructionLocked(SelectedStorageMipAssociation selected,
        GuestImage ordinary, SelectedNativeOriginalProof? constructing = null)
    {
        if (!_r32NativeOriginalRequested || _takenDepthCubeProducerWork is not ComputeGuestDispatch consumer ||
            !_nativeR32Queued.TryGetValue(consumer, out var leases)) return false;
        if (_closed || _guestWorkFailure is not null || _ordinaryStorageOriginalCleanupFailure is not null ||
            _selectedNativeOriginalCleanupFailure is not null ||
            _takenGuestWorkSequence != _executingGuestWorkSequence || _executingGuestWorkSequence <= 0 ||
            !_batchOpen || _batchCommandBuffer == 0 ||
            !_selectedStorageMipOwners.TryGetValue(selected.Key, out var accepted) || !ReferenceEquals(accepted, selected) ||
            !consumer.Textures.Any(texture => StorageImageWrites(texture) &&
                _selectedStorageMipAssociations.TryGetValue(texture, out var exact) && ReferenceEquals(exact, selected)))
            throw new NotSupportedException("Native R32 selected construction has no exact executing accepted writer/consumer.");
        var matched = false;
        foreach (var lease in leases)
        {
            var producer = lease.Producer;
            var original = producer.Original;
            if (!ReferenceEquals(original.Image, ordinary)) continue;
            if (lease.Consumed || !lease.Sealed || lease.CompletionUnknown || lease.Texture == 0 || lease.CommandBuffer == 0 ||
                lease.References <= 0 || _executingGuestWorkSequence <= lease.CopySequence ||
                _submittingGuestQueue != lease.Queue || producer.Queue != lease.Queue ||
                _batchQueue != producer.NativeQueue || producer.RetainsConsumed ||
                !consumer.Textures.Any(texture => _nativeR32Samples.TryGetValue(texture, out var exact) &&
                    ReferenceEquals(exact, lease) && SameNativeR32View(texture, lease)))
                throw new NotSupportedException("Native R32 selected construction lost its exact status4-sealed snapshot/producer/queue lifetime.");
            // Sealed was installed only after actual retained producer and
            // private copy status4. Recheck the original full native owner and
            // complete pending/taken physical cohort, never source CPU pixels:
            // later CPU writes cannot mutate this private readonly snapshot.
            RequireNativeR32PhysicalCohortLocked(lease, copying: true, constructing: constructing);
            if (!IsR32MipOneOriginal(selected, original.Physical.Memory, original.Physical.Session,
                    out var seed, constructing) || !seed.Source.IsMappedCopyObservationCurrent(seed.Mapping) ||
                SourcePhysicalSpans(seed.Mapping).Any(write => MetalStoragePhysicalPlacement.WriteSpans(original.Physical)
                    .Any(read => read.BackingIdentity == write.Backing &&
                        (UInt128)read.Offset < (UInt128)write.Offset + write.Bytes &&
                        (UInt128)write.Offset < (UInt128)read.Offset + read.ByteCount)))
                throw new NotSupportedException("Native R32 selected construction has no original physically disjoint complete mip-one writes.");
            matched = true;
        }
        return matched;
    }

    private static void ExecuteNativeR32Snapshot(NativeR32Snapshot lease)
    {
        if (Monitor.IsEntered(_gate)) throw new InvalidOperationException("Native R32 GPU snapshot requires a released presenter gate.");
        void CopyStillCurrent(long? waitDeadline)
        {
            EnterOriginalWatchMetadataGate(waitDeadline);
            try
            {
                RequireNativeR32CopyExecutionCurrentLocked(lease);
                RequireNativeR32PhysicalCohortLocked(lease, copying: true);
            }
            finally { Monitor.Exit(_gate); }
        }
        lock (_gate) RequireNativeR32CopyExecutionCurrentLocked(lease);
        RequireOrdinaryStorageOriginalContent(lease.Producer.Original, CopyStillCurrent);
        lock (_gate) RequireNativeR32PhysicalCohortLocked(lease, copying: true);
        var committed = FlushBatchedGuestCommands();
        if (committed != 0 && committed != lease.Producer.CommandBuffer)
            throw new NotSupportedException("Native R32 flush does not name its exact last encoded producer.");
        WaitForCommittedGuestBufferWriteback(lease.Producer.CommandBuffer); // real producer status4
        RequireOrdinaryStorageOriginalContent(lease.Producer.Original, CopyStillCurrent);
        lock (_gate) RequireNativeR32PhysicalCohortLocked(lease, copying: true);
        Exception? primary = null;
        nint descriptor = 0;
        try
        {
            var borrowedDescriptor = MetalNative.SendTextureDescriptor(MetalNative.Class("MTLTextureDescriptor"),
                MetalNative.Selector("texture2DDescriptorWithPixelFormat:width:height:mipmapped:"),
                (nuint)MtlPixelFormat.R32Float, 512, 256, false);
            if (borrowedDescriptor == 0) throw new InvalidOperationException("Native R32 snapshot descriptor creation failed.");
            descriptor = MetalNative.Send(borrowedDescriptor, MetalNative.Selector("retain"));
            if (descriptor == 0) throw new InvalidOperationException("Native R32 snapshot descriptor retain failed.");
            MetalNative.Send(descriptor, MetalNative.Selector("setStorageMode:"), (nint)2);
            MetalNative.Send(descriptor, MetalNative.Selector("setUsage:"), (nint)UsageShaderRead);
            MetalNative.SendVoidSwizzle(descriptor, MetalNative.Selector("setSwizzle:"), ToMetalSwizzle(lease.Descriptor.DstSelect));
            lease.Texture = MetalNative.Send(_device, MetalNative.Selector("newTextureWithDescriptor:"), descriptor);
            var commandBuffer = MetalNative.Send(_commandQueue, MetalNative.Selector("commandBuffer"));
            if (lease.Texture == 0 || commandBuffer == 0 || !MetalNative.SendBool(commandBuffer, MetalNative.Selector("retainedReferences")))
                throw new NotSupportedException("Native R32 copy requires its owned private resource and real retaining command buffer.");
            lease.CommandBuffer = MetalNative.Send(commandBuffer, MetalNative.Selector("retain"));
            if (lease.CommandBuffer == 0) throw new NotSupportedException("Native R32 copy cannot retain its exact command buffer.");
            var blit = MetalNative.Send(commandBuffer, MetalNative.Selector("blitCommandEncoder"));
            if (blit == 0) throw new InvalidOperationException("Native R32 copy encoder creation failed.");
            Exception? encoding = null;
            try { MetalNative.SendVoidCopyTexture(blit, MetalNative.Selector("copyFromTexture:toTexture:"), lease.Producer.Texture, lease.Texture); }
            catch (Exception failure) { encoding = failure; }
            try { MetalNative.SendVoid(blit, MetalNative.Selector("endEncoding")); }
            catch (Exception cleanup) when (encoding is not null) { try { encoding.Data["NativeR32CopyEncoderCleanup"] = cleanup; } catch { } }
            if (encoding is not null) ExceptionDispatchInfo.Capture(encoding).Throw();
            RequireOrdinaryStorageOriginalContent(lease.Producer.Original, CopyStillCurrent);
            lock (_gate) RequireNativeR32PhysicalCohortLocked(lease, copying: true);
            lease.CompletionUnknown = true; // never free exact native/backing owners after an uncertain commit/wait
            MetalNative.SendVoid(commandBuffer, MetalNative.Selector("commit"));
            _nativeR32AfterCopyCommit?.Invoke(commandBuffer);
            WaitForCommittedGuestBufferWriteback(commandBuffer); // real immutable copy status4, not just producer completion
            lease.CompletionUnknown = false;
            RequireOrdinaryStorageOriginalContent(lease.Producer.Original, CopyStillCurrent);
            lock (_gate)
            {
                RequireNativeR32PhysicalCohortLocked(lease, copying: true);
                lease.Sealed = true;
            }
        }
        catch (Exception failure) { primary = failure; throw; }
        finally
        {
            if (descriptor != 0)
                try { MetalNative.SendVoid(descriptor, MetalNative.Selector("release")); }
                catch (Exception cleanup) when (primary is not null) { try { primary.Data["NativeR32DescriptorCleanup"] = cleanup; } catch { } }
        }
    }

    private static void RequireNativeR32CopyExecutionCurrentLocked(NativeR32Snapshot lease)
    {
        if (lease.Consumed || lease.References <= 0 || lease.CopySequence != _executingGuestWorkSequence ||
            _submittingGuestQueue != lease.Queue || _takenDepthCubeProducerWork is not OrderedGuestAction executing ||
            !_nativeR32CopyActions.TryGetValue(executing.Action, out var exact) || !ReferenceEquals(exact, lease) ||
            !_nativeR32Queued.TryGetValue(executing, out var owners) || !owners.Contains(lease))
            throw new NotSupportedException("Native R32 copy lost its exact accepted/taken queued lifetime.");
    }

    private static bool SameNativeR32View(GuestDrawTexture texture, NativeR32Snapshot lease) =>
        (texture with { Sampler = lease.Descriptor.Sampler, Descriptor = lease.Descriptor.Descriptor }) == lease.Descriptor &&
        ReferenceEquals(texture.RgbaPixels, lease.Descriptor.RgbaPixels) && texture.Descriptor is { Length: 8 } raw &&
        lease.Descriptor.Descriptor is { Length: 8 } old && raw.AsSpan().SequenceEqual(old);
    private static void CopyNativeR32Snapshot(GuestDrawTexture source, GuestDrawTexture copy)
    {
        if (!_nativeR32Samples.TryGetValue(source, out var lease)) return;
        if (!SameNativeR32View(copy, lease)) throw new NotSupportedException("Native R32 copied DTO changed its exact readonly immutable view.");
        _nativeR32Samples.Add(copy, lease);
    }
    private static bool TryResolveNativeR32Snapshot(nint commandBuffer, GuestDrawTexture texture, out nint handle)
    {
        handle = 0;
        if (!_nativeR32Samples.TryGetValue(texture, out var lease)) return false;
        if (commandBuffer == 0 || !MetalNative.SendBool(commandBuffer, MetalNative.Selector("retainedReferences")))
            throw new NotSupportedException("Native R32 readonly consumer requires actual command-buffer resource retention.");
        lock (_gate)
        {
            if (_closed || _guestWorkFailure is not null || _ordinaryStorageOriginalCleanupFailure is not null ||
                !ReferenceEquals(_guestMemory, lease.Producer.Original.Physical.Memory) ||
                !SameNativeR32View(texture, lease) || lease.Consumed || !lease.Sealed || lease.CompletionUnknown || lease.Texture == 0 ||
                lease.References <= 0 || _submittingGuestQueue != lease.Queue ||
                _takenGuestWorkSequence != _executingGuestWorkSequence || _executingGuestWorkSequence <= lease.CopySequence ||
                commandBuffer != _batchCommandBuffer || !_batchOpen || _batchQueue != lease.Producer.NativeQueue ||
                _takenDepthCubeProducerWork is not ComputeGuestDispatch consumer ||
                !_nativeR32Queued.TryGetValue(consumer, out var owners) || !owners.Contains(lease) ||
                !consumer.Textures.Any(candidate => ReferenceEquals(candidate, texture)) ||
                lease.Producer.Original.Physical.Session != CurrentSelectedStorageMipSeedSession ||
                !lease.Producer.Original.Physical.Source.IsMappedCopyObservationCurrent(lease.Producer.Original.Physical.Mapping))
                throw new NotSupportedException("Native R32 readonly sample has no exact sealed resource/original mapping/session/queued lifetime.");
            // Exact per-call retain bridges gate exit through native argument
            // binding; existing temporary texture cleanup consumes it even if
            // binding fails. The real CB independently retains encoded use.
            handle = MetalNative.Send(lease.Texture, MetalNative.Selector("retain"));
            if (handle == 0) throw new NotSupportedException("Native R32 consumer cannot retain its exact private snapshot.");
        }
        // Sealed private pixels are independent of later source CPU writes.
        // No source token/watch or native address lookup occurs on this route.
        return true;
    }

    private static bool RetainQueuedNativeR32SnapshotsLocked(object work)
    {
        var leases = new List<NativeR32Snapshot>();
        if (work is OrderedGuestAction ordered && _nativeR32CopyActions.TryGetValue(ordered.Action, out var copy))
        { RequireNativeR32PhysicalCohortLocked(copy); leases.Add(copy); }
        IReadOnlyList<GuestDrawTexture> textures = work is ComputeGuestDispatch dispatch ? dispatch.Textures : [];
        foreach (var texture in textures)
            if (_nativeR32Samples.TryGetValue(texture, out var lease))
            {
                if (!SameNativeR32View(texture, lease) || lease.Consumed || lease.PreparationReleased || lease.References <= 0 ||
                    lease.CopySequence <= 0 || (_submittingGuestQueue ?? GuestQueueIdentity.Default) != lease.Queue)
                    throw new NotSupportedException("Native R32 consumer cannot adopt another prepared sample/queue.");
                RequireNativeR32PhysicalCohortLocked(lease, work);
                if (!leases.Contains(lease)) leases.Add(lease);
            }
        if (leases.Count == 0) return false;
        if (leases.Any(lease => lease.Consumed || lease.References <= 0 || lease.References == int.MaxValue) || _nativeR32Queued.Count >= 65)
            throw new NotSupportedException("Native R32 queued owner inventory is full or consumed.");
        var owners = leases.ToArray();
        _nativeR32Queued.Add(work, owners);
        foreach (var lease in owners) lease.References++;
        return true;
    }
    private static void ReleaseQueuedNativeR32SnapshotsLocked(object work, Exception? primary = null)
    {
        if (!_nativeR32Queued.Remove(work, out var owners)) return;
        Exception? first = primary;
        foreach (var lease in owners)
            try { ReleaseNativeR32SnapshotLocked(lease, first); } catch (Exception cleanup) { first ??= cleanup; }
        if (primary is null && first is not null) ExceptionDispatchInfo.Capture(first).Throw();
    }
    private static void ReleaseNativeR32SnapshotLocked(NativeR32Snapshot lease, Exception? primary = null)
    {
        if (lease.Consumed) return;
        if (lease.References <= 0) throw new InvalidOperationException("Unbalanced native R32 snapshot owner.");
        if (--lease.References != 0) return;
        lease.Consumed = true;
        if (lease.CompletionUnknown)
        {
            _ordinaryStorageOriginalCleanupFailure ??= new InvalidOperationException("Native R32 copy completion unknown; exact snapshot/producer owners remain quarantined.");
            return;
        }
        var commandBuffer = lease.CommandBuffer; var texture = lease.Texture;
        lease.CommandBuffer = lease.Texture = 0;
        Exception? first = null;
        ReleaseFiniteStorageHandle(commandBuffer, ref first); ReleaseFiniteStorageHandle(texture, ref first);
        if (first is null) _nativeR32Snapshots.Remove(lease);
        else { _ordinaryStorageOriginalCleanupFailure ??= first; if (primary is null) ExceptionDispatchInfo.Capture(first).Throw();
            try { primary.Data["NativeR32SnapshotCleanup"] = first; } catch { } }
    }
    private static Exception? ReleasePreparedNativeR32Snapshots(IReadOnlyList<GuestDrawTexture> textures, Exception? primary = null)
    {
        Exception? first = primary;
        lock (_gate)
        foreach (var texture in textures)
            if (_nativeR32Samples.TryGetValue(texture, out var lease) && !lease.PreparationReleased)
            {
                lease.PreparationReleased = true;
                try { ReleaseNativeR32SnapshotLocked(lease, first); } catch (Exception cleanup) { first ??= cleanup; }
            }
        return first;
    }
    internal static Exception? ReleaseFailedPreparedNativeR32Textures(IReadOnlyList<GuestDrawTexture> textures, GuestDrawTexture? unreturned, Exception primary)
    {
        var first = ReleasePreparedNativeR32Snapshots(textures, primary);
        if (unreturned is not null) first = ReleasePreparedNativeR32Snapshots([unreturned], first);
        return first;
    }
    private static void CancelQueuedNativeR32SnapshotsLocked(Exception? primary = null)
    {
        Exception? first = primary;
        while (_nativeR32Queued.Count != 0)
            try { ReleaseQueuedNativeR32SnapshotsLocked(_nativeR32Queued.Keys.First(), first); } catch (Exception cleanup) { first ??= cleanup; }
        if (primary is null && first is not null) ExceptionDispatchInfo.Capture(first).Throw();
    }
}
