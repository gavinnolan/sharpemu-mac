// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.Versioning;
using SharpEmu.Libs.VideoOut;
using SharpEmu.Libs.Gpu;
using SharpEmu.Gpu.Metal.V2;

namespace SharpEmu.Gpu.Metal;

/// <summary>
/// Composition root of the Metal backend package. The application calls <see cref="Register"/>
/// once at startup; the shared engine then resolves the renderer through <c>GuestGpu.Current</c>
/// without naming Metal, and never has to ask what OS it is on.
///
/// Two render paths live side by side while v2 catches up: the v2 host (the
/// address-keyed image cache over no-copy guest memory) is the default, and
/// SHARPEMU_METAL_LEGACY=1 selects the original snapshot path for comparison.
/// </summary>
[SupportedOSPlatform("macos")]
public static class MetalGpuBackend
{
    public const string Name = "Metal";

    public const string LegacyName = "Metal (legacy)";

    public static bool UseLegacyPath { get; } = string.Equals(
        Environment.GetEnvironmentVariable("SHARPEMU_METAL_LEGACY"), "1", StringComparison.Ordinal);

    public static void Register()
    {
        if (!OperatingSystem.IsMacOS())
        {
            throw new PlatformNotSupportedException(
                "SharpEmu.Gpu.Metal was composed into a process that is not running on macOS.");
        }

        if (UseLegacyPath)
        {
            GuestGpu.Register(static () => new MetalGuestGpuBackend(), LegacyName);
            HostVideoHost.RegisterPresenter(
                MetalVideoPresenter.TryConfigureVideo,
                // No HDR (EDR) output path yet, so Auto stays on SDR and On cannot be honoured.
                static () => false,
                autoHdrAllowed: false);
            Console.Error.WriteLine($"[LOADER][INFO] GPU backend: {LegacyName}.");
            return;
        }

        GuestGpu.Register(static () => new MetalV2Backend(), Name);
        HostVideoHost.RegisterPresenter(
            MetalV2Presenter.TryConfigureVideo,
            static () => false,
            autoHdrAllowed: false);
        Console.Error.WriteLine($"[LOADER][INFO] GPU backend: {Name} v2.");
    }
}
