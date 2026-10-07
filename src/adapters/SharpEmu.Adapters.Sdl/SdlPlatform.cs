// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Pad;
using SharpEmu.Adapters.Sdl.Display;
using SharpEmu.Adapters.Sdl.Input;

namespace SharpEmu.Adapters.Sdl;

/// <summary>
/// Composition root of the SDL adapter package: the SDL window/event loop, SDL gamepads and
/// the SDL display catalog, used on Windows and Linux. The macOS build composes
/// <c>SharpEmu.Platform.MacOS</c> instead and never links this package.
/// </summary>
public static class SdlPlatform
{
    public static void Register()
    {
        SdlDisplayCatalog.Register();
        LauncherGamepad.RegisterProvider(SdlLauncherGamepadProvider.Instance);
    }
}
