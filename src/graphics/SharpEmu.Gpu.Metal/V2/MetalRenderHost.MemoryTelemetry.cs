// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Globalization;
using SharpEmu.Logging;

namespace SharpEmu.Gpu.Metal.V2;

internal sealed partial class MetalRenderHost
{
    private static readonly nint SelRecommendedMaxWorkingSetSize = MetalNative.Selector("recommendedMaxWorkingSetSize");
    private static readonly nint SelDeviceCurrentAllocatedSize = MetalNative.Selector("currentAllocatedSize");

    // Windows an encoder named because a device-address program may reach them, for
    // the command buffer being recorded; folded into the per-command-buffer census at commit.
    private readonly MetalWindowSet _commandBufferReachWindows = new();

    // SHARPEMU_LOG_MEMORY=1: the "metal", "windows" and "gpu-refs" sections of the [MEM] lines.
    private void RegisterMemoryTelemetry()
    {
        if (!MemoryTelemetry.Enabled)
        {
            return;
        }

        MemoryTelemetry.Register("metal", DescribeMetalMemory);
        MemoryTelemetry.Register("windows", () => MetalMemoryTelemetry.TakeWindowReport(_guestMemory));
        MemoryTelemetry.Register("gpu-refs", MetalGpuReferenceCensus.TakeReport);
    }

    private string DescribeMetalMemory()
    {
        const double Mb = 1048576.0;
        var recommended = _device == 0 ? 0UL : MetalV2Native.SendValue64(_device, SelRecommendedMaxWorkingSetSize);
        var allocated = _device == 0 ? 0UL : MetalV2Native.SendValue64(_device, SelDeviceCurrentAllocatedSize);
        return string.Create(CultureInfo.InvariantCulture,
            $"recommended_working_set_mb={recommended / Mb:F0} device_allocated_mb={allocated / Mb:F0} " +
            $"ring_mb={_guestMemory.RingBytes / Mb:F0} {_imageCache.DescribeMemoryCensus()}");
    }

    // Called with the command buffer about to be committed.
    private void NoteCommandBufferForTelemetry()
    {
        if (!MemoryTelemetry.Enabled)
        {
            return;
        }

        MetalMemoryTelemetry.NoteCommit(_guestMemory, _commandBufferReachWindows);
        _commandBufferReachWindows.Clear();
    }
}
