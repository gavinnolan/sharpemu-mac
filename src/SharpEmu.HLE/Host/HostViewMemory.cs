// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.HLE.Host;

/// <summary>
/// Process-wide factory for the host placeholder/view memory backend. Each
/// <see cref="Create"/> call returns a fresh instance: callers own their own
/// reservation bookkeeping.
/// </summary>
/// <remarks>
/// Registered separately from <see cref="HostPlatform"/> on purpose. View
/// memory is plain host virtual memory and is exercised by memory tests in
/// processes that cannot run guest code at all (a native ARM64 test host), so
/// it must not be gated behind a host platform that legitimately rejects such
/// a process.
/// </remarks>
public static class HostViewMemory
{
    private static Func<IHostViewMemory>? _factory;

    /// <summary>Registers the view-memory factory. Last registration wins.</summary>
    public static void Initialize(Func<IHostViewMemory> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        Volatile.Write(ref _factory, factory);
    }

    public static IHostViewMemory Create()
    {
        var factory = Volatile.Read(ref _factory) ??
            throw new InvalidOperationException(
                "No host view-memory backend has been registered. The application must call " +
                "HostViewMemory.Initialize(...) with the SharpEmu.Host.* backend for its target " +
                "OS before any guest work starts (see SharpEmu.CLI's HostComposition).");
        return factory();
    }

    internal static bool IsValidRange(ulong address, ulong size) =>
        address != 0 && size != 0 && size <= ulong.MaxValue - address;

    internal static bool IsValidOffset(HostBackingObject backing, ulong offset, ulong size, ulong pageSize) =>
        offset % pageSize == 0 && offset < backing.Size && size != 0 && size <= backing.Size - offset;
}
