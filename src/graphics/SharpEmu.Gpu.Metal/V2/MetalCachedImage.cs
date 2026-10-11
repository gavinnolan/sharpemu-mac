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
    private readonly List<(ImageViewDescription Description, nint Texture)> _views = new();
    private bool _cpuDirty;
    private bool _gpuModified;
    private bool _disposed;

    public MetalCachedImage(in ImageDescription description, in MetalTextureShape shape, nint texture)
    {
        Description = description;
        Shape = shape;
        Texture = texture;
        // A newly created image starts with the guest's bytes as its contents, so the
        // first use uploads unless the GPU takes ownership first.
        _cpuDirty = !ImageDescription.IsEmptyRange(description.Data);
    }

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

    public bool IsGpuModified => _gpuModified;

    public bool IsWatched => WatchBegin != 0 && WatchEnd != 0;

    // The byte footprint the cache accounts for; a view-only image has none.
    public ulong AccountedSize => Texture == 0 ? 0 : (Description.Data.Size + 1023) & ~1023UL;

    // Why the next upload will happen; purely diagnostic.
    public MetalImageDirtyCause DirtyCause { get; private set; } = MetalImageDirtyCause.Created;

    public void MarkCpuDirty(MetalImageDirtyCause cause = MetalImageDirtyCause.Invalidated)
    {
        if (!_cpuDirty)
        {
            DirtyCause = cause;
        }

        _cpuDirty = true;
    }

    public void RefreshComplete() => _cpuDirty = false;

    public void MarkGpuModified()
    {
        _gpuModified = true;
        _cpuDirty = false;
        GpuWriteSequence = Interlocked.Increment(ref _gpuWriteCounter);
    }

    public void ClearGpuModified() => _gpuModified = false;

    // A CPU write inside the image's bytes makes it dirty; a write that only shares a
    // tracker page with it does too, since the page watch cannot tell them apart.
    public void InvalidateCpuWrite(ulong address, ulong size)
    {
        if (GuestRangeOverlap.Bytes(Description.Data.Address, Description.Data.Size, address, size) ||
            GuestRangeOverlap.Pages(Description.Data.Address, Description.Data.Size, address, size))
        {
            MarkCpuDirty(MetalImageDirtyCause.CpuWrite);
            _gpuModified = false;
        }
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
            _ => type is Mtl.TextureType2D or Mtl.TextureType2DArray or Mtl.TextureTypeCube or
                Mtl.TextureType2DMultisample or Mtl.TextureType2DMultisampleArray,
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
    private static MtlTextureSwizzleChannels SwizzleOf(in GpuComponentMapping mapping, out bool identity)
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

    public nint GetOrCreateView(in ImageViewDescription requested)
    {
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

            return Texture;
        }

        var wholeImage = requested.BaseLevel == 0 && requested.LevelCount >= Shape.MipLevels &&
            requested.BaseLayer == 0 && requested.LayerCount >= Shape.ArrayLength;
        if (wholeImage && identitySwizzle && viewFormat == Shape.PixelFormat && viewType == Shape.TextureType)
        {
            return Texture;
        }

        lock (_viewGate)
        {
            for (var index = 0; index < _views.Count; index++)
            {
                if (_views[index].Description.Equals(requested))
                {
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
                    return _views[index].Texture;
                }
            }

            _views.Add((requested, view));
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
    }

    private void ReleaseViews()
    {
        lock (_viewGate)
        {
            foreach (var (_, view) in _views)
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
