// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
using SharpEmu.Libs.Gpu.Images;

namespace SharpEmu.Gpu.Metal;

// Native capability admission is narrower than the host-neutral guest equation.
internal static class MetalVolumeStorageAdmission
{
    internal static bool IsQualified(ulong address, uint width, uint height, uint depth,
        uint format, uint numberType, uint type, uint tileMode, uint resourceMipLevels, uint dstSelect) =>
        address != 0 && (address & 4095) == 0 && Gfx10StandardVolumeLayout.IsSupported(width, height, depth, 16, tileMode, resourceMipLevels) &&
        format == 2 && numberType == 4 && type == 10 && dstSelect == 4;

    // Pin all non-address raw fields from the observed T#; unknown/reserved
    // fields must not silently widen the native qualification.
    internal static bool HasQualifiedRawFields(IReadOnlyList<uint> raw) =>
        raw.Count >= 8 && (raw[1] & 0xFFFF_FF00u) == 0xC0B0_0000 &&
        raw[2] == 0x0003_C003 && raw[3] == 0xA050_0004 && raw[4] == 0x0000_000F &&
        raw[5] == 0x0070_0000 && raw[6] == 0 && raw[7] == 0;
}
