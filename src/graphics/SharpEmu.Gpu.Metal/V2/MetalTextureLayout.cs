// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.Images;
using SharpEmu.Libs.Gpu.Scheduling;

namespace SharpEmu.Gpu.Metal.V2;

// One tiler dispatch: a linear span, a tiled span and the surface window between them.
// The port of the Vulkan backend's TileTransfer; Level and Slice are added so a transfer
// can be matched to the SubresourceRange a caller asks for.
internal struct MetalTileTransfer
{
    public TileBlockKind Kind;
    public uint BytesPerElement;
    public ulong LinearOffset;
    public ulong LinearSize;
    public ulong TiledOffset;
    public ulong TiledSize;
    public ulong LinearSliceStride;
    public uint Width;
    public uint Height;
    public uint Depth;
    public uint Pitch;
    public uint TailX;
    public uint TailY;
    public bool Tail;
    public uint TiledWidth;
    public uint TiledHeight;
    public uint SurfaceZ;
    public uint Level;
    public uint Slice;

    // The surface row this transfer's first row is, for a transfer cut down to a run
    // of block rows; zero for one that covers its subresource from the top.
    public uint BaseY;
}

// One buffer-to-texture (or texture-to-buffer) blit region. The Metal twin of
// VkBufferImageCopy: row length and image height count texels and are zero when the
// rows are tightly packed, exactly as the Vulkan regions are built.
internal struct MetalSubresourceCopy
{
    public ulong BufferOffset;
    public uint BufferRowLength;
    public uint BufferImageHeight;
    public uint Level;
    public uint Slice;
    public uint OriginZ;

    // The first texel row this copy writes, for a copy cut down to a run of rows.
    public uint OriginY;
    public uint Width;
    public uint Height;
    public uint Depth;
}

internal struct MetalTransferMipLayout
{
    public ulong Offset;
    public ulong Size;
    public uint RowLength;
    public uint ImageHeight;
}

// The linear staging layout of a guest texture and its tiled surface layout. A port of
// the Vulkan backend's TextureTransferLayout with no graphics-API types; the shared
// TileGeometry still owns every tiled offset, so both backends stage the same bytes.
internal sealed class MetalTextureLayout
{
    public uint Pitch;
    public ulong SliceStride;
    public ulong SourceSliceStride;
    public TiledSurfaceLayout Surface = new();

    public readonly MetalTransferMipLayout[] Mips = new MetalTransferMipLayout[TiledSurfaceLayout.MaxLevels];

    private static uint LevelDepth(uint depth, uint level, bool volume) => volume ? Math.Max(depth >> (int)level, 1) : depth;

    private static int RegionCount(uint depth, uint levels, bool volume)
    {
        var count = 0;
        for (uint level = 0; level < levels; level++)
        {
            count += (int)LevelDepth(depth, level, volume);
        }

        return count;
    }

    private static ulong SliceStrideOf(MetalTransferMipLayout[] mips, uint levels, ulong totalSize, uint depth)
    {
        ulong stride = 0;
        for (uint levelIndex = 0; levelIndex < levels; levelIndex++)
        {
            stride = Math.Max(stride, mips[levelIndex].Offset + mips[levelIndex].Size);
        }

        if (depth > 1 && totalSize != 0 && totalSize % depth == 0)
        {
            var guestStride = totalSize / depth;
            if (guestStride >= stride)
            {
                stride = guestStride;
            }
        }

        return stride;
    }

    private static ulong LinearLevelSize(in TileElementLayout element, uint pitch, uint height)
    {
        var elementsWide = Math.Max((pitch + element.TexelWidth - 1) / element.TexelWidth, 1);
        var elementsHigh = Math.Max((height + element.TexelHeight - 1) / element.TexelHeight, 1);
        return (ulong)elementsWide * elementsHigh * element.Bytes;
    }

    private static ulong SetLinearLevels(MetalTransferMipLayout[] mips, in TileElementLayout element, uint height, uint levels, uint basePitch)
    {
        ulong offset = 0;
        var pitch = basePitch;
        var levelHeight = height;
        for (uint levelIndex = 0; levelIndex < levels; levelIndex++)
        {
            var size = LinearLevelSize(element, pitch, levelHeight);
            mips[levelIndex].Size = size;
            mips[levelIndex].Offset = offset;
            offset += size;
            if (pitch > 1)
            {
                pitch /= 2;
            }

            if (levelHeight > 1)
            {
                levelHeight /= 2;
            }
        }

        return offset;
    }

    public static MetalTextureLayout Compute(
        GuestPixelFormat format,
        uint width,
        uint height,
        uint levels,
        uint depth,
        GuestTileMode tile,
        ulong uploadSize,
        bool allowDepthTile,
        bool volume,
        string owner)
    {
        var layout = new MetalTextureLayout();
        var description = new TiledSurfaceDescription(
            format,
            tile,
            volume ? TileSurfaceDimension.Volume3D : TileSurfaceDimension.Flat2D,
            width,
            height,
            volume ? depth : 1,
            levels,
            volume ? 1 : depth);
        layout.Surface.Description = description;
        var element = default(TileElementLayout);

        if (format == GuestPixelFormat.Invalid)
        {
            throw GpuFatal.Fatal($"{owner}: the texture upload format is unknown: format=0 tile={(uint)tile} size={uploadSize} extent={width}x{height} levels={levels}.");
        }

        switch (tile)
        {
            case GuestTileMode.Linear:
                if (!TileGeometry.TryGetElementLayout(format, out element))
                {
                    throw GpuFatal.Fatal($"{owner}: the linear texture format is not supported: format={(uint)format}.");
                }

                break;
            case GuestTileMode.Depth when !allowDepthTile:
                throw UnsupportedTiledUpload(owner, format, tile, uploadSize, width, height, levels);
            default:
                if (!TileGeometry.TryGetTiledTextureLayout(description, out var surface))
                {
                    throw UnsupportedTiledUpload(owner, format, tile, uploadSize, width, height, levels);
                }

                layout.Surface = surface;
                break;
        }

        if (tile != GuestTileMode.Linear)
        {
            var texture = layout.Surface.Texture;
            element = new TileElementLayout(texture.Block.BytesPerElement, texture.TexelWidth, texture.TexelHeight);
        }

        layout.Pitch = TileGeometry.TexturePitch(format, width, tile);
        if (tile == GuestTileMode.Linear)
        {
            var levelSpans = new TileLevelSpan[TiledSurfaceLayout.MaxLevels];
            var paddedSizes = new TilePaddedSize[TiledSurfaceLayout.MaxLevels];
            TileGeometry.TryGetTextureSize(format, width, height, levels, tile, out _, levelSpans, paddedSizes);
            for (uint level = 0; level < levels; level++)
            {
                layout.Mips[level] = new MetalTransferMipLayout
                {
                    Offset = levelSpans[level].Offset,
                    Size = levelSpans[level].Size,
                    RowLength = paddedSizes[level].Width,
                    ImageHeight = paddedSizes[level].Height,
                };
            }
        }
        else if (volume)
        {
            layout.SliceStride = SetLinearLevels(layout.Mips, element, height, levels, width);
        }
        else
        {
            layout.SourceSliceStride = layout.Surface.BlockSliceSize;
            if (depth > 1 && uploadSize != 0 && uploadSize % depth == 0)
            {
                layout.SourceSliceStride = Math.Max(layout.SourceSliceStride, uploadSize / depth);
            }

            SetLinearLevels(layout.Mips, element, height, levels, layout.Pitch);
        }

        if (!volume || tile == GuestTileMode.Linear)
        {
            layout.SliceStride = SliceStrideOf(layout.Mips, levels, uploadSize, depth);
        }

        return layout;
    }

    private static Exception UnsupportedTiledUpload(string owner, GuestPixelFormat format, GuestTileMode tile, ulong uploadSize, uint width, uint height, uint levels) =>
        GpuFatal.Fatal($"{owner}: the tiled texture upload is not supported: format={(uint)format} tile={(uint)tile} size={uploadSize} extent={width}x{height} levels={levels}.");

    public void BuildCopies(List<MetalSubresourceCopy> regions)
    {
        var description = Surface.Description;
        var volume = description.Dimension == TileSurfaceDimension.Volume3D;
        var depth = volume ? description.Depth : description.Layers;
        var mipWidth = description.Width;
        var mipHeight = description.Height;
        var mipPitch = volume && description.TileMode != GuestTileMode.Linear ? description.Width : Pitch;
        var linear = description.TileMode == GuestTileMode.Linear;

        regions.Clear();
        regions.EnsureCapacity(RegionCount(depth, description.Levels, volume));
        for (uint level = 0; level < description.Levels; level++)
        {
            if (Mips[level].Size == 0)
            {
                throw GpuFatal.Fatal($"A mip level of the transfer layout has no size: level={level}.");
            }

            var mipDepth = LevelDepth(depth, level, volume);
            for (uint depthSlice = 0; depthSlice < mipDepth; depthSlice++)
            {
                var region = new MetalSubresourceCopy
                {
                    BufferOffset = Mips[level].Offset + depthSlice * SliceStride,
                    Level = level,
                    Slice = volume ? 0 : depthSlice,
                    OriginZ = volume ? depthSlice : 0,
                    Width = mipWidth,
                    Height = mipHeight,
                    Depth = 1,
                };
                if (linear)
                {
                    region.BufferRowLength = Mips[level].RowLength;
                    region.BufferImageHeight = Mips[level].ImageHeight;
                }
                else
                {
                    var texelWidth = Surface.Texture.TexelWidth;
                    static uint Align(uint value, uint block) => (value + block - 1) / block * block;
                    var pitch = Align(mipPitch, texelWidth);
                    region.BufferRowLength = pitch > Align(mipWidth, texelWidth) ? pitch : 0;
                }

                regions.Add(region);
            }

            if (mipWidth > 1)
            {
                mipWidth /= 2;
            }

            if (mipHeight > 1)
            {
                mipHeight /= 2;
            }

            if (mipPitch > 1)
            {
                mipPitch /= 2;
            }
        }
    }

    private static bool TrySetSpan(ulong offset, ulong length, ulong capacity, out ulong size)
    {
        size = 0;
        if (offset > capacity || length > capacity - offset)
        {
            return false;
        }

        size = length;
        return true;
    }

    // One tile transfer per copy region, or false when the layout cannot be tiled on the GPU.
    public bool TryBuildTileTransfers(ulong tiledSize, List<MetalSubresourceCopy> regions, uint levels, List<MetalTileTransfer> transfers)
    {
        transfers.Clear();
        var description = Surface.Description;
        var volume = description.Dimension == TileSurfaceDimension.Volume3D;
        var depth = volume ? description.Depth : description.Layers;
        if (tiledSize == 0 || levels == 0 || levels > TiledSurfaceLayout.MaxLevels || depth == 0 ||
            regions.Count != RegionCount(depth, levels, volume) || GuestPixelFormats.IsFmaskFormat(description.Format))
        {
            return false;
        }

        var surface = Surface;
        var texture = surface.Texture;
        var block = texture.Block;
        if (surface.Description.Levels < levels || !block.IsValid)
        {
            return false;
        }

        transfers.EnsureCapacity(regions.Count);
        if (volume)
        {
            var regionBase = 0;
            for (uint level = 0; level < levels; level++)
            {
                var mipDepth = LevelDepth(depth, level, true);
                var tail = level >= surface.FirstTailLevel;
                var linearStride = SliceStride;
                ref var mip = ref surface.Mips[level];
                for (uint depthSlice = 0; depthSlice < mipDepth; depthSlice += block.BlockDepth)
                {
                    var copyDepth = Math.Min(block.BlockDepth, mipDepth - depthSlice);
                    var region = regions[regionBase + (int)depthSlice];
                    var pitch = region.BufferRowLength != 0 ? region.BufferRowLength : region.Width;
                    var logicalHeight = region.BufferImageHeight != 0 ? region.BufferImageHeight : region.Height;
                    var transfer = new MetalTileTransfer
                    {
                        Kind = block.Kind,
                        BytesPerElement = block.BytesPerElement,
                        LinearOffset = region.BufferOffset,
                        TiledOffset = (ulong)(depthSlice / block.BlockDepth) * surface.BlockSliceSize + mip.Offset,
                        Level = level,
                        Slice = depthSlice,
                    };
                    var linearSpan = (ulong)(copyDepth - 1) * linearStride + Mips[level].Size;
                    if (!TrySetSpan(transfer.LinearOffset, linearSpan, ulong.MaxValue, out transfer.LinearSize) ||
                        !TrySetSpan(transfer.TiledOffset, mip.Size, tiledSize, out transfer.TiledSize))
                    {
                        return false;
                    }

                    transfer.LinearSliceStride = linearStride;
                    transfer.Width = Math.Max((region.Width + texture.TexelWidth - 1) / texture.TexelWidth, 1);
                    transfer.Height = Math.Max((logicalHeight + texture.TexelHeight - 1) / texture.TexelHeight, 1);
                    transfer.Depth = copyDepth;
                    transfer.SurfaceZ = block.BlockDepth == 1 ? region.OriginZ : 0;
                    transfer.Pitch = Math.Max((pitch + texture.TexelWidth - 1) / texture.TexelWidth, 1);
                    transfer.TailX = tail ? mip.TailX : 0;
                    transfer.TailY = tail ? mip.TailY : 0;
                    transfer.Tail = tail;
                    transfer.TiledWidth = mip.PaddedWidth;
                    transfer.TiledHeight = mip.PaddedHeight;
                    transfers.Add(transfer);
                }

                regionBase += (int)mipDepth;
            }
        }
        else
        {
            var regionIndex = 0;
            for (uint level = 0; level < levels; level++)
            {
                var levelSize = Mips[level];
                ref var mip = ref surface.Mips[level];
                var tail = level >= surface.FirstTailLevel;
                var levelDepth = LevelDepth(depth, level, false);
                for (uint depthSlice = 0; depthSlice < levelDepth; depthSlice++)
                {
                    var region = regions[regionIndex++];
                    var pitch = region.BufferRowLength != 0 ? region.BufferRowLength : region.Width;
                    var logicalHeight = region.BufferImageHeight != 0 ? region.BufferImageHeight : region.Height;
                    var transfer = new MetalTileTransfer
                    {
                        Kind = block.Kind,
                        BytesPerElement = block.BytesPerElement,
                        LinearOffset = region.BufferOffset,
                        Level = level,
                        Slice = depthSlice,
                    };
                    var sourceStride = SourceSliceStride != 0 ? SourceSliceStride : surface.BlockSliceSize;
                    if (depthSlice > (ulong.MaxValue - mip.Offset) / sourceStride)
                    {
                        return false;
                    }

                    transfer.TiledOffset = mip.Offset + depthSlice * sourceStride;
                    if (!TrySetSpan(transfer.LinearOffset, levelSize.Size, ulong.MaxValue, out transfer.LinearSize) ||
                        !TrySetSpan(transfer.TiledOffset, mip.Size, tiledSize, out transfer.TiledSize))
                    {
                        return false;
                    }

                    transfer.Width = Math.Max((region.Width + texture.TexelWidth - 1) / texture.TexelWidth, 1);
                    transfer.Height = Math.Max((logicalHeight + texture.TexelHeight - 1) / texture.TexelHeight, 1);
                    transfer.Depth = 1;
                    transfer.SurfaceZ = block.Kind is TileBlockKind.RenderTarget64KB or TileBlockKind.RenderTarget64KBGen5 or TileBlockKind.Depth64KB
                        ? region.Slice
                        : 0;
                    transfer.Pitch = Math.Max((pitch + texture.TexelWidth - 1) / texture.TexelWidth, 1);
                    transfer.Tail = tail;
                    transfer.TailX = tail ? mip.TailX : 0;
                    transfer.TailY = tail ? mip.TailY : 0;
                    transfer.TiledWidth = mip.PaddedWidth;
                    transfer.TiledHeight = mip.PaddedHeight;
                    transfers.Add(transfer);
                }
            }
        }

        return transfers.Count != 0;
    }
}
