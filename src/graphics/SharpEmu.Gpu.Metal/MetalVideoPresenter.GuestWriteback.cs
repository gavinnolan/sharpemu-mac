// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
using SharpEmu.Libs.Gpu;

namespace SharpEmu.Gpu.Metal;

internal static partial class MetalVideoPresenter
{
    // Safe-side managed controls only; null uses the actual Metal command buffer.
    // Managed tests do not qualify native completion or resource lifetimes.
    private static Func<nint, nint>? _guestCommandBufferStatusOverride = null;
    private static Action<nint>? _guestCommandBufferWaitOverride = null;

    private static void WaitForCommittedGuestBufferWriteback(nint commandBuffer)
    {
        if (commandBuffer == 0)
            throw new InvalidOperationException("Metal GPU buffer writeback has no committed command buffer.");
        WaitForCommittedCommandBuffer(commandBuffer);
    }

    private static nint ReadGuestCommandBufferStatus(nint commandBuffer) =>
        _guestCommandBufferStatusOverride is { } status
            ? status(commandBuffer)
            : MetalNative.Send(commandBuffer, MetalNative.Selector("status"));

    private static InvalidOperationException GuestCommandBufferCompletionFailure(nint commandBuffer, nint status)
    {
        // Never query a synthetic managed-control handle through Objective-C.
        var error = "no native error queried";
        if (_guestCommandBufferStatusOverride is null && status == 5)
        {
            try { error = MetalNative.DescribeError(MetalNative.Send(commandBuffer, MetalNative.Selector("error"))); }
            catch (Exception) { error = "native error description unavailable"; }
        }
        return new InvalidOperationException(
            $"Metal guest command buffer did not complete successfully: status={status} error='{error}'.");
    }

    private static void ValidateGuestBufferWritebackUpload(GuestMemoryBuffer guest)
    {
        // The writeback pointer is an upload slice, so its declared extent must
        // not exceed the bytes allocated/copied. GDS uses a separately owned
        // exact-size slice and does not pass through this upload path.
        if (guest.Writable && guest.WriteBackToGuest &&
            (guest.Length < 0 || guest.Length > guest.Data.Length))
            throw new InvalidOperationException(
                $"Metal writable guest buffer snapshot is incomplete: address=0x{guest.BaseAddress:X16} bytes={guest.Length} snapshot_bytes={guest.Data.Length}.");
    }
}
