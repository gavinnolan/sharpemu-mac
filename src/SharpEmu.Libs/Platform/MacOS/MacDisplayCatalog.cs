// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using SharpEmu.Libs.VideoOut;

namespace SharpEmu.Libs.Platform.MacOS;

/// <summary>
/// Display and mode enumeration through CoreGraphics, with monitor names from
/// AppKit. Replaces the SDL video subsystem for the settings UI so no macOS
/// process ever has to initialize SDL just to list resolutions.
/// </summary>
/// <remarks>
/// Modes come from <c>CGDisplayCopyAllDisplayModes</c>, which is thread safe.
/// Names come from <c>-[NSScreen localizedName]</c>, which is not: NSScreen must
/// be touched on the main thread, so the lookup is skipped (falling back to
/// "Display N") when the query arrives from a background thread.
/// </remarks>
[SupportedOSPlatform("macos")]
internal static partial class MacDisplayCatalog
{
    private const int MaxDisplays = 16;

    private const string CoreFoundationFramework =
        "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";

    [LibraryImport(MacObjC.CoreGraphicsFramework)]
    private static unsafe partial int CGGetActiveDisplayList(
        uint maxDisplays,
        uint* activeDisplays,
        out uint displayCount);

    [LibraryImport(MacObjC.CoreGraphicsFramework)]
    private static partial nint CGDisplayCopyAllDisplayModes(uint display, nint options);

    [LibraryImport(MacObjC.CoreGraphicsFramework)]
    private static partial nint CGDisplayCopyDisplayMode(uint display);

    [LibraryImport(MacObjC.CoreGraphicsFramework)]
    private static partial nuint CGDisplayModeGetWidth(nint mode);

    [LibraryImport(MacObjC.CoreGraphicsFramework)]
    private static partial nuint CGDisplayModeGetHeight(nint mode);

    [LibraryImport(MacObjC.CoreGraphicsFramework)]
    private static partial double CGDisplayModeGetRefreshRate(nint mode);

    [LibraryImport(MacObjC.CoreGraphicsFramework)]
    private static partial void CGDisplayModeRelease(nint mode);

    [LibraryImport(CoreFoundationFramework)]
    private static partial nint CFArrayGetCount(nint array);

    [LibraryImport(CoreFoundationFramework)]
    private static partial nint CFArrayGetValueAtIndex(nint array, nint index);

    [LibraryImport(CoreFoundationFramework)]
    private static partial void CFRelease(nint value);

    public static unsafe IReadOnlyList<HostDisplayInfo>? TryQuery()
    {
        try
        {
            var identifiers = stackalloc uint[MaxDisplays];
            if (CGGetActiveDisplayList(MaxDisplays, identifiers, out var count) != 0 || count == 0)
            {
                return null;
            }

            var displayCount = (int)Math.Min(count, MaxDisplays);
            var names = ReadScreenNames();
            var result = new List<HostDisplayInfo>(displayCount);
            for (var index = 0; index < displayCount; index++)
            {
                var display = identifiers[index];
                result.Add(new HostDisplayInfo(
                    index,
                    names.TryGetValue(display, out var name) && !string.IsNullOrWhiteSpace(name)
                        ? name
                        : $"Display {index + 1}",
                    ReadModes(display)));
            }

            return result;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"[GUI][WARN] CoreGraphics display query failed: {exception.Message}");
            return null;
        }
    }

    private static IReadOnlyList<HostDisplayMode> ReadModes(uint display)
    {
        var modes = new HashSet<HostDisplayMode>();
        var all = CGDisplayCopyAllDisplayModes(display, 0);
        if (all != 0)
        {
            try
            {
                var count = CFArrayGetCount(all);
                for (nint index = 0; index < count; index++)
                {
                    AddMode(modes, CFArrayGetValueAtIndex(all, index));
                }
            }
            finally
            {
                CFRelease(all);
            }
        }

        var current = CGDisplayCopyDisplayMode(display);
        if (current != 0)
        {
            try
            {
                AddMode(modes, current);
            }
            finally
            {
                CGDisplayModeRelease(current);
            }
        }

        return modes.Count == 0
            ? []
            : modes
                .OrderByDescending(mode => (long)mode.Width * mode.Height)
                .ThenByDescending(mode => mode.Width)
                .ThenByDescending(mode => mode.RefreshRate)
                .ToArray();
    }

    private static void AddMode(HashSet<HostDisplayMode> modes, nint mode)
    {
        if (mode == 0)
        {
            return;
        }

        var converted = Convert(
            (int)CGDisplayModeGetWidth(mode),
            (int)CGDisplayModeGetHeight(mode),
            CGDisplayModeGetRefreshRate(mode));
        if (converted is { } value)
        {
            modes.Add(value);
        }
    }

    /// <summary>
    /// CGDisplayMode geometry to <see cref="HostDisplayMode"/>. Built-in panels
    /// report a refresh rate of 0, which the catalog's consumers already read as
    /// "use the display default", so it is preserved rather than invented.
    /// Pure — unit tested.
    /// </summary>
    internal static HostDisplayMode? Convert(int width, int height, double refreshRate)
    {
        if (width <= 0 || height <= 0)
        {
            return null;
        }

        var rounded = refreshRate > 0
            ? Math.Max(1, (int)Math.Round(refreshRate, MidpointRounding.AwayFromZero))
            : 0;
        return new HostDisplayMode(width, height, rounded);
    }

    /// <summary>
    /// CGDirectDisplayID to localized monitor name, via NSScreen's
    /// deviceDescription["NSScreenNumber"].
    /// </summary>
    private static Dictionary<uint, string> ReadScreenNames()
    {
        var names = new Dictionary<uint, string>();
        // The caller may be the launcher, which has not touched AppKit through
        // this path yet; without the framework loaded objc_getClass("NSScreen")
        // returns nil and every display silently degrades to "Display N".
        if (!NativeLibrary.TryLoad(MacObjC.AppKitFramework, out _))
        {
            return names;
        }

        var threadClass = MacObjC.Class("NSThread");
        if (threadClass == 0 ||
            !MacObjC.SendBool(threadClass, MacObjC.Selector("isMainThread")))
        {
            return names;
        }

        var pool = MacObjC.objc_autoreleasePoolPush();
        try
        {
            var screens = MacObjC.Send(MacObjC.Class("NSScreen"), MacObjC.Selector("screens"));
            var count = MacObjC.ArrayCount(screens);
            for (nuint index = 0; index < count; index++)
            {
                var screen = MacObjC.ArrayObjectAt(screens, index);
                var description = MacObjC.Send(screen, MacObjC.Selector("deviceDescription"));
                if (description == 0)
                {
                    continue;
                }

                var number = MacObjC.Send(
                    description,
                    MacObjC.Selector("objectForKey:"),
                    MacObjC.NsString("NSScreenNumber"));
                if (number == 0)
                {
                    continue;
                }

                var identifier = (uint)MacObjC.SendUInt(number, MacObjC.Selector("unsignedIntValue"));
                var name = MacObjC.Responds(screen, "localizedName")
                    ? MacObjC.ReadNsString(MacObjC.Send(screen, MacObjC.Selector("localizedName")))
                    : null;
                if (!string.IsNullOrWhiteSpace(name))
                {
                    names[identifier] = name;
                }
            }
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"[GUI][WARN] NSScreen name lookup failed: {exception.Message}");
        }
        finally
        {
            MacObjC.objc_autoreleasePoolPop(pool);
        }

        return names;
    }
}
