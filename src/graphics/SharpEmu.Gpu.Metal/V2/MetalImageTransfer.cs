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
        }

        return usage;
    }

    public void EncodeGuestToTexture(IMetalSubmissionContext context, in ImageDescription description,
        SubresourceRange range, MetalGuestRange source, MetalGuestRange stencilSource, nint texture)
    {
        // Per-sample tiled layouts are not decoded on either backend; the Vulkan cache
        // leaves a multisampled image untouched too.
        if (!Ready(context, description, source, texture))
        {
            return;
        }

        var plan = PlanFor(description);
        if (plan.DepthStencil)
        {
            EncodeDepthUpload(context, description, plan, source, stencilSource, texture);
            return;
        }

        Select(plan, description, range);
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
        SubresourceRange range, nint texture, MetalGuestRange destination, MetalGuestRange stencilDestination)
    {
        if (!Ready(context, description, destination, texture))
        {
            return;
        }

        var plan = PlanFor(description);
        if (plan.DepthStencil)
        {
            EncodeDepthDownload(context, description, plan, texture, destination, stencilDestination);
            return;
        }

        Select(plan, description, range);
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
            ? new MetalBufferSpan(destination.Buffer, destination.Offset, description.Data.Size)
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

        var guest = new MetalBufferSpan(destination.Buffer, destination.Offset, description.Data.Size);
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

    private bool Ready(IMetalSubmissionContext context, in ImageDescription description, in MetalGuestRange guest, nint texture)
    {
        if (_disposed || context.CommandBuffer == 0 || texture == 0 || !guest.IsValid ||
            ImageDescription.IsEmptyRange(description.Data) || description.Samples != 1 ||
            description.PixelFormat == GpuFormat.Undefined)
        {
            return false;
        }

        if (guest.Size < description.Data.Size)
        {
            throw GpuFatal.Fatal(
                $"The guest range is smaller than the image: range=0x{guest.Size:x} image=0x{description.Data.Size:x} " +
                $"address=0x{description.Data.Address:X16}.");
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
            _tiler.EncodeDetile(compute, new MetalBufferSpan(guest.Buffer, guest.Offset, TiledCapacity(plan, source)), linear, _tiles);
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

    private static ulong TiledCapacity(TransferPlan plan, in MetalGuestRange source)
    {
        ulong capacity = 0;
        foreach (var transfer in plan.Tiles)
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
    private void Select(TransferPlan plan, in ImageDescription description, SubresourceRange range)
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

            _copies.Add(copy);
        }

        if (!plan.Tiled)
        {
            return;
        }

        ulong linear = 0;
        foreach (var transfer in plan.Tiles)
        {
            if (transfer.Level < range.BaseLevel || transfer.Level > lastLevel || range.LevelCount == 0)
            {
                continue;
            }

            if (!plan.Volume && (transfer.Slice < range.BaseLayer || transfer.Slice > lastLayer || range.LayerCount == 0))
            {
                continue;
            }

            _tiles.Add(transfer);
            linear = Math.Max(linear, transfer.LinearOffset + transfer.LinearSize);
        }

        plan.LinearSize = Math.Max(linear, 4);
    }

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

    private void EncodeDepthUpload(IMetalSubmissionContext context, in ImageDescription description, TransferPlan plan,
        MetalGuestRange source, MetalGuestRange stencilSource, nint texture)
    {
        var depthPlane = new MetalBufferSpan(source.Buffer, source.Offset, description.Data.Size);
        var stencilPlane = plan.StencilPlane && stencilSource.IsValid
            ? new MetalBufferSpan(stencilSource.Buffer, stencilSource.Offset, description.Stencil.Size)
            : default;
        nint compute = 0;
        if (plan.Tiled)
        {
            compute = _tiler.BeginCompute(context);
            var detiled = _tiler.Acquire(context, description.Data.Size);
            _tiler.EncodeDetile(compute, depthPlane, detiled, plan.Tiles);
            depthPlane = detiled;
            if (stencilPlane.IsValid)
            {
                var detiledStencil = _tiler.Acquire(context, description.Stencil.Size);
                _tiler.EncodeDetile(compute, stencilPlane, detiledStencil, plan.StencilTiles);
                stencilPlane = detiledStencil;
            }
        }

        var depthRowBytes = (ulong)description.Pitch * plan.TransferBytes;
        var depthSliceBytes = depthRowBytes * description.Extent.Height;
        if (plan.TransferBytes != description.BytesPerBlock)
        {
            compute = compute != 0 ? compute : _tiler.BeginCompute(context);
            var widened = _tiler.Acquire(context, depthSliceBytes * plan.Layers);
            _tiler.EncodeDepthConversion(compute, depthPlane, widened, MetalDepthConversion.Widen, float32: true,
                new MetalDepthConversionLayout
                {
                    Width = description.Extent.Width,
                    Height = description.Extent.Height,
                    Layers = plan.Layers,
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
        for (uint layer = 0; layer < plan.Layers; layer++)
        {
            MetalNative.SendCopyBufferToTextureOptions(blit, SelCopyBufferToTexture,
                depthPlane.Buffer, (nuint)(depthPlane.Offset + depthSliceBytes * layer),
                (nuint)depthRowBytes, (nuint)depthSliceBytes,
                new MtlSize { Width = description.Extent.Width, Height = description.Extent.Height, Depth = 1 },
                texture, layer, 0, default, (nuint)depthOption);
        }

        if (stencilPlane.IsValid)
        {
            for (uint layer = 0; layer < plan.Layers; layer++)
            {
                MetalNative.SendCopyBufferToTextureOptions(blit, SelCopyBufferToTexture,
                    stencilPlane.Buffer, (nuint)(stencilPlane.Offset + plan.StencilSliceSize * layer),
                    plan.StencilPitch, (nuint)plan.StencilSliceSize,
                    new MtlSize { Width = description.Extent.Width, Height = description.Extent.Height, Depth = 1 },
                    texture, layer, 0, default, (nuint)MtlBlitOption.StencilFromDepthStencil);
            }
        }

        MetalTiler.EndEncoding(blit);
    }

    private void EncodeDepthDownload(IMetalSubmissionContext context, in ImageDescription description, TransferPlan plan,
        nint texture, MetalGuestRange destination, MetalGuestRange stencilDestination)
    {
        var widening = plan.TransferBytes != description.BytesPerBlock;
        var depthRowBytes = (ulong)description.Pitch * plan.TransferBytes;
        var depthSliceBytes = widening ? depthRowBytes * description.Extent.Height : plan.SliceSize;
        var direct = !plan.Tiled && !widening && destination.Offset % plan.TransferBytes == 0;
        var depthPlane = direct
            ? new MetalBufferSpan(destination.Buffer, destination.Offset, description.Data.Size)
            : _tiler.Acquire(context, depthSliceBytes * plan.Layers);
        var stencilWanted = plan.StencilPlane && stencilDestination.IsValid;
        var stencilDirect = stencilWanted && !plan.Tiled;
        var stencilPlane = stencilWanted
            ? stencilDirect
                ? new MetalBufferSpan(stencilDestination.Buffer, stencilDestination.Offset, description.Stencil.Size)
                : _tiler.Acquire(context, description.Stencil.Size)
            : default;

        var blit = _tiler.BeginBlit(context);
        var depthOption = MetalGuestFormats.HasStencilPlane(plan.Format)
            ? MtlBlitOption.DepthFromDepthStencil
            : MtlBlitOption.None;
        for (uint layer = 0; layer < plan.Layers; layer++)
        {
            MetalNative.SendCopyTextureToBufferOptions(blit, SelCopyTextureToBuffer,
                texture, layer, 0, default,
                new MtlSize { Width = description.Extent.Width, Height = description.Extent.Height, Depth = 1 },
                depthPlane.Buffer, (nuint)(depthPlane.Offset + depthSliceBytes * layer),
                (nuint)depthRowBytes, (nuint)depthSliceBytes, (nuint)depthOption);
        }

        if (stencilPlane.IsValid)
        {
            for (uint layer = 0; layer < plan.Layers; layer++)
            {
                MetalNative.SendCopyTextureToBufferOptions(blit, SelCopyTextureToBuffer,
                    texture, layer, 0, default,
                    new MtlSize { Width = description.Extent.Width, Height = description.Extent.Height, Depth = 1 },
                    stencilPlane.Buffer, (nuint)(stencilPlane.Offset + plan.StencilSliceSize * layer),
                    plan.StencilPitch, (nuint)plan.StencilSliceSize, (nuint)MtlBlitOption.StencilFromDepthStencil);
            }
        }

        MetalTiler.EndEncoding(blit);
        if (direct && !stencilWanted)
        {
            return;
        }

        var compute = _tiler.BeginCompute(context);
        if (widening)
        {
            var narrowed = _tiler.Acquire(context, description.Data.Size);
            _tiler.EncodeDepthConversion(compute, depthPlane, narrowed, MetalDepthConversion.Narrow, float32: true,
                new MetalDepthConversionLayout
                {
                    Width = description.Extent.Width,
                    Height = description.Extent.Height,
                    Layers = plan.Layers,
                    SourceRowStride = depthRowBytes,
                    TargetRowStride = (ulong)description.Pitch * description.BytesPerBlock,
                    SourceSliceStride = depthSliceBytes,
                    TargetSliceStride = plan.SliceSize,
                });
            depthPlane = narrowed;
        }

        if (plan.Tiled)
        {
            _tiler.EncodeTile(compute, depthPlane,
                new MetalBufferSpan(destination.Buffer, destination.Offset, description.Data.Size), plan.Tiles);
            if (stencilPlane.IsValid)
            {
                _tiler.EncodeTile(compute, stencilPlane,
                    new MetalBufferSpan(stencilDestination.Buffer, stencilDestination.Offset, description.Stencil.Size),
                    plan.StencilTiles);
            }
        }
        else if (!direct)
        {
            _tiler.EncodeRepackRows(compute, depthPlane,
                new MetalBufferSpan(destination.Buffer, destination.Offset, description.Data.Size),
                (uint)((ulong)description.Pitch * description.BytesPerBlock), description.Extent.Height, plan.Layers,
                (ulong)description.Pitch * description.BytesPerBlock,
                (ulong)description.Pitch * description.BytesPerBlock,
                plan.SliceSize, plan.SliceSize);
        }

        MetalTiler.EndEncoding(compute);
    }
}
