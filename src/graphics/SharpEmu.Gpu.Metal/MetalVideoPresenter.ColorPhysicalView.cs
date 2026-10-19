// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
using SharpEmu.HLE;
using SharpEmu.Libs.Agc;
using SharpEmu.Libs.Gpu.Images;
using SharpEmu.Libs.Gpu;
using Binding = SharpEmu.Libs.Agc.AgcExports.TranslatedImageBinding;

namespace SharpEmu.Gpu.Metal;

internal static partial class MetalVideoPresenter
{
    // Original normalized placement only. Neither native retention nor this
    // memory reference supplies mapping/content authority. No eviction/rebase.
    private static readonly Dictionary<ulong, MetalColorMipPlacement.Observation> _colorPhysicalAllocations = new();

    private static MetalColorMipPlacement.Observation[] PrepareColorPhysicalAdmissionLocked(object work)
    {
        if (!MetalColorMipPlacement.Enabled || work is not OffscreenGuestDraw draw) return [];
        var prepared = new List<MetalColorMipPlacement.Observation>();
        foreach (var target in draw.Targets)
        {
            if (!MetalColorMipPlacement.TryGet(target, out var view)) continue;
            foreach (var prior in _colorPhysicalAllocations.Values.Concat(prepared))
                if (ColorPhysicalOverlap(prior.Registers.BaseAddress, prior.BackingByteCount,
                    view.Registers.BaseAddress, view.BackingByteCount) &&
                    (!ReferenceEquals(prior.Memory, view.Memory) || prior.Registers != view.Registers ||
                     prior.PhysicalAddress != view.PhysicalAddress || prior.ByteCount != view.ByteCount))
                    throw new NotSupportedException("Metal color physical allocation has a conflicting original placement/domain.");
            if (_textureSourceGraphEnabled)
                throw new NotSupportedException("Metal color physical placement does not authorize dormant graph ownership.");
            foreach (var pair in _guestImages)
            {
                if (pair.Key == target.Address) continue;
                if (!_guestImageExtents.TryGetValue(pair.Key, out var extent) || extent.ByteCount == 0 ||
                    ColorPhysicalOverlap(pair.Key, extent.ByteCount, view.PhysicalAddress, view.ByteCount))
                    throw new NotSupportedException("Metal selected color physical span conflicts with an unknown/overlapping native image.");
            }
            if (_guestImages.TryGetValue(target.Address, out var image))
                ValidateColorPhysicalNativeTarget(target, image, MtlPixelFormat.Rgba16Float);
            if (_pendingGuestImageInitialData.ContainsKey(target.Address))
                throw new NotSupportedException("Metal selected color mip cannot consume address-only initial pixels.");
            prepared.Add(view);
        }
        if (_colorPhysicalAllocations.Count + prepared.Count(v => !_colorPhysicalAllocations.ContainsKey(v.Registers.BaseAddress)) > 128)
            throw new NotSupportedException("Metal original color placement inventory is full; no address fallback is qualified.");
        _colorPhysicalAllocations.EnsureCapacity(_colorPhysicalAllocations.Count + prepared.Count);
        return prepared.ToArray();
    }

    private static void PublishColorPhysicalAdmissionLocked(MetalColorMipPlacement.Observation[] views)
    {
        foreach (var view in views)
            _colorPhysicalAllocations.TryAdd(view.Registers.BaseAddress, view); // Retain FIRST facts.
    }

    private static void RejectColorPhysicalAddressOnly(ulong address, ulong bytes = 1)
    {
        if (!MetalColorMipPlacement.Enabled) return;
        lock (_gate)
            foreach (var view in _colorPhysicalAllocations.Values)
                if (ColorPhysicalOverlap(address, Math.Max(bytes, 1UL), view.Registers.BaseAddress, view.BackingByteCount))
                    throw new NotSupportedException("Metal address-only mutation/display cannot select a view within a finite color mip allocation.");
    }

    private static bool ColorPhysicalOverlap(ulong a, ulong countA, ulong b, ulong countB) =>
        (UInt128)a < (UInt128)b + countB && (UInt128)b < (UInt128)a + countA;

    private static void ValidateColorPhysicalNativeTarget(SharpEmu.Libs.Gpu.GuestRenderTarget target,
        GuestImage image, MtlPixelFormat format)
    {
        if (!MetalColorMipPlacement.TryGet(target, out var view)) return;
        if (image.ColorPhysicalView is not { } first || !ReferenceEquals(first.Memory, view.Memory) ||
            first.Registers != view.Registers || first.PhysicalAddress != target.Address ||
            first.ByteCount != view.ByteCount || image.Width != 512 || image.Height != 512 ||
            image.Format != format || format != MtlPixelFormat.Rgba16Float ||
            image.TextureType != MtlTextureType2D || image.Depth != 1 || image.ArrayLength != 1)
            throw new NotSupportedException("Metal selected color mip native owner has incompatible original placement/format/type.");
    }

    // Before upload-known, captured-source or CPU-copy shortcuts. A complete
    // request proves placement; it does NOT prove fresh CPU bytes after a writer.
    internal static void ValidateColorPhysicalSnapshotBinding(ICpuMemory memory, Binding binding)
    {
        if (!MetalColorMipPlacement.Enabled || binding.Descriptor.Address == 0) return;
        lock (_gate)
        {
            if (_colorPhysicalAllocations.Count == 0) return;
            if (binding.ResourceDescriptor is not { Count: 8 } raw ||
                !AgcBackendServices.TryDecodeTextureDescriptor(raw, out var decoded) || decoded != binding.Descriptor)
                throw new NotSupportedException("Metal color physical overlap inspection needs complete matching original raw texture metadata.");
            Span<uint> words = stackalloc uint[8];
            for (var index = 0; index < 8; index++) words[index] = raw[index];
            var request = ImageRequestBuilders.Texture(words, binding.Shape).Request.Description;
            foreach (var view in _colorPhysicalAllocations.Values)
            {
                if (!ColorPhysicalOverlap(request.Data.Address, request.Data.Size,
                    view.Registers.BaseAddress, view.BackingByteCount)) continue;
                if (!ReferenceEquals(memory, view.Memory) || request.Data.Size == 0)
                    throw new NotSupportedException("Metal color physical view belongs to another original domain or lacks a complete range.");
                // Exact observed standalone tail, independently normalized to
                // base+[0,64KiB); it is not the color mip at base+0xB0000.
                var d = binding.Descriptor;
                if (d.Address == view.Registers.BaseAddress && raw[0] == (uint)(d.Address >> 8) &&
                    raw[1] == (0xC4700000u | (uint)((d.Address >> 40) & 0xFF)) &&
                    raw[2] == 0x000FC01F && raw[3] == 0x91B00FAC && raw[4] == 0 &&
                    raw[5] == 0x00700000 && raw[6] == 0 && raw[7] == 0 &&
                    !binding.DynamicMip && binding.MipLevel == 0 && binding.Dimension == 1 && !binding.IsArrayed &&
                    binding.Access != GuestImageAccess.Unknown &&
                    (binding.Access & ~(GuestImageAccess.Read | GuestImageAccess.Write)) == 0 &&
                    (binding.IsStorage || binding.Access == GuestImageAccess.Read) &&
                    binding.Shape.NumericClass == TextureNumericClass.Float && !binding.Shape.Volume &&
                    !binding.Shape.Arrayed && !binding.Shape.Cube && !binding.Shape.OneDimensional &&
                    !binding.Shape.Multisampled && !binding.Shape.DepthCompare && !binding.Shape.Atomic &&
                    !binding.Shape.R128 && !binding.Shape.DynamicMip && binding.Shape.Storage == binding.IsStorage &&
                    request.Resources.Levels == 1 && request.Resources.Layers == 1 && request.Pitch == 128 &&
                    request.BytesPerBlock == 8 && request.Extent.Width == 128 && request.Extent.Height == 64 &&
                    request.Data.Size == 0x10000 && request.MipLayout[0].Offset == 0 &&
                    request.MipLayout[0].Size == 0x10000 && !request.HasMetadata &&
                    !ColorPhysicalOverlap(d.Address, 0x10000, view.PhysicalAddress, view.ByteCount)) continue;
                // Do not recreate even the correctly placed selected mip from
                // stale CPU bytes. An exact immutable native borrow is separate.
                throw ColorPhysicalBindingFailure(view, request);
            }
        }
    }

    // Failure-only original placement and current inventory scalars. No new
    // provider/content read or native query; none of these fields admits a view.
    private static NotSupportedException ColorPhysicalBindingFailure(
        MetalColorMipPlacement.Observation original, ImageDescription request)
    {
        const string primary = "Metal binding overlaps a finite color backing allocation without a qualified disjoint typed view/native source receipt.";
        try
        {
            var imagePresent = _guestImages.TryGetValue(original.PhysicalAddress, out var image);
            return new NotSupportedException($"{primary} " +
                $"request_addr=0x{request.Data.Address:X} request_bytes={request.Data.Size} " +
                $"request_extent={request.Extent.Width}x{request.Extent.Height}x{request.Extent.Depth} " +
                $"request_pitch={request.Pitch} request_levels={request.Resources.Levels} request_layers={request.Resources.Layers} " +
                $"original_base=0x{original.Registers.BaseAddress:X} original_backing_bytes={original.BackingByteCount} " +
                $"original_selected=0x{original.PhysicalAddress:X} original_selected_bytes={original.ByteCount} " +
                $"selected_overlap={ColorPhysicalOverlap(request.Data.Address, request.Data.Size, original.PhysicalAddress, original.ByteCount)} " +
                $"native_present={imagePresent} native_initialized={image?.Initialized} native_written={image?.GpuWritten} " +
                $"native_version={image?.ContentVersion} native_width={image?.Width} native_height={image?.Height} " +
                $"native_type={image?.TextureType} native_format={image?.Format} " +
                $"native_original_placement_same={ReferenceEquals(image?.ColorPhysicalView, original)} pending_work={_pendingGuestWorkCount}");
        }
        catch (Exception) { return new NotSupportedException(primary); }
    }
}
