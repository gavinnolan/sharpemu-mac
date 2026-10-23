// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Core.Memory;

namespace SharpEmu.Core.Loader;

// Workaround for macOS 27, which refuses host mappings in [0x10_0000_0000, 0x70_0000_0000) - a
// permanent guard region every process starts with (vmmap: Guard fc0000000-7000000000, ---/---),
// which neither MAP_FIXED nor mach_vm_allocate with VM_FLAGS_OVERWRITE can replace. Titles that
// hardcode the start of the PS5 user area (0x10_0000_0000) as the base of an arena they map with
// sceKernelBatchMap crash at boot, and direct execution cannot relocate those fixed requests. Until
// guest addresses in that window can be translated, the hardcoded base is moved above the window
// where the loader finds it stored as an arena base:
//  - GTA V Enhanced (RAGE) stores its streaming arena base in a global;
//  - Ghost of Tsushima maps its direct-memory arenas one batch entry at a time from a base it keeps
//    in a global after the call.
// Other loads of the constant (range checks such as "below the user area") stay as they are: an
// arena moved higher is still above the user area's start.
internal static class GuestVaHolePatcher
{
    private const ulong HardcodedUserAreaBase = 0x10_0000_0000UL;
    private const ulong DefaultRelocatedBase = 0x80_0000_0000UL;

    // Signature wildcards: any byte, REX.W or REX.WB, and the movabs opcode for any register.
    private const short AnyByte = -1;
    private const short RexW = -2;
    private const short MovAbs = -3;

    // movabs rax, 0x1000000000
    // lea    rdi, [rip+disp32]
    // mov    esi, imm32
    // mov    r64, r64
    // mov    [rip+disp32], rax
    // RAGE (GTA V Enhanced) stores its streaming arena base this way.
    private static readonly short[] RageArenaBaseSignature =
    [
        0x48, 0xB8, 0x00, 0x00, 0x00, 0x00, 0x10, 0x00, 0x00, 0x00,
        0x48, 0x8D, 0x3D, AnyByte, AnyByte, AnyByte, AnyByte,
        0xBE, AnyByte, AnyByte, AnyByte, AnyByte,
        0x48, 0x89, AnyByte,
        0x48, 0x89, 0x05,
    ];

    // movabs r64, 0x1000000000
    // lea    rdi, [rbp+disp8]      one SceKernelBatchMapEntry on the stack
    // mov    esi, 1                one entry
    // xor    edx, edx              no count out
    // Ghost of Tsushima maps each of its direct-memory arenas this way.
    private static readonly short[] BatchMapArenaBaseSignature =
    [
        RexW, MovAbs, 0x00, 0x00, 0x00, 0x00, 0x10, 0x00, 0x00, 0x00,
        0x48, 0x8D, 0x7D, AnyByte,
        0xBE, 0x01, 0x00, 0x00, 0x00,
        0x31, 0xD2,
    ];

    private static readonly short[][] ArenaBaseSignatures = [RageArenaBaseSignature, BatchMapArenaBaseSignature];

    // The offsets of every arena-base load in code, each a movabs whose immediate starts two bytes in.
    internal static List<int> FindArenaBaseSites(ReadOnlySpan<byte> code)
    {
        var sites = new List<int>();
        for (var offset = 0; offset + 10 <= code.Length; offset++)
        {
            if ((code[offset] != 0x48 && code[offset] != 0x49) || code[offset + 1] < 0xB8 || code[offset + 1] > 0xBF)
            {
                continue;
            }

            foreach (var signature in ArenaBaseSignatures)
            {
                if (Matches(code, offset, signature))
                {
                    sites.Add(offset);
                    break;
                }
            }
        }

        return sites;
    }

    public static int Patch(IVirtualMemory memory, IReadOnlyList<ProgramHeader> programHeaders, ulong imageBase)
    {
        if (!OperatingSystem.IsMacOSVersionAtLeast(27) ||
            Environment.GetEnvironmentVariable("SHARPEMU_DISABLE_VA_HOLE_PATCH") == "1")
        {
            return 0;
        }

        var relocatedBase = DefaultRelocatedBase;
        var overrideText = Environment.GetEnvironmentVariable("SHARPEMU_VA_HOLE_BASE");
        if (!string.IsNullOrWhiteSpace(overrideText))
        {
            relocatedBase = Convert.ToUInt64(overrideText.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? overrideText[2..] : overrideText, 16);
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

            foreach (var offset in FindArenaBaseSites(bytes))
            {
                var site = segmentStart + (ulong)offset;
                Span<byte> immediate = stackalloc byte[8];
                BitConverter.TryWriteBytes(immediate, relocatedBase);
                var ok = memory.TryWrite(site + 2, immediate);
                Console.Error.WriteLine(
                    $"[LOADER][INFO] VA hole patch: arena base 0x{HardcodedUserAreaBase:X} -> 0x{relocatedBase:X} at 0x{site:X16} ok={ok}");
                if (ok)
                {
                    patched++;
                }
            }
        }

        return patched;
    }

    private static bool Matches(ReadOnlySpan<byte> code, int offset, short[] signature)
    {
        if (offset + signature.Length > code.Length)
        {
            return false;
        }

        for (var index = 0; index < signature.Length; index++)
        {
            var actual = code[offset + index];
            var expected = signature[index];
            var matches = expected switch
            {
                AnyByte => true,
                RexW => actual is 0x48 or 0x49,
                MovAbs => actual is >= 0xB8 and <= 0xBF,
                _ => actual == expected,
            };
            if (!matches)
            {
                return false;
            }
        }

        return true;
    }
}
