// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SDL;
using static SDL.SDL3;

namespace SharpEmu.Libs.VideoOut;

/// <summary>
/// The SDL display enumerator behind <see cref="HostDisplayCatalog"/> on Windows and Linux.
/// It initializes the SDL video subsystem only if nothing else already did, and quits it
/// again on the way out, so listing modes never leaves a video subsystem behind.
/// </summary>
public static unsafe class SdlDisplayCatalog
{
    private const SDL_InitFlags VideoFlag = SDL_InitFlags.SDL_INIT_VIDEO;

    /// <summary>Registers this enumerator as the process's display provider.</summary>
    public static void Register() => HostDisplayCatalog.RegisterProvider("SDL", TryQuery);

    public static IReadOnlyList<HostDisplayInfo>? TryQuery()
    {
        var initializedHere = false;
        var videoReady = false;
        try
        {
            initializedHere = (SDL_WasInit(VideoFlag) & VideoFlag) == 0;
            if (initializedHere && !SDL_InitSubSystem(VideoFlag))
            {
                throw new InvalidOperationException(SDL_GetError() ?? "unknown SDL error");
            }
            videoReady = true;

            using var displays = SDL_GetDisplays();
            if (displays is null || displays.Count == 0)
            {
                return null;
            }

            var result = new List<HostDisplayInfo>(displays.Count);
            for (var index = 0; index < displays.Count; index++)
            {
                var display = displays[index];
                var name = SDL_GetDisplayName(display);
                var modes = ReadModes(display);
                result.Add(new HostDisplayInfo(
                    index,
                    string.IsNullOrWhiteSpace(name) ? $"Display {index + 1}" : name,
                    modes));
            }

            return result;
        }
        finally
        {
            if (initializedHere && videoReady)
            {
                SDL_QuitSubSystem(VideoFlag);
            }
        }
    }

    private static IReadOnlyList<HostDisplayMode> ReadModes(SDL_DisplayID display)
    {
        var modes = new HashSet<HostDisplayMode>();
        using (var fullscreenModes = SDL_GetFullscreenDisplayModes(display))
        {
            if (fullscreenModes is not null)
            {
                for (var index = 0; index < fullscreenModes.Count; index++)
                {
                    var mode = fullscreenModes[index];
                    AddMode(modes, &mode);
                }
            }
        }

        AddMode(modes, SDL_GetDesktopDisplayMode(display));
        if (modes.Count == 0)
        {
            // An empty list makes HostDisplayCatalog substitute its own fallback modes.
            return [];
        }

        return modes
            .OrderByDescending(mode => (long)mode.Width * mode.Height)
            .ThenByDescending(mode => mode.Width)
            .ThenByDescending(mode => mode.RefreshRate)
            .ToArray();
    }

    private static void AddMode(HashSet<HostDisplayMode> modes, SDL_DisplayMode* mode)
    {
        if (mode is null || mode->w <= 0 || mode->h <= 0)
        {
            return;
        }

        var refreshRate = mode->refresh_rate > 0
            ? Math.Max(1, (int)Math.Round(mode->refresh_rate, MidpointRounding.AwayFromZero))
            : 0;
        modes.Add(new HostDisplayMode(mode->w, mode->h, refreshRate));
    }
}
