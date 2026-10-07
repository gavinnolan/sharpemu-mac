// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Core.Cpu.Native.Platform;

namespace SharpEmu.CLI;

/// <summary>
/// Static composition of the OS direct-execution integration — the counterpart
/// to <see cref="HostComposition"/> and <see cref="GpuComposition"/>, using the
/// same RID-to-OS convention.
/// </summary>
/// <remarks>
/// The OS execution package is chosen at build time, not at run time: the
/// csproj references exactly one SharpEmu.Cpu.DirectExecution.&lt;OS&gt;.X64
/// project for the target RuntimeIdentifier and defines the matching
/// SHARPEMU_CPU_* symbol, so an osx-* build never carries the Windows vectored
/// exception handler or the Linux signal layouts (and vice versa).
///
/// Registration is lazy for the same reason the host backend's is: creating
/// the platform resolves native routines and emits executable stub pages, and
/// a process that will never execute guest code should not pay for that or
/// fail on it. <see cref="DirectExecutionPlatform.Current"/> forces it the
/// first time the execution engine needs the OS.
/// </remarks>
internal static class CpuComposition
{
    /// <summary>
    /// Registers the process's OS execution integration. Must run before any
    /// guest code executes — that is, before the CPU dispatcher constructs the
    /// direct-execution backend.
    /// </summary>
    public static void Register()
    {
#if SHARPEMU_CPU_MACOS_X64
        DirectExecutionPlatform.Initialize(SharpEmu.Cpu.DirectExecution.MacOS.X64.MacOSExecutionPlatform.Create);
#elif SHARPEMU_CPU_LINUX_X64
        DirectExecutionPlatform.Initialize(SharpEmu.Cpu.DirectExecution.Linux.X64.LinuxExecutionPlatform.Create);
#elif SHARPEMU_CPU_WINDOWS_X64
        DirectExecutionPlatform.Initialize(SharpEmu.Cpu.DirectExecution.Windows.X64.WindowsExecutionPlatform.Create);
#else
#error No SHARPEMU_CPU_*_X64 symbol was defined. SharpEmu.CLI.csproj selects the execution package from the target RuntimeIdentifier; an unrecognized RID leaves the application with no way to run guest code.
#endif
    }
}
