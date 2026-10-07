// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE.Host;

namespace SharpEmu.Libs.Tests;

/// <summary>
/// The one place in the test assembly that names concrete host backend types.
/// </summary>
/// <remarks>
/// Only the SharpEmu.Host.* package for the build host OS is referenced (see
/// SharpEmu.Libs.Tests.csproj), so these are compile-time selections rather
/// than OperatingSystem checks. The backend internals reached here are the
/// test-only failure-injection hooks and the raw memory backend, both covered
/// by each host package's InternalsVisibleTo for this assembly.
/// </remarks>
internal static class HostBackendUnderTest
{
    /// <summary>
    /// A fresh raw host memory backend. Unlike <see cref="HostPlatform.Current"/>
    /// this is not gated on the process architecture: host virtual memory works
    /// in a process that cannot execute the x86-64 guest, and the memory tests
    /// must still run there.
    /// </summary>
    public static IHostMemory CreateMemory() =>
#if SHARPEMU_HOST_MACOS
        new SharpEmu.Host.MacOS.MacHostMemory();
#elif SHARPEMU_HOST_LINUX
        new SharpEmu.Host.Linux.LinuxHostMemory();
#elif SHARPEMU_HOST_WINDOWS
        new SharpEmu.Host.Windows.WindowsHostMemory();
#endif

    /// <summary>Forces the backing-object alias mapping to fail.</summary>
    public static void SetViewAliasFailure(bool fail)
    {
#if SHARPEMU_HOST_MACOS
        SharpEmu.Host.MacOS.MacHostViews.FailAliasMapForTests = fail;
#elif SHARPEMU_HOST_LINUX
        SharpEmu.Host.Linux.LinuxHostViews.FailAliasMapForTests = fail;
#elif SHARPEMU_HOST_WINDOWS
        SharpEmu.Host.Windows.WindowsHostViews.FailAliasMapForTests = fail;
#endif
    }

    /// <summary>
    /// Picks the guest backing for backings this thread creates: true the chunked purgeable
    /// backing, false the single object, null whatever SHARPEMU_GUEST_BACKING says. Only macOS
    /// has a chunked backing; false when the host has none.
    /// </summary>
    public static bool SetChunkedBacking(bool? chunked)
    {
#if SHARPEMU_HOST_MACOS
        SharpEmu.Host.MacOS.MacHostViews.ChunkedBackingForTests = chunked;
        return true;
#else
        _ = chunked;
        return false;
#endif
    }

    /// <summary>
    /// Forces the placeholder protection step to fail. Windows-only: the
    /// POSIX backends have no separate protect step inside TryMapView.
    /// </summary>
    public static void SetViewProtectFailure(bool fail)
    {
#if SHARPEMU_HOST_WINDOWS
        SharpEmu.Host.Windows.WindowsHostViews.FailProtectForTests = fail;
#else
        _ = fail;
#endif
    }
}
