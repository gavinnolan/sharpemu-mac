// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.CompilerServices;
using SharpEmu.HLE;
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

    private readonly record struct TextureSourceView(
        ulong Address, uint Width, uint Height, uint Format, uint NumberType,
        uint TileMode, uint Pitch, uint Type, uint MipLevels,
        uint BaseMipLevel, uint MipLevel, uint ResourceMipLevels,
        bool Arrayed, uint Layers, uint Depth)
    {
        public static TextureSourceView Of(GuestDrawTexture texture) => new(
            texture.Address, texture.Width, texture.Height, texture.Format, texture.NumberType,
            texture.TileMode, texture.Pitch == 0 ? texture.Width : texture.Pitch,
            texture.Type, texture.MipLevels, texture.BaseMipLevel, texture.MipLevel,
            texture.ResourceMipLevels, texture.ArrayedView, texture.ArrayLayers, texture.Depth);
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
    }

    private sealed class TextureSourceLease(TextureSourceVersion version)
    {
        public TextureSourceVersion Version { get; } = version;
        // One preparation owns the initial reference. Each admitted work owns
        // an additional reference; a failed admission must not steal either.
        public int References = 1;
        public bool PreparationReleased;
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
        var leases = TextureSourcesOf(work).ToArray();
        foreach (var lease in leases)
            if (lease.References <= 0 || lease.PreparationReleased)
                throw new InvalidOperationException("Metal texture source preparation has already been released.");
        foreach (var lease in leases)
        {
            lease.References++;
            lease.Version.References++;
        }
    }

    private static void ReleaseQueuedTextureSourcesLocked(object work)
    {
        foreach (var lease in TextureSourcesOf(work)) ReleaseTextureSourceLeaseLocked(lease);
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
            if (_textureSourceLeases.TryGetValue(source, out var lease))
                _textureSourceLeases.Add(copy, lease);
    }

    private static long CurrentTextureWriteGeneration(ulong address) =>
        GuestImageWriteTracker.TryGetWriteGeneration(address, out var generation) ? generation : -1;

    private static bool SupportedTextureSourceView(GuestDrawTexture texture) =>
        texture.Type == 9 && !texture.ArrayedView && texture.ArrayLayers == 1 && texture.Depth == 1 &&
        texture.MipLevels == 1 && texture.ResourceMipLevels == 1 && texture.BaseMipLevel == 0 && texture.MipLevel == 0 &&
        texture.DstSelect is 0 or 0xFAC && texture.Shape.NumericClass == TextureNumericClass.Float &&
        !texture.Shape.Volume && !texture.Shape.Arrayed && !texture.Shape.Cube && !texture.Shape.OneDimensional &&
        !texture.Shape.Multisampled && !texture.Shape.DepthCompare && !texture.Shape.DynamicMip &&
        !texture.Shape.R128 && !texture.Shape.Atomic && (texture.Access & GuestImageAccess.Atomic) == 0 &&
        texture.Shape.Storage == texture.IsStorage && texture.Access != GuestImageAccess.Unknown &&
        (texture.Access & ~(GuestImageAccess.Read | GuestImageAccess.Write | GuestImageAccess.Atomic)) == 0 &&
        (texture.IsStorage || texture.Access == GuestImageAccess.Read);

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
                if (_guestImages.TryGetValue(texture.Address, out var image) && image.GpuWritten ||
                    _guestDepthImages.ContainsKey(texture.Address) || _guestDepthReadAliases.ContainsKey(texture.Address))
                    throw new NotSupportedException($"Metal texture 0x{texture.Address:X} has GPU content without a supported ordered source.");
                return false;
            }
            if (!SupportedTextureSourceView(texture) || nonzeroBaseLayer || source.View != TextureSourceView.Of(texture))
                throw new NotSupportedException($"Metal GPU texture source view mismatch at 0x{texture.Address:X}: requested={TextureSourceView.Of(texture)} producer={source.View}.");
            if (source.Failed || source.Retired || source.MutationStarted && source.ImmutableTexture == 0 ||
                source.MappingEpoch != HostMemory.MappingGeneration || source.WriteGeneration != CurrentTextureWriteGeneration(texture.Address))
                throw new InvalidOperationException($"Metal GPU texture source invalidated at 0x{texture.Address:X}: producer={source.ProducerSequence} identity={source.Identity}.");
            if (sourceBytes > source.ObservedGuestByteCount)
            {
                // Widen first-write observation before publishing a read
                // reservation; replacing a tracker range preserves generation.
                GuestImageWriteTracker.Track(texture.Address, sourceBytes, source: "metal.gpu-source-read");
                source.ObservedGuestByteCount = sourceBytes;
                if (CurrentTextureWriteGeneration(texture.Address) != source.WriteGeneration)
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
        var view = TextureSourceView.Of(descriptor);
        _latestTextureSources.TryGetValue(descriptor.Address, out var previous);
        var hasPrevious = previous is { Failed: false, Retired: false } && previous.View == view;
        var hasSeed = descriptor.CpuSnapshotStable &&
            (descriptor.RgbaPixels.Length != 0 || descriptor.TiledSource is { Length: > 0 });
        if (!initializesWholeImage && !hasPrevious && !hasSeed && !_textureSourceLeases.TryGetValue(descriptor, out _))
            throw new InvalidOperationException($"Metal storage writer 0x{descriptor.Address:X} has no faithful initialization source.");
        var nativeFormat = MetalGuestFormats.DecodeTextureFormat(descriptor.Format, descriptor.NumberType);
        var byteCount = MetalGuestFormats.GetTextureByteCount(nativeFormat, descriptor.Width, descriptor.Height);
        var observedBytes = Math.Max(byteCount, descriptor.SourceByteCount);
        var observed = GuestImageWriteTracker.BeginReadSnapshot(descriptor.Address, observedBytes,
            sequence, "metal.gpu-source-producer");
        var source = new TextureSourceVersion
        {
            View = view,
            ProducerSequence = sequence,
            ProducerQueue = queue,
            ByteCount = byteCount,
            ObservedGuestByteCount = observedBytes,
            MappingEpoch = HostMemory.MappingGeneration,
            WriteGeneration = observed.Active ? observed.Generation : -1,
            PreviousProducerSequence = hasPrevious ? previous!.ProducerSequence : 0,
            OwnsInitialization = initializesWholeImage || hasSeed || hasPrevious || _textureSourceLeases.TryGetValue(descriptor, out _),
        };
        _latestTextureSources[descriptor.Address] = source;
        _textureSourceVersions.Add(source);
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
