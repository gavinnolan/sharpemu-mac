// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.Versioning;
using SharpEmu.GpuRemote;
using SharpEmu.Libs.Gpu;
using SharpEmu.Libs.VideoOut;

namespace SharpEmu.GpuSplit;

/// <summary>
/// The guest process's composition of the split (<c>SHARPEMU_GPU_PROCESS=1</c>), in place of
/// <c>MetalGpuBackend.Register</c>: the backend the exports see is <see cref="RemoteGpuBackend"/>,
/// and the GPU host starts with it, at the first read of <see cref="GuestGpu.Current"/> - by then
/// the runtime has made the guest memory the GPU host maps.
/// </summary>
[SupportedOSPlatform("macos")]
public static class SplitGpuComposition
{
    private static HostVideoOptions? _videoOptions;
    private static SplitGpuSession? _session;

    /// <summary>Whether the split was asked for (<see cref="GpuProcessHost.IsRequested"/>).</summary>
    public static bool IsRequested => GpuProcessHost.IsRequested;

    /// <summary>The running session, once the GPU host started.</summary>
    public static SplitGpuSession? Session => Volatile.Read(ref _session);

    public static void Register()
    {
        // The name stays the build's backend name: SHARPEMU_GPU_BACKEND=Metal still matches.
        GuestGpu.Register(CreateBackend, "Metal");
        // Like the Metal presenter: Auto stays SDR, only --hdr=on presents HDR.
        HostVideoHost.RegisterPresenter(ConfigureVideo, QueryHdrOutputSupport, autoHdrAllowed: false);
        Console.Error.WriteLine("[LOADER][INFO] GPU backend: Metal v2 in a separate GPU process (SHARPEMU_GPU_PROCESS=1).");
    }

    private static IGuestGpuBackend CreateBackend()
    {
        var session = SplitGpuSession.Start(Volatile.Read(ref _videoOptions));
        Volatile.Write(ref _session, session);
        return session.Backend;
    }

    // Before the GPU host exists the options wait for its start; after, they are sent.
    private static bool ConfigureVideo(HostVideoOptions options)
    {
        Volatile.Write(ref _videoOptions, options);
        Session?.SendVideoOptions(options);
        return true;
    }

    private static bool QueryHdrOutputSupport() =>
        GuestGpu.Current is RemoteGpuBackend backend && backend.QueryHdrOutputSupport();
}
