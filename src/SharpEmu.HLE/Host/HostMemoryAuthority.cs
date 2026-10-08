// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.HLE.Host;

/// <summary>Immutable process-wide routing for anonymous host-memory operations.</summary>
/// <remarks>
/// Startup constructs the plain memory provider without constructing a guest
/// execution backend. A common reference unifies routing, not kernel lifetime:
/// view/backing/tracker mutation exclusion is not activated by this service.
/// </remarks>
public static class HostMemoryAuthority
{
    private static readonly object StartupGate = new();
    private static IHostMemoryRaw? _instance;
    private static bool _initializing;
    private static long _startupCleanupFailureAddress;
    private static int _startupCleanupFailureKind;

    /// <summary>Most recent startup allocation whose release receipt declined.</summary>
    /// <remarks>Diagnostic only: a declined receipt does not prove the address is still mapped.</remarks>
    public static ulong LastStartupCleanupFailureAddress => unchecked((ulong)Interlocked.Read(ref _startupCleanupFailureAddress));

    /// <summary>Diagnostic outcome: zero none, one declined receipt, two thrown cleanup.</summary>
    public static int LastStartupCleanupFailureKind => Volatile.Read(ref _startupCleanupFailureKind);

    internal static void RecordStartupCleanupFailure(ulong address, bool threw)
    {
        Interlocked.Exchange(ref _startupCleanupFailureAddress, unchecked((long)address));
        Volatile.Write(ref _startupCleanupFailureKind, threw ? 2 : 1);
    }

    public static bool IsInitialized => Volatile.Read(ref _instance) is not null;

    /// <summary>Constructs and publishes once on the safe startup thread.</summary>
    public static void Initialize(Func<IHostMemoryRaw> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        lock (StartupGate)
        {
            if (_instance is not null || _initializing)
                throw new InvalidOperationException("The process host-memory authority is already registered and cannot be replaced.");
            _initializing = true;
            try
            {
                var provider = factory() ?? throw new InvalidOperationException("The registered host-memory factory returned null.");
                HostMemory.WarmUpProvider(provider);
                Volatile.Write(ref _instance, provider);
            }
            finally { _initializing = false; }
        }
    }

    /// <summary>Reads an already-published provider; never constructs or waits.</summary>
    public static IHostMemoryRaw Current => Volatile.Read(ref _instance) ??
        throw new InvalidOperationException(
            "No host-memory authority has been registered. Call HostMemoryAuthority.Initialize(...) " +
            "with the selected OS CreateMemory factory at safe startup before any host-memory or guest work.");

    internal static void VerifyPlatformMemory(IHostPlatform platform)
    {
        ArgumentNullException.ThrowIfNull(platform);
        if (!ReferenceEquals(platform.Memory, Current))
            throw new InvalidOperationException(
                "The registered host platform's Memory must reference the process HostMemoryAuthority.Current provider.");
    }
}
