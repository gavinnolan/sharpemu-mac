// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
using SharpEmu.Libs.Gpu;

namespace SharpEmu.Gpu.Metal;

internal static partial class MetalVideoPresenter
{
    // Delegates isolate the ownership algorithm from Objective-C dispatch. Both
    // production creation paths use this same algorithm; there is no global
    // native override or alternate sampler implementation.
    private static nint CreateOwnedSamplerState(
        Func<nint> createDescriptor,
        Action<nint> configureDescriptor,
        Func<nint, nint> createState,
        Action<nint> release)
    {
        var descriptor = createDescriptor();
        if (descriptor == 0)
            throw new InvalidOperationException("Metal sampler descriptor creation returned nil.");

        nint ownedState = 0;
        try
        {
            try
            {
                configureDescriptor(descriptor);
                ownedState = createState(descriptor);
                if (ownedState == 0)
                    throw new InvalidOperationException("Metal sampler state creation returned nil.");
            }
            finally
            {
                release(descriptor);
            }

            var result = ownedState;
            ownedState = 0;
            return result;
        }
        finally
        {
            // Includes descriptor-release failure after successful newState.
            if (ownedState != 0) release(ownedState);
        }
    }

    private static nint CreateNativeSamplerDescriptor() => MetalNative.Send(
        MetalNative.Send(MetalNative.Class("MTLSamplerDescriptor"), MetalNative.Selector("alloc")),
        MetalNative.Selector("init"));

    private static void ReleaseOwnedSamplerObject(nint handle) =>
        MetalNative.SendVoid(handle, MetalNative.Selector("release"));

    private static nint PublishOwnedSamplerState(GuestSampler key, nint ownedState, Action<nint> release)
    {
        if (ownedState == 0)
            throw new InvalidOperationException("Metal cannot publish a nil sampler state.");
        try
        {
            lock (_samplerCache)
            {
                if (_samplerCache.TryGetValue(key, out var winner))
                {
                    if (winner == 0)
                        throw new InvalidOperationException("Metal sampler cache contains a nil state.");
                    return winner;
                }

                _samplerCache.Add(key, ownedState);
                var result = ownedState;
                ownedState = 0; // Exact accepted transfer to the cache.
                return result;
            }
        }
        finally
        {
            // A duplicate winner or failed dictionary publication leaves only
            // this new state's retain owned by the current call.
            if (ownedState != 0) release(ownedState);
        }
    }
}
