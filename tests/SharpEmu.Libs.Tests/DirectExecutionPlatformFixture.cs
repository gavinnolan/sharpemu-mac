// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.CompilerServices;
using SharpEmu.Core.Cpu.Native.Platform;

namespace SharpEmu.Libs.Tests;

/// <summary>
/// Registers the OS direct-execution integration for the OS these tests run
/// on, mirroring what SharpEmu.CLI's CpuComposition does for the application.
/// </summary>
/// <remarks>
/// A module initializer rather than an xunit fixture, for the same reason as
/// <see cref="HostPlatformFixture"/>: the execution engine reaches the
/// platform through the process-wide <see cref="DirectExecutionPlatform"/>
/// static, so the registration cannot be scoped to a collection and must be in
/// place before the first test constructs a DirectExecutionBackend.
/// </remarks>
internal static class DirectExecutionPlatformFixture
{
    [ModuleInitializer]
    internal static void Initialize()
    {
#if SHARPEMU_CPU_MACOS_X64
        DirectExecutionPlatform.Initialize(SharpEmu.Cpu.DirectExecution.MacOS.MacOSExecutionPlatform.Create);
#elif SHARPEMU_CPU_LINUX_X64
        DirectExecutionPlatform.Initialize(SharpEmu.Cpu.DirectExecution.Linux.LinuxExecutionPlatform.Create);
#elif SHARPEMU_CPU_WINDOWS_X64
        DirectExecutionPlatform.Initialize(SharpEmu.Cpu.DirectExecution.Windows.WindowsExecutionPlatform.Create);
#else
#error No SHARPEMU_CPU_*_X64 symbol was defined; see SharpEmu.Libs.Tests.csproj.
#endif
    }
}
