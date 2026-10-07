// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.InteropServices;
using SharpEmu.HLE.Host;

namespace SharpEmu.Host.MacOS;

/// <summary>
/// Composition entry point for the macOS host package: the only type an
/// application needs to see in order to register this backend.
/// </summary>
public static class MacOSHost
{
    /// <summary>
    /// Creates the macOS host platform.
    /// </summary>
    /// <exception cref="PlatformNotSupportedException">
    /// The current process cannot execute the guest. The backend executes
    /// guest x86-64 natively and emits x86-64 stubs, so a native ARM64 process
    /// must be rejected here rather than crash undefined later (x64 processes
    /// under Rosetta 2 report <see cref="Architecture.X64"/>).
    /// </exception>
    public static IHostPlatform Create()
    {
        if (!OperatingSystem.IsMacOS() || RuntimeInformation.ProcessArchitecture != Architecture.X64)
        {
            throw new PlatformNotSupportedException(
                "SharpEmu native guest execution requires an x86-64 macOS process. " +
                "On Apple Silicon, use the osx-x64 build under Rosetta 2.");
        }

        return new MacOSHostPlatform();
    }

    /// <summary>
    /// Creates a macOS placeholder/view memory backend. Not gated on the
    /// process architecture: this is plain host virtual memory, usable in
    /// processes that cannot run guest code.
    /// </summary>
    public static IHostViewMemory CreateViewMemory() => new MacHostViews();
}
