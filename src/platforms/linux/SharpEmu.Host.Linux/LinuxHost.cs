// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.InteropServices;
using SharpEmu.HLE.Host;

namespace SharpEmu.Host.Linux;

/// <summary>
/// Composition entry point for the Linux host package: the only type an
/// application needs to see in order to register this backend.
/// </summary>
public static class LinuxHost
{
    /// <summary>
    /// Creates the Linux host platform.
    /// </summary>
    /// <exception cref="PlatformNotSupportedException">
    /// The current process cannot execute the guest. The backend executes
    /// guest x86-64 natively and emits x86-64 stubs, so a non-x64 process must
    /// be rejected here rather than crash undefined later.
    /// </exception>
    /// <param name="audio">
    /// The audio output to use. SDL remains the qualified default, and the
    /// application passes <c>SdlHostAudio</c> here (see SharpEmu.CLI's
    /// HostComposition) rather than this package referencing the SDL adapter:
    /// that adapter also owns the window and input and reaches into
    /// SharpEmu.Libs, which does not belong in a host package's closure.
    /// Null falls back to the native ALSA output in this package, which is
    /// not yet qualified -- promoting it needs latency, underrun, clock,
    /// device-change and shutdown evidence.
    /// </param>
    public static IHostPlatform Create(IHostAudioOutput? audio = null)
    {
        if (!OperatingSystem.IsLinux() || RuntimeInformation.ProcessArchitecture != Architecture.X64)
        {
            throw new PlatformNotSupportedException(
                "SharpEmu native guest execution requires an x86-64 Linux process.");
        }

        return new LinuxHostPlatform(audio ?? new LinuxAlsaAudio());
    }

    /// <summary>
    /// Creates a Linux placeholder/view memory backend. Not gated on the
    /// process architecture: this is plain host virtual memory, usable in
    /// processes that cannot run guest code.
    /// </summary>
    public static IHostViewMemory CreateViewMemory() => new LinuxHostViews();
}
