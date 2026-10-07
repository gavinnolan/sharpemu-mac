// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.InteropServices;
using SharpEmu.HLE.Host;

namespace SharpEmu.Host.Windows;

/// <summary>
/// Composition entry point for the Windows host package: the only type an
/// application needs to see in order to register this backend.
/// </summary>
public static class WindowsHost
{
    /// <summary>
    /// Creates the Windows host platform.
    /// </summary>
    /// <exception cref="PlatformNotSupportedException">
    /// The current process cannot execute the guest. The Windows backend runs
    /// guest x86-64 natively and emits x86-64 stubs, so a native ARM64 process
    /// must be rejected here rather than crash undefined later (x64 processes
    /// under emulation report <see cref="Architecture.X64"/>).
    /// </exception>
    public static IHostPlatform Create()
    {
        if (!OperatingSystem.IsWindows() || RuntimeInformation.ProcessArchitecture != Architecture.X64)
        {
            throw new PlatformNotSupportedException(
                "SharpEmu native guest execution requires an x86-64 Windows process.");
        }

        return new WindowsHostPlatform();
    }

    /// <summary>
    /// Creates a Windows placeholder/view memory backend. Not gated on the
    /// process architecture: this is plain host virtual memory, usable in
    /// processes that cannot run guest code.
    /// </summary>
    public static IHostViewMemory CreateViewMemory() => new WindowsHostViews();
}
