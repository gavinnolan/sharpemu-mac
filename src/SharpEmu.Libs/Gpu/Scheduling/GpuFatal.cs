// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Libs.Gpu.Scheduling;

/// <summary>
/// How the GPU engine gives up. Every backend and every layer of the shared
/// engine reports a fatal error the same way, so the helper carries no backend
/// dependency of its own; the Vulkan submission scheduler forwards its own
/// Fatal to this one.
/// </summary>
public static class GpuFatal
{
    internal static Action<string> OnFatal = message => Environment.FailFast(message);

    /// <summary>
    /// Reports the fatal error, then returns an exception for the caller to
    /// throw so control stops there.
    /// </summary>
    /// <remarks>Backend contract.</remarks>
    public static Exception Fatal(string message)
    {
        Console.Error.WriteLine($"[GPU][FATAL] {message}");
        OnFatal(message);
        return new InvalidOperationException(message);
    }
}
