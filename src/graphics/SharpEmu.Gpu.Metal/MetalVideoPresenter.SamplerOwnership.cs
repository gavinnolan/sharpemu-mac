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
        Exception? failure = null, descriptorCleanupFailure = null, stateCleanupFailure = null;
        try
        {
            configureDescriptor(descriptor);
            ownedState = createState(descriptor);
            if (ownedState == 0)
                throw new InvalidOperationException("Metal sampler state creation returned nil.");
        }
        catch (Exception ex) { failure = ex; }
        try { release(descriptor); }
        catch (Exception ex) { descriptorCleanupFailure = ex; failure ??= ex; }
        if (failure is not null)
        {
            // Includes descriptor-release failure after successful newState.
            if (ownedState != 0)
            {
                try { release(ownedState); }
                catch (Exception ex) { stateCleanupFailure = ex; }
            }
            ThrowSamplerFailure(failure, descriptorCleanupFailure, stateCleanupFailure);
        }
        return ownedState;
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
        nint result = 0;
        Exception? failure = null, stateCleanupFailure = null;
        try
        {
            lock (_samplerCache)
            {
                if (_samplerCache.TryGetValue(key, out var winner))
                {
                    if (winner == 0)
                        throw new InvalidOperationException("Metal sampler cache contains a nil state.");
                    result = winner;
                }
                else
                {
                    _samplerCache.Add(key, ownedState);
                    result = ownedState;
                    ownedState = 0; // Exact accepted transfer to the cache.
                }
            }
        }
        catch (Exception ex) { failure = ex; }
        // A duplicate winner or failed dictionary publication leaves only
        // this new state's retain owned by the current call.
        if (ownedState != 0)
        {
            try { release(ownedState); }
            catch (Exception ex) { stateCleanupFailure = ex; failure ??= ex; }
        }
        if (failure is not null) ThrowSamplerFailure(failure, null, stateCleanupFailure);
        return result;
    }

    private static void ThrowSamplerFailure(Exception failure, Exception? descriptorCleanupFailure, Exception? stateCleanupFailure)
    {
        // Preserve the first cause and stack. Diagnostics follow all owned
        // cleanup attempts, so recording a secondary cause cannot skip one.
        if (descriptorCleanupFailure is not null && !ReferenceEquals(failure, descriptorCleanupFailure))
            failure.Data["Metal sampler descriptor cleanup failure"] = descriptorCleanupFailure;
        if (stateCleanupFailure is not null && !ReferenceEquals(failure, stateCleanupFailure))
            failure.Data["Metal sampler state cleanup failure"] = stateCleanupFailure;
        System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
