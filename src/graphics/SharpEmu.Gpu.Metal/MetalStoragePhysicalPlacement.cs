// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
using SharpEmu.HLE;
using SharpEmu.Libs.Agc;
using SharpEmu.Libs.Gpu;
using SharpEmu.Libs.Gpu.Images;

namespace SharpEmu.Gpu.Metal;

// Original provider placement only. The discarded probe is never upload data,
// a CPU-content receipt, a GPU-completion receipt or a native borrow.
internal static class MetalStoragePhysicalPlacement
{
    internal sealed record Observation(ICpuMemory Memory, ICpuMappedCopySource Source,
        CpuMappedCopyObservation Mapping, GuestDrawTexture Original,
        ulong WriteAddress, ulong WriteBytes, long Session);

    internal readonly record struct PhysicalSpan(long BackingIdentity, ulong Offset, ulong ByteCount);

    internal static Observation Capture(ICpuMemory memory, GuestDrawTexture texture, long session)
    {
        if (memory is not ICpuMappedCopySource source)
            throw new NotSupportedException("Native storage placement requires the original mapped-copy provider.");
        var (allocation, allocationBytes, address, bytes) = Describe(texture);
        Span<byte> discarded = stackalloc byte[8];
        var result = source.TryReadMappedCopy(allocation, allocationBytes, address, discarded, out var mapping);
        if (result != CpuMappedCopyResult.Copied || mapping is null ||
            mapping.FootprintAddress != allocation || mapping.FootprintByteCount != allocationBytes ||
            mapping.SourceAddress != address || mapping.SourceByteCount != (ulong)discarded.Length ||
            !source.IsMappedCopyObservationCurrent(mapping))
            throw new InvalidOperationException("Native storage original full allocation mapping was refused.");
        var original = new Observation(memory, source, mapping,
            texture with { Descriptor = texture.Descriptor!.ToArray() }, address, bytes, session);
        // Validate COMPLETE potential writes, not the eight discarded bytes.
        _ = WriteSpans(original);
        if (!source.IsMappedCopyObservationCurrent(mapping))
            throw new InvalidOperationException("Native storage original mapping changed during placement capture.");
        return original;
    }

    internal static (ulong Allocation, ulong AllocationBytes, ulong Address, ulong Bytes) Describe(GuestDrawTexture texture)
    {
        if (!texture.IsStorage || texture.Type != 9 || texture.ArrayedView || texture.ArrayLayers != 1 || texture.Depth != 1 ||
            texture.MipLevel != 0 || texture.BaseMipLevel != 0 || texture.Descriptor is not { Length: 8 } raw ||
            texture.Shape is not { Storage: true, Volume: false, Arrayed: false, Cube: false,
                DynamicMip: false, OneDimensional: false, Multisampled: false, DepthCompare: false,
                R128: false, Atomic: false } shape ||
            texture.Access == GuestImageAccess.Unknown || (texture.Access & ~(GuestImageAccess.Read | GuestImageAccess.Write)) != 0 ||
            !AgcBackendServices.TryDecodeTextureDescriptor(raw, out var decoded) ||
            decoded.Address != texture.Address || decoded.Width != texture.Width || decoded.Height != texture.Height ||
            decoded.Pitch != texture.Pitch || decoded.Format != texture.Format || decoded.NumberType != texture.NumberType ||
            decoded.TileMode != texture.TileMode || decoded.Type != texture.Type || decoded.Depth != 1 ||
            decoded.BaseLevel != 0 || decoded.LastLevel != 0 || decoded.ResourceMipLevels != texture.ResourceMipLevels)
            throw new NotSupportedException("Native storage physical placement requires complete matching static mip-zero 2D metadata.");
        // These are the observed ordinary storage classes. Additional shapes
        // require their own full layout/producer controls before enrollment.
        var qualified = decoded.TileMode is 9 or 27 && shape.NumericClass == TextureNumericClass.Float &&
            (decoded.ResourceMipLevels == 1 &&
                (decoded.Width == 256 && decoded.Height == 256 &&
                    (decoded.Format == 5 && decoded.NumberType == 7 || decoded.Format == 2 && decoded.NumberType == 7) ||
                 decoded.Width == 48 && decoded.Height == 24 && decoded.Format == 2 && decoded.NumberType == 0 ||
                 decoded.Width == 64 && decoded.Height == 64 && decoded.Format == 12 && decoded.NumberType == 7) ||
             decoded.TileMode == 9 && decoded.ResourceMipLevels == 9 && decoded.Width == 512 && decoded.Height == 256 &&
             decoded.Format == 4 && decoded.NumberType == 7);
        if (!qualified) throw new NotSupportedException("Native storage physical placement has an unqualified ordinary writer class.");
        var description = ImageRequestBuilders.Texture(raw, shape).Request.Description;
        var mip = description.MipLayout[0];
        if (description.Data.Address != texture.Address || description.Data.Size == 0 || description.Data.Size > 0x10000000 ||
            description.Resources.Levels != texture.ResourceMipLevels || description.Resources.Layers != 1 ||
            description.Extent.Width != texture.Width || description.Extent.Height != texture.Height || description.Extent.Depth != 1 ||
            description.HasMetadata || description.HasStencil || description.IsVolume || description.IsBlock ||
            description.Samples != 1 || mip.Size < 8 || mip.Offset > description.Data.Size ||
            mip.Size > description.Data.Size - mip.Offset || texture.Address > ulong.MaxValue - description.Data.Size)
            throw new NotSupportedException("Native storage normalized complete write footprint is unknown or inconsistent.");
        return (texture.Address, description.Data.Size, checked(texture.Address + mip.Offset), mip.Size);
    }

    internal static PhysicalSpan[] WriteSpans(Observation original)
    {
        var mapping = original.Mapping;
        if (original.WriteBytes == 0 || original.WriteAddress < mapping.FootprintAddress ||
            original.WriteAddress - mapping.FootprintAddress > mapping.FootprintByteCount ||
            original.WriteBytes > mapping.FootprintByteCount - (original.WriteAddress - mapping.FootprintAddress))
            throw new InvalidOperationException("Native storage original write span escapes its original allocation.");
        var end = checked(original.WriteAddress + original.WriteBytes);
        var cursor = original.WriteAddress;
        var spans = new List<PhysicalSpan>(mapping.Segments.Count);
        foreach (var segment in mapping.Segments)
        {
            var first = Math.Max(cursor, segment.CoveredAddress);
            var last = Math.Min(end, checked(segment.CoveredAddress + segment.CoveredByteCount));
            if (first >= last) continue;
            if (first != cursor || first < segment.MappingAddress ||
                (segment.GuestAccess & (GuestPageProtection.Read | GuestPageProtection.Write)) !=
                    (GuestPageProtection.Read | GuestPageProtection.Write))
                throw new InvalidOperationException("Native storage original physical coverage/guest-write rights are incomplete.");
            var offset = checked(segment.MappingBackingOffset + first - segment.MappingAddress);
            var count = last - first;
            if (offset > segment.BackingLogicalByteCount || count > segment.BackingLogicalByteCount - offset)
                throw new InvalidOperationException("Native storage original write exceeds its original backing.");
            spans.Add(new(segment.BackingIdentity, offset, count));
            cursor = last;
        }
        if (cursor != end || spans.Count == 0)
            throw new InvalidOperationException("Native storage original physical write coverage has a gap.");
        return spans.ToArray();
    }

    internal static bool Overlap(PhysicalSpan a, PhysicalSpan b) => a.BackingIdentity == b.BackingIdentity &&
        (UInt128)a.Offset < (UInt128)b.Offset + b.ByteCount && (UInt128)b.Offset < (UInt128)a.Offset + a.ByteCount;
}
