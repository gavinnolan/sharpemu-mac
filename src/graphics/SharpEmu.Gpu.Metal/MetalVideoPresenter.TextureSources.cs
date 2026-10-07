// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.CompilerServices;
using SharpEmu.HLE;
using SharpEmu.Libs.Agc;
using SharpEmu.Libs.Gpu;
using SharpEmu.Libs.Gpu.Images;

namespace SharpEmu.Gpu.Metal;

internal static partial class MetalVideoPresenter
{
    // This budget is separate from queued CPU bytes: a preparation can pin an
    // old GPU version before its consumer is admitted to the queue.
    private const ulong MaximumReservedTextureSourceBytes = 256UL * 1024 * 1024;
    private static ulong _reservedTextureSourceBytes;
    private static ulong _immutableTextureSourceBytes;
    private static long _textureSourceIdentity;
    private static readonly Dictionary<ulong, TextureSourceVersion> _latestTextureSources = new();
    private static readonly List<TextureSourceVersion> _textureSourceVersions = [];
    private static readonly ConditionalWeakTable<GuestDrawTexture, TextureSourceLease> _textureSourceLeases = new();
    private static readonly ConditionalWeakTable<object, QueuedTextureSourceOwner> _queuedTextureSourceOwners = new();
    private static readonly ConditionalWeakTable<GuestDrawTexture, CpuTextureSnapshotProvenance> _cpuTextureSnapshotProvenance = new();

    internal readonly record struct CpuTextureSnapshotObservation(
        long MappingEpoch, GuestImageWriteTracker.ContentToken Token, bool Observed, ulong ByteCount);
    private sealed record CpuTextureSnapshotProvenance(
        CpuTextureSnapshotObservation Observation, long WriteGeneration, bool Stable);

    // Capture only after establishing observation and before copying bytes.
    // This tuple is a conservative freshness veto, never a backing lease.
    internal static CpuTextureSnapshotObservation ObserveCpuTextureSnapshot(
        ulong address, ulong byteCount, long beforeMappingEpoch)
    {
        var observed = GuestImageWriteTracker.TryGetObservedContentToken(address, byteCount, out var token);
        return new(beforeMappingEpoch, token, observed, byteCount);
    }

    internal static bool IsCpuTextureSnapshotObservationStable(
        ulong address, in CpuTextureSnapshotObservation observation) =>
        observation.MappingEpoch == HostMemory.MappingGeneration &&
        (!observation.Observed || GuestImageWriteTracker.TryGetObservedContentToken(address, observation.ByteCount, out var current) &&
            current == observation.Token);

    internal static void RegisterCpuTextureSnapshotProvenance(
        GuestDrawTexture texture, in CpuTextureSnapshotObservation observation)
    {
        if (texture.Address == 0 || texture.RgbaPixels.Length == 0 && texture.TiledSource is not { Length: > 0 }) return;
        lock (_gate)
        {
            if (!_cpuTextureSnapshotProvenance.TryGetValue(texture, out _))
                _cpuTextureSnapshotProvenance.Add(texture, new(observation, texture.WriteGeneration,
                    IsCpuTextureSnapshotObservationStable(texture.Address, observation)));
        }
    }

    private readonly record struct TextureSourceView(
        ulong Address, uint Width, uint Height, uint Format, uint NumberType,
        uint TileMode, uint Pitch, uint Type, uint MipLevels,
        uint BaseMipLevel, uint MipLevel, uint ResourceMipLevels,
        bool Arrayed, uint Layers, uint Depth, uint DstSelect)
    {
        public static TextureSourceView Of(GuestDrawTexture texture, bool verifiedColorProducer = false)
        {
            // DTO pitch describes upload rows, which can differ from the
            // actual tiled guest resource pitch. Owned raw descriptor words
            // retain that resource identity; never borrow a consumer's view
            // to manufacture producer metadata.
            var pitch = texture.Pitch == 0 ? texture.Width : texture.Pitch;
            if (texture.Descriptor is { Length: >= 8 } words)
            {
                if (!AgcBackendServices.TryDecodeTextureDescriptor(words, out var decoded) ||
                    decoded.Address != texture.Address || decoded.Width != texture.Width || decoded.Height != texture.Height ||
                    decoded.Format != texture.Format || decoded.NumberType != texture.NumberType || decoded.Type != texture.Type ||
                    decoded.TileMode != texture.TileMode || decoded.Depth != texture.Depth || decoded.DstSelect != texture.DstSelect ||
                    decoded.BaseLevel != texture.BaseMipLevel || decoded.MipLevels != texture.MipLevels ||
                    decoded.ResourceMipLevels != texture.ResourceMipLevels || decoded.BaseArray != 0 || decoded.ArrayPitch != 0 ||
                    decoded.MetadataAddress != 0 || decoded.DescriptorFlags != 0 || decoded.BcSwizzle != 0 ||
                    decoded.MinLod != 0 || decoded.MinLodWarn != 0)
                    throw new NotSupportedException("Metal texture source raw descriptor does not match its prepared view.");
                pitch = decoded.Pitch;
            }
            else if (!verifiedColorProducer)
                throw new NotSupportedException("Metal ordered texture sources require all eight owned resource descriptor words.");
            return new(texture.Address, texture.Width, texture.Height, texture.Format, texture.NumberType,
                texture.TileMode, pitch, texture.Type, texture.MipLevels, texture.BaseMipLevel, texture.MipLevel,
                texture.ResourceMipLevels, texture.ArrayedView, texture.ArrayLayers, texture.Depth, texture.DstSelect);
        }
    }

    // A logical producer version is separate from its mutable image registry
    // entry. Native handles here are private to Metal and are released only on
    // the render thread; command buffers retain their bound texture references.
    private sealed class TextureSourceVersion
    {
        public readonly long Identity = ++_textureSourceIdentity;
        public required TextureSourceView View;
        public required long ProducerSequence;
        public required GuestQueueIdentity ProducerQueue;
        public required ulong ByteCount;
        public required long MappingEpoch;
        public required long WriteGeneration;
        public GuestImageWriteTracker.ContentToken ContentToken;
        public bool ContentObserved;
        public ulong ObservedGuestByteCount;
        public long PreviousProducerSequence;
        public bool OwnsInitialization;
        public int References;
        public bool Completed;
        public bool Failed;
        public bool MutationStarted;
        public GuestImage? Image;
        public int ImageContentVersion;
        public nint Texture;
        public nint ImmutableTexture;
        public bool Retired;
        public GuestDrawTexture? Descriptor;
        public TextureSourceVersion? Initializer;
        public bool ClearsOutput;
        public bool Budgeted;
        public bool ProducerReleased;
        public int NativePins;
        public bool NativeCompleted;
        public TextureSourceNativeBatch? CopyBatch;
    }

    private sealed class TextureSourceLease(TextureSourceVersion version)
    {
        public TextureSourceVersion Version { get; } = version;
        // One preparation owns the initial reference. Each admitted work owns
        // an additional reference; a failed admission must not steal either.
        public int References = 1;
        public bool PreparationReleased;
    }

    private sealed class QueuedTextureSourceOwner(TextureSourceLease[] leases)
    {
        public TextureSourceLease[] Leases { get; } = leases;
        public bool Released;
    }

    internal static void ReleasePreparedTextureSource(GuestDrawTexture texture)
    {
        lock (_gate)
        {
            if (!_textureSourceLeases.TryGetValue(texture, out var lease) || lease.PreparationReleased) return;
            lease.PreparationReleased = true;
            ReleaseTextureSourceLeaseLocked(lease);
        }
    }

    internal static void ReleasePreparedTextureSources(IReadOnlyList<GuestDrawTexture> textures)
    {
        lock (_gate)
        {
            foreach (var texture in textures)
            {
                if (!_textureSourceLeases.TryGetValue(texture, out var lease) || lease.PreparationReleased)
                    continue;
                lease.PreparationReleased = true;
                ReleaseTextureSourceLeaseLocked(lease);
            }
        }
    }

    private static void ReleaseTextureSourceLeaseLocked(TextureSourceLease lease)
    {
        if (lease.References <= 0) return;
        lease.References--;
        if (--lease.Version.References == 0)
            _reservedTextureSourceBytes -= lease.Version.ByteCount;
    }

    private static void RetainQueuedTextureSourcesLocked(object work)
    {
        // Enumerating/deduplicating can allocate or fail. Do it and validate
        // every owner before changing any counters so admission is atomic.
        if (_queuedTextureSourceOwners.TryGetValue(work, out _))
            throw new InvalidOperationException("Metal guest work already owns texture source leases.");
        var leases = TextureSourcesOf(work).ToArray();
        foreach (var lease in leases)
            if (lease.References <= 0 || lease.PreparationReleased)
                throw new InvalidOperationException("Metal texture source preparation has already been released.");
        if (leases.Length == 0) return;
        // Establish a distinct owner before acquiring references. Allocation or
        // duplicate-key failure cannot leak partial retains, and release is
        // idempotent per work rather than stealing another work's reference.
        _queuedTextureSourceOwners.Add(work, new QueuedTextureSourceOwner(leases));
        foreach (var lease in leases)
        {
            lease.References++;
            lease.Version.References++;
        }
    }

    private static void ReleaseQueuedTextureSourcesLocked(object work)
    {
        if (!_queuedTextureSourceOwners.TryGetValue(work, out var owner) || owner.Released) return;
        owner.Released = true;
        foreach (var lease in owner.Leases) ReleaseTextureSourceLeaseLocked(lease);
    }

    private static IEnumerable<TextureSourceLease> TextureSourcesOf(object work)
    {
        var textures = work switch
        {
            OffscreenGuestDraw draw => draw.Draw.Textures,
            ComputeGuestDispatch dispatch => dispatch.Textures,
            _ => [],
        };
        var unique = new HashSet<TextureSourceLease>(ReferenceEqualityComparer.Instance);
        foreach (var texture in textures)
            if (_textureSourceLeases.TryGetValue(texture, out var lease) && unique.Add(lease)) yield return lease;
    }

    internal static void CopyCapturedTextureSource(GuestDrawTexture source, GuestDrawTexture copy)
    {
        if (ReferenceEquals(source, copy)) return;
        lock (_gate)
        {
            _textureSourceLeases.TryGetValue(source, out var lease);
            _cpuTextureSnapshotProvenance.TryGetValue(source, out var provenance);
            if (lease is not null && (lease.PreparationReleased || lease.References <= 0 ||
                TextureSourceView.Of(copy) != TextureSourceView.Of(source) || copy.Shape != source.Shape || copy.Access != source.Access))
                throw new InvalidOperationException("Metal captured texture source copy must preserve its live preparation, view, shape and access.");
            if (_textureSourceLeases.TryGetValue(copy, out _) || _cpuTextureSnapshotProvenance.TryGetValue(copy, out _))
                throw new InvalidOperationException("Metal texture copy already has a provenance association.");
            if (lease is not null) _textureSourceLeases.Add(copy, lease);
            try { if (provenance is not null) _cpuTextureSnapshotProvenance.Add(copy, provenance); }
            catch
            {
                if (lease is not null) _textureSourceLeases.Remove(copy);
                throw;
            }
        }
    }

    private static long CurrentTextureWriteGeneration(ulong address) =>
        GuestImageWriteTracker.TryGetWriteGeneration(address, out var generation) ? generation : -1;

    private static bool SupportedFloatTextureSourceFormat(GuestDrawTexture texture)
    {
        if (texture.NumberType is not (0 or 1 or 7 or 9)) return false;
        var format = MetalGuestFormats.DecodeTextureFormat(texture.Format, texture.NumberType);
        return !format.IsBlockCompressed && !(texture.IsStorage && format.Format == MtlPixelFormat.Rgba8UnormSrgb);
    }

    private static bool SupportedTextureSourceView(GuestDrawTexture texture) =>
        SupportedFloatTextureSourceFormat(texture) &&
        texture.Type == 9 && !texture.ArrayedView && texture.ArrayLayers == 1 && texture.Depth == 1 &&
        texture.MipLevels == 1 && texture.ResourceMipLevels >= 1 && texture.BaseMipLevel == 0 && texture.MipLevel == 0 &&
        texture.DstSelect == 0xFAC && texture.Shape.NumericClass == TextureNumericClass.Float &&
        !texture.Shape.Volume && !texture.Shape.Arrayed && !texture.Shape.Cube && !texture.Shape.OneDimensional &&
        !texture.Shape.Multisampled && !texture.Shape.DepthCompare && !texture.Shape.DynamicMip &&
        !texture.Shape.R128 && !texture.Shape.Atomic && (texture.Access & GuestImageAccess.Atomic) == 0 &&
        texture.Shape.Storage == texture.IsStorage && texture.Access != GuestImageAccess.Unknown &&
        (texture.Access & ~(GuestImageAccess.Read | GuestImageAccess.Write | GuestImageAccess.Atomic)) == 0 &&
        (texture.IsStorage || texture.Access == GuestImageAccess.Read);

    internal static bool TextureSourceGraphEnabled => _textureSourceGraphEnabled;

    internal static void RequireNoUncapturedGpuTextureSource(GuestDrawTexture texture)
    {
        lock (_gate)
        {
            if (_textureSourceGraphEnabled && texture.Address != 0 && _gpuTextureSourceAddresses.Contains(texture.Address) &&
                !_textureSourceLeases.TryGetValue(texture, out _))
                throw new NotSupportedException($"Metal GPU-produced texture 0x{texture.Address:X} requires a captured immutable source; retired backing cannot be reconstructed from guest memory or mutable live aliases.");
        }
    }

    // No native calls or guest reads: preparation captures a real admitted
    // producer, never an address advertisement or its own prospective writer.
    internal static bool TryCaptureTextureSource(GuestDrawTexture texture, ulong sourceBytes, bool nonzeroBaseLayer)
    {
        if (texture.Address == 0 || texture.IsFallback) return false;
        lock (_gate)
        {
            if (_textureSourceLeases.TryGetValue(texture, out _))
                throw new InvalidOperationException("Metal texture already has a captured source association.");
            if (!_latestTextureSources.TryGetValue(texture.Address, out var source))
            {
                RequireNoUncapturedGpuTextureSource(texture);
                if (_guestImages.TryGetValue(texture.Address, out var image) && image.GpuWritten ||
                    _guestDepthImages.ContainsKey(texture.Address) || _guestDepthReadAliases.ContainsKey(texture.Address))
                    throw new NotSupportedException($"Metal texture 0x{texture.Address:X} has GPU content without a supported ordered source.");
                return false;
            }
            if (!SupportedTextureSourceView(texture) || nonzeroBaseLayer || source.View != TextureSourceView.Of(texture))
                throw new NotSupportedException($"Metal GPU texture source view mismatch at 0x{texture.Address:X}: requested={TextureSourceView.Of(texture)} producer={source.View}.");
            if (source.Failed || source.Retired || source.MutationStarted && source.ImmutableTexture == 0 ||
                source.MappingEpoch != HostMemory.MappingGeneration || source.WriteGeneration != CurrentTextureWriteGeneration(texture.Address) ||
                !source.ContentObserved || !GuestImageWriteTracker.TryGetObservedContentToken(texture.Address, source.ObservedGuestByteCount, out var currentToken) ||
                currentToken != source.ContentToken)
                throw new InvalidOperationException($"Metal GPU texture source invalidated at 0x{texture.Address:X}: producer={source.ProducerSequence} identity={source.Identity}.");
            if (sourceBytes > source.ObservedGuestByteCount)
            {
                // Widen first-write observation before publishing a read
                // reservation; replacing a tracker range preserves generation.
                GuestImageWriteTracker.Track(texture.Address, sourceBytes, source: "metal.gpu-source-read");
                source.ObservedGuestByteCount = sourceBytes;
                if (CurrentTextureWriteGeneration(texture.Address) != source.WriteGeneration ||
                    source.MappingEpoch != HostMemory.MappingGeneration ||
                    !GuestImageWriteTracker.TryGetObservedContentToken(texture.Address, source.ObservedGuestByteCount, out var widenedToken) || widenedToken != source.ContentToken)
                    throw new InvalidOperationException("Metal GPU texture source CPU generation changed while observing its view.");
            }
            if (source.References == 0)
            {
                if (source.ByteCount > MaximumReservedTextureSourceBytes - Math.Min(_reservedTextureSourceBytes, MaximumReservedTextureSourceBytes))
                    throw new InvalidOperationException("Metal reserved texture source budget exceeded.");
                _reservedTextureSourceBytes += source.ByteCount;
            }
            source.References++;
            try { _textureSourceLeases.Add(texture, new TextureSourceLease(source)); }
            catch
            {
                if (--source.References == 0) _reservedTextureSourceBytes -= source.ByteCount;
                throw;
            }
            return true;
        }
    }
    // Called under the presenter gate only after queue admission assigned a
    // real sequence. A pending producer owns faithful initialization or follows
    // an admitted compatible writer; advertisements never enter this registry.
    private static void PublishAdmittedTextureSourceLocked(
        GuestDrawTexture descriptor, long sequence, GuestQueueIdentity queue, bool initializesWholeImage)
    {
        if (sequence <= 0 || descriptor.Address == 0) return;
        var source = PrepareTextureSourceVersionLocked(descriptor, sequence, queue, initializesWholeImage);
        _latestTextureSources[descriptor.Address] = source;
        _textureSourceVersions.Add(source);
    }

    private static TextureSourceVersion PrepareTextureSourceVersionLocked(
        GuestDrawTexture descriptor, long sequence, GuestQueueIdentity queue, bool initializesWholeImage,
        bool verifiedColorProducer = false)
    {
        if (!SupportedTextureSourceView(descriptor) || descriptor.Width == 0 || descriptor.Height == 0 ||
            descriptor.Width > 16384 || descriptor.Height > 16384)
            throw new NotSupportedException($"Metal ordered producer has unsupported view at 0x{descriptor.Address:X}.");
        var view = TextureSourceView.Of(descriptor, verifiedColorProducer);
        _latestTextureSources.TryGetValue(descriptor.Address, out var previous);
        var hasPrevious = previous is { Failed: false, Retired: false } && previous.View == view &&
            previous.MappingEpoch == HostMemory.MappingGeneration &&
            previous.WriteGeneration == CurrentTextureWriteGeneration(descriptor.Address) &&
            previous.ContentObserved && GuestImageWriteTracker.TryGetObservedContentToken(descriptor.Address, previous.ObservedGuestByteCount, out var previousToken) &&
            previousToken == previous.ContentToken;
        var hasSeed = descriptor.CpuSnapshotStable &&
            (descriptor.RgbaPixels.Length != 0 || descriptor.TiledSource is { Length: > 0 });
        CpuTextureSnapshotProvenance? provenance = null;
        if (hasSeed)
        {
            if (!_cpuTextureSnapshotProvenance.TryGetValue(descriptor, out provenance) ||
                !provenance.Stable || !provenance.Observation.Observed ||
                !IsCpuTextureSnapshotObservationStable(descriptor.Address, provenance.Observation) ||
                provenance.WriteGeneration != descriptor.WriteGeneration ||
                provenance.WriteGeneration != CurrentTextureWriteGeneration(descriptor.Address))
                throw new InvalidOperationException("Metal storage seed provenance changed between preparation and publication.");
            var prepared = PrepareDrawTextureUpload(descriptor);
            _ = StorageTextureSeedLayout(prepared, StorageTextureFormat(prepared));
        }
        var hasCaptured = _textureSourceLeases.TryGetValue(descriptor, out var captured) &&
            !captured.PreparationReleased && captured.References > 0 && !captured.Version.Failed &&
            captured.Version.View == view &&
            captured.Version.MappingEpoch == HostMemory.MappingGeneration &&
            captured.Version.WriteGeneration == CurrentTextureWriteGeneration(descriptor.Address) &&
            captured.Version.ContentObserved && GuestImageWriteTracker.TryGetObservedContentToken(descriptor.Address, captured.Version.ObservedGuestByteCount, out var capturedToken) &&
            capturedToken == captured.Version.ContentToken;
        if (hasSeed && hasPrevious && !hasCaptured)
            throw new InvalidOperationException("Metal CPU seed cannot overwrite a current GPU producer without a captured source choice.");
        if (!initializesWholeImage && !hasPrevious && !hasSeed && !hasCaptured)
            throw new InvalidOperationException($"Metal storage writer 0x{descriptor.Address:X} has no faithful initialization source.");
        var nativeFormat = MetalGuestFormats.DecodeTextureFormat(descriptor.Format, descriptor.NumberType);
        var byteCount = MetalGuestFormats.GetTextureByteCount(nativeFormat, descriptor.Width, descriptor.Height);
        var observedBytes = Math.Max(byteCount, descriptor.SourceByteCount);
        var observed = GuestImageWriteTracker.BeginReadSnapshot(descriptor.Address, observedBytes,
            sequence, "metal.gpu-source-producer");
        var contentObserved = GuestImageWriteTracker.TryGetObservedContentToken(descriptor.Address, observedBytes, out var observedContentToken);
        if (!contentObserved)
            throw new InvalidOperationException("Metal ordered producer has no complete content observation.");
        // Observation may register a previously absent range at generation0;
        // it must never retimestamp a known copied seed after a CPU write.
        if (hasSeed && (!provenance!.Stable || !IsCpuTextureSnapshotObservationStable(descriptor.Address, provenance.Observation) ||
            (observed.Active ? observed.Generation : -1) != provenance.WriteGeneration ||
            CurrentTextureWriteGeneration(descriptor.Address) != provenance.WriteGeneration))
            throw new InvalidOperationException("Metal storage seed provenance changed while publishing its producer.");
        if (hasPrevious && (previous!.MappingEpoch != HostMemory.MappingGeneration ||
            previous.WriteGeneration != (observed.Active ? observed.Generation : -1) ||
            !GuestImageWriteTracker.TryGetObservedContentToken(descriptor.Address, previous.ObservedGuestByteCount, out var observedPrevious) || observedPrevious != previous.ContentToken))
            throw new InvalidOperationException("Metal previous storage producer changed while publishing its successor.");
        if (hasCaptured && (captured!.Version.MappingEpoch != HostMemory.MappingGeneration ||
            captured.Version.WriteGeneration != (observed.Active ? observed.Generation : -1) ||
            !GuestImageWriteTracker.TryGetObservedContentToken(descriptor.Address, captured.Version.ObservedGuestByteCount, out var observedCaptured) || observedCaptured != captured.Version.ContentToken))
            throw new InvalidOperationException("Metal captured storage initializer changed while publishing its producer.");
        var contentToken = hasSeed ? provenance!.Observation.Token :
            hasCaptured ? captured!.Version.ContentToken :
            hasPrevious ? previous!.ContentToken : observedContentToken;
        var source = new TextureSourceVersion
        {
            View = view,
            ProducerSequence = sequence,
            ProducerQueue = queue,
            ByteCount = byteCount,
            ObservedGuestByteCount = observedBytes,
            MappingEpoch = hasSeed ? provenance!.Observation.MappingEpoch : HostMemory.MappingGeneration,
            WriteGeneration = observed.Active ? observed.Generation : -1,
            ContentObserved = contentObserved,
            ContentToken = contentToken,
            PreviousProducerSequence = hasPrevious ? previous!.ProducerSequence : 0,
            OwnsInitialization = initializesWholeImage || hasSeed || hasPrevious || hasCaptured,
            Descriptor = descriptor,
            Initializer = hasCaptured ? captured!.Version : hasPrevious ? previous : null,
            ClearsOutput = initializesWholeImage && !hasCaptured && !hasPrevious && !hasSeed,
        };
        return source;
    }

    // Metadata invalidation is real even before active queue/native ownership
    // lands. Activation must also cancel explicit work owners and uncommitted
    // native-use pins; this method alone does not claim resource cleanup.
    private static void InvalidateTextureSourceVersionsLocked()
    {
        foreach (var source in _textureSourceVersions) source.Failed = true;
    }

    internal static (long Producer, int References, bool Completed, bool Failed, ulong ReservedBytes) TextureSourceState(GuestDrawTexture texture)
    {
        lock (_gate)
        {
            if (!_textureSourceLeases.TryGetValue(texture, out var lease)) return default;
            return (lease.Version.ProducerSequence, lease.References, lease.Version.Completed, lease.Version.Failed, _reservedTextureSourceBytes);
        }
    }

}
