// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections.Concurrent;

namespace SharpEmu.ShaderCompiler;

/// <summary>Documented deviations the translators take, each reported once per process.</summary>
public static class ShaderCompilerWarnings
{
    private static readonly ConcurrentDictionary<string, byte> Reported = new();

    public static void WarnOnce(string key, string message)
    {
        if (Reported.TryAdd(key, 0))
        {
            Console.Error.WriteLine($"[GPU][WARN] {message}");
        }
    }
}
