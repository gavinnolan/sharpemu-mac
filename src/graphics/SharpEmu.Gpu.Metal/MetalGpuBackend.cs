// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.Versioning;
using SharpEmu.Libs.VideoOut;
using SharpEmu.Libs.Gpu;

namespace SharpEmu.Gpu.Metal;

/// <summary>
/// Composition root of the Metal backend package. The application calls <see cref="Register"/>
/// once at startup; the shared engine then resolves the renderer through <c>GuestGpu.Current</c>
/// without naming Metal, and never has to ask what OS it is on.
/// </summary>
[SupportedOSPlatform("macos")]
public static class MetalGpuBackend
{
    public const string Name = "Metal";

    public static void Register()
    {
        if (!OperatingSystem.IsMacOS())
        {
            throw new PlatformNotSupportedException(
                "SharpEmu.Gpu.Metal was composed into a process that is not running on macOS.");
        }

        GuestGpu.Register(static () => new MetalGuestGpuBackend(), Name);
        HostVideoHost.RegisterPresenter(
            MetalVideoPresenter.TryConfigureVideo,
            // No HDR (EDR) output path yet, so Auto stays on SDR and On cannot be honoured.
            static () => false,
            autoHdrAllowed: false);
        Console.Error.WriteLine($"[LOADER][INFO] GPU backend: {Name}.");
    }
}
