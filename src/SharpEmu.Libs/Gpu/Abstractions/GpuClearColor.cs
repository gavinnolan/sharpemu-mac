// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.InteropServices;

namespace SharpEmu.Libs.Gpu.Abstractions;

// One clear colour in the format the target expects, as four floats, four signed or four
// unsigned integers. The three readings overlap exactly as VkClearColorValue does, and the
// member names match VkClearColorValue's so the Vulkan backend copies the bits
// across unchanged.
[StructLayout(LayoutKind.Explicit, Size = 16)]
public struct GpuClearColor
{
    [FieldOffset(0)] public float Float32_0;
    [FieldOffset(4)] public float Float32_1;
    [FieldOffset(8)] public float Float32_2;
    [FieldOffset(12)] public float Float32_3;

    [FieldOffset(0)] public int Int32_0;
    [FieldOffset(4)] public int Int32_1;
    [FieldOffset(8)] public int Int32_2;
    [FieldOffset(12)] public int Int32_3;

    [FieldOffset(0)] public uint Uint32_0;
    [FieldOffset(4)] public uint Uint32_1;
    [FieldOffset(8)] public uint Uint32_2;
    [FieldOffset(12)] public uint Uint32_3;
}
