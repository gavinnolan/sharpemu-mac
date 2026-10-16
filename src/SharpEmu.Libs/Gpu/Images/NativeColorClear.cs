// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.Abstractions;

namespace SharpEmu.Libs.Gpu.Images;

public static class NativeColorClear
{
    public static ulong CmaskSliceSize(uint width, uint height) => width == 0 || height == 0 ? 0 :
        ((width + 1023UL) / 1024) * ((height + 511UL) / 512) * 4096;

    public static ulong SliceSize(uint width, uint height, uint bytesPerPixel)
    {
        var block = bytesPerPixel switch
        {
            1 => (Width: 1024u, Height: 1024u),
            2 => (Width: 1024u, Height: 512u),
            4 => (Width: 512u, Height: 512u),
            8 => (Width: 512u, Height: 256u),
            16 => (Width: 256u, Height: 256u),
            _ => (Width: 0u, Height: 0u),
        };
        if (width == 0 || height == 0 || block.Width == 0) return 0;
        return ((width + (ulong)block.Width - 1) / block.Width) *
            ((height + (ulong)block.Height - 1) / block.Height) * 4096;
    }

    public static bool TryDecode(byte code, GpuFormat format, bool alphaOnLeastSignificantBits, out GpuClearColor color)
    {
        color = default;
        if (code is not (0x00 or 0x40 or 0x80 or 0xc0)) return false;
        var rgb = (code & 0x80) != 0 ? 1f : 0f;
        var alpha = (code & 0x40) != 0 ? 1f : 0f;
        var first = alphaOnLeastSignificantBits ? alpha : rgb;
        var last = alphaOnLeastSignificantBits ? rgb : alpha;
        GpuClearColor? decoded = format switch
        {
            GpuFormat.R8Unorm or GpuFormat.R16Unorm or GpuFormat.R16Sfloat or GpuFormat.R32Sfloat =>
                Color(last, rgb, rgb, last),
            GpuFormat.R8G8Unorm or GpuFormat.R16G16Unorm or GpuFormat.R16G16Sfloat or GpuFormat.R32G32Sfloat =>
                Color(first, last, rgb, last),
            GpuFormat.B8G8R8A8Unorm or GpuFormat.B8G8R8A8Srgb or GpuFormat.A2R10G10B10UnormPack32 or
                GpuFormat.A1R5G5B5UnormPack16 or GpuFormat.R5G6B5UnormPack16 =>
                Color(rgb, rgb, first, last),
            GpuFormat.R4G4B4A4UnormPack16 => Color(last, rgb, rgb, first),
            GpuFormat.R8G8B8A8Unorm or GpuFormat.R8G8B8A8Srgb or GpuFormat.A2B10G10R10UnormPack32 or
                GpuFormat.R16G16B16A16Unorm or GpuFormat.R16G16B16A16Sfloat or GpuFormat.R32G32B32A32Sfloat or
                GpuFormat.B10G11R11UfloatPack32 => Color(first, rgb, rgb, last),
            _ => null,
        };
        color = decoded.GetValueOrDefault();
        return decoded.HasValue;
    }

    private static GpuClearColor Color(float red, float green, float blue, float alpha) => new()
    {
        Float32_0 = red,
        Float32_1 = green,
        Float32_2 = blue,
        Float32_3 = alpha,
    };

    public static bool IsUniform(ReadOnlySpan<byte> bytes, byte code) => !bytes.IsEmpty && bytes.IndexOfAnyExcept(code) < 0;
}
