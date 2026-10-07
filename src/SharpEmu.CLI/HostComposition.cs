// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE.Host;

namespace SharpEmu.CLI;

/// <summary>
/// Static composition of the host OS backend — the counterpart to
/// <see cref="GpuComposition"/>, using the same RID-to-OS convention.
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
///
/// Audio is the one host service supplied from here rather than from inside
/// the host package: SDL remains the qualified default on Windows and Linux,
/// and SharpEmu.Adapters.Sdl also owns the window and input and reaches into
/// SharpEmu.Libs, so a host package must not depend on it. macOS takes the
/// native CoreAudio output that SharpEmu.Host.MacOS owns.
/// </remarks>
internal static class HostComposition
{
    /// <summary>
    /// Registers the process's host OS backend. Must run before anything
    /// touches <see cref="HostPlatform"/> or <see cref="HostViewMemory"/> --
    /// that is, before emulation or the GUI starts.
    /// </summary>
    public static void Register()
    {
#if SHARPEMU_HOST_MACOS
        SharpEmu.Libs.Font.GuestFontServices.Initialize(new SharpEmu.Fonts.CoreTextMac.CoreTextFontFactory());
        HostPlatform.Initialize(static () => SharpEmu.Host.MacOS.MacOSHost.Create());
        HostViewMemory.Initialize(SharpEmu.Host.MacOS.MacOSHost.CreateViewMemory);
#elif SHARPEMU_HOST_LINUX
        SharpEmu.Libs.Font.GuestFontServices.Initialize(new SharpEmu.Fonts.Skia.SkiaFontFactory());
        HostPlatform.Initialize(static () =>
            SharpEmu.Host.Linux.LinuxHost.Create(new SharpEmu.Adapters.Sdl.Audio.SdlHostAudio()));
        HostViewMemory.Initialize(SharpEmu.Host.Linux.LinuxHost.CreateViewMemory);
#elif SHARPEMU_HOST_WINDOWS
        SharpEmu.Libs.Font.GuestFontServices.Initialize(new SharpEmu.Fonts.Skia.SkiaFontFactory());
        HostPlatform.Initialize(static () =>
            SharpEmu.Host.Windows.WindowsHost.Create(new SharpEmu.Adapters.Sdl.Audio.SdlHostAudio()));
        HostViewMemory.Initialize(SharpEmu.Host.Windows.WindowsHost.CreateViewMemory);
#else
#error No SHARPEMU_HOST_* symbol was defined. SharpEmu.CLI.csproj selects the host package from the target RuntimeIdentifier; an unrecognized RID leaves the application with no host OS backend.
#endif
    }
}
