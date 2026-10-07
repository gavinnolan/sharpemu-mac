// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE.Host;

namespace SharpEmu.CLI;

/// <summary>
/// The single place where the application picks its host OS backend.
/// </summary>
/// <remarks>
/// The OS host package is chosen at build time, not at run time: the csproj
/// references exactly one SharpEmu.Host.* project for the target
/// RuntimeIdentifier and defines the matching SHARPEMU_HOST_* symbol, so an
/// osx-* build never carries the Windows or Linux implementations (and vice
/// versa). Registration is deliberately lazy -- a host package rejects a
/// process it cannot run the guest in (a native ARM64 process), and that
/// diagnostic belongs at the point guest work starts, not at frontend launch,
/// so the GUI still opens on an unsupported process architecture.
/// </remarks>
internal static class HostComposition
{
    public static void Initialize()
    {
#if SHARPEMU_HOST_MACOS
        HostPlatform.Initialize(SharpEmu.Host.MacOS.MacOSHost.Create);
        HostViewMemory.Initialize(SharpEmu.Host.MacOS.MacOSHost.CreateViewMemory);
#elif SHARPEMU_HOST_LINUX
        HostPlatform.Initialize(SharpEmu.Host.Linux.LinuxHost.Create);
        HostViewMemory.Initialize(SharpEmu.Host.Linux.LinuxHost.CreateViewMemory);
#elif SHARPEMU_HOST_WINDOWS
        HostPlatform.Initialize(SharpEmu.Host.Windows.WindowsHost.Create);
        HostViewMemory.Initialize(SharpEmu.Host.Windows.WindowsHost.CreateViewMemory);
#else
#error No SHARPEMU_HOST_* symbol was defined. SharpEmu.CLI.csproj selects the host package from the target RuntimeIdentifier; an unrecognized RID leaves the application with no host OS backend.
#endif
    }
}
