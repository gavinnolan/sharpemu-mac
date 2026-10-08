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
    private static readonly ConditionalWeakTable<ICpuMemory, SelectedStorageMipDomain> _selectedStorageMipDomains = new();
    private static readonly ConditionalWeakTable<GuestDrawTexture, SelectedStorageMipAssociation> _selectedStorageMipAssociations = new();
    private static readonly Dictionary<SelectedStorageMipKey, GuestImage> _selectedStorageMipImages = new();
    private static readonly Dictionary<SelectedStorageMipKey, nint> _selectedStorageMipNativeQueues = new();

    internal static void RegisterSelectedStorageMip(ICpuMemory memory, Descriptor resource, GuestDrawTexture texture,
        long epoch, GuestImageWriteTracker.ContentToken token, bool hasToken, CpuTextureSnapshotObservation observation)
    {
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
        if (requireObserved && (!selected.Observation.Observed ||
            !IsCpuTextureSnapshotObservationStable(selected.Key.Resource.Address, selected.Observation)))
            throw new InvalidOperationException("Metal selected storage mip live reuse requires unchanged complete content observation; tracker identity alone cannot authorize unobserved CPU writers.");
        // Preserve the original session -> global epoch -> token short circuit.
        // Diagnostics reuse exactly the observation that vetoed admission; no
        // extra tracker/host query runs on either success or rejection.
        var currentSession = Volatile.Read(ref _selectedStorageMipSession);
        if (selected.Session != currentSession)
            throw SelectedStorageMipObservationFailure(selected, stage, "presenter-session",
                $"session_before={selected.Session} session_now={currentSession}");
        var currentMappingEpoch = HostMemory.MappingGeneration;
        if (selected.MappingEpoch != currentMappingEpoch)
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

    private readonly record struct SelectedStorageMipAdmission(SelectedStorageMipAssociation Owner, SelectedStorageMipPending Pending);

    private static SelectedStorageMipAdmission[] PrepareSelectedStorageMipAdmissionLocked(object work, GuestQueueIdentity queue)
    {
        if (work is not ComputeGuestDispatch dispatch ||
            !dispatch.Textures.Any(texture => _selectedStorageMipAssociations.TryGetValue(texture, out _))) return [];
        if (_enqueueAsImmediateQueueFollowup && dispatch.Textures.Any(texture => _selectedStorageMipAssociations.TryGetValue(texture, out _)))
            throw new NotSupportedException("Metal selected storage mip immediate queue follow-up ordering is not qualified.");
        var selected = new Dictionary<SelectedStorageMipKey, SelectedStorageMipAssociation>();
        foreach (var texture in dispatch.Textures)
        {
            if (!_selectedStorageMipAssociations.TryGetValue(texture, out var association)) continue;
            ValidateSelectedStorageMipOwnerAtStage(association, false, "admission");
            if (_selectedStorageMipPending.TryGetValue(association.Key, out var pending) && pending.Queue != queue.Name)
                throw new NotSupportedException("Metal selected storage mip has an outstanding producer on another logical queue.");
            if (StorageImageWrites(texture)) selected.TryAdd(association.Key, association);
        }
        var added = selected.Keys.Count(key => !_selectedStorageMipOwners.ContainsKey(key));
        if (_selectedStorageMipOwners.Count + added > 128)
            throw new NotSupportedException("Metal selected storage mip owner limit exceeded.");
        _selectedStorageMipOwners.EnsureCapacity(_selectedStorageMipOwners.Count + added);
        _selectedStorageMipPending.EnsureCapacity(_selectedStorageMipPending.Count + selected.Count);
        // Construct every metadata object before queue admission mutates its
        // sequence/list. Publication below has no allocating iterator/object.
        return selected.Values.Select(owner => new SelectedStorageMipAdmission(owner,
            new(queue.Name, _enqueuedGuestWorkSequence + 1))).ToArray();
    }

    private static void PublishSelectedStorageMipAdmissionLocked(SelectedStorageMipAdmission[] admission)
    {
        foreach (var item in admission)
        {
            _selectedStorageMipOwners.TryAdd(item.Owner.Key, item.Owner);
            _selectedStorageMipPending[item.Owner.Key] = item.Pending;
        }
    }

    private static void ValidateSelectedStorageMipExecution(GuestDrawTexture texture, nint queue)
    {
        if (!_selectedStorageMipAssociations.TryGetValue(texture, out var selected)) return;
        lock (_gate)
        {
            ValidateSelectedStorageMipOwner(selected);
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
        texture.Width == 256 && texture.Height == 128 && texture.Pitch == 256 &&
        texture.Address == association.Key.Resource.Address && texture.MipLevel == association.Key.Mip && texture.BaseMipLevel == 1 &&
        texture.ResourceMipLevels == 9 && texture.SourceByteCount == 262144 && texture.MipLevels == 1 &&
        texture.Format == 4 && texture.NumberType == 7 && texture.Type == 9 && texture.TileMode == 9 &&
        texture.DstSelect == association.Key.Resource.DstSelect &&
        texture.Descriptor is { Length: >= 8 } raw &&
        AgcBackendServices.TryDecodeTextureDescriptor(raw, out var resource) && resource == association.Key.Resource;

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
            foreach (var image in _selectedStorageMipImages.Values)
                MetalNative.SendVoid(image.Texture, MetalNative.Selector("release"));
            Interlocked.Increment(ref _selectedStorageMipSession);
            _selectedStorageMipImages.Clear();
            _selectedStorageMipOwners.Clear();
            _selectedStorageMipPending.Clear();
            _selectedStorageMipNativeQueues.Clear();
        }
    }

    private static bool SelectedStorageMipOverlaps(SelectedStorageMipKey key, ulong address, ulong bytes) =>
        (UInt128)address < (UInt128)key.Resource.Address + 786432 &&
        (UInt128)key.Resource.Address < (UInt128)address + Math.Max(bytes, 1UL);

    private static void RejectAddressOnlySelectedStorageAlias(ulong address, ulong bytes = 1)
    {
        lock (_gate)
            if (_selectedStorageMipOwners.Keys.Any(key => SelectedStorageMipOverlaps(key, address, bytes)))
                throw new NotSupportedException($"Metal address-only image use at 0x{address:X} cannot consume native-only selected storage mip contents.");
    }

    internal static void RejectSelectedStorageAlias(ICpuMemory memory, Descriptor resource, bool storage, uint mip)
    {
        if (!_selectedStorageMipDomains.TryGetValue(memory, out var domain)) return;
        lock (_gate)
            foreach (var key in _selectedStorageMipOwners.Keys)
                if (key.Domain == domain.Id && SelectedStorageMipOverlaps(key, resource.Address, checked((ulong)resource.Width * resource.Height * Math.Max(resource.Depth, 1u) * 16)) &&
                    (!storage || key.Resource != resource || key.Mip != mip))
                    throw new NotSupportedException($"Metal selected storage mip {key.Mip} has native-only contents; allocation-wide or incompatible alias at 0x{resource.Address:X} requires qualified subresource coherence.");
    }
}
