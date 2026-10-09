// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.CompilerServices;
using SharpEmu.HLE;
using SharpEmu.Libs.Agc;
using SharpEmu.Libs.Gpu;
using Descriptor = SharpEmu.Libs.Agc.AgcExports.TextureDescriptor;

namespace SharpEmu.Gpu.Metal;

internal static partial class MetalVideoPresenter
{
    private sealed record SelectedStorageMipDomain(long Id);
    private readonly record struct SelectedStorageMipKey(long Domain, Descriptor Resource, uint Mip);
    private sealed record SelectedStorageMipAssociation(SelectedStorageMipKey Key,
        long MappingEpoch, GuestImageWriteTracker.ContentToken Token, bool HasToken, long Session, CpuTextureSnapshotObservation Observation);
    private sealed record SelectedStorageMipPending(string Queue, long Sequence);
    private static readonly Dictionary<SelectedStorageMipKey, SelectedStorageMipAssociation> _selectedStorageMipOwners = new();
    private static readonly Dictionary<SelectedStorageMipKey, SelectedStorageMipPending> _selectedStorageMipPending = new();
    private static long _nextSelectedStorageMipDomain;
    private static long _selectedStorageMipSession;
    private static bool _selectedStorageMipCloseInProgress;
    private static readonly ConditionalWeakTable<ICpuMemory, SelectedStorageMipDomain> _selectedStorageMipDomains = new();
    private static readonly ConditionalWeakTable<GuestDrawTexture, SelectedStorageMipAssociation> _selectedStorageMipAssociations = new();
    private static readonly Dictionary<SelectedStorageMipKey, GuestImage> _selectedStorageMipImages = new();
    private static readonly Dictionary<SelectedStorageMipKey, nint> _selectedStorageMipNativeQueues = new();

    internal static void RegisterSelectedStorageMip(ICpuMemory memory, Descriptor resource, GuestDrawTexture texture,
        long epoch, GuestImageWriteTracker.ContentToken token, bool hasToken, CpuTextureSnapshotObservation observation)
    {
        if (MetalR16FloatSelectedMip.TryPlacement(resource, texture.MipLevel, out _) &&
            Volatile.Read(ref _r16SelectedRetainCleanupFailure) is { } cleanupFailure)
            throw new InvalidOperationException("Metal R16Float selected retain cleanup has an unknown outcome; reuse is unavailable.", cleanupFailure);
        var domain = _selectedStorageMipDomains.GetValue(memory,
            static _ => new SelectedStorageMipDomain(Interlocked.Increment(ref _nextSelectedStorageMipDomain)));
        _selectedStorageMipAssociations.Add(texture, new(new(domain.Id, resource, texture.MipLevel), epoch, token, hasToken, Volatile.Read(ref _selectedStorageMipSession), observation));
    }

    internal static bool TryAttachLiveSelectedStorageMip(ICpuMemory memory, Descriptor resource, GuestDrawTexture texture)
    {
        if (!_selectedStorageMipDomains.TryGetValue(memory, out var domain)) return false;
        var key = new SelectedStorageMipKey(domain.Id, resource, texture.MipLevel);
        lock (_gate)
        {
            if (!_selectedStorageMipImages.TryGetValue(key, out var image)) return false;
            if (image.Texture == 0 || !image.Initialized)
                throw new InvalidOperationException("Metal selected storage mip has no initialized native owner.");
            if (MetalR16FloatSelectedMip.TryPlacement(resource, texture.MipLevel, out _) && !image.R16SelectedOwnsRetain)
                throw new InvalidOperationException("Metal R16Float selected construction has not transferred its exact native retain.");
            var owner = _selectedStorageMipOwners[key];
            ValidateSelectedStorageMipOwner(owner, requireObserved: true);
            _selectedStorageMipAssociations.Add(texture, owner);
            return true; // Never re-read guest bytes to initialize an existing GPU writer.
        }
    }

    private static void ValidateSelectedStorageMipOwner(SelectedStorageMipAssociation selected, bool requireObserved = false)
        => ValidateSelectedStorageMipOwnerAtStage(selected, requireObserved, "owner-validation");

    private static void ValidateSelectedStorageMipOwnerAtStage(SelectedStorageMipAssociation selected,
        bool requireObserved, string stage)
    {
        var originalNative = false;
        if (_selectedNativeOriginalReuseEnabled && !TextureSourceGraphEnabled)
        {
            if (Volatile.Read(ref _selectedNativeOriginalCleanupFailure) is { } unknown)
                throw new InvalidOperationException("Metal selected original watch cleanup has an unknown outcome; native reuse is unavailable.", unknown);
            lock (_gate) originalNative = HasCurrentSelectedNativeOriginalProofLocked(selected);
        }
        if (requireObserved && !originalNative && (!selected.Observation.Observed ||
            !IsCpuTextureSnapshotObservationStable(selected.Key.Resource.Address, selected.Observation)))
            throw new InvalidOperationException("Metal selected storage mip live reuse requires unchanged complete content observation; tracker identity alone cannot authorize unobserved CPU writers.");
        // Legacy owners retain session -> aggregate epoch -> token policy.
        // Qualified native owners carry ORIGINAL exact mapping/content watches;
        // unrelated aggregate drift never replaces or retimestamps those proofs.
        var currentSession = Volatile.Read(ref _selectedStorageMipSession);
        if (selected.Session != currentSession)
            throw SelectedStorageMipObservationFailure(selected, stage, "presenter-session",
                $"session_before={selected.Session} session_now={currentSession}");
        ValidateOriginalSelectedStorageMipSeedStage(selected, requireObserved, stage);
        var currentMappingEpoch = HostMemory.MappingGeneration;
        if (selected.MappingEpoch != currentMappingEpoch && !originalNative &&
            (requireObserved || !CanUseOriginalSelectedStorageMipSeed(selected, stage)))
            throw SelectedStorageMipObservationFailure(selected, stage, "global-mapping-epoch",
                $"mapping_before={selected.MappingEpoch} mapping_now={currentMappingEpoch}");
        if (selected.HasToken)
        {
            var present = GuestImageWriteTracker.TryGetContentToken(selected.Key.Resource.Address, out var current);
            if (!present || current != selected.Token)
                throw SelectedStorageMipObservationFailure(selected, stage,
                    present ? "content-token-changed" : "content-token-missing",
                    $"token_present={present} {DescribeSelectedStorageMipToken("before", selected.Token)} {DescribeSelectedStorageMipToken("now", current)}");
        }
    }

    private static InvalidOperationException SelectedStorageMipObservationFailure(
        SelectedStorageMipAssociation selected, string stage, string reason, string scalars) =>
        new($"Metal selected storage mip backing observation changed; native-only contents cannot be replaced by stale guest bytes. " +
            $"reason={reason} stage={stage} addr=0x{selected.Key.Resource.Address:X} selected_mip={selected.Key.Mip} domain={selected.Key.Domain} {scalars}. " +
            "These scalar observations are conservative vetoes, not backing ownership or proof of a range mutation.");

    private static string DescribeSelectedStorageMipToken(string suffix, GuestImageWriteTracker.ContentToken token) =>
        $"protected_identity_{suffix}={token.ProtectedIdentity} protected_generation_{suffix}={token.ProtectedGeneration} " +
        $"watch_identity_{suffix}={token.WatchIdentity} watch_generation_{suffix}={token.WatchGeneration} " +
        $"protected_bytes_{suffix}={token.ProtectedByteCount} watch_bytes_{suffix}={token.WatchByteCount}";

    private readonly record struct SelectedStorageMipAdmission(SelectedStorageMipAssociation Owner,
        SelectedStorageMipPending? Pending, SelectedStorageMipSeedAdmission? Seed);

    private static SelectedStorageMipAdmission[] PrepareSelectedStorageMipAdmissionLocked(object work, GuestQueueIdentity queue)
    {
        ValidateOwnedR32MipZeroAdmissionLocked(work);
        if (work is not ComputeGuestDispatch dispatch ||
            !dispatch.Textures.Any(texture => _selectedStorageMipAssociations.TryGetValue(texture, out _))) return [];
        if (_enqueueAsImmediateQueueFollowup && dispatch.Textures.Any(texture => _selectedStorageMipAssociations.TryGetValue(texture, out _)))
            throw new NotSupportedException("Metal selected storage mip immediate queue follow-up ordering is not qualified.");
        var selected = new Dictionary<SelectedStorageMipKey, (SelectedStorageMipAssociation Owner, bool Writes)>();
        foreach (var texture in dispatch.Textures)
        {
            if (!_selectedStorageMipAssociations.TryGetValue(texture, out var association)) continue;
            ValidateSelectedStorageMipOwnerAtStage(association, false, "admission");
            if (_selectedStorageMipOwners.Keys.Concat(selected.Keys).Any(key =>
                SelectedStorageMipR16Conflict(key, association.Key)))
                throw new NotSupportedException("Metal selected storage admission conflicts with an incompatible native-only resource view.");
            if (_selectedStorageMipPending.TryGetValue(association.Key, out var pending) && pending.Queue != queue.Name)
                throw new NotSupportedException("Metal selected storage mip has an outstanding producer on another logical queue.");
            var writes = StorageImageWrites(texture);
            var initialSeed = _selectedStorageMipSeeds.TryGetValue(association, out var seed) && seed.Phase == 0 &&
                !_selectedStorageMipOwners.ContainsKey(association.Key);
            if (!writes && !initialSeed) continue;
            if (!selected.TryAdd(association.Key, (association, writes)))
            {
                var existing = selected[association.Key];
                if (!ReferenceEquals(existing.Owner, association) &&
                    (_selectedStorageMipSeeds.TryGetValue(association, out _) || _selectedStorageMipSeeds.TryGetValue(existing.Owner, out _)))
                    throw new NotSupportedException("Metal first-use selected seeds cannot alias distinct original proofs in one dispatch.");
                if (writes && !existing.Writes) selected[association.Key] = (existing.Owner, true);
            }
        }
        var added = selected.Count(item => item.Value.Writes && !_selectedStorageMipOwners.ContainsKey(item.Key));
        if (selected.Count > 128 || _selectedStorageMipOwners.Count + added > 128)
            throw new NotSupportedException("Metal selected storage mip owner limit exceeded.");
        _selectedStorageMipOwners.EnsureCapacity(_selectedStorageMipOwners.Count + added);
        _selectedStorageMipPending.EnsureCapacity(_selectedStorageMipPending.Count + selected.Count(item => item.Value.Writes));
        // Construct every metadata object before queue admission mutates its
        // sequence/list. Publication below has no allocating iterator/object.
        return selected.Values.Select(item => new SelectedStorageMipAdmission(item.Owner,
            item.Writes ? new(queue.Name, _enqueuedGuestWorkSequence + 1) : null,
            PrepareInitialSelectedStorageMipSeedAdmission(item.Owner, work, _enqueuedGuestWorkSequence + 1))).ToArray();
    }

    private static void PublishSelectedStorageMipAdmissionLocked(SelectedStorageMipAdmission[] admission)
    {
        foreach (var item in admission)
        {
            if (item.Pending is { } pending)
            {
                _selectedStorageMipOwners.TryAdd(item.Owner.Key, item.Owner);
                _selectedStorageMipPending[item.Owner.Key] = pending;
            }
            PublishInitialSelectedStorageMipSeedAdmission(item.Seed);
        }
    }

    private static void ValidateSelectedStorageMipExecution(GuestDrawTexture texture, nint queue)
    {
        if (!_selectedStorageMipAssociations.TryGetValue(texture, out var selected)) return;
        lock (_gate)
        {
            ValidateSelectedStorageMipOwner(selected);
            if (_selectedNativeOriginalProofs.TryGetValue(selected.Key, out var original) &&
                original.ProducerSequence > 0 && original.ProducerSequence < _executingGuestWorkSequence &&
                !IsGuestWorkCompletedLocked(original.ProducerSequence))
                throw new NotSupportedException("Metal original selected native owner requires its exact earlier producer acknowledgment.");
            if (_selectedStorageMipNativeQueues.TryGetValue(selected.Key, out var nativeQueue) && nativeQueue != queue)
                throw new NotSupportedException("Metal selected storage mip cannot cross native command queues without a qualified completion dependency.");
            _selectedStorageMipNativeQueues[selected.Key] = queue;
            if (_selectedStorageMipPending.TryGetValue(selected.Key, out var pending) &&
                pending.Sequence < _executingGuestWorkSequence && !IsGuestWorkCompletedLocked(pending.Sequence))
                throw new NotSupportedException("Metal selected storage mip requires its earlier producer to execute before this consumer; cross-queue dependency is not qualified.");
        }
    }

    private static long SelectedStorageMipDomainOf(GuestDrawTexture texture) =>
        _selectedStorageMipAssociations.TryGetValue(texture, out var association) ? association.Key.Domain : 0;

    private static bool IsQualifiedSelectedStorageMip(GuestDrawTexture texture) =>
        _selectedStorageMipAssociations.TryGetValue(texture, out var association) &&
        (MetalR16FloatSelectedMip.IsQualifiedUpload(texture, association.Key.Resource) ||
        texture.Width == 256 && texture.Height == 128 && texture.Pitch == 256 &&
        texture.Address == association.Key.Resource.Address && texture.MipLevel == association.Key.Mip && texture.BaseMipLevel == 1 &&
        texture.ResourceMipLevels == 9 && texture.SourceByteCount == 262144 && texture.MipLevels == 1 &&
        texture.Format == 4 && texture.NumberType == 7 && texture.Type == 9 && texture.TileMode == 9 &&
        texture.DstSelect == association.Key.Resource.DstSelect &&
        texture.Descriptor is { Length: >= 8 } raw &&
        AgcBackendServices.TryDecodeTextureDescriptor(raw, out var resource) && resource == association.Key.Resource);

    private static void CopySelectedStorageMip(GuestDrawTexture source, GuestDrawTexture copy)
    {
        if (_selectedStorageMipAssociations.TryGetValue(source, out var association))
        {
            if (source.Address != copy.Address || source.Width != copy.Width || source.Height != copy.Height ||
                source.Pitch != copy.Pitch || source.MipLevel != copy.MipLevel || source.BaseMipLevel != copy.BaseMipLevel ||
                source.Type != copy.Type || source.Format != copy.Format || source.NumberType != copy.NumberType)
                throw new InvalidOperationException("Metal selected storage mip copy must preserve its exact view identity.");
            _selectedStorageMipAssociations.Add(copy, association);
        }
    }

    // The registry owns each texture exactly once. Submitted Metal command
    // buffers retain bound resources themselves through actual completion.
    private static void ReleaseSelectedStorageMipImages()
    {
        lock (_gate)
        {
            // Optional bookkeeping cleanup may reenter ordinary close. The
            // admitted outer close alone owns native releases/session advance.
            if (_selectedStorageMipCloseInProgress) return;
            _selectedStorageMipCloseInProgress = true;
            try
            {
                foreach (var image in _selectedStorageMipImages.Values)
                    NoteFiniteStorageOwnerRetirementLocked(image);
                var watchCleanup = ReleaseSelectedNativeOriginalProofsLocked(); // Consume before release/reentrant close; attempt every watch.
                // Snapshot the bounded new class before a release callback can
                // reenter close and clear the dictionary. Consume only exact owners.
                var r16 = _selectedStorageMipImages.Where(entry =>
                    MetalR16FloatSelectedMip.TryPlacement(entry.Key.Resource, entry.Key.Mip, out _)).ToArray();
                foreach (var entry in r16)
                    if (_selectedStorageMipImages.TryGetValue(entry.Key, out var current) && ReferenceEquals(current, entry.Value))
                        ReleaseAcceptedR16SelectedRetain(entry.Value);
                try
                {
                    foreach (var entry in _selectedStorageMipImages)
                        if (!MetalR16FloatSelectedMip.TryPlacement(entry.Key.Resource, entry.Key.Mip, out _))
                            MetalNative.SendVoid(entry.Value.Texture, MetalNative.Selector("release"));
                }
                catch (Exception later) when (watchCleanup is not null || _r16SelectedRetainCleanupFailure is not null)
                {
                    var primary = watchCleanup ?? _r16SelectedRetainCleanupFailure!;
                    // Keep standalone DS behavior unchanged. In a mixed failure,
                    // preserve the earlier watch/R16 cause; DS's uncertain cleanup and
                    // incomplete metadata close remain explicitly unqualified.
                    try { primary.Data["MetalSelectedMipSecondaryCleanupFailure"] = later; }
                    catch { /* Secondary diagnostics cannot replace first cause. */ }
                    System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(primary).Throw();
                    throw;
                }
                Interlocked.Increment(ref _selectedStorageMipSession);
                _selectedStorageMipImages.Clear();
                _selectedStorageMipOwners.Clear();
                _selectedStorageMipPending.Clear();
                _selectedStorageMipNativeQueues.Clear();
                if (watchCleanup is not null)
                    System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(watchCleanup).Throw();
                if (_r16SelectedRetainCleanupFailure is not null)
                    System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(_r16SelectedRetainCleanupFailure).Throw();
            }
            finally { _selectedStorageMipCloseInProgress = false; }
        }
    }

    private static bool SelectedStorageMipOverlaps(SelectedStorageMipKey key, ulong address, ulong bytes) =>
        (UInt128)address < (UInt128)key.Resource.Address + SelectedStorageMipAllocationBytes(key) &&
        (UInt128)key.Resource.Address < (UInt128)address + Math.Max(bytes, 1UL);

    private static ulong SelectedStorageMipAllocationBytes(SelectedStorageMipKey key) =>
        MetalR16FloatSelectedMip.TryPlacement(key.Resource, key.Mip, out _) ? MetalR16FloatSelectedMip.ChainBytes : 786432UL;

    private static bool SelectedStorageMipKeysMayCoexist(SelectedStorageMipKey a, SelectedStorageMipKey b) =>
        a == b || a.Domain == b.Domain && MetalR16FloatSelectedMip.SameChainDisjoint(a.Resource, a.Mip, b.Resource, b.Mip);

    private static bool SelectedStorageMipR16Conflict(SelectedStorageMipKey a, SelectedStorageMipKey b) =>
        a.Domain == b.Domain &&
        (MetalR16FloatSelectedMip.TryPlacement(a.Resource, a.Mip, out _) ||
         MetalR16FloatSelectedMip.TryPlacement(b.Resource, b.Mip, out _)) &&
        SelectedStorageMipOverlaps(a, b.Resource.Address, SelectedStorageMipAllocationBytes(b)) &&
        !SelectedStorageMipKeysMayCoexist(a, b);

    private static void RejectConflictingSelectedStorageConstructionLocked(SelectedStorageMipAssociation selected)
    {
        if (_selectedStorageMipOwners.Keys.Concat(_selectedStorageMipImages.Keys).Any(key =>
            SelectedStorageMipR16Conflict(key, selected.Key)))
            throw new NotSupportedException("Metal selected construction overlaps an incompatible R16Float selected chain.");
        if (!MetalR16FloatSelectedMip.TryPlacement(selected.Key.Resource, selected.Key.Mip, out _))
        {
            // Keep the existing DS class's construction admission unchanged.
            if (_guestImages.ContainsKey(selected.Key.Resource.Address) ||
                _selectedStorageMipImages.Keys.Any(key => key.Domain == selected.Key.Domain &&
                    key.Resource.Address == selected.Key.Resource.Address && key != selected.Key))
                throw new NotSupportedException("Metal selected storage view conflicts with an address-only live image.");
            return;
        }
        foreach (var entry in _guestImages)
        {
            if (!_guestImageExtents.TryGetValue(entry.Key, out var extent) || extent.ByteCount == 0)
                throw new NotSupportedException("Metal R16Float selected construction cannot prove disjointness from an address-only image with unknown extent.");
            if (SelectedStorageMipOverlaps(selected.Key, entry.Key, extent.ByteCount))
                throw new NotSupportedException("Metal R16Float selected construction overlaps an address-only native image.");
        }
        // Include accepted ordinary producer extents before native creation.
        if (_guestImageExtents.Any(entry => entry.Value.ByteCount == 0 ||
                SelectedStorageMipOverlaps(selected.Key, entry.Key, entry.Value.ByteCount)) ||
            _selectedStorageMipOwners.Keys.Concat(_selectedStorageMipImages.Keys).Any(key =>
                key.Domain == selected.Key.Domain && SelectedStorageMipOverlaps(key, selected.Key.Resource.Address,
                    SelectedStorageMipAllocationBytes(selected.Key)) && !SelectedStorageMipKeysMayCoexist(key, selected.Key)))
            throw new NotSupportedException("Metal selected storage view conflicts with an address-only or incompatible selected native image.");
    }

    private static void RejectAddressOnlySelectedStorageAlias(ulong address, ulong bytes = 1)
    {
        lock (_gate)
            if (_selectedStorageMipOwners.Keys.Any(key => SelectedStorageMipOverlaps(key, address, bytes)))
                throw new NotSupportedException($"Metal address-only image use at 0x{address:X} cannot consume native-only selected storage mip contents.");
    }

    // Same original materializer veto, with failure-only request metadata.
    // No provider/content/native query, replacement owner lookup or admission.
    private static void RejectSelectedStorageMaterializerAlias(GuestDrawTexture texture)
    {
        if (_ownedR32MipZeros.TryGetValue(texture, out var copied))
        {
            lock (_gate) RequireOwnedR32MipZeroCurrentLocked(texture, copied);
            return;
        }
        var bytes = Math.Max(texture.SourceByteCount,
            checked((ulong)texture.Width * texture.Height * Math.Max(texture.Depth, 1u) * 16));
        lock (_gate)
            foreach (var owner in _selectedStorageMipOwners.Keys)
                if (SelectedStorageMipOverlaps(owner, texture.Address, bytes))
                {
                    var message = $"Metal address-only image use at 0x{texture.Address:X} cannot consume native-only selected storage mip contents.";
                    if (_selectedCopyDiagnostics)
                    {
                        try
                        {
                            _selectedStorageMipImages.TryGetValue(owner, out var image);
                            _selectedStorageMipPending.TryGetValue(owner, out var pending);
                            var associated = _selectedStorageMipAssociations.TryGetValue(texture, out var original);
                            message += $" request={texture.Width}x{texture.Height}x{texture.Depth} request_format={texture.Format}/{texture.NumberType} " +
                                $"request_type={texture.Type} request_storage={texture.IsStorage} request_access={texture.Access} " +
                                $"request_pitch={texture.Pitch} request_tile={texture.TileMode} request_selected={texture.MipLevel} " +
                                $"request_levels={texture.MipLevels} request_base_level={texture.BaseMipLevel} request_resource_levels={texture.ResourceMipLevels} " +
                                $"request_source_bytes={texture.SourceByteCount} request_overlap_bytes={bytes} request_shape={texture.Shape} " +
                                $"request_raw=[{string.Join(",", texture.Descriptor?.Select(static word => $"{word:X8}") ?? [])}] " +
                                $"request_associated={associated} request_same_owner={associated && original!.Key == owner} " +
                                $"original_domain={owner.Domain} original_resource={owner.Resource} original_selected={owner.Mip} " +
                                $"native_present={image is not null} native_initialized={image?.Initialized} native_written={image?.GpuWritten} " +
                                $"native_version={image?.ContentVersion} native_width={image?.Width} native_height={image?.Height} " +
                                $"native_type={image?.TextureType} native_format={image?.Format} " +
                                $"pending_queue={pending?.Queue} pending_sequence={pending?.Sequence}";
                        }
                        catch (Exception) { } // Preserve the original refusal if formatting fails.
                    }
                    throw new NotSupportedException(message);
                }
    }

    internal static void RejectSelectedStorageAlias(ICpuMemory memory, Descriptor resource, bool storage, uint mip)
    {
        if (!_selectedStorageMipDomains.TryGetValue(memory, out var domain)) return;
        lock (_gate)
            foreach (var key in _selectedStorageMipOwners.Keys)
                if (key.Domain == domain.Id && SelectedStorageMipOverlaps(key, resource.Address, checked((ulong)resource.Width * resource.Height * Math.Max(resource.Depth, 1u) * 16)) &&
                    (!storage || !SelectedStorageMipKeysMayCoexist(key, new(domain.Id, resource, mip))))
                    throw new NotSupportedException($"Metal selected storage mip {key.Mip} has native-only contents; allocation-wide or incompatible alias at 0x{resource.Address:X} requires qualified subresource coherence.");
    }
}
