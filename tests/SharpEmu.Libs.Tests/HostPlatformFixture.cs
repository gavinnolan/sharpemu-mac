// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.CompilerServices;
using SharpEmu.HLE.Host;

namespace SharpEmu.Libs.Tests;

/// <summary>
/// Registers the host OS backend for the OS these tests run on, mirroring what
/// SharpEmu.CLI's HostComposition does for the application.
/// </summary>
/// <remarks>
/// A module initializer rather than an xunit fixture: host primitives are
/// reached through the process-wide <see cref="HostPlatform"/>/
/// <see cref="HostViewMemory"/> statics by static guest export classes, so the
/// registration cannot be scoped to a collection and must be in place before
/// the first test class is constructed.
/// </remarks>
internal static class HostPlatformFixture
{
    [ModuleInitializer]
    internal static void Initialize()
    {
        // Same audio output the application composes, so the tests exercise the
        // qualified default rather than each package's unqualified fallback.
#if SHARPEMU_HOST_MACOS
        HostMemoryAuthority.Initialize(SharpEmu.Host.MacOS.MacOSHost.CreateMemory);
        HostPlatform.Initialize(static () => SharpEmu.Host.MacOS.MacOSHost.Create());
        HostViewMemory.Initialize(SharpEmu.Host.MacOS.MacOSHost.CreateViewMemory);
#elif SHARPEMU_HOST_LINUX
        HostMemoryAuthority.Initialize(SharpEmu.Host.Linux.LinuxHost.CreateMemory);
        HostPlatform.Initialize(static () =>
            SharpEmu.Host.Linux.LinuxHost.Create(new SharpEmu.Adapters.Sdl.Audio.SdlHostAudio()));
        HostViewMemory.Initialize(SharpEmu.Host.Linux.LinuxHost.CreateViewMemory);
#elif SHARPEMU_HOST_WINDOWS
        HostMemoryAuthority.Initialize(SharpEmu.Host.Windows.WindowsHost.CreateMemory);
        HostPlatform.Initialize(static () =>
            SharpEmu.Host.Windows.WindowsHost.Create(new SharpEmu.Adapters.Sdl.Audio.SdlHostAudio()));
        HostViewMemory.Initialize(SharpEmu.Host.Windows.WindowsHost.CreateViewMemory);
#else
#error No SHARPEMU_HOST_* symbol was defined; see SharpEmu.Libs.Tests.csproj.
#endif
    }
}
