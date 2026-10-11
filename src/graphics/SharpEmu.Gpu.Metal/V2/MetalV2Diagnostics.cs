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

    // SHARPEMU_METAL_V2_IGNORE_TESTS=1 drops face culling, the depth compare and the
    // stencil test from every draw. A pass whose target never changes either had its
    // fragments thrown away by one of those three or never had a fragment to throw
    // away, and nothing in a target dump tells the two apart.
    public static readonly bool IgnoreFragmentTests =
        string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_METAL_V2_IGNORE_TESTS"), "1", StringComparison.Ordinal);

    // SHARPEMU_METAL_V2_REPORT_VERTEX_LAYOUT=1 keeps the vertex descriptor each
    // pipeline was built with, for the per-draw report: a stride or an attribute format
    // that does not match what the program fetches is invisible in everything else.
    public static readonly bool ReportVertexLayout =
        string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_METAL_V2_REPORT_VERTEX_LAYOUT"), "1", StringComparison.Ordinal);

    // SHARPEMU_METAL_V2_DEFER_STORE=0 makes every pass store every attachment the
    // moment its descriptor is built, the way it did before the store action waited
    // to see what the next pass does with the same attachment. Kept as a switch
    // because asking Metal for MTLStoreActionUnknown changes what the driver can
    // decide up front, and that is worth being able to measure against.
    public static readonly bool DeferStoreActions =
        !string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_METAL_V2_DEFER_STORE"), "0", StringComparison.Ordinal);

    // SHARPEMU_METAL_V2_DEFER_LABELS=0 makes an end-of-pipe label's bytes end the open
    // render pass to be copied, as they did before. A label is four or eight bytes and
    // a pass end on a tile-based GPU costs the whole framebuffer twice, so the copy
    // waits for a boundary that was going to happen anyway — but it also means the
    // guest sees the label slightly later, so the old behaviour stays reachable.
    public static readonly bool DeferLabelWrites =
        !string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_METAL_V2_DEFER_LABELS"), "0", StringComparison.Ordinal);

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
