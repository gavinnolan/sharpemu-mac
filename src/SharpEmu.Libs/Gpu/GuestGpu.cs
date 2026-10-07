// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Agc;

namespace SharpEmu.Libs.Gpu;

/// <summary>
/// Process-wide access point for the guest-GPU backend, mirroring HostPlatform for the
/// host seam: static HLE export classes resolve the renderer through <see cref="Current"/>.
///
/// The backend is <em>composed</em>, not detected: exactly one backend package is linked
/// into an application (Metal on macOS, Vulkan on Windows and Linux) and registers itself
/// through <see cref="Register"/> at startup. The shared engine therefore carries no OS
/// check and no reference to either backend assembly. There is no fallback; requesting a
/// backend the application was not built with is a startup error.
/// </summary>
public static class GuestGpu
{
    private static readonly object Gate = new();
    private static Func<IGuestGpuBackend>? _factory;
    private static string? _backendName;
    private static IGuestGpuBackend? _instance;

    /// <summary>
    /// Installs the process's single GPU backend. Called once from the application's
    /// composition root before emulation or the GUI starts; the backend itself is created
    /// lazily, on the first <see cref="Current"/> read.
    /// </summary>
    /// <param name="factory">Creates the backend. Not invoked by this method.</param>
    /// <param name="backendName">The name reported as <see cref="HostBackendName"/>
    /// ("Metal", "Vulkan"); it must match the backend's own <c>BackendName</c>.</param>
    public static void Register(Func<IGuestGpuBackend> factory, string backendName)
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentException.ThrowIfNullOrEmpty(backendName);

        var requested = Environment.GetEnvironmentVariable("SHARPEMU_GPU_BACKEND");
        if (!string.IsNullOrEmpty(requested) &&
            !requested.Equals(backendName, StringComparison.OrdinalIgnoreCase))
        {
            throw new PlatformNotSupportedException(
                $"SHARPEMU_GPU_BACKEND={requested} is not available in this build; " +
                $"the only GPU backend here is {backendName}.");
        }

        lock (Gate)
        {
            if (_instance is not null)
            {
                throw new InvalidOperationException(
                    $"The {_backendName} GPU backend is already in use; a backend cannot be " +
                    "registered after the first GuestGpu.Current read.");
            }

            _factory = factory;
            _backendName = backendName;
        }
    }

    /// <summary>Whether a backend package has registered itself yet.</summary>
    public static bool IsRegistered
    {
        get
        {
            lock (Gate)
            {
                return _factory is not null;
            }
        }
    }

    public static IGuestGpuBackend Current
    {
        get
        {
            lock (Gate)
            {
                if (_instance is { } existing)
                {
                    return existing;
                }

                var factory = RequireFactory();
                var created = HeadlessCommandStreamFactory is { } streams
                    ? new HeadlessCommandStreamBackend(streams, factory)
                    : factory();
                _instance = created;
                return created;
            }
        }
    }

    /// <summary>The backend this application was composed with.</summary>
    public static string HostBackendName
    {
        get
        {
            lock (Gate)
            {
                RequireFactory();
                return _backendName!;
            }
        }
    }

    /// <summary>
    /// Test support: when set before <see cref="Current"/> is first read, guest command streams
    /// run inline through this factory instead of the host backend's GPU worker.
    /// </summary>
    internal static Func<ICpuMemory, AgcExports.HeadlessCommandStream>? HeadlessCommandStreamFactory { get; set; }

    private static Func<IGuestGpuBackend> RequireFactory() =>
        _factory ?? throw new InvalidOperationException(
            "No GPU backend was registered. The application's composition root must call " +
            "GuestGpu.Register (SharpEmu.Gpu.Metal's MetalGpuBackend.Register on macOS, " +
            "SharpEmu.Gpu.Vulkan's VulkanGpuBackend.Register on Windows and Linux) before " +
            "anything touches the renderer.");
}
