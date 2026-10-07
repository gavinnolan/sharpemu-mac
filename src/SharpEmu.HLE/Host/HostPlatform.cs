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
    private static readonly object Gate = new();
    private static Func<IHostPlatform>? _factory;
    private static IHostPlatform? _instance;

    /// <summary>
    /// Whether a backend has been registered. Does not create it.
    /// </summary>
    public static bool IsInitialized
    {
        get
        {
            lock (Gate)
            {
                return _instance is not null || _factory is not null;
            }
        }
    }

    /// <summary>Registers the backend eagerly. Last registration wins.</summary>
    public static void Initialize(IHostPlatform platform)
    {
        ArgumentNullException.ThrowIfNull(platform);
        lock (Gate)
        {
            _factory = null;
            _instance = platform;
        }
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
        lock (Gate)
        {
            _instance = null;
            _factory = factory;
        }
    }

    public static IHostPlatform Current
    {
        get
        {
            lock (Gate)
            {
                if (_instance is not null)
                {
                    return _instance;
                }

                if (_factory is null)
                {
                    throw new InvalidOperationException(
                        "No host platform has been registered. The application must call " +
                        "HostPlatform.Initialize(...) with the SharpEmu.Host.* backend for its " +
                        "target OS before any guest work starts (see SharpEmu.CLI's HostComposition).");
                }

                _instance = _factory() ??
                    throw new InvalidOperationException("The registered host platform factory returned null.");
                return _instance;
            }
        }
    }
}
