// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace SharpEmu.Platform.MacOS;

/// <summary>
/// Holds an <c>NSProcessInfo</c> activity assertion for the life of the process.
///
/// Without one, macOS treats SharpEmu like any other app: App Nap may throttle it
/// whenever its window stops being frontmost, and timer coalescing is free to slide
/// wakeups by tens of milliseconds to save power. Both are wrong for an emulator —
/// the guest keeps running while the window is behind another one, and a coalesced
/// vblank tick shows up directly as a dropped guest frame.
///
/// The options are the ones Apple documents for exactly this case:
/// <list type="bullet">
/// <item><c>NSActivityUserInitiated</c> — the work is on the user's behalf, so it is
/// never napped. It already implies <c>NSActivityIdleSystemSleepDisabled</c>; that
/// flag is repeated below because the requirement is "the machine must not sleep
/// under us", not "whatever UserInitiated happens to expand to".</item>
/// <item><c>NSActivityLatencyCritical</c> — opts the process out of timer
/// coalescing, which is the half that matters for frame pacing.</item>
/// </list>
///
/// Idle *display* sleep is deliberately left alone. A guest that is paused at a menu
/// for twenty minutes should still let the screen dim; the video path takes its own
/// display assertion when it is actually presenting.
/// </summary>
/// <remarks>
/// The returned activity token is autoreleased, so it is retained here and
/// intentionally never released: the assertion must outlive every pool and last
/// until the process exits, and macOS drops it with the task.
/// </remarks>
[SupportedOSPlatform("macos")]
public static class MacProcessActivity
{
    private const string FoundationFramework =
        "/System/Library/Frameworks/Foundation.framework/Foundation";

    // NSActivityOptions, from <Foundation/NSProcessInfo.h>. They are a uint64_t
    // bitmask; the high half is reserved for the latency flags.
    private const ulong NSActivityIdleSystemSleepDisabled = 1UL << 20;
    private const ulong NSActivityUserInitiated = 0x00FFFFFFUL | NSActivityIdleSystemSleepDisabled;
    private const ulong NSActivityLatencyCritical = 0xFF00000000UL;

    private const ulong EmulatorActivityOptions =
        NSActivityUserInitiated | NSActivityLatencyCritical | NSActivityIdleSystemSleepDisabled;

    private const string ActivityReason =
        "SharpEmu is emulating a PS5 title: guest execution, GPU submission and frame pacing "
        + "must not be napped or coalesced.";

    private static nint _token;

    /// <summary>
    /// Begins the process-wide activity once. Returns true when the assertion is
    /// held (either by this call or an earlier one). Idempotent and never throws:
    /// losing the assertion costs frame-pacing stability, not correctness, so a
    /// failure is reported and the emulator continues.
    /// </summary>
    public static bool Begin()
    {
        if (Volatile.Read(ref _token) != 0)
        {
            return true;
        }

        try
        {
            // NSProcessInfo lives in Foundation, and nothing in the startup path
            // before this point has necessarily pulled Foundation in — libobjc and
            // the CoreCLR host only link CoreFoundation. objc_getClass would
            // return nil for a framework that is not resident yet.
            if (!NativeLibrary.TryLoad(FoundationFramework, out _))
            {
                Console.Error.WriteLine(
                    "[LOADER][WARN] Foundation could not be loaded; App Nap and timer "
                    + "coalescing stay enabled.");
                return false;
            }

            var processInfoClass = MacObjC.Class("NSProcessInfo");
            if (processInfoClass == 0)
            {
                return false;
            }

            var processInfo = MacObjC.Send(processInfoClass, MacObjC.Selector("processInfo"));
            if (processInfo == 0 ||
                !MacObjC.Responds(processInfo, "beginActivityWithOptions:reason:"))
            {
                return false;
            }

            var pool = MacObjC.objc_autoreleasePoolPush();
            nint token;
            try
            {
                token = MacObjC.SendActivity(
                    processInfo,
                    MacObjC.Selector("beginActivityWithOptions:reason:"),
                    EmulatorActivityOptions,
                    MacObjC.NsString(ActivityReason));

                // Retain before the pool drains; see the remarks above.
                if (token != 0)
                {
                    token = MacObjC.Send(token, MacObjC.Selector("retain"));
                }
            }
            finally
            {
                MacObjC.objc_autoreleasePoolPop(pool);
            }

            if (token == 0)
            {
                Console.Error.WriteLine(
                    "[LOADER][WARN] NSProcessInfo beginActivityWithOptions: returned nil; "
                    + "App Nap and timer coalescing stay enabled.");
                return false;
            }

            if (Interlocked.CompareExchange(ref _token, token, 0) != 0)
            {
                // Another thread won the race; drop this duplicate assertion.
                MacObjC.SendVoid(
                    MacObjC.Send(
                        MacObjC.Class("NSProcessInfo"),
                        MacObjC.Selector("processInfo")),
                    MacObjC.Selector("endActivity:"),
                    token);
                MacObjC.Release(token);
                return true;
            }

            Console.Error.WriteLine(
                "[LOADER][INFO] macOS process activity held "
                + $"(options=0x{EmulatorActivityOptions:X}: user-initiated, latency-critical, "
                + "idle system sleep disabled).");
            return true;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(
                $"[LOADER][WARN] NSProcessInfo activity assertion failed: {exception.Message}");
            return false;
        }
    }
}
