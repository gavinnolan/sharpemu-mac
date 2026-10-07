// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Libs.Gpu.Scheduling;

// How the GPU engine gives up. Every backend and every layer of the shared engine reports a
// fatal error the same way, so the helper carries no backend dependency of its own; the
// Vulkan submission scheduler forwards its own Fatal to this one.
public static class GpuFatal
{
    internal static Action<string> OnFatal = message => Environment.FailFast(message);

    // Report the fatal error, then return an exception to stop the caller.
    internal static Exception Fatal(string message)
    {
        Console.Error.WriteLine($"[GPU][FATAL] {message}");
        OnFatal(message);
        return new InvalidOperationException(message);
    }
}
