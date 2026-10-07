// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Libs.VideoOut;

public sealed record HostDisplayMode(int Width, int Height, int RefreshRate);

public sealed record HostDisplayInfo(
    int Index,
    string Name,
    IReadOnlyList<HostDisplayMode> Modes);

/// <summary>
/// Enumerates the host's displays through the provider the application composed in:
/// CoreGraphics/AppKit on macOS, SDL on Windows and Linux. The platform package registers
/// its provider at startup, so this type carries no OS check and no windowing dependency —
/// which is also why a macOS process never spins up an SDL video subsystem just to list modes.
/// With no provider registered (unit tests, headless tools) the fallback mode list is returned.
/// </summary>
public static class HostDisplayCatalog
{
    private static int _queryFailureLogged;
    private static Func<IReadOnlyList<HostDisplayInfo>?>? _provider;
    private static string _providerName = "none";

    /// <summary>
    /// Installs the display enumerator. The provider returns null or an empty list when it
    /// cannot enumerate; the caller then gets <see cref="CreateFallback"/> and one warning.
    /// </summary>
    public static void RegisterProvider(string name, Func<IReadOnlyList<HostDisplayInfo>?> provider)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(provider);
        _providerName = name;
        _provider = provider;
    }

    public static IReadOnlyList<HostDisplayInfo> Query()
    {
        if (_provider is not { } provider)
        {
            return CreateFallback();
        }

        IReadOnlyList<HostDisplayInfo>? displays;
        try
        {
            displays = provider();
        }
        catch (Exception exception)
        {
            LogQueryFailure(exception.Message);
            return CreateFallback();
        }

        if (displays is not { Count: > 0 })
        {
            LogQueryFailure("the display provider reported no displays");
            return CreateFallback();
        }

        // A display whose mode list came back empty still has to offer something selectable.
        return displays
            .Select(display => display.Modes.Count == 0
                ? new HostDisplayInfo(display.Index, display.Name, CreateFallbackModes())
                : display)
            .ToArray();
    }

    internal static IReadOnlyList<HostDisplayInfo> CreateFallback() =>
        [new HostDisplayInfo(0, "Display 1", CreateFallbackModes())];

    internal static IReadOnlyList<HostDisplayMode> CreateFallbackModes() =>
        [
            new HostDisplayMode(3840, 2160, 60),
            new HostDisplayMode(2560, 1440, 60),
            new HostDisplayMode(1920, 1080, 60),
            new HostDisplayMode(1280, 720, 60),
        ];

    private static void LogQueryFailure(string message)
    {
        if (Interlocked.Exchange(ref _queryFailureLogged, 1) == 0)
        {
            Console.Error.WriteLine($"[GUI][WARN] {_providerName} display query failed: {message}");
        }
    }
}
