// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE.Host;
using SharpEmu.Libs.Platform.MacOS;

namespace SharpEmu.Libs.Pad;

/// <summary>
/// Gamepad polling for launcher menu navigation, routed to the platform's native
/// backend: GameController.framework on macOS, SDL everywhere else.
/// </summary>
/// <remarks>
/// GameController must be touched from the main thread, which for the Avalonia
/// launcher is the dispatcher thread its poll timer already ticks on. Output
/// (rumble, lightbar, adaptive triggers) is deliberately not wired up here — the
/// launcher only reads navigation input, and a running session owns the pad's
/// outputs.
/// </remarks>
public static class LauncherGamepad
{
    private static readonly object Gate = new();
    private static MacGameControllerGamepad? _macGamepad;

    public static void EnsureStarted()
    {
        if (!OperatingSystem.IsMacOS())
        {
            SdlLauncherGamepad.EnsureStarted();
            return;
        }

        lock (Gate)
        {
            _macGamepad ??= new MacGameControllerGamepad(outputEnabled: false);
        }
    }

    public static bool TryGetState(out HostGamepadState state)
    {
        if (!OperatingSystem.IsMacOS())
        {
            return SdlLauncherGamepad.TryGetState(out state);
        }

        MacGameControllerGamepad? gamepad;
        lock (Gate)
        {
            gamepad = _macGamepad;
        }

        if (gamepad is null)
        {
            state = default;
            return false;
        }

        return gamepad.TryReadState(out state);
    }

    public static void Shutdown()
    {
        if (!OperatingSystem.IsMacOS())
        {
            SdlLauncherGamepad.Shutdown();
            return;
        }

        MacGameControllerGamepad? gamepad;
        lock (Gate)
        {
            gamepad = _macGamepad;
            _macGamepad = null;
        }

        gamepad?.Dispose();
    }
}
