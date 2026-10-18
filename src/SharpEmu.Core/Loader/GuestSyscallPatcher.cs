// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Core.Cpu.Native;
using SharpEmu.Core.Memory;
using SharpEmu.HLE;
using SharpEmu.Libs.Kernel.Syscalls;

namespace SharpEmu.Core.Loader;

/// <summary>
/// Load-time pass that makes raw guest <c>syscall</c> instructions trap
/// (SPEC §5.6).
/// </summary>
/// <remarks>
/// Guest code runs natively, so a raw <c>syscall</c> would reach the host
/// kernel with a Prospero number and no host syscall class. Each site the
/// <see cref="GuestSyscallScanner"/> recognises in an executable segment has
/// its <c>0F 05</c> replaced by <c>ud2</c> (<c>0F 0B</c>, same length) and is
/// registered with <see cref="GuestSyscallTrap"/>; the #UD handler sends a
/// registered site through the syscall table and resumes after it. A
/// <c>ud2</c> that is not registered keeps its old behaviour.
///
/// <para>Trade-off: the scanner only accepts the FreeBSD stub idiom, so it
/// misses a raw syscall whose number is not loaded by an adjacent immediate
/// (computed numbers, scheduled code). On macOS those still reach the host
/// kernel as class-0 syscalls, which XNU/Rosetta refuse with SIGSYS, and the
/// SIGSYS fallback in the backend services them the same way. A false
/// positive would turn a data byte pair or the tail of another instruction
/// into <c>ud2</c>; the idiom requires 7-12 specific bytes ending in
/// <c>0F 05</c> with a number &lt;= 0x2D2, which code in an executable
/// segment produces essentially only as the real instruction, and a wrong
/// patch faults loudly at the site instead of corrupting state silently.</para>
/// </remarks>
internal static class GuestSyscallPatcher
{
    private const byte Ud2SecondByte = 0x0B;

    /// <summary>
    /// Patches the module's raw syscall sites and binds the trap stub into
    /// <paramref name="importStubs"/> under the trap NID, so the backend wires
    /// it to the trap export like any other import.
    /// </summary>
    public static int Patch(
        IVirtualMemory memory,
        IReadOnlyList<ProgramHeader> programHeaders,
        ulong imageBase,
        IDictionary<ulong, string> importStubs,
        IModuleManager? moduleManager)
    {
        if (moduleManager is not null)
        {
            KernelSyscallDispatcher.ExportLookup = nid =>
                moduleManager.TryGetExport(nid, out var export) ? export : null;
        }

        var patched = 0;
        foreach (var header in programHeaders)
        {
            if (header.HeaderType != ProgramHeaderType.Load ||
                (header.Flags & ProgramHeaderFlags.Execute) == 0 ||
                header.FileSize == 0 ||
                header.FileSize > int.MaxValue)
            {
                continue;
            }

            var segmentStart = imageBase + header.VirtualAddress;
            var bytes = GC.AllocateUninitializedArray<byte>((int)header.FileSize);
            if (!memory.TryRead(segmentStart, bytes))
            {
                continue;
            }

            foreach (var site in GuestSyscallScanner.Scan(bytes, OrbisSyscallTable.MaxNumber))
            {
                var address = segmentStart + (ulong)site.Offset;
                if (!memory.TryWrite(address + 1, [Ud2SecondByte]))
                {
                    Console.Error.WriteLine(
                        $"[LOADER][WARN] raw syscall 0x{site.Number:X} at 0x{address:X16} could not be patched");
                    continue;
                }

                GuestSyscallTrap.RegisterSite(address, site.Number);
                patched++;
            }
        }

        // Bound even with no patched site: the SIGSYS fallback for a raw
        // syscall the scanner missed resumes through the same stub.
        importStubs[GuestSyscallTrap.StubAddress] = GuestSyscallFrame.TrapNid;
        if (patched != 0)
        {
            Console.Error.WriteLine(
                $"[LOADER][INFO] Raw syscall trap: patched {patched} site(s) in image 0x{imageBase:X16}");
        }

        return patched;
    }
}
