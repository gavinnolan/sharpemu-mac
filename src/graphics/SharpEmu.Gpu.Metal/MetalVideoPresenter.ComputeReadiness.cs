// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
using System.Runtime.ExceptionServices;
using SharpEmu.Libs.Gpu;

namespace SharpEmu.Gpu.Metal;

internal static partial class MetalVideoPresenter
{
    // Default-null safe-side failure controls. They do not alter the native ABI,
    // dependency closure or supported dispatch capabilities.
    private static Func<nint, nint>? _computeBeginCommandsOverride = null;
    private static Func<nint, nint>? _computeEncoderCreationOverride = null;
    private static Func<nint, nint, GuestDrawTexture, (nint Handle, bool Owned)>? _computeTextureCreationOverride = null;
    private static Action<nint>? _computeTextureReleaseOverride = null;

    private static void ReleaseComputeTemporaryTextures(Span<nint> handles, Span<bool> owned, Exception? primaryFailure)
    {
        Exception? cleanupFailure = null;
        for (var index = 0; index < handles.Length; index++)
        {
            if (!owned[index] || handles[index] == 0) continue;
            var handle = handles[index];
            owned[index] = false;
            handles[index] = 0; // consume this call's retain before any throwing release
            try
            {
                if (_computeTextureReleaseOverride is { } release) release(handle);
                else MetalNative.SendVoid(handle, MetalNative.Selector("release"));
            }
            catch (Exception exception) { cleanupFailure ??= exception; }
        }
        if (cleanupFailure is null) return;
        if (primaryFailure is null) ExceptionDispatchInfo.Capture(cleanupFailure).Throw();
        // A cleanup diagnostic cannot replace the original dispatch failure or
        // stop remaining temporary retains from receiving one release attempt.
        lock (_gate) _textureSourceCleanupFailure ??= cleanupFailure;
    }
}
