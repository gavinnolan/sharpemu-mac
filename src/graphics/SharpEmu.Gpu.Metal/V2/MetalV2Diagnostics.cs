// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections.Concurrent;

namespace SharpEmu.Gpu.Metal.V2;

// One-shot warnings and the environment switches of the v2 backend. Anything v2
// cannot do exactly is reported once by key, never per draw and never fatally.
internal static class MetalV2Diagnostics
{
    private static readonly ConcurrentDictionary<string, byte> _reported = new(StringComparer.Ordinal);

    public static readonly bool Verbose =
        string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_METAL_V2_TRACE"), "1", StringComparison.Ordinal);

    // Naming a range that has to be staged costs two extra backing lookups, so it is
    // on only while someone is hunting ring fallbacks. Defaults on under the trace
    // switch; SHARPEMU_METAL_V2_REPORT_UNBACKED=1 turns it on by itself.
    public static readonly bool ReportUnbackedRanges = Verbose ||
        string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_METAL_V2_REPORT_UNBACKED"), "1", StringComparison.Ordinal);

    public static void WarnOnce(string key, string message)
    {
        if (_reported.TryAdd(key, 0))
        {
            Console.Error.WriteLine($"[METAL-V2][WARN] {message}");
        }
    }

    public static void InfoOnce(string key, string message)
    {
        if (_reported.TryAdd(key, 0))
        {
            Console.Error.WriteLine($"[METAL-V2][INFO] {message}");
        }
    }

    public static void Trace(string message)
    {
        if (Verbose)
        {
            Console.Error.WriteLine($"[METAL-V2][TRACE] {message}");
        }
    }
}
