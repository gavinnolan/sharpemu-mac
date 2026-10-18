// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.HLE.Host;

/// <summary>
/// Routes emulated input through the active cross-platform host window.
/// </summary>
/// <remarks>
/// Host-package contract: every <c>SharpEmu.Host.*</c> package returns this as
/// its <see cref="IHostPlatform.Input"/>, because the window - not the OS host
/// layer - is what owns device discovery and the input pump.
/// </remarks>
public sealed class WindowHostInput : IHostInput
{
    public void EnsureStarted()
    {
        // SDL owns device discovery and pumps it on the window thread.
    }

    public int GetGamepadStates(Span<HostGamepadState> destination) =>
        HostWindowInputSource.Current?.GetGamepadStates(destination) ?? 0;

    public string? DescribeConnectedGamepad() =>
        HostWindowInputSource.Current?.DescribeConnectedGamepad();

    public void SetRumble(byte largeMotor, byte smallMotor) =>
        HostWindowInputSource.Current?.SetRumble(largeMotor, smallMotor);

    public void SetTriggerRumble(byte? leftTrigger, byte? rightTrigger) =>
        HostWindowInputSource.Current?.SetTriggerRumble(leftTrigger, rightTrigger);

    public void SetAdaptiveTriggerEffect(
        HostAdaptiveTriggerEffect? leftTrigger,
        HostAdaptiveTriggerEffect? rightTrigger) =>
        HostWindowInputSource.Current?.SetAdaptiveTriggerEffect(leftTrigger, rightTrigger);

    public void SetLightbar(byte red, byte green, byte blue) =>
        HostWindowInputSource.Current?.SetLightbar(red, green, blue);

    public void ResetLightbar() => HostWindowInputSource.Current?.ResetLightbar();

    public bool IsHostWindowFocused() =>
        (HostWindowInputSource.Current?.HasKeyboardFocus ?? false) ||
        (ScriptedKeyInput.IsEnabled && ScriptedKeyInput.AnyHeld);

    public bool IsKeyDown(int virtualKey) =>
        (ScriptedKeyInput.IsEnabled && ScriptedKeyInput.IsHeld(virtualKey)) ||
        (HostWindowInputSource.Current?.IsKeyDown(virtualKey) ?? false);
}
