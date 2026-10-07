// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE.Host;
using SharpEmu.Libs.Pad;

namespace SharpEmu.Adapters.Sdl.Input;

/// <summary>Adapts the SDL launcher pad to <see cref="ILauncherGamepadProvider"/>.</summary>
public sealed class SdlLauncherGamepadProvider : ILauncherGamepadProvider
{
    public static SdlLauncherGamepadProvider Instance { get; } = new();

    public void EnsureStarted() => SdlLauncherGamepad.EnsureStarted();

    public bool TryGetState(out HostGamepadState state) => SdlLauncherGamepad.TryGetState(out state);

    public void Shutdown() => SdlLauncherGamepad.Shutdown();
}
