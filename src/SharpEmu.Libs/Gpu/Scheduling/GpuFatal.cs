// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Libs.Gpu.Scheduling;

/// <summary>A reported GPU failure; callers must let it terminate execution.</summary>
public sealed class GpuFatalException(string message, Exception? innerException = null)
    : InvalidOperationException(message, innerException);

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
    public static Exception Fatal(string message) => Fatal(message, null);

    /// <summary>Reports a GPU failure without losing its original cause.</summary>
    public static Exception Fatal(string message, Exception? innerException)
    {
        var diagnostic = innerException is null ? message : $"{message}{Environment.NewLine}{innerException}";
        Console.Error.WriteLine($"[GPU][FATAL] {diagnostic}");
        OnFatal(diagnostic);
        return new GpuFatalException(message, innerException);
    }
}
