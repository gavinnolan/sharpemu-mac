// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Core.Loader;
using SharpEmu.HLE;
using SharpEmu.Libs.Kernel;

namespace SharpEmu.Core.Memory;

// Tells the kernel's guest VA map about memory the loader and the runtime map for the guest,
// so sceKernelVirtualQuery, QueryMemoryProtection and IsStack answer for it. Only the address
// space the kernel maps into (an IGuestBackedSpace) is tracked; scratch memories are not.
internal static class GuestHostMappings
{
    // ELF segment flags (PF_X 1, PF_W 2, PF_R 4) as SCE CPU protection bits (R 1, W 2, X 4).
    public static int ToOrbisProtection(ProgramHeaderFlags flags) =>
        ((flags & ProgramHeaderFlags.Read) != 0 ? 0x1 : 0) |
        ((flags & ProgramHeaderFlags.Write) != 0 ? 0x2 : 0) |
        ((flags & ProgramHeaderFlags.Execute) != 0 ? 0x4 : 0);

    public static void Register(IVirtualMemory memory, ulong address, ulong length, ProgramHeaderFlags flags,
        KernelMemoryCompatExports.HostMappingKind kind, string? name = null)
    {
        if (memory is IGuestBackedSpace)
            KernelMemoryCompatExports.RegisterHostMapping(address, length, ToOrbisProtection(flags), kind, name);
    }

    public static void Unregister(IVirtualMemory memory, ulong address, ulong length)
    {
        if (memory is IGuestBackedSpace)
            KernelMemoryCompatExports.UnregisterHostMapping(address, length);
    }

    // Every PT_LOAD segment with its own protection, under the module's name.
    public static void RegisterModuleSegments(IVirtualMemory memory, IReadOnlyList<ProgramHeader> programHeaders,
        ulong imageBase, string? name)
    {
        foreach (var header in programHeaders)
        {
            if (header.HeaderType != ProgramHeaderType.Load || header.MemorySize == 0)
                continue;
            Register(memory, unchecked(imageBase + header.VirtualAddress), header.MemorySize, header.Flags,
                KernelMemoryCompatExports.HostMappingKind.Module, name);
        }
    }
}
