// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.Versioning;
using SharpEmu.Libs.Pad;
using SharpEmu.Libs.VideoOut;

namespace SharpEmu.Libs.Platform.MacOS;

/// <summary>
/// Composition root of the native macOS platform package: AppKit window, GameController pads
/// and the CoreGraphics display catalog. The application calls <see cref="Register"/> once at
/// startup; nothing in the shared engine reaches for these types by name.
/// </summary>
[SupportedOSPlatform("macos")]
public static class MacPlatform
{
    public static void Register()
    {
        if (!OperatingSystem.IsMacOS())
        {
            throw new PlatformNotSupportedException(
                "SharpEmu.Platform.MacOS was composed into a process that is not running on macOS.");
        }

        HostDisplayCatalog.RegisterProvider("CoreGraphics", MacDisplayCatalog.TryQuery);
        LauncherGamepad.RegisterProvider(MacLauncherGamepadProvider.Instance);
    }
}
