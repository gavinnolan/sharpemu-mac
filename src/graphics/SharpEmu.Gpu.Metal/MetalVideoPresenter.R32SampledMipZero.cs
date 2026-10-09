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
    // This receipt owns detached CPU pixels. It is neither a live GPU owner nor
    // permission to reconstruct a later image from its virtual address.
    private sealed record OwnedR32MipZero(ICpuMemory Memory, ICpuMappedReadWatchSource Source,
        CpuMappedCopyObservation Mapping, GuestDrawTexture Original, long Session);
    private static readonly ConditionalWeakTable<GuestDrawTexture, OwnedR32MipZero> _ownedR32MipZeros = new();
    private readonly record struct R32PhysicalSpan(long Backing, ulong Offset, ulong Bytes);

    private static bool MatchesOwnedR32MipZero(GuestDrawTexture texture, OwnedR32MipZero original) =>
        ReferenceEquals(texture.RgbaPixels, original.Original.RgbaPixels) && texture.RgbaPixels.Length == 0x80000 &&
        (texture with { Sampler = original.Original.Sampler, Descriptor = original.Original.Descriptor }) == original.Original &&
        texture.Descriptor is { Length: 8 } raw && original.Original.Descriptor is { Length: 8 } old && raw.AsSpan().SequenceEqual(old);

    private static InvalidOperationException R32MipZeroRefused(string reason) =>
        new($"Metal owned R32 sampled mip zero was refused: {reason}; no base-address read or native-owner replacement is qualified.");

    // A prior original mip1 mapping can describe the whole provider mapping,
    // even though its validated copy footprint is only the mip1 prefix. Before
    // any CPU read, require one such original segment to contain ALL mip0.
    // The actual mip0 copy subsequently validates its own complete footprint.
    internal static void RequireR32SampledMipZeroCopyPreflight(ICpuMemory memory, Descriptor resource, long session)
    {
        lock (_gate)
        {
            RequireR32MipZeroInventoryLocked(session);
            if (_selectedStorageMipOwners.Count == 0) return;
            if (!_selectedStorageMipDomains.TryGetValue(memory, out var domain))
                throw R32MipZeroRefused("foreign selected-owner domain before original copy");
            SelectedStorageMipSeed? anchor = null;
            foreach (var pair in _selectedStorageMipOwners)
                if (pair.Key.Domain == domain.Id && pair.Key.Resource.Address == resource.Address &&
                    IsR32MipOneOriginal(pair.Value, memory, session, out var candidate))
                { anchor = candidate; break; }
            if (anchor is null || !anchor.Source.IsMappedCopyObservationCurrent(anchor.Mapping))
                throw R32MipZeroRefused("missing current original same-chain mip1 mapping before copy");
            var address = checked(resource.Address + 0x40000);
            R32PhysicalSpan? projected = null;
            foreach (var segment in anchor.Mapping.Segments)
                if (address >= segment.MappingAddress && address - segment.MappingAddress <= segment.MappingByteCount &&
                    0x80000UL <= segment.MappingByteCount - (address - segment.MappingAddress))
                {
                    var span = new R32PhysicalSpan(segment.BackingIdentity,
                        checked(segment.MappingBackingOffset + address - segment.MappingAddress), 0x80000);
                    if (projected is { } previous && previous != span)
                        throw R32MipZeroRefused("contradictory original mip0 physical projection");
                    projected = span;
                }
            if (projected is not { } complete) throw R32MipZeroRefused("original mip1 mapping does not describe complete mip0");
            foreach (var pair in _selectedStorageMipOwners)
            {
                if (!IsR32MipOneOriginal(pair.Value, memory, session, out var other) ||
                    !other.Source.IsMappedCopyObservationCurrent(other.Mapping) ||
                    SourcePhysicalSpans(other.Mapping).Any(span => PhysicalSpansOverlap(complete, span)))
                    throw R32MipZeroRefused("unknown, stale or physically overlapping selected writer before copy");
            }
        }
    }

    internal static void RegisterOwnedR32SampledMipZero(ICpuMemory memory, ICpuMappedReadWatchSource source,
        CpuMappedCopyObservation mapping, GuestDrawTexture texture, long session)
    {
        if (!ReferenceEquals(memory, source) || mapping.FootprintAddress != texture.Address ||
            mapping.FootprintByteCount != 0xC0000 || mapping.SourceAddress != texture.Address + 0x40000 ||
            mapping.SourceByteCount != 0x80000 || !source.IsMappedCopyObservationCurrent(mapping) ||
            !source.IsMappedCopyWriteWatchCurrent(mapping)) throw R32MipZeroRefused("original watched-copy registration");
        var original = new OwnedR32MipZero(memory, source, mapping, texture, session);
        if (!MatchesOwnedR32MipZero(texture, original)) throw R32MipZeroRefused("incomplete owned payload");
        lock (_gate)
        {
            RequireOwnedR32MipZeroCurrentLocked(texture, original);
            _ownedR32MipZeros.Add(texture, original);
        }
    }

    private static void CopyOwnedR32SampledMipZero(GuestDrawTexture source, GuestDrawTexture copy)
    {
        if (!_ownedR32MipZeros.TryGetValue(source, out var original)) return;
        if (!MatchesOwnedR32MipZero(copy, original)) throw R32MipZeroRefused("copy changed exact detached pixels/view");
        _ownedR32MipZeros.Add(copy, original);
    }

    private static bool IsOwnedR32SampledMipZero(GuestDrawTexture texture) => _ownedR32MipZeros.TryGetValue(texture, out _);

    private static void RequireR32MipZeroInventoryLocked(long session)
    {
        if (TextureSourceGraphEnabled || session != CurrentSelectedStorageMipSeedSession ||
            _selectedNativeOriginalCleanupFailure is not null || _textureSourceCleanupFailure is not null ||
            _finiteStorageRetiredUnknown || _finiteStorageRetiredOriginCount != 0 || _finiteRetiredDisplayOriginCount != 0 ||
            _guestImages.Count != 0 || _guestImageExtents.Count != 0 || _latestTextureSources.Count != 0 ||
            _guestDepthImages.Count != 0 || _guestDepthReadAliases.Count != 0 ||
            _unorderedPresentationWriteSequence != 0 ||
            _selectedStorageMipOwners.Count > 128 || _selectedStorageMipImages.Count > 128)
            throw R32MipZeroInventoryRefusedLocked(session);
        foreach (var pending in _pendingGuestWorkByQueue.Values.SelectMany(static queue => queue))
            RequireR32KnownWriterWork(pending.Work);
        foreach (var pair in _selectedStorageMipImages)
            if (!_selectedStorageMipOwners.TryGetValue(pair.Key, out var owner) ||
                !_selectedNativeOriginalProofs.TryGetValue(pair.Key, out var proof) ||
                !ReferenceEquals(proof.Owner, owner) || !ReferenceEquals(proof.Image, pair.Value))
                throw R32MipZeroRefused("orphaned selected native image or original proof");
        if (_selectedNativeOriginalProofs.Keys.Any(key => !_selectedStorageMipImages.ContainsKey(key)) ||
            _selectedStorageMipPending.Keys.Any(key => !_selectedStorageMipOwners.ContainsKey(key)) ||
            _selectedStorageMipNativeQueues.Keys.Any(key => !_selectedStorageMipOwners.ContainsKey(key)))
            throw R32MipZeroRefused("orphaned original proof, pending writer or native queue");
        // A taken producer is no longer in the pending list. This existing
        // retained DTO is set for EVERY taken work kind before leaving _gate;
        // inspect its original associations, never an address advertisement.
        if (_takenGuestWorkSequence != 0)
        {
            if (_takenDepthCubeProducerWork is not { } taken)
                throw R32MipZeroRefused("taken native work lacks its exact accepted DTO");
            RequireR32KnownWriterWork(taken);
        }
    }

    private static void RequireR32KnownWriterWork(object work)
    {
        if (work is GlobalDataShareFill or GlobalDataShareCopyFromGuest) return;
        if (work is not ComputeGuestDispatch dispatch ||
            dispatch.GlobalMemoryBuffers.Any(static buffer => buffer.Writable && buffer.WriteBackToGuest) ||
            dispatch.Textures.Any(texture => StorageImageWrites(texture) && !_selectedStorageMipAssociations.TryGetValue(texture, out _)))
            throw R32MipZeroRefused("pending/incoming work has an unqualified ordinary image, buffer or guest writer");
    }

    private static bool IsR32MipOneOriginal(SelectedStorageMipAssociation owner, ICpuMemory memory,
        long session, out SelectedStorageMipSeed seed)
    {
        seed = null!;
        var d = owner.Key.Resource;
        if (owner.Session != session || owner.Key.Mip != 1 || d.Type != 9 || d.Format != 4 || d.NumberType != 7 ||
            d.Width != 512 || d.Height != 256 || d.Pitch != 512 || d.Depth != 1 || d.TileMode != 9 ||
            d.ResourceMipLevels != 9 || d.BaseLevel != 1 || d.LastLevel != 1 ||
            !_selectedStorageMipDomains.TryGetValue(memory, out var domain) || owner.Key.Domain != domain.Id ||
            !_selectedStorageMipSeeds.TryGetValue(owner, out var original) || !ReferenceEquals(original.Source, memory) ||
            original.Mapping.FootprintAddress != d.Address || original.Mapping.FootprintByteCount != 0x40000 ||
            original.Mapping.SourceAddress != d.Address + 0x20000 || original.Mapping.SourceByteCount != 0x20000) return false;
        // If a native owner exists, bind its original creation proof rather than
        // assuming that an address registry still names that original mapping.
        if (_selectedStorageMipImages.TryGetValue(owner.Key, out var image) &&
            (!_selectedNativeOriginalProofs.TryGetValue(owner.Key, out var proof) || !ReferenceEquals(proof.Owner, owner) ||
             !ReferenceEquals(proof.Seed, original) || !ReferenceEquals(proof.Image, image) || image.Texture != proof.Texture ||
             image.ContentVersion != proof.Version || !image.Initialized)) return false;
        seed = original; return true;
    }

    private static IEnumerable<R32PhysicalSpan> SourcePhysicalSpans(CpuMappedCopyObservation mapping)
    {
        var end = checked(mapping.SourceAddress + mapping.SourceByteCount);
        var cursor = mapping.SourceAddress;
        foreach (var segment in mapping.Segments)
        {
            var first = Math.Max(mapping.SourceAddress, segment.CoveredAddress);
            var last = Math.Min(end, checked(segment.CoveredAddress + segment.CoveredByteCount));
            if (first >= last) continue;
            if (first != cursor) throw R32MipZeroRefused("original source physical coverage gap");
            yield return new(segment.BackingIdentity, checked(segment.MappingBackingOffset + first - segment.MappingAddress), last - first);
            cursor = last;
        }
        if (cursor != end) throw R32MipZeroRefused("original source physical coverage incomplete");
    }

    private static bool PhysicalSpansOverlap(R32PhysicalSpan a, R32PhysicalSpan b) => a.Backing == b.Backing &&
        (UInt128)a.Offset < (UInt128)b.Offset + b.Bytes && (UInt128)b.Offset < (UInt128)a.Offset + a.Bytes;

    private static void RequireOwnedR32MipZeroCurrentLocked(GuestDrawTexture texture, OwnedR32MipZero original,
        IEnumerable<SelectedStorageMipAssociation>? incoming = null)
    {
        RequireR32MipZeroInventoryLocked(original.Session);
        if (!MatchesOwnedR32MipZero(texture, original) || !original.Source.IsMappedCopyObservationCurrent(original.Mapping))
            throw R32MipZeroRefused("original copied view/mapping changed");
        var spans = SourcePhysicalSpans(original.Mapping).ToArray();
        foreach (var owner in _selectedStorageMipOwners.Values.Concat(incoming ?? []))
        {
            if (!IsR32MipOneOriginal(owner, original.Memory, original.Session, out var seed) ||
                !seed.Source.IsMappedCopyObservationCurrent(seed.Mapping) ||
                SourcePhysicalSpans(seed.Mapping).Any(other => spans.Any(span => PhysicalSpansOverlap(span, other))))
                throw R32MipZeroRefused("unknown, stale or physically overlapping original selected writer");
        }
    }

    private static void ValidateOwnedR32MipZeroAdmissionLocked(object work)
    {
        if (work is not ComputeGuestDispatch dispatch)
        {
            if (work is OffscreenGuestDraw draw && draw.Draw.Textures.Any(IsOwnedR32SampledMipZero))
                throw R32MipZeroRefused("owned sampled mip0 draw dependency is not qualified");
            return;
        }
        if (!dispatch.Textures.Any(IsOwnedR32SampledMipZero)) return;
        RequireR32KnownWriterWork(work);
        var incoming = dispatch.Textures.Select(texture => _selectedStorageMipAssociations.TryGetValue(texture, out var owner) ? owner : null)
            .Where(static owner => owner is not null).Cast<SelectedStorageMipAssociation>().ToArray();
        foreach (var texture in dispatch.Textures)
            if (_ownedR32MipZeros.TryGetValue(texture, out var original)) RequireOwnedR32MipZeroCurrentLocked(texture, original, incoming);
    }
}
