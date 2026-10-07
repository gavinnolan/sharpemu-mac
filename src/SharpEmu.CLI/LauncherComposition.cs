// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
namespace SharpEmu.CLI;

internal static class LauncherComposition
{
    public static int Run()
    {
#if SHARPEMU_HOST_MACOS
        return SharpEmu.Launcher.MacOS.MacLauncher.Run();
#else
        return SharpEmu.GUI.GuiLauncher.Run();
#endif
    }
}
