// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Agc;
using SharpEmu.Libs.Gpu.Metal;
using SharpEmu.Libs.Gpu.Vulkan;

namespace SharpEmu.Libs.Gpu;

/// <summary>
/// Process-wide access point for the guest-GPU backend, mirroring HostPlatform for the
/// host seam: static HLE export classes resolve the renderer through <see cref="Current"/>.
/// Each host OS has exactly one backend: Metal on macOS, Vulkan on Windows and Linux.
/// There is no fallback; requesting a backend the host does not provide is a startup error.
/// </summary>
internal static class GuestGpu
{
    private static readonly Lazy<IGuestGpuBackend> Instance = new(Create);

    public static IGuestGpuBackend Current => Instance.Value;

    /// <summary>The backend this host OS provides.</summary>
    public static string HostBackendName => OperatingSystem.IsMacOS() ? "Metal" : "Vulkan";

    /// <summary>
    /// Test support: when set before <see cref="Current"/> is first read, guest command streams
    /// run inline through this factory instead of the host backend's GPU worker.
    /// </summary>
    internal static Func<ICpuMemory, AgcExports.HeadlessCommandStream>? HeadlessCommandStreamFactory { get; set; }

    private static IGuestGpuBackend Create() =>
        HeadlessCommandStreamFactory is { } streams
            ? new HeadlessCommandStreamBackend(streams, CreateHostBackend)
            : CreateHostBackend();

    private static IGuestGpuBackend CreateHostBackend()
    {
        var requested = Environment.GetEnvironmentVariable("SHARPEMU_GPU_BACKEND");
        if (!string.IsNullOrEmpty(requested) &&
            !requested.Equals(HostBackendName, StringComparison.OrdinalIgnoreCase))
        {
            throw new PlatformNotSupportedException(
                $"SHARPEMU_GPU_BACKEND={requested} is not available on this host; " +
                $"the only GPU backend here is {HostBackendName}.");
        }

        if (OperatingSystem.IsMacOS())
        {
            Console.Error.WriteLine("[LOADER][INFO] GPU backend: Metal.");
            return new MetalGuestGpuBackend();
        }

        return new VulkanGuestGpuBackend();
    }
}
