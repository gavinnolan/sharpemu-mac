// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE.GpuMemory;
using SharpEmu.Libs.Gpu.Abstractions;
using SharpEmu.Libs.Gpu.Images;
using SharpEmu.Libs.Gpu.Scheduling;

namespace SharpEmu.Gpu.Metal.V2;

// Why a cached image has to be uploaded from guest memory again. Counted per cause
// so a run can say whether the churn comes from the guest writing the bytes or from
// the host invalidating them itself.
internal enum MetalImageDirtyCause : byte
{
    Created,
    CpuWrite,
    GpuWrite,
    Invalidated,
    AliasWrite,
}

// Why a cached image was dropped and a new one created in its place. A title that
// recreates the same surface every frame pays an upload and a writeback for each
// cycle, and the reason is the only thing that says which rule to relax.
internal enum MetalImageReleaseCause : byte
{
    FormatMismatch,
    FewerResources,
    ViewType,
    SameAddressReplaced,
    SubsetOfLarger,
    OverlapEvicted,
    Budget,
    Unmapped,
    // A texture that read the guest window in place became an owned texture because the
    // image was written, stored to, copied into, or needed a view the window texture cannot give.
    LinearViewPromoted,
}

// Why a GPU-written image had to be published into guest memory.
internal enum MetalWritebackCause : byte
{
    BufferBinding,
    StorageDescriptor,
    HostRead,
    GuestCopy,
    ImageOverlap,
    Release,
    DrainAll,
}

// What a cached image has been bound as; the upload role follows from it.
internal struct MetalImageUses
{
    public bool Texture;
    public bool Storage;
    public bool RenderTarget;
    public bool DepthTarget;
    public bool VideoOut;
}

// One guest image resident on the device: its description, the MTLTexture behind it,
// the views the shaders bind, and who owns its current contents.
internal sealed class MetalCachedImage : IDisposable
{
    private static readonly nint SelRelease = MetalNative.Selector("release");
    private static readonly nint SelGpuResourceId = MetalNative.Selector("gpuResourceID");
    private static readonly nint SelNewTextureView =
        MetalNative.Selector("newTextureViewWithPixelFormat:textureType:levels:slices:");
    private static readonly nint SelNewSwizzledTextureView =
        MetalNative.Selector("newTextureViewWithPixelFormat:textureType:levels:slices:swizzle:");
    private static long _gpuWriteCounter;

    // The flip path reaches GetOrCreateView from the GPU worker while the cache
    // reaches it under its own lock, so the view list needs one of its own. It
    // used to be reached rarely enough for the race to stay latent: before the
    // guest swizzle was honoured, the common whole-image view returned the
    // texture without ever touching the list.
    private readonly object _viewGate = new();
    private readonly List<(ImageViewDescription Description, nint Texture, ulong ResourceId)> _views = new();
    private nint _textureIdHandle;
    private ulong _textureId;
    private bool _cpuDirty;
    private bool _gpuModified;
    private bool _disposed;
    private bool _windowLinearView;

    public MetalCachedImage(in ImageDescription description, in MetalTextureShape shape, nint texture)
    {
        Description = description;
        Shape = shape;
        Texture = texture;
        // A newly created image starts with the guest's bytes as its contents, so the
        // first use uploads unless the GPU takes ownership first.
        _cpuDirty = !ImageDescription.IsEmptyRange(description.Data);
        NoteContentChanged();
    }

    private static long _contentCounter;

    /// <summary>
    /// Changes whenever what the texture holds may have changed: a GPU write is about to
    /// happen, guest memory got newer bytes for it, an upload or a copy landed, or the
    /// texture itself was replaced. One counter across every image, so a version names
    /// one state of one texture and can never match another image's.
    /// </summary>
    public long ContentVersion { get; private set; }

    public void NoteContentChanged()
    {
        ContentVersion = Interlocked.Increment(ref _contentCounter);
        DepthPlaneVersion = ContentVersion;
    }

    /// <summary>
    /// The version of the depth plane alone: every change moves it with
    /// <see cref="ContentVersion"/> except a GPU write that can only reach the stencil plane.
    /// </summary>
    public long DepthPlaneVersion { get; private set; }

    // Only the stencil plane is about to change; what the depth plane holds stays as it is.
    private void NoteStencilChanged() => ContentVersion = Interlocked.Increment(ref _contentCounter);

    // What the last device publication into this image copied: the source texture and
    // the version of its depth plane, and this image's own version once the copy landed.
    // While all three still hold, this texture already holds what a new copy would put
    // there. The device publication copies a depth plane into a colour alias and nothing
    // else (MetalImageTransfer.TryEncodeTextureToTexture), so the source's stencil plane
    // is no part of what the alias holds.
    private nint _publishedSourceTexture;
    private long _publishedSourceVersion;
    private long _publishedOwnVersion;

    /// <summary>Records that this image now holds a device copy of the source's depth plane as it is.</summary>
    public void NotePublishedFrom(MetalCachedImage source)
    {
        _publishedSourceTexture = source.Texture;
        _publishedSourceVersion = source.DepthPlaneVersion;
        _publishedOwnVersion = ContentVersion;
    }

    /// <summary>
    /// True when the last device publication into this image was from this source, and
    /// neither this texture nor the source's depth plane has changed since: a copy now
    /// would rewrite the same texels.
    /// </summary>
    public bool StillHoldsPublicationFrom(MetalCachedImage source) =>
        !_cpuDirty && Texture != 0 && source.Texture != 0 &&
        _publishedSourceTexture == source.Texture &&
        _publishedSourceVersion == source.DepthPlaneVersion &&
        _publishedOwnVersion == ContentVersion;

    public ImageDescription Description;

    public MetalTextureShape Shape { get; private set; }

    public nint Texture { get; private set; }

    public MetalImageUses Uses;

    public bool Registered;

    public uint QueryEpoch;

    public ulong WatchBegin;

    public ulong WatchEnd;

    public ulong LastAccessTick;

    // The submission serial of the command buffer that last wrote the image, so a
    // writeback knows whether the GPU work it depends on has been recorded.
    public ulong GpuWriteSerial;

    // The newest submission that bound this image's texture or any of its views.
    // An argument buffer holds a gpuResourceID, which Metal does not retain, so
    // the Metal objects have to outlive every command buffer that named them.
    public ulong LastUseSerial;

    public void NoteUse(ulong serial)
    {
        if (serial > LastUseSerial)
        {
            LastUseSerial = serial;
        }
    }

    public long GpuWriteSequence { get; private set; }

    public bool IsCpuDirty => _cpuDirty;

    /// <summary>
    /// True while the texture is a linear texture over the guest window buffer: it reads
    /// the guest bytes in place, so it has no contents of its own to upload, watch or write
    /// back. The image still owns and releases the texture object; the texture retains the
    /// window buffer, which is never freed under it.
    /// </summary>
    public bool IsWindowLinearView => _windowLinearView;

    /// <summary>Marks the texture the image was made with as a window view: never dirty, never uploaded.</summary>
    public void MarkWindowLinearView()
    {
        _windowLinearView = true;
        _cpuDirty = false;
        DirtyStart = 0;
        DirtyEnd = 0;
        NoteContentChanged();
    }

    /// <summary>
    /// Whether the window texture can serve this view: the whole image in its own format
    /// and type. A component swizzle is fine; a buffer-backed texture makes those views.
    /// </summary>
    public bool CanWindowViewServe(in ImageViewDescription requested)
    {
        return MetalV2Formats.PixelFormat(requested.Format) == Shape.PixelFormat &&
            MetalV2Formats.ViewTextureType(requested.Type, Shape.SampleCount) == Shape.TextureType &&
            requested.BaseLevel == 0 && requested.LevelCount >= Shape.MipLevels &&
            requested.BaseLayer == 0 && requested.LayerCount >= Shape.ArrayLength;
    }

    /// <summary>
    /// Gives the image an owned texture in place of its window view. The old texture and its
    /// views move to the returned image, which the cache retires once the GPU is done with it.
    /// The new texture holds nothing yet, so the image is CPU-dirty over its whole range.
    /// </summary>
    public MetalCachedImage PromoteToOwned(nint texture, in MetalTextureShape shape)
    {
        var retired = new MetalCachedImage(in Description, Shape, Texture) { LastUseSerial = LastUseSerial };
        lock (_viewGate)
        {
            retired._views.AddRange(_views);
            _views.Clear();
        }

        Texture = texture;
        Shape = shape;
        _windowLinearView = false;
        MarkCpuDirty(MetalImageDirtyCause.Invalidated);
        NoteContentChanged();
        return retired;
    }

    public bool IsGpuModified => _gpuModified;

    public bool IsWatched => WatchBegin != 0 && WatchEnd != 0;

    // The byte footprint the cache accounts for; a view-only image has none.
    public ulong AccountedSize => Texture == 0 ? 0 : (Description.Data.Size + 1023) & ~1023UL;

    // Why the next upload will happen; purely diagnostic.
    public MetalImageDirtyCause DirtyCause { get; private set; } = MetalImageDirtyCause.Created;

    /// <summary>
    /// The guest bytes of this image, as offsets into it, that guest memory holds a newer
    /// version of than the texture does. One merged interval rather than a set: a second
    /// range that does not touch the first widens it to cover both, which costs an upload
    /// of the bytes between them and never an upload too few.
    /// </summary>
    public ulong DirtyStart { get; private set; }

    public ulong DirtyEnd { get; private set; }

    /// <summary>
    /// The bytes already published to guest memory since this image was last written by
    /// the GPU. A partial publication leaves the texture authoritative for the rest, so
    /// the image stays GPU-modified and this is what keeps the next alias from publishing
    /// the same bytes again.
    /// </summary>
    public ulong PublishedStart { get; private set; }

    public ulong PublishedEnd { get; private set; }

    public void MarkCpuDirty(MetalImageDirtyCause cause = MetalImageDirtyCause.Invalidated) =>
        MarkCpuDirty(cause, 0, Description.Data.Size);

    /// <summary>Guest memory is newer than the texture over <c>[start, end)</c> of the image.</summary>
    public void MarkCpuDirty(MetalImageDirtyCause cause, ulong start, ulong end)
    {
        // A window view reads guest memory in place, so guest memory is never newer.
        if (_windowLinearView)
        {
            return;
        }

        end = Math.Min(end, Description.Data.Size);
        if (end <= start)
        {
            return;
        }

        NoteContentChanged();
        if (!_cpuDirty)
        {
            DirtyCause = cause;
            DirtyStart = start;
            DirtyEnd = end;
            _cpuDirty = true;
            return;
        }

        DirtyStart = Math.Min(DirtyStart, start);
        DirtyEnd = Math.Max(DirtyEnd, end);
    }

    /// <summary>The whole image is in the texture again.</summary>
    public void RefreshComplete()
    {
        NoteContentChanged();
        _cpuDirty = false;
        DirtyStart = 0;
        DirtyEnd = 0;
    }

    /// <summary>Only <c>[start, end)</c> was uploaded; whatever is left stays dirty.</summary>
    public void RefreshComplete(ulong start, ulong end)
    {
        if (!_cpuDirty)
        {
            return;
        }

        NoteContentChanged();
        if (start <= DirtyStart && end >= DirtyEnd)
        {
            RefreshComplete();
            return;
        }

        // A cut from the front or the back narrows the interval; a cut from the middle
        // would split it, and one interval cannot hold two, so it keeps the wider half.
        if (start <= DirtyStart && end > DirtyStart)
        {
            DirtyStart = end;
        }
        else if (end >= DirtyEnd && start < DirtyEnd)
        {
            DirtyEnd = start;
        }

        if (DirtyEnd <= DirtyStart)
        {
            RefreshComplete();
        }
    }

    /// <param name="depthPlaneChanged">
    /// False for a depth-stencil target the GPU writes only the stencil of.
    /// </param>
    public void MarkGpuModified(bool depthPlaneChanged = true)
    {
        if (depthPlaneChanged)
        {
            NoteContentChanged();
        }
        else
        {
            NoteStencilChanged();
        }

        _gpuModified = true;
        _cpuDirty = false;
        DirtyStart = 0;
        DirtyEnd = 0;
        PublishedStart = 0;
        PublishedEnd = 0;
        GpuWriteSequence = Interlocked.Increment(ref _gpuWriteCounter);
    }

    /// <summary>Records that <c>[start, end)</c> of this image now also sits in guest memory.</summary>
    public void MarkPublished(ulong start, ulong end)
    {
        end = Math.Min(end, Description.Data.Size);
        if (end <= start)
        {
            return;
        }

        if (PublishedEnd <= PublishedStart)
        {
            PublishedStart = start;
            PublishedEnd = end;
            return;
        }

        if (start > PublishedEnd || end < PublishedStart)
        {
            // Two runs with unpublished bytes between them: one interval cannot hold both,
            // and widening it over the gap would claim bytes guest memory never got. The
            // longer run is kept; forgetting the other only costs publishing it again.
            if (end - start > PublishedEnd - PublishedStart)
            {
                PublishedStart = start;
                PublishedEnd = end;
            }

            return;
        }

        PublishedStart = Math.Min(PublishedStart, start);
        PublishedEnd = Math.Max(PublishedEnd, end);
    }

    /// <summary>True when guest memory already holds this run of the image.</summary>
    public bool IsPublished(ulong start, ulong end) =>
        PublishedEnd > PublishedStart && start >= PublishedStart && end <= PublishedEnd;

    public void ClearGpuModified()
    {
        _gpuModified = false;
        PublishedStart = 0;
        PublishedEnd = 0;
    }

    // A CPU write inside the image's bytes makes it dirty; a write that only shares a
    // tracker page with it does too, since the page watch cannot tell them apart.
    public void InvalidateCpuWrite(ulong address, ulong size)
    {
        // The whole image is dirtied either way: the caller drops the page watch for the
        // image and a page fault reports a fixed small size, so later stores go unseen
        // until the next whole-image refresh.
        if (GuestRangeOverlap.Bytes(Description.Data.Address, Description.Data.Size, address, size)
            || GuestRangeOverlap.Pages(Description.Data.Address, Description.Data.Size, address, size))
        {
            MarkCpuDirty(MetalImageDirtyCause.CpuWrite);
            ClearGpuModified();
        }
    }

    /// <summary>
    /// A guest byte range intersected with this image, as offsets into the image. Empty
    /// when the range does not reach it.
    /// </summary>
    public (ulong Start, ulong End) OverlapWithin(ulong address, ulong size)
    {
        var imageStart = Description.Data.Address;
        var imageEnd = imageStart + Description.Data.Size;
        var rangeEnd = size > ulong.MaxValue - address ? ulong.MaxValue : address + size;
        var start = Math.Max(imageStart, address);
        var end = Math.Min(imageEnd, rangeEnd);
        return end > start ? (start - imageStart, end - imageStart) : (0, 0);
    }

    public bool Overlaps(ulong address, ulong size, bool pages = false) => pages
        ? GuestRangeOverlap.Pages(Description.Data.Address, Description.Data.Size, address, size)
        : GuestRangeOverlap.Bytes(Description.Data.Address, Description.Data.Size, address, size);

    public bool GpuOverlaps(ulong address, ulong size) => _gpuModified && Overlaps(address, size);

    public bool SupportsViewType(in ImageViewDescription view)
    {
        var type = Shape.TextureType;
        return view.Type switch
        {
            GpuImageViewType.Type3D => type == Mtl.TextureType3D,
            GpuImageViewType.Type1D or GpuImageViewType.Type1DArray =>
                type is Mtl.TextureType1D or Mtl.TextureType1DArray,
            // A volume serves a flat view too: Vulkan makes a 2D-array view of a 3D
            // image to render it slice by slice, and Metal names the slice with the
            // attachment's depth plane. Refusing it would drop and recreate the volume
            // on every acquisition - the description makes a 3D texture again - which
            // is a 24 MB texture released out from under the submissions still reading
            // it, every pass of a title's volumetric fog.
            _ => type is Mtl.TextureType2D or Mtl.TextureType2DArray or Mtl.TextureTypeCube or
                Mtl.TextureType2DMultisample or Mtl.TextureType2DMultisampleArray or Mtl.TextureType3D,
        };
    }

    /// <summary>
    /// The texture a shader or attachment binds for one view. A view over the whole
    /// image with its own format reuses the image texture; anything narrower (a mip,
    /// a slice range, a reinterpreted format) gets an MTLTexture view, which is a
    /// handle onto the same allocation and never copies.
    /// </summary>
    // The guest T#'s DST_SEL, which the Vulkan backend puts on its image view's
    // component mapping. Without it a single-channel font atlas samples as
    // (r, 0, 0, 1), so a shader that reads the glyph coverage out of .a gets 1
    // everywhere and every glyph quad fills in solid.
    internal static MtlTextureSwizzleChannels SwizzleOf(in GpuComponentMapping mapping, out bool identity)
    {
        var red = Channel(mapping.R, MtlTextureSwizzle.Red);
        var green = Channel(mapping.G, MtlTextureSwizzle.Green);
        var blue = Channel(mapping.B, MtlTextureSwizzle.Blue);
        var alpha = Channel(mapping.A, MtlTextureSwizzle.Alpha);
        identity = red == (byte)MtlTextureSwizzle.Red && green == (byte)MtlTextureSwizzle.Green &&
            blue == (byte)MtlTextureSwizzle.Blue && alpha == (byte)MtlTextureSwizzle.Alpha;
        return new MtlTextureSwizzleChannels { Red = red, Green = green, Blue = blue, Alpha = alpha };
    }

    private static byte Channel(GpuComponentSwizzle component, MtlTextureSwizzle identity) => (byte)(component switch
    {
        GpuComponentSwizzle.Zero => MtlTextureSwizzle.Zero,
        GpuComponentSwizzle.One => MtlTextureSwizzle.One,
        GpuComponentSwizzle.R => MtlTextureSwizzle.Red,
        GpuComponentSwizzle.G => MtlTextureSwizzle.Green,
        GpuComponentSwizzle.B => MtlTextureSwizzle.Blue,
        GpuComponentSwizzle.A => MtlTextureSwizzle.Alpha,
        _ => identity,
    });

    public nint GetOrCreateView(in ImageViewDescription requested) => GetOrCreateView(requested, out _);

    // The gpuResourceID of the backing texture; it is fixed for a given MTLTexture, so it is
    // asked once per handle rather than once per bind.
    private ulong TextureResourceId()
    {
        if (_textureIdHandle != Texture)
        {
            _textureId = MetalNative.SendGpuResourceId(Texture, SelGpuResourceId);
            _textureIdHandle = Texture;
        }

        return _textureId;
    }

    /// <summary>The view for a request, with its cached gpuResourceID.</summary>
    public nint GetOrCreateView(in ImageViewDescription requested, out ulong resourceId)
    {
        resourceId = 0;
        if (Texture == 0)
        {
            throw GpuFatal.Fatal(
                $"An image without a texture cannot make a view: address=0x{Description.Data.Address:X16}.");
        }

        var viewFormat = MetalV2Formats.PixelFormat(requested.Format);
        var viewType = MetalV2Formats.ViewTextureType(requested.Type, Shape.SampleCount);
        var swizzle = SwizzleOf(requested.Mapping, out var identitySwizzle);
        // MTLTextureUsagePixelFormatView is only needed to reinterpret the pixel
        // format or the swizzle. Narrowing a view to a level or a slice of the
        // same format needs nothing, so a depth or compressed image -- which the
        // cache creates without that usage -- can still have the subresource view
        // a mipped or layered attachment binds.
        var viewable = (Shape.Usage & Mtl.TextureUsagePixelFormatView) != 0;
        if (!viewable && !identitySwizzle)
        {
            identitySwizzle = true;
        }

        // A volume rendered slice by slice asks for the 2D-array view Vulkan makes of a
        // 3D image. Metal has no such view and raises rather than returning nil, but it
        // does not need one: a render pass names the slice of a 3D attachment with its
        // depth plane, which the attachment already carries, so the volume binds whole.
        if (Shape.TextureType == Mtl.TextureType3D && viewType != Mtl.TextureType3D)
        {
            if ((requested.Usage & (GpuImageUsage.ColorAttachmentBit | GpuImageUsage.DepthStencilAttachmentBit)) == 0)
            {
                // Read as something other than a volume, the binding is wrong either
                // way; being wrong is better than raising, which ends the process.
                MetalV2Diagnostics.WarnOnce(
                    $"volume-view:{viewType}",
                    $"A volume cannot be viewed as another texture type; binding the volume instead: " +
                    $"type=3D->{viewType} format={Shape.PixelFormat}->{viewFormat} " +
                    $"layers={requested.BaseLayer}+{requested.LayerCount} usage={requested.Usage}.");
            }

            resourceId = TextureResourceId();
            return Texture;
        }

        var wholeImage = requested.BaseLevel == 0 && requested.LevelCount >= Shape.MipLevels &&
            requested.BaseLayer == 0 && requested.LayerCount >= Shape.ArrayLength;
        if (wholeImage && identitySwizzle && viewFormat == Shape.PixelFormat && viewType == Shape.TextureType)
        {
            resourceId = TextureResourceId();
            return Texture;
        }

        lock (_viewGate)
        {
            for (var index = 0; index < _views.Count; index++)
            {
                if (_views[index].Description.Equals(requested))
                {
                    resourceId = _views[index].ResourceId;
                    return _views[index].Texture;
                }
            }
        }

        if (!viewable && viewFormat != Shape.PixelFormat)
        {
            // Metal raises rather than returning nil for a reinterpreting view of
            // a texture that was not created for one, and a raised ObjC exception
            // on the GPU worker takes the process down. A compressed image is the
            // usual case: it is sampled whole, in its own format, so binding it
            // unviewed is what the draw wanted anyway.
            MetalV2Diagnostics.WarnOnce(
                $"view-usage:{Shape.PixelFormat}->{viewFormat}:{Shape.TextureType}->{viewType}",
                $"A texture without the pixel-format-view usage cannot be reinterpreted; binding the image instead: " +
                $"format={Shape.PixelFormat}->{viewFormat} type={Shape.TextureType}->{viewType} " +
                $"usage=0x{Shape.Usage:X} levels={requested.BaseLevel}+{requested.LevelCount} " +
                $"layers={requested.BaseLayer}+{requested.LayerCount}.");
            resourceId = TextureResourceId();
            return Texture;
        }

        var levelCount = Math.Min(requested.LevelCount, Shape.MipLevels - Math.Min(requested.BaseLevel, Shape.MipLevels - 1));
        var baseLayer = Math.Min(requested.BaseLayer, Shape.ArrayLength - 1);
        var layerCount = Math.Min(requested.LayerCount, Shape.ArrayLength - baseLayer);
        if (viewType == Mtl.TextureType3D)
        {
            // A volume has one slice; its depth is not an array dimension.
            baseLayer = 0;
            layerCount = 1;
        }

        var levelRange = new NsRange { Location = requested.BaseLevel, Length = Math.Max(levelCount, 1) };
        var sliceRange = new NsRange { Location = baseLayer, Length = Math.Max(layerCount, 1) };
        var view = identitySwizzle
            ? MetalV2Native.SendNewTextureView(
                Texture, SelNewTextureView, (nuint)viewFormat, (nuint)viewType, levelRange, sliceRange)
            : MetalV2Native.SendNewSwizzledTextureView(
                Texture, SelNewSwizzledTextureView, (nuint)viewFormat, (nuint)viewType, levelRange, sliceRange, swizzle);
        if (view == 0)
        {
            // A refused reinterpretation is not fatal for a texture: the image itself
            // still carries the bytes, so the draw samples the unreinterpreted texture.
            MetalV2Diagnostics.WarnOnce(
                $"view:{Shape.PixelFormat}->{viewFormat}:{Shape.TextureType}->{viewType}",
                $"A texture view was refused; binding the image instead: format={Shape.PixelFormat}->{viewFormat} " +
                $"type={Shape.TextureType}->{viewType} levels={requested.BaseLevel}+{levelCount} layers={baseLayer}+{layerCount}.");
            resourceId = TextureResourceId();
            return Texture;
        }

        lock (_viewGate)
        {
            // Another thread may have made the same view while this one was
            // asking Metal for it; keep theirs so the list holds one of each.
            for (var index = 0; index < _views.Count; index++)
            {
                if (_views[index].Description.Equals(requested))
                {
                    MetalNative.SendVoid(view, SelRelease);
                    resourceId = _views[index].ResourceId;
                    return _views[index].Texture;
                }
            }

            resourceId = MetalNative.SendGpuResourceId(view, SelGpuResourceId);
            _views.Add((requested, view, resourceId));
        }

        return view;
    }

    /// <summary>Replaces the backing texture, dropping every view of the old one.</summary>
    public void ReplaceTexture(nint texture, in MetalTextureShape shape)
    {
        ReleaseViews();
        if (Texture != 0)
        {
            MetalNative.SendVoid(Texture, SelRelease);
        }

        Texture = texture;
        Shape = shape;
        NoteContentChanged();
    }

    private void ReleaseViews()
    {
        lock (_viewGate)
        {
            foreach (var (_, view, _) in _views)
            {
                MetalNative.SendVoid(view, SelRelease);
            }

            _views.Clear();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        ReleaseViews();
        if (Texture != 0)
        {
            MetalNative.SendVoid(Texture, SelRelease);
            Texture = 0;
        }
    }
}
