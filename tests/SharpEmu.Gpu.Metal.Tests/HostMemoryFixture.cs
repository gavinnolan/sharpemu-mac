// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.CompilerServices;
using SharpEmu.HLE.Host;

namespace SharpEmu.Gpu.Metal.Tests;

internal static class HostMemoryFixture
{
    [ModuleInitializer]
    internal static void Initialize()
    {
        // Configure(true) and snapshot tracking reach raw host-memory queries.
        // Register before the first fixture, independently of test order or any
        // other test assembly's initializer. Plain memory has no x64 CPU gate.
        if (OperatingSystem.IsMacOS() && !HostMemoryAuthority.IsInitialized)
            HostMemoryAuthority.Initialize(SharpEmu.Host.MacOS.MacOSHost.CreateMemory);
    }
}
