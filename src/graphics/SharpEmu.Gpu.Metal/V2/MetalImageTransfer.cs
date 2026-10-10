// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.Abstractions;
using SharpEmu.Libs.Gpu.Images;
using SharpEmu.Libs.Gpu.Scheduling;

namespace SharpEmu.Gpu.Metal.V2;

// MTLTextureType, only the members a guest image can need. Cube faces arrive as array
// layers because the MSL translator declares every cube binding Dim2DArray
// (Gen5MslArgumentLayout.TextureType), so a cube is allocated as a 2D array.
internal enum MtlTextureType : uint
{
    Type1D = 0,
    Type1DArray = 1,
    Type2D = 2,
    Type2DArray = 3,
    Type2DMultisample = 4,
    Type3D = 7,
    Type2DMultisampleArray = 8,
}

[Flags]
internal enum MtlTextureUsage : uint
{
    Unknown = 0,
    ShaderRead = 1,
    ShaderWrite = 2,
    RenderTarget = 4,
    PixelFormatView = 16,
    // macOS 14 and later. Metal allows a texture atomic only on a texture
    // created with this bit, and only on R32Uint, R32Sint or R64Uint.
    ShaderAtomic = 32,
}

[Flags]
internal enum MtlBlitOption : uint
{
    None = 0,
    DepthFromDepthStencil = 1,
    StencilFromDepthStencil = 2,
}

// Moves image contents between guest memory and Metal textures entirely on the GPU.
//
// Every tiled surface goes through the tiler compute kernels into a linear staging
// buffer and then through one MTLBlitCommandEncoder copy per subresource, which is the
// one path that also covers depth, stencil and block-compressed formats. A linear
// surface skips the compute pass whenever its guest offset and row pitch are something
// a buffer-to-texture blit can address, which for the guest's 256-byte-aligned linear
// pitches is the normal case.
//
// Called from the GPU worker thread only: the selection scratch lists and the plan cache
// are not synchronised, matching how the Vulkan GuestImageCache records transfers.
internal sealed class MetalImageTransfer : IMetalImageTransfer, IDisposable
{
    // Conservative blit alignment. Metal wants a buffer-to-texture offset and row pitch
    // that are multiples of the texel block size; four bytes on top of that keeps the
    // sub-dword formats off the slow path only when the guest layout is genuinely odd.
    private const uint BlitAlignment = 4;

    private const ulong StagingAlignment = 16;

    private static readonly nint SelCopyBufferToTexture = MetalNative.Selector(
        "copyFromBuffer:sourceOffset:sourceBytesPerRow:sourceBytesPerImage:sourceSize:" +
        "toTexture:destinationSlice:destinationLevel:destinationOrigin:options:");

    private static readonly nint SelCopyTextureToBuffer = MetalNative.Selector(
        "copyFromTexture:sourceSlice:sourceLevel:sourceOrigin:sourceSize:" +
        "toBuffer:destinationOffset:destinationBytesPerRow:destinationBytesPerImage:options:");

    // Everything about one description that changes the staged bytes. Two images that
    // agree on all of it share a plan, so a steady-state frame builds none.
    private readonly record struct PlanKey(
        GuestPixelFormat GuestFormat,
        GpuFormat PixelFormat,
        GuestTileMode TileMode,
        GuestImageType Type,
        uint Width,
        uint Height,
        uint Depth,
        uint Levels,
        uint Layers,
        uint Pitch,
        uint BytesPerBlock,
        ulong DataSize,
        ulong StencilSize,
        bool Bgra16);

    private sealed class TransferPlan
    {
        public MetalTextureLayout Layout = null!;
        public readonly List<MetalSubresourceCopy> Copies = [];
        public readonly List<MetalTileTransfer> Tiles = [];
        public bool Tiled;
        public bool Volume;
        public bool Swap;
        public uint ElementBytes;
        public uint TexelWidth = 1;
        public uint TexelHeight = 1;
        public MtlPixelFormat Format;
        public ulong LinearSize;

        // Depth and stencil images carry their own per-layer plane transfers instead of
        // a mip chain: a depth surface is one level with one 64 KiB block layout per layer.
        public bool DepthStencil;
        public uint Layers;
        public ulong SliceSize;
        public uint TransferBytes;
        public bool StencilPlane;
        public uint StencilPitch;
        public ulong StencilSliceSize;
        public readonly List<MetalTileTransfer> StencilTiles = [];
    }

    private readonly nint _device;
    private readonly MetalTiler _tiler;
    private readonly Dictionary<PlanKey, TransferPlan> _plans = [];
    private readonly List<MetalSubresourceCopy> _copies = [];
    private readonly List<MetalTileTransfer> _tiles = [];
    private readonly List<MetalTileTransfer> _stencilTiles = [];
    private readonly List<ulong> _stagedOffsets = [];
    private bool _disposed;

    public MetalImageTransfer(nint device)
    {
        if (device == 0)
        {
            throw GpuFatal.Fatal("The Metal image transfer needs a device.");
        }

        _device = device;
        _tiler = new MetalTiler(device);
    }

    public MetalTiler Tiler => _tiler;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _tiler.Dispose();
        _plans.Clear();
    }

    public MetalTextureShape ShapeFor(in ImageDescription description)
    {
        if (!MetalGuestFormats.TryDecodeHostFormat(description.PixelFormat, out var format))
        {
            throw GpuFatal.Fatal(
                $"No Metal pixel format carries this image: hostFormat={(int)description.PixelFormat} " +
                $"guestFormat={(uint)description.GuestFormat} bytesPerBlock={description.BytesPerBlock}.");
        }

        return new MetalTextureShape(
            (ulong)format,
            (ulong)TextureTypeOf(description),
            description.Extent.Width,
            description.Extent.Height,
            description.IsVolume ? description.Extent.Depth : 1,
            description.Resources.Levels,
            description.IsVolume ? 1 : description.Resources.Layers,
            description.Samples,
            (ulong)UsageOf(description, format));
    }

    private static MtlTextureType TextureTypeOf(in ImageDescription description)
    {
        if (description.IsVolume)
        {
            return MtlTextureType.Type3D;
        }

        var arrayed = description.Resources.Layers > 1;
        if (description.Type == GuestImageType.Color1D)
        {
            return arrayed ? MtlTextureType.Type1DArray : MtlTextureType.Type1D;
        }

        if (description.Samples > 1)
        {
            return arrayed ? MtlTextureType.Type2DMultisampleArray : MtlTextureType.Type2DMultisample;
        }

        return arrayed ? MtlTextureType.Type2DArray : MtlTextureType.Type2D;
    }

    private static MtlTextureUsage UsageOf(in ImageDescription description, MtlPixelFormat format)
    {
        if (MetalGuestFormats.IsDepthStencil(format))
        {
            return MtlTextureUsage.ShaderRead | MtlTextureUsage.RenderTarget;
        }

        // A compressed texture is only ever sampled: Metal rejects a render-target or
        // writable compressed texture, and a reinterpreting view of one needs the
        // block-size-compatible view rules the cache does not use.
        if (description.IsBlock)
        {
            return MtlTextureUsage.ShaderRead;
        }

        var usage = MtlTextureUsage.ShaderRead | MtlTextureUsage.RenderTarget | MtlTextureUsage.PixelFormatView;
        if (description.Samples == 1 && !MetalGuestFormats.IsSrgb(format))
        {
            usage |= MtlTextureUsage.ShaderWrite;

            // A shader that runs IMAGE_ATOMIC_* on this image needs the atomic
            // usage bit, and Metal only accepts it on the single-channel 32-bit
            // integer formats. Those are exactly the formats a guest atomic
            // image has (BindingLayout gives an atomic binding to a uint image
            // only), so the bit goes on by format rather than by waiting for a
            // shader to be compiled against the texture the cache already made.
            if (format is MtlPixelFormat.R32Uint or MtlPixelFormat.R32Sint)
            {
                usage |= MtlTextureUsage.ShaderAtomic;
            }
        }

        return usage;
    }

    public void EncodeGuestToTexture(IMetalSubmissionContext context, in ImageDescription description,
        SubresourceRange range, MetalGuestRange source, MetalGuestRange stencilSource, nint texture,
        ulong guestBase, ulong pieceSize)
    {
        // Per-sample tiled layouts are not decoded on either backend; the Vulkan cache
        // leaves a multisampled image untouched too.
        if (!Ready(context, description, source, texture, guestBase, pieceSize))
        {
            return;
        }

        var plan = PlanFor(description);
        if (plan.DepthStencil)
        {
            EncodeDepthUpload(context, description, plan, range, source, stencilSource, texture, guestBase);
            return;
        }

        Select(plan, description, range, guestBase);
        if (_copies.Count == 0)
        {
            return;
        }

        var linear = StageUpload(context, plan, source, out var tight);
        var encoder = _tiler.BeginBlit(context);
        for (var index = 0; index < _copies.Count; index++)
        {
            var copy = _copies[index];
            var (offset, rowBytes, rows) = BlitRegion(plan, index, linear, tight);
            MetalNative.SendCopyBufferToTextureOptions(encoder, SelCopyBufferToTexture,
                linear.Buffer, (nuint)offset, rowBytes, (nuint)((ulong)rowBytes * rows),
                new MtlSize { Width = copy.Width, Height = copy.Height, Depth = copy.Depth },
                texture, copy.Slice, copy.Level,
                new MtlOrigin { X = 0, Y = 0, Z = copy.OriginZ }, (nuint)MtlBlitOption.None);
        }

        MetalTiler.EndEncoding(encoder);
    }

    // Where one subresource's rows sit in the buffer a blit reads or writes. A tight
    // staging buffer holds the mip's own width per row; everything else keeps the guest
    // layout, where a padded pitch becomes the row stride.
    private (ulong Offset, uint RowBytes, uint Rows) BlitRegion(TransferPlan plan, int index, MetalBufferSpan linear, bool tight)
    {
        var copy = _copies[index];
        if (tight)
        {
            return (linear.Offset + _stagedOffsets[index], RowBytes(plan, copy.Width), BlockRows(plan, copy.Height));
        }

        var rowTexels = copy.BufferRowLength != 0 ? copy.BufferRowLength : copy.Width;
        var rows = BlockRows(plan, copy.BufferImageHeight != 0 ? copy.BufferImageHeight : copy.Height);
        return (linear.Offset + copy.BufferOffset, RowBytes(plan, rowTexels), rows);
    }

    public void EncodeTextureToGuest(IMetalSubmissionContext context, in ImageDescription description,
        SubresourceRange range, nint texture, MetalGuestRange destination, MetalGuestRange stencilDestination,
        ulong guestBase, ulong pieceSize)
    {
        if (!Ready(context, description, destination, texture, guestBase, pieceSize))
        {
            return;
        }

        var plan = PlanFor(description);
        if (plan.DepthStencil)
        {
            EncodeDepthDownload(context, description, plan, range, texture, destination, stencilDestination, guestBase);
            return;
        }

        Select(plan, description, range, guestBase);
        if (_copies.Count == 0)
        {
            return;
        }

        // A readback lands in staging first whenever the retile reads it, the channels
        // need swapping, or the guest offset and pitch are not something a blit can write.
        var staged = BuildStagedOffsets(plan);
        var direct = !plan.Tiled && !plan.Swap && IsDirectlyBlittable(plan, destination.Offset);
        var tight = !direct && !plan.Tiled;
        var linear = direct
            ? new MetalBufferSpan(destination.Buffer, destination.Offset, pieceSize)
            : _tiler.Acquire(context, plan.Tiled ? plan.LinearSize : staged);

        var blit = _tiler.BeginBlit(context);
        for (var index = 0; index < _copies.Count; index++)
        {
            var copy = _copies[index];
            var (offset, rowBytes, rows) = BlitRegion(plan, index, linear, tight);
            MetalNative.SendCopyTextureToBufferOptions(blit, SelCopyTextureToBuffer,
                texture, copy.Slice, copy.Level,
                new MtlOrigin { X = 0, Y = 0, Z = copy.OriginZ },
                new MtlSize { Width = copy.Width, Height = copy.Height, Depth = copy.Depth },
                linear.Buffer, (nuint)offset, rowBytes, (nuint)((ulong)rowBytes * rows),
                (nuint)MtlBlitOption.None);
        }

        MetalTiler.EndEncoding(blit);
        if (direct)
        {
            return;
        }

        var compute = _tiler.BeginCompute(context);
        var source = linear;
        if (plan.Swap)
        {
            var swapped = _tiler.Acquire(context, plan.Tiled ? plan.LinearSize : staged);
            _tiler.EncodeSwapBgra16(compute, new MetalBufferSpan(linear.Buffer, linear.Offset, swapped.Size), swapped);
            source = swapped;
        }

        var guest = new MetalBufferSpan(destination.Buffer, destination.Offset, pieceSize);
        if (plan.Tiled)
        {
            _tiler.EncodeTile(compute, new MetalBufferSpan(source.Buffer, source.Offset, plan.LinearSize), guest, _tiles);
        }
        else
        {
            RepackSubresources(compute, plan, guest, source, toGuest: true);
        }

        MetalTiler.EndEncoding(compute);
    }

    private bool Ready(
        IMetalSubmissionContext context, in ImageDescription description, in MetalGuestRange guest, nint texture,
        ulong guestBase, ulong pieceSize)
    {
        if (_disposed || context.CommandBuffer == 0 || texture == 0 || !guest.IsValid ||
            ImageDescription.IsEmptyRange(description.Data) || description.Samples != 1 ||
            description.PixelFormat == GpuFormat.Undefined)
        {
            return false;
        }

        if (guestBase > description.Data.Size || pieceSize > description.Data.Size - guestBase)
        {
            throw GpuFatal.Fatal(
                $"An image piece falls outside its image: base=0x{guestBase:X} piece=0x{pieceSize:X} " +
                $"image=0x{description.Data.Size:X}.");
        }

        if (guest.Size < pieceSize)
        {
            // The resolver could not map even this piece. A copy sized from the
            // description would run off the end of what it did map, so this piece is
            // dropped and the texture keeps whatever it already holds there. The caller
            // splits a large image into pieces precisely so that one unmappable piece
            // costs only its own subresources; never fatal, and never silent.
            MetalV2Diagnostics.WarnOnce(
                "short-image-range",
                $"A guest range is smaller than the image piece, so the piece is skipped: " +
                $"range=0x{guest.Size:x} piece=0x{pieceSize:x} at 0x{guestBase:x} of 0x{description.Data.Size:x} " +
                $"address=0x{description.Data.Address:X16} format={description.PixelFormat} " +
                $"type={description.Type} extent={description.Extent.Width}x{description.Extent.Height}x" +
                $"{description.Extent.Depth} levels={description.Resources.Levels} " +
                $"layers={description.TransferLayers} tile={description.TileMode}.");
            return false;
        }

        return true;
    }

    // Staging for an upload: the detiled bytes, the swapped bytes, or nothing at all when
    // the guest range is already something the blit can read. `tight` reports whether the
    // result holds one tightly packed subresource per copy rather than the guest layout.
    private MetalBufferSpan StageUpload(IMetalSubmissionContext context, TransferPlan plan, MetalGuestRange source, out bool tight)
    {
        tight = false;
        var staged = BuildStagedOffsets(plan);
        if (!plan.Tiled && !plan.Swap && IsDirectlyBlittable(plan, source.Offset))
        {
            return new MetalBufferSpan(source.Buffer, source.Offset, source.Size);
        }

        var guest = new MetalBufferSpan(source.Buffer, source.Offset, source.Size);
        var compute = _tiler.BeginCompute(context);
        MetalBufferSpan linear;
        if (plan.Tiled)
        {
            linear = _tiler.Acquire(context, plan.LinearSize);
            _tiler.EncodeDetile(compute, new MetalBufferSpan(guest.Buffer, guest.Offset, TiledCapacity(source)), linear, _tiles);
        }
        else
        {
            tight = true;
            linear = _tiler.Acquire(context, staged);
            RepackSubresources(compute, plan, guest, linear, toGuest: false);
        }

        if (plan.Swap)
        {
            var swapped = _tiler.Acquire(context, linear.Size);
            _tiler.EncodeSwapBgra16(compute, linear, swapped);
            linear = swapped;
        }

        MetalTiler.EndEncoding(compute);
        return linear;
    }

    // How much of the mapped guest piece the selected tiles actually reach. The selection
    // is already rebased onto the piece, so this is the piece's own extent, not the
    // image's.
    private ulong TiledCapacity(in MetalGuestRange source)
    {
        ulong capacity = 0;
        foreach (var transfer in _tiles)
        {
            capacity = Math.Max(capacity, transfer.TiledOffset + transfer.TiledSize);
        }

        return Math.Min(Math.Max(capacity, 4), source.Size);
    }

    // A linear subresource staged tightly has rows of exactly the mip's own width, which
    // is what the repack kernel writes and what the blit then reads. Fills the per-copy
    // offsets and returns the total.
    private ulong BuildStagedOffsets(TransferPlan plan)
    {
        _stagedOffsets.Clear();
        ulong offset = 0;
        foreach (var copy in _copies)
        {
            _stagedOffsets.Add(offset);
            offset += StagedSize(plan, copy);
        }

        return Math.Max(offset, 4);
    }

    private static ulong StagedSize(TransferPlan plan, in MetalSubresourceCopy copy)
    {
        var size = (ulong)RowBytes(plan, copy.Width) * BlockRows(plan, copy.Height) * Math.Max(copy.Depth, 1);
        return (size + StagingAlignment - 1) & ~(StagingAlignment - 1);
    }

    private void RepackSubresources(nint encoder, TransferPlan plan, MetalBufferSpan guest, MetalBufferSpan staging, bool toGuest)
    {
        for (var index = 0; index < _copies.Count; index++)
        {
            var copy = _copies[index];
            var rowTexels = copy.BufferRowLength != 0 ? copy.BufferRowLength : copy.Width;
            var guestRowBytes = RowBytes(plan, rowTexels);
            var tightRowBytes = RowBytes(plan, copy.Width);
            var rows = BlockRows(plan, copy.Height);
            var guestSpan = new MetalBufferSpan(guest.Buffer, guest.Offset + copy.BufferOffset, guest.Size - copy.BufferOffset);
            var stagedSpan = new MetalBufferSpan(staging.Buffer, staging.Offset + _stagedOffsets[index], StagedSize(plan, copy));
            if (toGuest)
            {
                _tiler.EncodeRepackRows(encoder, stagedSpan, guestSpan, tightRowBytes, rows, 1,
                    tightRowBytes, guestRowBytes, 0, 0);
            }
            else
            {
                _tiler.EncodeRepackRows(encoder, guestSpan, stagedSpan, tightRowBytes, rows, 1,
                    guestRowBytes, tightRowBytes, 0, 0);
            }
        }
    }

    private bool IsDirectlyBlittable(TransferPlan plan, ulong guestOffset)
    {
        var alignment = Math.Max(plan.ElementBytes, BlitAlignment);
        if (guestOffset % alignment != 0)
        {
            return false;
        }

        foreach (var copy in _copies)
        {
            var rowTexels = copy.BufferRowLength != 0 ? copy.BufferRowLength : copy.Width;
            if (copy.BufferOffset % alignment != 0 || RowBytes(plan, rowTexels) % alignment != 0)
            {
                return false;
            }
        }

        return true;
    }

    private static uint RowBytes(TransferPlan plan, uint texels) =>
        Math.Max((texels + plan.TexelWidth - 1) / plan.TexelWidth, 1) * plan.ElementBytes;

    private static uint BlockRows(TransferPlan plan, uint texels) =>
        Math.Max((texels + plan.TexelHeight - 1) / plan.TexelHeight, 1);

    // Narrows the plan to the subresources the caller asked for. A volume's layers are
    // depth slices of one texture, so only its levels are selected.
    //
    // guestBase is where the guest range the caller resolved starts inside the image, so a
    // caller that could only map part of a very large image hands over that part and the
    // subresources inside it. Every selected offset is rebased onto it: the guest offsets
    // become relative to the piece that was mapped, and the linear offsets to the start of
    // the piece's own staging, so the staging a transfer asks for is the piece's size and
    // never the whole image's.
    private void Select(TransferPlan plan, in ImageDescription description, SubresourceRange range, ulong guestBase)
    {
        _copies.Clear();
        _tiles.Clear();
        var lastLevel = range.LevelCount == 0 ? 0 : range.BaseLevel + range.LevelCount - 1;
        var lastLayer = range.LayerCount == 0 ? 0 : range.BaseLayer + range.LayerCount - 1;
        foreach (var copy in plan.Copies)
        {
            if (copy.Level < range.BaseLevel || copy.Level > lastLevel || range.LevelCount == 0)
            {
                continue;
            }

            if (!plan.Volume && (copy.Slice < range.BaseLayer || copy.Slice > lastLayer || range.LayerCount == 0))
            {
                continue;
            }

            var rebased = copy;
            rebased.BufferOffset = Rebase(copy.BufferOffset, guestBase, "a subresource copy");
            _copies.Add(rebased);
        }

        if (!plan.Tiled)
        {
            return;
        }

        var linearBase = ulong.MaxValue;
        foreach (var transfer in plan.Tiles)
        {
            if (Selected(plan, transfer, range, lastLevel, lastLayer))
            {
                linearBase = Math.Min(linearBase, transfer.LinearOffset);
            }
        }

        if (linearBase == ulong.MaxValue)
        {
            plan.LinearSize = 4;
            return;
        }

        ulong linear = 0;
        foreach (var transfer in plan.Tiles)
        {
            if (!Selected(plan, transfer, range, lastLevel, lastLayer))
            {
                continue;
            }

            var rebased = transfer;
            rebased.TiledOffset = Rebase(transfer.TiledOffset, guestBase, "a tile transfer");
            rebased.LinearOffset = transfer.LinearOffset - linearBase;
            _tiles.Add(rebased);
            linear = Math.Max(linear, rebased.LinearOffset + rebased.LinearSize);
        }

        plan.LinearSize = Math.Max(linear, 4);
    }

    private static bool Selected(
        TransferPlan plan, in MetalTileTransfer transfer, SubresourceRange range, uint lastLevel, uint lastLayer) =>
        range.LevelCount != 0 &&
        transfer.Level >= range.BaseLevel && transfer.Level <= lastLevel &&
        (plan.Volume || (range.LayerCount != 0 && transfer.Slice >= range.BaseLayer && transfer.Slice <= lastLayer));

    private static ulong Rebase(ulong offset, ulong guestBase, string what) =>
        offset >= guestBase
            ? offset - guestBase
            : throw GpuFatal.Fatal(
                $"{what} starts before the mapped piece of its image: offset=0x{offset:X} base=0x{guestBase:X}.");

    private TransferPlan PlanFor(in ImageDescription description)
    {
        var key = new PlanKey(
            description.GuestFormat,
            description.PixelFormat,
            description.TileMode,
            description.Type,
            description.Extent.Width,
            description.Extent.Height,
            description.Extent.Depth,
            description.Resources.Levels,
            description.Resources.Layers,
            description.Pitch,
            description.BytesPerBlock,
            description.Data.Size,
            description.Stencil.Size,
            description.Bgra16);
        if (_plans.TryGetValue(key, out var plan))
        {
            return plan;
        }

        plan = description.IsDepth ? PlanDepth(description) : PlanColor(description);
        _plans[key] = plan;
        return plan;
    }

    private static TransferPlan PlanColor(in ImageDescription description)
    {
        if (!MetalGuestFormats.TryDecodeHostFormat(description.PixelFormat, out var format))
        {
            throw GpuFatal.Fatal($"No Metal pixel format carries this image: hostFormat={(int)description.PixelFormat}.");
        }

        // A render-target or depth-tiled surface is addressed by element size, not by its
        // sampled channel layout, exactly as the Vulkan ColorTarget role does.
        var tilerFormat = description.TileMode is GuestTileMode.RenderTarget or GuestTileMode.Depth
            ? ImageDescription.RenderTargetTransferFormat(description.BytesPerBlock)
            : description.GuestFormat;
        var plan = new TransferPlan
        {
            Format = format,
            Volume = description.IsVolume,
            Swap = description.Bgra16,
            Tiled = description.IsTiled,
        };
        plan.Layout = MetalTextureLayout.Compute(
            tilerFormat,
            description.Extent.Width,
            description.Extent.Height,
            description.Resources.Levels,
            description.TransferLayers,
            description.TileMode,
            description.Data.Size,
            allowDepthTile: true,
            volume: description.IsVolume,
            owner: "image transfer");
        plan.Layout.BuildCopies(plan.Copies);
        if (plan.Tiled)
        {
            var texture = plan.Layout.Surface.Texture;
            plan.ElementBytes = texture.Block.BytesPerElement;
            plan.TexelWidth = texture.TexelWidth;
            plan.TexelHeight = texture.TexelHeight;
            if (!plan.Layout.TryBuildTileTransfers(description.Data.Size, plan.Copies, description.Resources.Levels, plan.Tiles))
            {
                throw GpuFatal.Fatal(
                    $"The tiled image cannot be transferred on the GPU: address=0x{description.Data.Address:X16} " +
                    $"size=0x{description.Data.Size:x} format={(uint)tilerFormat} tile={(uint)description.TileMode} " +
                    $"extent={description.Extent.Width}x{description.Extent.Height}x{description.Extent.Depth} " +
                    $"levels={description.Resources.Levels} layers={description.Resources.Layers}.");
            }
        }
        else
        {
            if (!TileGeometry.TryGetElementLayout(tilerFormat, out var element))
            {
                throw GpuFatal.Fatal($"The linear image format is not supported: format={(uint)tilerFormat}.");
            }

            plan.ElementBytes = element.Bytes;
            plan.TexelWidth = element.TexelWidth;
            plan.TexelHeight = element.TexelHeight;
        }

        return plan;
    }

    private static TransferPlan PlanDepth(in ImageDescription description)
    {
        if (!MetalGuestFormats.TryDecodeHostFormat(description.PixelFormat, out var format))
        {
            throw GpuFatal.Fatal($"No Metal depth format carries this image: hostFormat={(int)description.PixelFormat}.");
        }

        var layers = description.Resources.Layers;
        if (layers == 0 || description.Data.Size % layers != 0 || description.Resources.Levels != 1 ||
            GuestPixelFormats.BytesPerElement(description.GuestFormat) != description.BytesPerBlock)
        {
            throw GpuFatal.Fatal(
                $"The depth transfer is invalid: address=0x{description.Data.Address:X16} size=0x{description.Data.Size:x} " +
                $"layers={layers} levels={description.Resources.Levels} guestFormat={(uint)description.GuestFormat} " +
                $"bytesPerBlock={description.BytesPerBlock}.");
        }

        var plan = new TransferPlan
        {
            Format = format,
            DepthStencil = true,
            Tiled = description.IsTiled,
            Layers = layers,
            SliceSize = description.Data.Size / layers,
            ElementBytes = description.BytesPerBlock,
            TransferBytes = MetalGuestFormats.DepthTransferBytes(format),
            StencilPlane = description.HasStencil && MetalGuestFormats.HasStencilPlane(format),
        };
        if (plan.TransferBytes == 0 ||
            (plan.TransferBytes != description.BytesPerBlock && (description.BytesPerBlock != 2 || plan.TransferBytes != 4)))
        {
            throw GpuFatal.Fatal(
                $"The depth transfer conversion is not supported: bytesPerBlock={description.BytesPerBlock} transferBytes={plan.TransferBytes}.");
        }

        if (plan.Tiled)
        {
            AddPlaneTransfers(plan.Tiles, description.BytesPerBlock, description.Pitch,
                description.Extent.Width, description.Extent.Height, layers, plan.SliceSize);
        }

        if (plan.StencilPlane)
        {
            if (description.Stencil.Size % layers != 0)
            {
                throw GpuFatal.Fatal(
                    $"The stencil plane does not divide by its layers: size=0x{description.Stencil.Size:x} layers={layers}.");
            }

            plan.StencilSliceSize = description.Stencil.Size / layers;
            plan.StencilPitch = plan.Tiled ? TileGeometry.DepthPitch(description.Extent.Width, 1) : description.Pitch;
            if (plan.Tiled)
            {
                AddPlaneTransfers(plan.StencilTiles, 1, plan.StencilPitch,
                    description.Extent.Width, description.Extent.Height, layers, plan.StencilSliceSize);
            }
        }

        return plan;
    }

    // One 64 KiB depth block transfer per array layer; the layer rides in SurfaceZ because
    // the depth kind folds it into every element offset.
    private static void AddPlaneTransfers(List<MetalTileTransfer> transfers, uint bytes, uint pitch, uint width, uint height, uint layers, ulong sliceSize)
    {
        if (!TileGeometry.TryGetBlockLayout(TileBlockKind.Depth64KB, bytes, out _))
        {
            throw GpuFatal.Fatal($"The depth tile layout is not supported: bytesPerElement={bytes}.");
        }

        transfers.Clear();
        for (uint layer = 0; layer < layers; layer++)
        {
            transfers.Add(new MetalTileTransfer
            {
                Kind = TileBlockKind.Depth64KB,
                BytesPerElement = bytes,
                LinearOffset = sliceSize * layer,
                LinearSize = sliceSize,
                TiledOffset = sliceSize * layer,
                TiledSize = sliceSize,
                Width = width,
                Height = height,
                Depth = 1,
                Pitch = pitch,
                SurfaceZ = layer,
                Slice = layer,
            });
        }
    }

    // The layers of one piece of a depth image. A depth plan is always a single level
    // with equal-sized layers (PlanDepth enforces both), so a piece is a run of layers and
    // guestBase has to be exactly where that run starts.
    private static (uint Base, uint Count) DepthLayers(TransferPlan plan, SubresourceRange range, ulong guestBase)
    {
        var first = Math.Min(range.BaseLayer, plan.Layers);
        var count = Math.Min(range.LayerCount, plan.Layers - first);
        if (guestBase != plan.SliceSize * first)
        {
            throw GpuFatal.Fatal(
                $"A depth piece does not start on a layer boundary: base=0x{guestBase:X} " +
                $"layer={first} sliceSize=0x{plan.SliceSize:X}.");
        }

        return (first, count);
    }

    // The stencil plane has its own tile list; rebase it onto the piece the way Select
    // rebases the depth plane's.
    private List<MetalTileTransfer> SelectStencilTiles(TransferPlan plan, uint firstLayer, uint layerCount)
    {
        _stencilTiles.Clear();
        var guestBase = plan.StencilSliceSize * firstLayer;
        var lastLayer = firstLayer + layerCount - 1;
        var linearBase = ulong.MaxValue;
        foreach (var transfer in plan.StencilTiles)
        {
            if (transfer.Slice >= firstLayer && transfer.Slice <= lastLayer)
            {
                linearBase = Math.Min(linearBase, transfer.LinearOffset);
            }
        }

        if (linearBase == ulong.MaxValue)
        {
            return _stencilTiles;
        }

        foreach (var transfer in plan.StencilTiles)
        {
            if (transfer.Slice < firstLayer || transfer.Slice > lastLayer)
            {
                continue;
            }

            var rebased = transfer;
            rebased.TiledOffset = Rebase(transfer.TiledOffset, guestBase, "a stencil tile transfer");
            rebased.LinearOffset = transfer.LinearOffset - linearBase;
            _stencilTiles.Add(rebased);
        }

        return _stencilTiles;
    }

    private void EncodeDepthUpload(IMetalSubmissionContext context, in ImageDescription description, TransferPlan plan,
        SubresourceRange range, MetalGuestRange source, MetalGuestRange stencilSource, nint texture, ulong guestBase)
    {
        var (firstLayer, layerCount) = DepthLayers(plan, range, guestBase);
        if (layerCount == 0)
        {
            return;
        }

        Select(plan, description, range, guestBase);
        var depthPiece = plan.SliceSize * layerCount;
        var stencilPiece = plan.StencilSliceSize * layerCount;
        var depthPlane = new MetalBufferSpan(source.Buffer, source.Offset, depthPiece);
        var stencilPlane = plan.StencilPlane && stencilSource.IsValid && stencilSource.Size >= stencilPiece
            ? new MetalBufferSpan(stencilSource.Buffer, stencilSource.Offset, stencilPiece)
            : default;
        nint compute = 0;
        if (plan.Tiled)
        {
            compute = _tiler.BeginCompute(context);
            var detiled = _tiler.Acquire(context, depthPiece);
            _tiler.EncodeDetile(compute, depthPlane, detiled, _tiles);
            depthPlane = detiled;
            if (stencilPlane.IsValid)
            {
                var stencilTiles = SelectStencilTiles(plan, firstLayer, layerCount);
                var detiledStencil = _tiler.Acquire(context, stencilPiece);
                _tiler.EncodeDetile(compute, stencilPlane, detiledStencil, stencilTiles);
                stencilPlane = detiledStencil;
            }
        }

        var depthRowBytes = (ulong)description.Pitch * plan.TransferBytes;
        var depthSliceBytes = depthRowBytes * description.Extent.Height;
        if (plan.TransferBytes != description.BytesPerBlock)
        {
            compute = compute != 0 ? compute : _tiler.BeginCompute(context);
            var widened = _tiler.Acquire(context, depthSliceBytes * layerCount);
            _tiler.EncodeDepthConversion(compute, depthPlane, widened, MetalDepthConversion.Widen, float32: true,
                new MetalDepthConversionLayout
                {
                    Width = description.Extent.Width,
                    Height = description.Extent.Height,
                    Layers = layerCount,
                    SourceRowStride = (ulong)description.Pitch * description.BytesPerBlock,
                    TargetRowStride = depthRowBytes,
                    SourceSliceStride = plan.SliceSize,
                    TargetSliceStride = depthSliceBytes,
                });
            depthPlane = widened;
        }
        else
        {
            depthSliceBytes = plan.SliceSize;
        }

        if (compute != 0)
        {
            MetalTiler.EndEncoding(compute);
        }

        var blit = _tiler.BeginBlit(context);
        var depthOption = MetalGuestFormats.HasStencilPlane(plan.Format)
            ? MtlBlitOption.DepthFromDepthStencil
            : MtlBlitOption.None;
        for (uint layer = 0; layer < layerCount; layer++)
        {
            MetalNative.SendCopyBufferToTextureOptions(blit, SelCopyBufferToTexture,
                depthPlane.Buffer, (nuint)(depthPlane.Offset + depthSliceBytes * layer),
                (nuint)depthRowBytes, (nuint)depthSliceBytes,
                new MtlSize { Width = description.Extent.Width, Height = description.Extent.Height, Depth = 1 },
                texture, firstLayer + layer, 0, default, (nuint)depthOption);
        }

        if (stencilPlane.IsValid)
        {
            for (uint layer = 0; layer < layerCount; layer++)
            {
                MetalNative.SendCopyBufferToTextureOptions(blit, SelCopyBufferToTexture,
                    stencilPlane.Buffer, (nuint)(stencilPlane.Offset + plan.StencilSliceSize * layer),
                    plan.StencilPitch, (nuint)plan.StencilSliceSize,
                    new MtlSize { Width = description.Extent.Width, Height = description.Extent.Height, Depth = 1 },
                    texture, firstLayer + layer, 0, default, (nuint)MtlBlitOption.StencilFromDepthStencil);
            }
        }

        MetalTiler.EndEncoding(blit);
    }

    // The way out of a tiled depth surface: one compute pass reads the depth texture and
    // tiles the guest plane straight out of it, so the plane never lands in a staging
    // buffer and the widening the upload did is undone inside the same kernel. The
    // stencil plane still takes a blit, because its only route out of a combined
    // depth-stencil texture is MTLBlitOptionStencilFromDepthStencil: reading it in a
    // shader would need an X32_Stencil8 view, and the PixelFormatView usage that view
    // asks for is what turns off the attachment's lossless compression while it renders.
    private void EncodeTiledDepthDownload(IMetalSubmissionContext context, in ImageDescription description,
        TransferPlan plan, SubresourceRange range, nint texture, MetalGuestRange destination,
        MetalGuestRange stencilDestination, ulong guestBase)
    {
        var (firstLayer, layerCount) = DepthLayers(plan, range, guestBase);
        if (layerCount == 0)
        {
            return;
        }

        // The selected tiles carry the texture slice they came from and guest offsets
        // rebased onto this piece, so the kernel reads the right array slices and writes
        // inside the piece that was mapped.
        Select(plan, description, range, guestBase);
        var depthPiece = plan.SliceSize * layerCount;
        var stencilPiece = plan.StencilSliceSize * layerCount;
        var stencilWanted = plan.StencilPlane && stencilDestination.IsValid && stencilDestination.Size >= stencilPiece;
        var stencilPlane = stencilWanted ? _tiler.Acquire(context, stencilPiece) : default;
        if (stencilWanted)
        {
            var blit = _tiler.BeginBlit(context);
            for (uint layer = 0; layer < layerCount; layer++)
            {
                MetalNative.SendCopyTextureToBufferOptions(blit, SelCopyTextureToBuffer,
                    texture, firstLayer + layer, 0, default,
                    new MtlSize { Width = description.Extent.Width, Height = description.Extent.Height, Depth = 1 },
                    stencilPlane.Buffer, (nuint)(stencilPlane.Offset + plan.StencilSliceSize * layer),
                    plan.StencilPitch, (nuint)plan.StencilSliceSize, (nuint)MtlBlitOption.StencilFromDepthStencil);
            }

            MetalTiler.EndEncoding(blit);
        }

        var compute = _tiler.BeginCompute(context);
        _tiler.EncodeDepthTextureWriteback(compute, texture,
            new MetalBufferSpan(destination.Buffer, destination.Offset, depthPiece),
            description.BytesPerBlock, plan.Layers > 1, _tiles);
        if (stencilWanted)
        {
            _tiler.EncodeTile(compute, stencilPlane,
                new MetalBufferSpan(stencilDestination.Buffer, stencilDestination.Offset, stencilPiece),
                SelectStencilTiles(plan, firstLayer, layerCount));
        }

        MetalTiler.EndEncoding(compute);
    }

    private void EncodeDepthDownload(IMetalSubmissionContext context, in ImageDescription description, TransferPlan plan,
        SubresourceRange range, nint texture, MetalGuestRange destination, MetalGuestRange stencilDestination,
        ulong guestBase)
    {
        var (firstLayer, layerCount) = DepthLayers(plan, range, guestBase);
        if (layerCount == 0)
        {
            return;
        }

        if (plan.Tiled)
        {
            EncodeTiledDepthDownload(
                context, description, plan, range, texture, destination, stencilDestination, guestBase);
            return;
        }

        var depthPiece = plan.SliceSize * layerCount;
        var stencilPiece = plan.StencilSliceSize * layerCount;
        var widening = plan.TransferBytes != description.BytesPerBlock;
        var depthRowBytes = (ulong)description.Pitch * plan.TransferBytes;
        var depthSliceBytes = widening ? depthRowBytes * description.Extent.Height : plan.SliceSize;
        var direct = !widening && destination.Offset % plan.TransferBytes == 0;
        var depthPlane = direct
            ? new MetalBufferSpan(destination.Buffer, destination.Offset, depthPiece)
            : _tiler.Acquire(context, depthSliceBytes * layerCount);
        var stencilWanted = plan.StencilPlane && stencilDestination.IsValid && stencilDestination.Size >= stencilPiece;
        var stencilPlane = stencilWanted
            ? new MetalBufferSpan(stencilDestination.Buffer, stencilDestination.Offset, stencilPiece)
            : default;

        var blit = _tiler.BeginBlit(context);
        var depthOption = MetalGuestFormats.HasStencilPlane(plan.Format)
            ? MtlBlitOption.DepthFromDepthStencil
            : MtlBlitOption.None;
        for (uint layer = 0; layer < layerCount; layer++)
        {
            MetalNative.SendCopyTextureToBufferOptions(blit, SelCopyTextureToBuffer,
                texture, firstLayer + layer, 0, default,
                new MtlSize { Width = description.Extent.Width, Height = description.Extent.Height, Depth = 1 },
                depthPlane.Buffer, (nuint)(depthPlane.Offset + depthSliceBytes * layer),
                (nuint)depthRowBytes, (nuint)depthSliceBytes, (nuint)depthOption);
        }

        if (stencilPlane.IsValid)
        {
            for (uint layer = 0; layer < layerCount; layer++)
            {
                MetalNative.SendCopyTextureToBufferOptions(blit, SelCopyTextureToBuffer,
                    texture, firstLayer + layer, 0, default,
                    new MtlSize { Width = description.Extent.Width, Height = description.Extent.Height, Depth = 1 },
                    stencilPlane.Buffer, (nuint)(stencilPlane.Offset + plan.StencilSliceSize * layer),
                    plan.StencilPitch, (nuint)plan.StencilSliceSize, (nuint)MtlBlitOption.StencilFromDepthStencil);
            }
        }

        MetalTiler.EndEncoding(blit);
        if (direct)
        {
            return;
        }

        var compute = _tiler.BeginCompute(context);
        if (widening)
        {
            var narrowed = _tiler.Acquire(context, depthPiece);
            _tiler.EncodeDepthConversion(compute, depthPlane, narrowed, MetalDepthConversion.Narrow, float32: true,
                new MetalDepthConversionLayout
                {
                    Width = description.Extent.Width,
                    Height = description.Extent.Height,
                    Layers = layerCount,
                    SourceRowStride = depthRowBytes,
                    TargetRowStride = (ulong)description.Pitch * description.BytesPerBlock,
                    SourceSliceStride = depthSliceBytes,
                    TargetSliceStride = plan.SliceSize,
                });
            depthPlane = narrowed;
        }

        _tiler.EncodeRepackRows(compute, depthPlane,
            new MetalBufferSpan(destination.Buffer, destination.Offset, depthPiece),
            (uint)((ulong)description.Pitch * description.BytesPerBlock), description.Extent.Height, layerCount,
            (ulong)description.Pitch * description.BytesPerBlock,
            (ulong)description.Pitch * description.BytesPerBlock,
            plan.SliceSize, plan.SliceSize);
        MetalTiler.EndEncoding(compute);
    }
}
