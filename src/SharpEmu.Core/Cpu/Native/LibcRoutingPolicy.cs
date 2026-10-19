// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace SharpEmu.Core.Cpu.Native;

/// <summary>
/// Which libc implementation an import binds to when the title ships its own
/// libc.prx (SPEC §0.1). The default keeps the historical routing: HLE first,
/// with a short allowlist going to the loaded module.
/// <c>SHARPEMU_LIBC_LLE_FIRST=1</c> flips it so the loaded libc.prx wins for
/// every export it provides, and HLE is only the fallback for symbols the
/// module lacks or for the named deviations below.
/// </summary>
internal static class LibcRoutingPolicy
{
    internal const string LleFirstVariable = "SHARPEMU_LIBC_LLE_FIRST";

    internal readonly record struct Deviation(string Nid, string Name, string Reason, bool AppliesWhenLleFirst);

    /// <summary>
    /// libc exports that stay on HLE even when the loaded module provides
    /// them. Each entry is a named deviation (docs/deviations.md).
    /// </summary>
    internal static readonly IReadOnlyList<Deviation> HleDeviations =
    [
        new("8zTFvBIAIN8", "memset",
            "the shipped body has a PS5-specific int 45h path that is not a host instruction",
            AppliesWhenLleFirst: true),
        new("Q3VBxCXhUHs", "memcpy",
            "same mem* family as memset; the bounds-aware HLE copy is kept",
            AppliesWhenLleFirst: true),
        new("QrZZdJ8XsX0", "fputs",
            "HLE stdio owns FILE objects in the default routing; under LLE-first the module's FILE layout is used throughout",
            AppliesWhenLleFirst: false),
    ];

    private static readonly bool _lleFirst = Parse(Environment.GetEnvironmentVariable(LleFirstVariable));
    private static int _logged;

    internal static bool LleFirst => _lleFirst;

    internal static bool Parse(string? value) =>
        string.Equals(value, "1", StringComparison.Ordinal) ||
        string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);

    internal static bool IsHleDeviation(string nid) => IsHleDeviation(nid, _lleFirst);

    internal static bool IsHleDeviation(string nid, bool lleFirst)
    {
        foreach (var deviation in HleDeviations)
        {
            if (string.Equals(deviation.Nid, nid, StringComparison.Ordinal))
            {
                return !lleFirst || deviation.AppliesWhenLleFirst;
            }
        }

        return false;
    }

    /// <summary>Logs the active routing and its overrides once per process.</summary>
    internal static void LogOnce()
    {
        if (!_lleFirst || Interlocked.Exchange(ref _logged, 1) != 0)
        {
            return;
        }

        var names = string.Join(", ", HleDeviations.Where(d => d.AppliesWhenLleFirst).Select(d => d.Name));
        Console.Error.WriteLine(
            $"[LOADER][INFO] libc routing: LLE-first ({LleFirstVariable}=1); HLE deviations: {names}");
    }
}
