// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE.Host;

namespace SharpEmu.Libs.Pad;

/// <summary>
/// One launcher-navigation gamepad implementation: GameController.framework on macOS, SDL on
/// Windows and Linux. The platform package registers its own, so this facade names neither.
/// </summary>
/// <remarks>
/// The provider is polled from the launcher's dispatcher thread, which GameController requires
/// (it must be touched from the main thread) and SDL tolerates. Output (rumble, lightbar,
/// adaptive triggers) is deliberately outside this contract — the launcher only reads
/// navigation input, and a running session owns the pad's outputs.
/// </remarks>
public interface ILauncherGamepadProvider
{
    void EnsureStarted();

    bool TryGetState(out HostGamepadState state);

    void Shutdown();
}

/// <summary>
/// Gamepad polling for launcher menu navigation, routed to whichever
/// <see cref="ILauncherGamepadProvider"/> the application composed in. With no provider
/// registered the launcher simply reports no pad.
/// </summary>
public static class LauncherGamepad
{
    private static readonly object Gate = new();
    private static ILauncherGamepadProvider? _provider;

    /// <summary>Installs the launcher's pad reader. Called once from the composition root.</summary>
    public static void RegisterProvider(ILauncherGamepadProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        lock (Gate)
        {
            _provider = provider;
        }
    }

    public static void EnsureStarted() => Provider?.EnsureStarted();

    public static bool TryGetState(out HostGamepadState state)
    {
        if (Provider is { } provider)
        {
            return provider.TryGetState(out state);
        }

        state = default;
        return false;
    }

    public static void Shutdown() => Provider?.Shutdown();

    private static ILauncherGamepadProvider? Provider
    {
        get
        {
            lock (Gate)
            {
                return _provider;
            }
        }
    }
}
