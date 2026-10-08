// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
using System.Runtime.ExceptionServices;

namespace SharpEmu.Gpu.Metal;

internal static partial class MetalVideoPresenter
{
    // Ordinary safe-side test seam, null in production. Never fault callbacks.
    private static Action<nint>? _r16SelectedTextureReleaseOverride = null;
    private static Exception? _r16SelectedRetainCleanupFailure;

    private static void ReleaseR16SelectedConstructionRetain(nint owned, Exception? primary)
    {
        if (owned == 0) return;
        try { ReleaseR16SelectedNativeRetain(owned); }
        catch (Exception cleanup)
        {
            lock (_gate) _r16SelectedRetainCleanupFailure ??= cleanup;
            if (primary is null) ExceptionDispatchInfo.Capture(cleanup).Throw();
            try { primary.Data["MetalR16SelectedSecondaryCleanupFailure"] = cleanup; }
            catch { /* Diagnostics must not replace the construction failure. */ }
        }
    }

    private static void ReleaseR16SelectedNativeRetain(nint owned)
    {
        if (_r16SelectedTextureReleaseOverride is { } release) release(owned);
        else MetalNative.SendVoid(owned, MetalNative.Selector("release"));
    }

    private static void ReleaseAcceptedR16SelectedRetain(GuestImage image)
    {
        if (!image.R16SelectedOwnsRetain) return;
        image.R16SelectedOwnsRetain = false;
        var owned = image.Texture; image.Texture = 0;
        // Consume accepted authority before any callback/reentry; an uncertain
        // release cannot be rediscovered by an address or retried on next close.
        if (owned == 0) return;
        try { ReleaseR16SelectedNativeRetain(owned); }
        catch (Exception cleanup) { _r16SelectedRetainCleanupFailure ??= cleanup; }
    }
}
