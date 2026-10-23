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
//  - Ghost of Tsushima maps its arenas one batch entry at a time (0x10_0000_0000 for libc and its
//    direct-memory pools, 0x11_0000_0000 and 0x15_0000_0000 for two more) from a base it keeps in a
//    global after the call.
// Every such base moves by the same amount (0x10_0000_0000 -> 0x80_0000_0000), so arenas keep their
// order and spacing. Other loads of these constants stay as they are: the user area base also feeds
// range checks ("below the user area"), which an arena moved higher still passes, and the others are
// packed 32-bit pairs in struct initialisers, which must not change.
internal static class GuestVaHolePatcher
{
    private const ulong HardcodedUserAreaBase = 0x10_0000_0000UL;
    private const ulong DefaultRelocatedBase = 0x80_0000_0000UL;

    // A signature byte that matches anything.
    private const short AnyByte = -1;

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

    // The window macOS 27 guards, of which the user area's arenas are 4 GiB-aligned bases.
    private const ulong WindowStart = 0x10_0000_0000UL;
    private const ulong WindowEnd = 0x70_0000_0000UL;
    private const ulong ArenaAlignment = 0x1_0000_0000UL;

    // An arena base loaded for a one-entry sceKernelBatchMap: a movabs of a 4 GiB-aligned base in the
    // window, then the batch setup - at most one store of the base into the entry (mov [rbp+disp8], r64),
    // lea rdi, [rbp+disp8]; mov esi, 1; xor edx, edx - or, for a base chosen on another path, a store of
    // another register into the arena's record (mov [r+disp32], r64) and a jmp to such a setup.
    internal readonly record struct ArenaBaseSite(int Offset, ulong Base);

    // Every arena-base load in code, each a movabs whose immediate starts two bytes in.
    internal static List<ArenaBaseSite> FindArenaBaseSites(ReadOnlySpan<byte> code)
    {
        var sites = new List<ArenaBaseSite>();
        for (var offset = 0; offset + 10 <= code.Length; offset++)
        {
            if ((code[offset] != 0x48 && code[offset] != 0x49) || code[offset + 1] < 0xB8 || code[offset + 1] > 0xBF)
            {
                continue;
            }

            var immediate = BitConverter.ToUInt64(code.Slice(offset + 2, 8));
            if (Matches(code, offset, RageArenaBaseSignature) ||
                (immediate is >= WindowStart and < WindowEnd && immediate % ArenaAlignment == 0 &&
                 FeedsOneEntryBatchMap(code, offset + 10)))
            {
                sites.Add(new ArenaBaseSite(offset, immediate));
            }
        }

        return sites;
    }

    private static bool FeedsOneEntryBatchMap(ReadOnlySpan<byte> code, int next)
    {
        if (IsOneEntryBatchSetup(code, next))
        {
            return true;
        }

        // mov [r64+disp32], r64 (REX.WB 89 /r, mod 10) and jmp rel32 to the shared setup.
        if (next + 12 > code.Length || code[next] != 0x49 || code[next + 1] != 0x89 || (code[next + 2] & 0xC0) != 0x80 ||
            code[next + 7] != 0xE9)
        {
            return false;
        }

        var target = next + 12 + BitConverter.ToInt32(code.Slice(next + 8, 4));
        return target >= 0 && IsOneEntryBatchSetup(code, target);
    }

    private static bool IsOneEntryBatchSetup(ReadOnlySpan<byte> code, int at)
    {
        // An optional mov [rbp+disp8], r64 (REX.W or REX.WR 89, mod 01 rm 101).
        if (at + 4 <= code.Length && (code[at] is 0x48 or 0x4C) && code[at + 1] == 0x89 && (code[at + 2] & 0xC7) == 0x45)
        {
            at += 4;
        }

        ReadOnlySpan<byte> setup = [0x48, 0x8D, 0x7D];
        ReadOnlySpan<byte> oneEntry = [0xBE, 0x01, 0x00, 0x00, 0x00, 0x31, 0xD2];
        return at + 11 <= code.Length && code.Slice(at, 3).SequenceEqual(setup) && code.Slice(at + 4, 7).SequenceEqual(oneEntry);
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

            Span<byte> immediate = stackalloc byte[8];
            foreach (var found in FindArenaBaseSites(bytes))
            {
                var site = segmentStart + (ulong)found.Offset;
                var moved = found.Base - HardcodedUserAreaBase + relocatedBase;
                BitConverter.TryWriteBytes(immediate, moved);
                var ok = memory.TryWrite(site + 2, immediate);
                Console.Error.WriteLine(
                    $"[LOADER][INFO] VA hole patch: arena base 0x{found.Base:X} -> 0x{moved:X} at 0x{site:X16} ok={ok}");
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
            if (expected != AnyByte && actual != expected)
            {
                return false;
            }
        }

        return true;
    }
}
