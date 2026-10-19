// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Core.Cpu.Native.Platform;

/// <summary>
/// The process's registered execution platform. Mirrors
/// <c>SharpEmu.HLE.Host.HostPlatform</c>: explicit registration from the
/// application's composition root, never a factory that picks an
/// implementation from <c>OperatingSystem.Is*</c>.
/// </summary>
/// <remarks>
/// Registration is lazy on purpose. Creating the platform resolves native
/// routines and emits stub pages, which must not happen while a frontend is
/// merely starting up — and on a process architecture that cannot execute
/// guest code it would fail. The first guest-execution step forces it.
/// </remarks>
public static class DirectExecutionPlatform
{
    private static Func<IDirectExecutionPlatform>? _factory;
    private static IDirectExecutionPlatform? _current;
    private static readonly object Gate = new();

    /// <summary>
    /// Registers the platform factory. Called once, from the composition root,
    /// before anything touches <see cref="Current"/>.
    /// </summary>
    public static void Initialize(Func<IDirectExecutionPlatform> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        lock (Gate)
        {
            _factory = factory;
            _current = null;
        }
    }

    /// <summary>True when a factory has been registered.</summary>
    public static bool IsRegistered
    {
        get { lock (Gate) { return _factory is not null; } }
    }

    /// <summary>
    /// The process's execution platform, created on first use.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// No platform was registered. This is a composition bug, not a runtime
    /// condition: see <c>src/SharpEmu.CLI/CpuComposition.cs</c>.
    /// </exception>
    public static IDirectExecutionPlatform Current
    {
        get
        {
            var existing = Volatile.Read(ref _current);
            if (existing is not null)
            {
                return existing;
            }

            lock (Gate)
            {
                if (_current is not null)
                {
                    return _current;
                }

                if (_factory is null)
                {
                    throw new InvalidOperationException(
                        "No direct-execution platform is registered. The application must call " +
                        "DirectExecutionPlatform.Initialize before guest code runs; SharpEmu.CLI does that in " +
                        "CpuComposition.Register, and SharpEmu.Libs.Tests in DirectExecutionPlatformFixture.");
                }

                var created = _factory();
                Volatile.Write(ref _current, created);
                return created;
            }
        }
    }

    /// <summary>
    /// Test hook: forgets the registration so a test can install another
    /// platform. Not for use while guest code can still fault.
    /// </summary>
    internal static void ResetForTests()
    {
        lock (Gate)
        {
            _factory = null;
            _current = null;
        }
    }
}
