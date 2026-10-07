// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.Abstractions;

namespace SharpEmu.Libs.Gpu.Buffers;

// Answers whether the device can create an image with the given format and usage.
public interface IImageFormatSupport
{
    bool TryGetImageFormatProperties(GpuFormat format, GpuImageType type, GpuImageTiling tiling, GpuImageUsage usage, GpuImageCreateFlags flags, out GpuImageFormatProperties properties);
}
