// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.Versioning;
using SharpEmu.HLE.Host;
using SharpEmu.Libs.Pad;

namespace SharpEmu.Platform.MacOS.Input;

/// <summary>
/// The macOS launcher pad: GameController.framework, read without the output path (no rumble,
/// lightbar or adaptive triggers) because a running session owns the pad's outputs.
/// </summary>
[SupportedOSPlatform("macos")]
public sealed class MacLauncherGamepadProvider : ILauncherGamepadProvider
{
    public static MacLauncherGamepadProvider Instance { get; } = new();

    private readonly object _gate = new();
    private MacGameControllerGamepad? _gamepad;

    public void EnsureStarted()
    {
        lock (_gate)
        {
            _gamepad ??= new MacGameControllerGamepad(outputEnabled: false);
        }
    }

    public bool TryGetState(out HostGamepadState state)
    {
        MacGameControllerGamepad? gamepad;
        lock (_gate)
        {
            gamepad = _gamepad;
        }

        if (gamepad is null)
        {
            state = default;
            return false;
        }

        return gamepad.TryReadState(out state);
    }

    public void Shutdown()
    {
        MacGameControllerGamepad? gamepad;
        lock (_gate)
        {
            gamepad = _gamepad;
            _gamepad = null;
        }

        gamepad?.Dispose();
    }
}
