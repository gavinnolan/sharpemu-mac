// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Gpu.Metal.V2;

/// <summary>
/// An Objective-C autorelease pool scoped to a <c>using</c>. A thread the runtime did not
/// start through AppKit has no pool of its own, so everything Metal autoreleases on it
/// (command buffers and encoders, descriptors and strings made by class factories, the
/// driver's own temporaries) is never freed. Every loop that calls into Metal on such a
/// thread opens one of these around each iteration.
/// </summary>
/// <remarks>
/// Ownership rule: an object obtained from a factory that autoreleases it is valid only
/// until the pool drains. One that has to outlive the scope (an encoder held open across
/// calls, a command buffer kept until it completes) is retained when it is obtained and
/// released exactly once by its owner. Pools nest; a pool pops on the thread that pushed it.
/// </remarks>
internal readonly struct MetalAutoreleasePool : IDisposable
{
    private readonly nint _pool;

    private MetalAutoreleasePool(nint pool) => _pool = pool;

    public static MetalAutoreleasePool Enter() => new(MetalNative.objc_autoreleasePoolPush());

    public void Dispose() => MetalNative.objc_autoreleasePoolPop(_pool);
}
