// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.HLE.GpuMemory;
using SharpEmu.Libs.Kernel;

namespace SharpEmu.Libs.Gpu;

/// <summary>Reads physical guest storage for backend resource materialization.</summary>
public static class GuestBackingServices
{
    public static bool TryReadPrtBacking(IGuestBackedSpace backing, ulong address, Span<byte> destination) =>
        KernelMemoryCompatExports.TryReadPrtBacking(backing, address, destination);
}
