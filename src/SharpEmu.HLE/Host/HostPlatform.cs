// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.HLE.Host;

/// <summary>
/// Process-wide access point for the host platform backend. Static HLE export
/// classes (which cannot receive constructor injection) resolve host primitives
/// through <see cref="Current"/>; injectable components should instead accept an
/// <see cref="IHostPlatform"/> and merely default to this.
/// </summary>
/// <remarks>
/// The backend is registered explicitly by the application at startup (see
/// SharpEmu.CLI's HostComposition) rather than chosen here from
/// <see cref="OperatingSystem"/> checks: a macOS build links only
/// SharpEmu.Host.MacOS, so there is nothing for an OS check to choose between.
/// </remarks>
public static class HostPlatform
{
    // Lazy, not a hand-rolled lock: it keeps the publication semantics the OS-check
    // factory had (one construction, a cached failure rather than a retry on every
    // access) and it detects a backend whose construction reaches back into
    // Current instead of recursing until the stack runs out.
    private static Lazy<IHostPlatform>? _instance;

    /// <summary>
    /// Whether a backend has been registered. Does not construct it.
    /// </summary>
    public static bool IsInitialized => Volatile.Read(ref _instance) is not null;

    /// <summary>Registers an already-constructed backend. Last registration wins.</summary>
    public static void Initialize(IHostPlatform platform)
    {
        ArgumentNullException.ThrowIfNull(platform);
        Volatile.Write(ref _instance, new Lazy<IHostPlatform>(platform));
    }

    /// <summary>
    /// Registers a backend factory invoked once, on first use. Applications
    /// prefer this overload: a host package may reject the running process
    /// (for example a native ARM64 process, which cannot execute the x86-64
    /// guest), and that diagnostic must surface when guest work starts rather
    /// than when the frontend launches.
    /// </summary>
    public static void Initialize(Func<IHostPlatform> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        Volatile.Write(ref _instance, new Lazy<IHostPlatform>(factory));
    }

    public static IHostPlatform Current
    {
        get
        {
            var instance = Volatile.Read(ref _instance) ??
                throw new InvalidOperationException(
                    "No host platform has been registered. The application must call " +
                    "HostPlatform.Initialize(...) with the SharpEmu.Host.* backend for its " +
                    "target OS before any guest work starts (see SharpEmu.CLI's HostComposition).");
            return instance.Value ??
                throw new InvalidOperationException("The registered host platform factory returned null.");
        }
    }
}
