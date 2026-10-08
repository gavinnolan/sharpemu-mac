// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
using SharpEmu.Libs.Gpu;
using SharpEmu.Libs.Gpu.Images;
using static SharpEmu.Libs.Agc.AgcBackendServices;
using TranslatedImageBinding = SharpEmu.Libs.Agc.AgcExports.TranslatedImageBinding;

namespace SharpEmu.Gpu.Metal;

internal static partial class MetalTextureSnapshots
{
    // Gen5 V_CUBE* has already selected face+UV before MIMG. Preserve that
    // complete face array, not a direction texture or a manufactured base face.
    private static bool RequireDepthCubeSnapshot(TranslatedImageBinding binding)
    {
        var d = binding.Descriptor;
        if (!binding.Shape.DepthCompare || d.Type != Gen5TextureTypeCube || d.Address == 0) return false;
        var s = binding.Shape;
        if (binding.IsStorage || !binding.IsArrayed || binding.Access != GuestImageAccess.Read ||
            !s.Cube || !s.Arrayed || s.Storage || s.Volume || s.OneDimensional || s.Multisampled || s.Atomic || s.R128 ||
            s.NumericClass != TextureNumericClass.Float || d.Width == 0 || d.Width != d.Height ||
            d.Depth < 6 || d.Depth % 6 != 0 || d.BaseArray != 0 || binding.MipLevel != 0 ||
            d.BaseLevel != 0 || d.LastLevel + 1 != d.ResourceMipLevels || !d.HasExtendedDescriptor ||
            d.ArrayPitch != 0 || d.MinLod != 0 || d.MinLodWarn != 0 || d.BcSwizzle != 0 ||
            d.MetadataAddress != 0 || d.DescriptorFlags != 0 ||
            !((d.Format == 2 && d.NumberType == 0) || (d.Format == 4 && d.NumberType == 7)) ||
            binding.ResourceDescriptor is not { Count: 8 } raw || !TryDecodeTextureDescriptor(raw, out var decoded) || decoded != d)
            throw new NotSupportedException("Metal depth cube snapshot requires a complete Float R32 or Unorm R16 square face array, full original mip view, base-layer zero and matching raw descriptor without metadata.");
        return true;
    }
}
