// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.HLE.GpuMemory;
using SharpEmu.Libs.Gpu.Abstractions;
using SharpEmu.Libs.Gpu.Buffers;
using SharpEmu.Libs.Gpu.Images;
using SharpEmu.Libs.Gpu.Scheduling;

namespace SharpEmu.Gpu.Metal.V2;

// What the cache needs from the render host to move image contents: a command
// buffer with no open encoder, GPU fills of guest memory, texture clears, and a
// way to finish recorded work before the CPU reads guest bytes.
internal interface IMetalImageCacheOwner
{
    nint Device { get; }

    // The serial of the command buffer being recorded, without touching its
    // encoders: noting that an image is GPU-written must not end the render pass.
    ulong CurrentSerial { get; }

    // Ends any open encoder and hands over the command buffer being recorded.
    IMetalSubmissionContext BeginTransfer();

    // Fills a guest byte range with a repeating dword on the GPU.
    void FillGuestRange(ulong address, ulong size, uint value);

    // Recorded work stores into this guest range, so a later read of these bytes
    // waits for the submission being recorded and a read of anything else does not.
    void NoteGuestWrite(ulong address, ulong size);

    // Finishes every recorded command buffer so the CPU sees the GPU's writes.
    void WaitForGpu();

    void ClearTextureColor(nint texture, ulong pixelFormat, in SubresourceRange range, uint width, uint height, in GpuClearColor color);

    void ClearTextureDepthStencil(
        nint texture,
        ulong pixelFormat,
        in SubresourceRange range,
        uint width,
        uint height,
        float depth,
        byte stencil,
        bool clearDepth,
        bool clearStencil);
}

/// <summary>
/// The address-keyed image store, with the semantics of the Vulkan
/// <c>GuestImageCache</c>: images are found by guest range through a page-owner
/// table, CPU writes are caught by the shared HLE page guard, and GPU writes are
/// written back to guest memory lazily.
///
/// Unified memory removes most of the Vulkan machinery: guest memory is the single
/// source of truth for every image, so a replacement, a growth or an eviction is
/// "write the texture back, then drop it" rather than an image-to-image copy. There
/// are no size caps, no CPU detiling and no qualified-descriptor list: an unknown
/// format maps to a default and logs once.
/// </summary>
internal sealed partial class MetalImageCache : IGuestImageStore, IDisposable
{
    private const ulong TicksBeforeRemoval = 32;
    private const ulong MiB = 1024 * 1024;

    private static readonly nint SelAlloc = MetalNative.Selector("alloc");
    private static readonly nint SelInit = MetalNative.Selector("init");
    private static readonly nint SelRelease = MetalNative.Selector("release");
    private static readonly nint SelNewTexture = MetalNative.Selector("newTextureWithDescriptor:");
    private static readonly nint SelSetTextureType = MetalNative.Selector("setTextureType:");
    private static readonly nint SelSetPixelFormat = MetalNative.Selector("setPixelFormat:");
    private static readonly nint SelSetWidth = MetalNative.Selector("setWidth:");
    private static readonly nint SelSetHeight = MetalNative.Selector("setHeight:");
    private static readonly nint SelSetDepth = MetalNative.Selector("setDepth:");
    private static readonly nint SelSetMipmapLevelCount = MetalNative.Selector("setMipmapLevelCount:");
    private static readonly nint SelSetArrayLength = MetalNative.Selector("setArrayLength:");
    private static readonly nint SelSetSampleCount = MetalNative.Selector("setSampleCount:");
    private static readonly nint SelSetUsage = MetalNative.Selector("setUsage:");
    private static readonly nint SelSetStorageMode = MetalNative.Selector("setStorageMode:");

    private readonly IMetalImageCacheOwner _owner;
    private readonly IMetalImageTransfer _transfer;
    private readonly MetalGuestMemory _guestMemory;
    private readonly PageGuard _pages;
    private readonly IGuestBackedSpace _backing;
    private readonly RegionLock _lock = new(RegionLock.Category.ImageCache);
    private readonly MetalImageSlots _slots = new();
    private readonly MetalImagePageOwners _pageOwners = new();
    private readonly SortedDictionary<ulong, SurfaceMetadata> _surfaceMetadata = new();
    private readonly Dictionary<GpuFormat, ResourceSlotIdentifier> _nullImages = new();
    private ulong _totalUsedMemory;
    private ulong _memoryBudgetBytes;
    private ulong _collectionTick;
    private uint _queryEpoch;
    private long _createdImages;
    private long _uploads;
    private long _writebacks;
    private long _evictions;
    private bool _watchingStopped;
    private bool _disposed;

    public MetalImageCache(
        IMetalImageCacheOwner owner,
        IMetalImageTransfer transfer,
        MetalGuestMemory guestMemory,
        PageGuard pages,
        IGuestBackedSpace backing)
    {
        _owner = owner;
        _transfer = transfer;
        _guestMemory = guestMemory;
        _pages = pages;
        _backing = backing;
        _memoryBudgetBytes = ResolveMemoryBudget();
    }

    public ulong TotalUsedMemory => _totalUsedMemory;

    public int ImageCount => _slots.Count;

    public (long Images, long Uploads, long Writebacks, long Evictions) Counters =>
        (Volatile.Read(ref _createdImages), Volatile.Read(ref _uploads),
            Volatile.Read(ref _writebacks), Volatile.Read(ref _evictions));

    private static ulong ResolveMemoryBudget()
    {
        var configured = Environment.GetEnvironmentVariable("SHARPEMU_METAL_V2_IMAGE_BUDGET_MB");
        return ulong.TryParse(configured, out var megabytes) && megabytes >= 256
            ? megabytes * MiB
            : 3072 * MiB;
    }

    private static bool IsValidRange(ulong address, ulong size) =>
        ImageDescription.IsValidRange(new GuestSpan(address, size));

    /// <summary>
    /// The image for a request: an existing one when the backing matches or the
    /// overlap rules say so, otherwise a new one. The request's view may come back
    /// retargeted at a mip or a layer of a larger cached image.
    /// </summary>
    public ResourceSlotIdentifier FindImage(ref ImageRequest request, bool exactFormat = false)
    {
        ValidateRequest(in request);
        if (ImageDescription.IsEmptyRange(request.Description.Data))
        {
            using var nullHeld = _lock.Hold();
            return GetNullImage(in request);
        }

        var metadataBaseLayer = request.View.BaseLayer;
        var result = LookUpImage(ref request, exactFormat);
        if (request.Role is ImageRole.Texture or ImageRole.StorageImage)
        {
            using var held = _lock.Hold();
            var image = _slots[result];
            if (request.Description.Metadata.Kind == MetadataKind.Dcc &&
                request.Description.Metadata.Range.Size == 0 &&
                image.Description.Metadata.Kind == MetadataKind.None)
            {
                image.Description.Metadata = request.Description.Metadata;
            }
        }

        MaterializeDccClear(result, in request, metadataBaseLayer);
        return result;
    }

    private ResourceSlotIdentifier LookUpImage(ref ImageRequest request, bool exactFormat)
    {
        using var held = _lock.Hold();
        var candidates = FindImagesInRange(request.Description.Data.Address, request.Description.Data.Size, pageOverlap: false);
        var result = ResourceSlotIdentifier.Invalid;
        foreach (var candidate in candidates)
        {
            if (HasSameBacking(_slots[candidate].Description, request.Description, exactFormat))
            {
                result = candidate;
            }
        }

        var viewMip = -1;
        var viewLayer = -1;
        if (!result.IsValid)
        {
            foreach (var candidate in candidates)
            {
                viewMip = -1;
                viewLayer = -1;
                var merged = result.IsValid ? _slots[result].Description : request.Description;
                var overlap = ResolveOverlap(in merged, request.Role, candidate, result, exactFormat);
                if (overlap.Image.IsValid)
                {
                    result = overlap.Image;
                    viewMip = overlap.Mip;
                    viewLayer = overlap.Layer;
                }
            }
        }

        if (result.IsValid)
        {
            var resolved = _slots[result];
            if ((exactFormat && resolved.Description.PixelFormat != request.Description.PixelFormat) ||
                resolved.Description.Resources < request.Description.Resources ||
                !resolved.SupportsViewType(request.View))
            {
                ReleaseImage(result);
                result = ResourceSlotIdentifier.Invalid;
                viewMip = -1;
                viewLayer = -1;
            }
        }

        if (!result.IsValid)
        {
            result = InsertImage(request.Description);
        }

        var image = _slots[result];
        if (viewMip >= 0)
        {
            request.View = request.View with { BaseLevel = (uint)viewMip };
        }

        if (viewLayer >= 0)
        {
            request.View = request.View with { BaseLayer = (uint)viewLayer };
        }

        image.LastAccessTick = _collectionTick;
        return result;
    }

    public MetalCachedImage GetImage(ResourceSlotIdentifier imageIdentifier) => _slots[imageIdentifier];

    public bool TryGetImage(ResourceSlotIdentifier imageIdentifier, out MetalCachedImage image)
    {
        using var held = _lock.Hold();
        var found = _slots.TryGet(imageIdentifier);
        image = found!;
        return found is not null;
    }

    /// <summary>The texture a shader binds for one image request.</summary>
    public nint AcquireTextureView(ResourceSlotIdentifier imageIdentifier, in ImageRequest request)
    {
        using var held = _lock.Hold();
        var image = _slots[imageIdentifier];
        image.LastAccessTick = _collectionTick;
        if (request.Role == ImageRole.StorageImage)
        {
            WatchImage(imageIdentifier);
            RefreshFromGuest(image);
            image.Uses.Storage = true;
            image.MarkGpuModified();
            image.GpuWriteSerial = _owner.CurrentSerial;
        }
        else
        {
            RefreshFromGuest(image);
            image.Uses.Texture = true;
        }

        return image.GetOrCreateView(request.View);
    }

    public nint AcquireColorTargetView(ResourceSlotIdentifier imageIdentifier, in ImageRequest request)
    {
        using var held = _lock.Hold();
        var image = _slots[imageIdentifier];
        image.LastAccessTick = _collectionTick;
        WatchImage(imageIdentifier);
        RefreshFromGuest(image);
        image.Uses.RenderTarget = true;
        if (request.Description.Metadata.Kind == MetadataKind.Dcc && request.Description.Metadata.Range.Size == 0)
        {
            image.Description.Metadata = request.Description.Metadata;
            RegisterDccMetadata(request.Description);
        }

        image.MarkGpuModified();
        image.GpuWriteSerial = _owner.CurrentSerial;
        return image.GetOrCreateView(request.View);
    }

    public nint AcquireDepthTargetView(ResourceSlotIdentifier imageIdentifier, in ImageRequest request)
    {
        using var held = _lock.Hold();
        var image = _slots[imageIdentifier];
        image.LastAccessTick = _collectionTick;
        WatchImage(imageIdentifier);
        RefreshFromGuest(image);
        image.Uses.DepthTarget = true;
        if (request.Description.HasMetadata)
        {
            image.Description.Metadata = request.Description.Metadata;
            RegisterHtileMetadata(request.Description);
        }

        if (request.Description.HasStencil)
        {
            image.Description.Stencil = request.Description.Stencil;
        }

        image.MarkGpuModified();
        image.GpuWriteSerial = _owner.CurrentSerial;
        return image.GetOrCreateView(request.View);
    }

    public void MarkGpuWritten(ResourceSlotIdentifier imageIdentifier)
    {
        using var held = _lock.Hold();
        if (_slots.TryGet(imageIdentifier) is not { } image)
        {
            return;
        }

        WatchImage(imageIdentifier);
        image.MarkGpuModified();
        image.GpuWriteSerial = _owner.CurrentSerial;
    }

    public void RefreshImage(ResourceSlotIdentifier imageIdentifier)
    {
        using var held = _lock.Hold();
        if (_slots.TryGet(imageIdentifier) is { } image)
        {
            RefreshFromGuest(image);
        }
    }

    /// <summary>
    /// Writes every GPU-modified image that overlaps the range back into guest
    /// memory. Called before a buffer binding, an incompatible image request or a
    /// host read can observe the bytes.
    /// </summary>
    public void FlushGpuWrites(ulong address, ulong size, MetalWritebackCause cause)
    {
        if (!IsValidRange(address, size) || !_pageOwners.MayHaveOwners(address, size))
        {
            return;
        }

        using var held = _lock.Hold();
        foreach (var imageIdentifier in FindImagesInRange(address, size, pageOverlap: false))
        {
            if (_slots.TryGet(imageIdentifier) is { } image && image.GpuOverlaps(address, size))
            {
                WriteBackToGuest(image, cause);
            }
        }
    }

    /// <summary>A GPU shader or transfer wrote these guest bytes: the images reload.</summary>
    public void InvalidateMemoryFromGpu(ulong address, ulong size)
    {
        if (!IsValidRange(address, size) || !_pageOwners.MayHaveOwners(address, size))
        {
            return;
        }

        using var held = _lock.Hold();
        foreach (var imageIdentifier in FindImagesInRange(address, size, pageOverlap: true))
        {
            if (_slots.TryGet(imageIdentifier) is not { } image || !image.Overlaps(address, size))
            {
                continue;
            }

            MetalV2Diagnostics.WarnOnce(
                $"gpu-invalidate:{size:X}:{image.Description.Data.Size:X}",
                $"A GPU write to guest memory invalidated a cached image, so the image uploads again: " +
                $"write=0x{address:X16}+0x{size:X} image=0x{image.Description.Data.Address:X16}+0x{image.Description.Data.Size:X} " +
                $"extent={image.Description.Extent.Width}x{image.Description.Extent.Height} " +
                $"format={image.Description.PixelFormat} gpu_modified={image.IsGpuModified}.");
            image.ClearGpuModified();
            image.MarkCpuDirty(MetalImageDirtyCause.GpuWrite);
            UnwatchImage(imageIdentifier);
        }
    }

    /// <summary>True while a GPU-modified image owns bytes in the range.</summary>
    public bool HasGpuModifiedImageBytes(ulong address, ulong size)
    {
        if (!IsValidRange(address, size) || !_pageOwners.MayHaveOwners(address, size))
        {
            return false;
        }

        using var held = _lock.Hold();
        foreach (var imageIdentifier in FindImagesInRange(address, size, pageOverlap: false))
        {
            if (_slots.TryGet(imageIdentifier) is { } image && image.GpuOverlaps(address, size))
            {
                return true;
            }
        }

        return false;
    }

    private static bool SameExtent(in GpuExtent3D left, in GpuExtent3D right) =>
        left.Width == right.Width && left.Height == right.Height && left.Depth == right.Depth;

    // The cached image holds exactly the requested surface; only the view may differ.
    private static bool HasSameBacking(in ImageDescription cached, in ImageDescription requested, bool exactFormat)
    {
        if (cached.Data.Address != requested.Data.Address || cached.Data.Size != requested.Data.Size ||
            !SameExtent(cached.Extent, requested.Extent) || cached.Samples != requested.Samples ||
            cached.BytesPerBlock != requested.BytesPerBlock || cached.TileMode != requested.TileMode ||
            cached.Resources != requested.Resources ||
            !ViewFormatRules.AreCompatible(cached.PixelFormat, requested.PixelFormat))
        {
            return false;
        }

        if (cached.Type != requested.Type && !SameExtent(requested.Extent, new GpuExtent3D(1, 1, 1)))
        {
            return false;
        }

        return !exactFormat || cached.PixelFormat == requested.PixelFormat;
    }

    private readonly record struct OverlapResolution(ResourceSlotIdentifier Image, int Mip = -1, int Layer = -1);

    // Resolves a cached image that shares guest bytes with the request. Guest memory
    // is authoritative, so anything the cached image cannot serve is written back and
    // dropped instead of being copied image-to-image.
    private OverlapResolution ResolveOverlap(
        in ImageDescription requested,
        ImageRole role,
        ResourceSlotIdentifier cachedImageIdentifier,
        ResourceSlotIdentifier mergedImageIdentifier,
        bool exactFormat)
    {
        if (_slots.TryGet(cachedImageIdentifier) is not { } cached)
        {
            return new OverlapResolution(mergedImageIdentifier);
        }

        ref var cachedInfo = ref cached.Description;
        var safeToDelete = _collectionTick - Math.Min(_collectionTick, cached.LastAccessTick) > TicksBeforeRemoval;
        var requestedBlock = requested.BytesPerBlock * requested.Samples;
        var cachedBlock = cachedInfo.BytesPerBlock * cachedInfo.Samples;
        var requestedExtent = requested.BlockExtent;
        var cachedExtent = cachedInfo.BlockExtent;
        if (requested.Data.Address == cachedInfo.Data.Address &&
            requestedExtent.Width == cachedExtent.Width && requestedExtent.Height == cachedExtent.Height &&
            requestedBlock == cachedBlock)
        {
            // Same surface, reinterpreted: a compatible format binds through a view.
            if (!exactFormat && requested.Data.Size == cachedInfo.Data.Size &&
                requested.Resources == cachedInfo.Resources && requested.TileMode == cachedInfo.TileMode &&
                requested.Type == cachedInfo.Type && requested.IsBlock == cachedInfo.IsBlock &&
                ViewFormatRules.AreCompatible(cachedInfo.PixelFormat, requested.PixelFormat))
            {
                return new OverlapResolution(mergedImageIdentifier.IsValid ? mergedImageIdentifier : cachedImageIdentifier);
            }

            // Anything else at the same address replaces the cached image.
            ReleaseImage(cachedImageIdentifier);
            return new OverlapResolution(mergedImageIdentifier);
        }

        var requestedMip = requested.FindMatchingMipLevel(cachedInfo);
        if (requestedMip >= 0)
        {
            var layer = requested.FindMatchingArraySlice(cachedInfo, requestedMip);
            if (layer >= 0 && (!exactFormat || cachedInfo.PixelFormat == requested.PixelFormat))
            {
                return new OverlapResolution(cachedImageIdentifier, requestedMip, layer);
            }
        }

        // The cached image is a piece of the requested one: its GPU contents go back to
        // guest memory, from where the new, larger image uploads them.
        if (cachedInfo.FindMatchingMipLevel(requested) >= 0)
        {
            ReleaseImage(cachedImageIdentifier);
            return new OverlapResolution(mergedImageIdentifier);
        }

        _ = role;
        if (requested.Data.Address >= cachedInfo.Data.Address && safeToDelete)
        {
            ReleaseImage(cachedImageIdentifier);
        }

        return new OverlapResolution(mergedImageIdentifier);
    }

    private void ValidateRequest(in ImageRequest request)
    {
        request.Description.Validate();
        ref readonly var description = ref request.Description;
        var view = request.View;
        if (view.Format == GpuFormat.Undefined || view.LevelCount == 0 || view.LayerCount == 0 ||
            view.BaseLevel >= description.Resources.Levels ||
            view.LevelCount > description.Resources.Levels - view.BaseLevel ||
            (!description.IsVolume && (view.BaseLayer >= description.Resources.Layers ||
                view.LayerCount > description.Resources.Layers - view.BaseLayer)))
        {
            throw GpuFatal.Fatal(
                $"The image view description is invalid: format={(int)view.Format} mip={view.BaseLevel}+{view.LevelCount} " +
                $"layer={view.BaseLayer}+{view.LayerCount} levels={description.Resources.Levels} layers={description.Resources.Layers}.");
        }
    }

    // A descriptor with no memory still needs something to bind; one 1x1 image per format serves.
    private ResourceSlotIdentifier GetNullImage(in ImageRequest request)
    {
        var format = request.Description.PixelFormat;
        if (_nullImages.TryGetValue(format, out var found) && _slots.TryGet(found) is not null)
        {
            return found;
        }

        var description = ImageDescription.Create();
        description.PixelFormat = format;
        description.GuestFormat = request.Description.GuestFormat;
        description.Type = GuestImageType.Color2D;
        description.Extent = new GpuExtent3D(1, 1, 1);
        description.Resources = SubresourceCount.Single;
        description.Pitch = 1;
        description.BytesPerBlock = Math.Max(request.Description.BytesPerBlock, 1);
        description.Samples = 1;
        description.TileMode = GuestTileMode.Linear;
        description.MipLayout[0] = new MipLevelLayout { Offset = 0, Size = description.BytesPerBlock, Pitch = 1, Height = 1 };
        var imageIdentifier = InsertImage(description);
        _slots[imageIdentifier].RefreshComplete();
        _nullImages[format] = imageIdentifier;
        return imageIdentifier;
    }

    private ResourceSlotIdentifier InsertImage(in ImageDescription description)
    {
        var admitted = Admit(in description);
        var shape = _transfer.ShapeFor(in admitted);
        var texture = CreateTexture(in shape, in admitted);
        var image = new MetalCachedImage(in admitted, in shape, texture);
        var imageIdentifier = _slots.Insert(image);
        if (!ImageDescription.IsEmptyRange(description.Data))
        {
            AddToIndex(imageIdentifier);
        }

        Interlocked.Increment(ref _createdImages);
        CollectIfOverBudget();
        return imageIdentifier;
    }

    // An image format the transfer cannot carry would be a fatal error there, and a
    // texture is never worth stopping the title for: the description takes the
    // size-equivalent format instead, and the substitution is logged once.
    private static ImageDescription Admit(in ImageDescription description)
    {
        if (MetalV2Formats.IsSupported(description.PixelFormat))
        {
            return description;
        }

        var substitute = MetalV2Formats.SubstituteFor(description.BytesPerBlock);
        MetalV2Diagnostics.WarnOnce(
            $"admit:{(int)description.PixelFormat}",
            $"No Metal format carries this image; using the size-equivalent one: " +
            $"format={(int)description.PixelFormat} guestFormat={(uint)description.GuestFormat} " +
            $"bytesPerBlock={description.BytesPerBlock} substitute={(int)substitute}.");
        var admitted = description;
        admitted.PixelFormat = substitute;
        return admitted;
    }

    private nint CreateTexture(in MetalTextureShape shape, in ImageDescription description)
    {
        var descriptor = MetalNative.Send(
            MetalNative.Send(MetalNative.Class("MTLTextureDescriptor"), SelAlloc), SelInit);
        if (descriptor == 0)
        {
            throw GpuFatal.Fatal("A Metal texture descriptor could not be created.");
        }

        try
        {
            MetalV2Native.SendVoidValue(descriptor, SelSetTextureType, (nuint)shape.TextureType);
            MetalV2Native.SendVoidValue(descriptor, SelSetPixelFormat, (nuint)shape.PixelFormat);
            MetalV2Native.SendVoidValue(descriptor, SelSetWidth, Math.Max(shape.Width, 1));
            MetalV2Native.SendVoidValue(descriptor, SelSetHeight, Math.Max(shape.Height, 1));
            MetalV2Native.SendVoidValue(descriptor, SelSetDepth, Math.Max(shape.Depth, 1));
            MetalV2Native.SendVoidValue(descriptor, SelSetMipmapLevelCount, Math.Max(shape.MipLevels, 1));
            MetalV2Native.SendVoidValue(descriptor, SelSetArrayLength, Math.Max(shape.ArrayLength, 1));
            MetalV2Native.SendVoidValue(descriptor, SelSetSampleCount, Math.Max(shape.SampleCount, 1));
            MetalV2Native.SendVoidValue(descriptor, SelSetUsage, (nuint)shape.Usage);
            MetalV2Native.SendVoidValue(descriptor, SelSetStorageMode, Mtl.StorageModePrivate);
            var texture = MetalNative.Send(_owner.Device, SelNewTexture, descriptor);
            if (texture == 0)
            {
                throw GpuFatal.Fatal(
                    $"A Metal texture could not be created: format={shape.PixelFormat} type={shape.TextureType} " +
                    $"extent={shape.Width}x{shape.Height}x{shape.Depth} mips={shape.MipLevels} layers={shape.ArrayLength} " +
                    $"samples={shape.SampleCount} usage=0x{shape.Usage:X} address=0x{description.Data.Address:X16}.");
            }

            return texture;
        }
        finally
        {
            MetalNative.SendVoid(descriptor, SelRelease);
        }
    }

    // Uploads the guest bytes of a CPU-dirty image through the tiler.
    private void RefreshFromGuest(MetalCachedImage image)
    {
        if (!image.IsCpuDirty || image.Texture == 0 || ImageDescription.IsEmptyRange(image.Description.Data))
        {
            return;
        }

        ref readonly var description = ref image.Description;
        var context = _owner.BeginTransfer();
        var source = _guestMemory.Resolve(description.Data.Address, description.Data.Size);
        if (!source.IsValid)
        {
            MetalV2Diagnostics.WarnOnce(
                "upload-unresolved",
                $"An image range could not be resolved for an upload: address=0x{description.Data.Address:X16} size=0x{description.Data.Size:X}.");
            image.RefreshComplete();
            return;
        }

        var stencil = description.HasStencil
            ? _guestMemory.Resolve(description.Stencil.Address, description.Stencil.Size)
            : default;
        var range = new SubresourceRange(0, description.Resources.Levels, 0, description.TransferLayers);
        _transfer.EncodeGuestToTexture(context, in description, range, source, stencil, image.Texture);
        _uploadCauses[(int)image.DirtyCause]++;
        image.RefreshComplete();
        Interlocked.Increment(ref _uploads);
    }

    // Retiles a GPU-written image back into guest memory; afterwards guest memory and
    // the texture agree, so the image stays cached and clean.
    private void WriteBackToGuest(MetalCachedImage image, MetalWritebackCause cause)
    {
        if (!image.IsGpuModified || image.Texture == 0 || ImageDescription.IsEmptyRange(image.Description.Data))
        {
            image.ClearGpuModified();
            return;
        }

        ref readonly var description = ref image.Description;
        var destination = _guestMemory.ResolveWritable(description.Data.Address, description.Data.Size);
        if (!destination.IsValid)
        {
            // Nothing in guest memory can receive the bytes; keep the texture
            // authoritative rather than publishing a wrong result.
            MetalV2Diagnostics.WarnOnce(
                "writeback-unresolved",
                $"A GPU-written image has no writable guest window: address=0x{description.Data.Address:X16} size=0x{description.Data.Size:X}.");
            return;
        }

        var stencil = description.HasStencil
            ? _guestMemory.ResolveWritable(description.Stencil.Address, description.Stencil.Size)
            : default;
        var context = _owner.BeginTransfer();
        var range = new SubresourceRange(0, description.Resources.Levels, 0, description.TransferLayers);
        _transfer.EncodeTextureToGuest(context, in description, range, image.Texture, destination, stencil);
        image.ClearGpuModified();
        // The retile is a GPU store into guest memory: until it completes the bytes
        // in guest memory are the old ones, so a read of them has to wait for it.
        _owner.NoteGuestWrite(description.Data.Address, description.Data.Size);
        if (description.HasStencil && stencil.IsValid)
        {
            _owner.NoteGuestWrite(description.Stencil.Address, description.Stencil.Size);
        }

        _writebackCauses[(int)cause]++;
        Interlocked.Increment(ref _writebacks);
    }

    /// <summary>Writes every GPU-modified image back, then lets the CPU read guest memory.</summary>
    public void FlushAllGpuWrites()
    {
        using var held = _lock.Hold();
        var pending = new List<MetalCachedImage>();
        _slots.ForEach((_, image) =>
        {
            if (image.IsGpuModified)
            {
                pending.Add(image);
            }
        });
        foreach (var image in pending)
        {
            WriteBackToGuest(image, MetalWritebackCause.DrainAll);
        }
    }

    private readonly long[] _uploadCauses = new long[4];
    private readonly long[] _writebackCauses = new long[7];

    /// <summary>
    /// Where the uploads and the writebacks since the last read came from. A static
    /// menu that uploads and writes back every frame is churning, and only the cause
    /// says which side of the host is asking for it.
    /// </summary>
    public (long Created, long CpuWrite, long GpuWrite, long Invalidated,
        long Binding, long Storage, long HostRead, long GuestCopy, long Overlap, long Release, long Drain)
        TakeTransferCauses()
    {
        var uploads = (
            Interlocked.Exchange(ref _uploadCauses[0], 0),
            Interlocked.Exchange(ref _uploadCauses[1], 0),
            Interlocked.Exchange(ref _uploadCauses[2], 0),
            Interlocked.Exchange(ref _uploadCauses[3], 0));
        return (uploads.Item1, uploads.Item2, uploads.Item3, uploads.Item4,
            Interlocked.Exchange(ref _writebackCauses[0], 0),
            Interlocked.Exchange(ref _writebackCauses[1], 0),
            Interlocked.Exchange(ref _writebackCauses[2], 0),
            Interlocked.Exchange(ref _writebackCauses[3], 0),
            Interlocked.Exchange(ref _writebackCauses[4], 0),
            Interlocked.Exchange(ref _writebackCauses[5], 0),
            Interlocked.Exchange(ref _writebackCauses[6], 0));
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _slots.ForEach(static (_, image) => image.Dispose());
    }

    /// <summary>Flushes GPU-owned contents and drops every image.</summary>
    public void Shutdown()
    {
        try
        {
            FlushAllGpuWrites();
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"[METAL-V2][WARN] The image cache could not drain on shutdown: {exception.Message}");
        }

        using (var held = _lock.Hold())
        {
            var registered = new List<ResourceSlotIdentifier>();
            _slots.ForEach((imageIdentifier, image) =>
            {
                if (image.Registered)
                {
                    registered.Add(imageIdentifier);
                }
            });
            foreach (var imageIdentifier in registered)
            {
                RemoveFromIndex(imageIdentifier);
            }
        }

        Dispose();
    }
}
