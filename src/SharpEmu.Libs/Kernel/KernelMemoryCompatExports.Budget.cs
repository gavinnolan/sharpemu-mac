// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;

namespace SharpEmu.Libs.Kernel;

// The title's memory budget. Flexible memory comes from param.json (kernel.flexibleMemorySize),
// as the SDK documents for SCE_KERNEL_FLEXIBLE_MEMORY_SIZE; without one the default of
// 448 MiB applies.
//
// Budget decision: direct memory is (title total - flexible), with a title total of
// 12736 MiB. On a real base PS5 a process sees 12416 MiB of direct memory at a 320 MiB flexible
// size, i.e. about 12288 MiB (12 GiB, 0x3_0000_0000) at the 448 MiB default, and lowering
// flexible memory enlarges direct by the same amount (the same direct + flexible invariant
// holds on PS4). Source: https://github.com/blackbearreloaded/ProsperoEden/pull/156 (real
// hardware, homebrew process, firmware 6.02). The earlier
// 13824 MiB came from Kyty's assumed PhysicalMemory::TotalSize() and gave titles ~1.5 GiB more
// than a console does (Demon's Souls premaps the excess as "debug memory" above its retail
// constant 0x3_0000_0000). SHARPEMU_DIRECT_MEMORY_MB sets the direct size outright, ignoring
// the flexible adjustment. Direct and flexible live in the one backing object, direct from
// offset 0 and flexible at its top.
public static partial class KernelMemoryCompatExports
{
    internal const ulong DefaultFlexibleMemoryBytes = 448UL * 1024 * 1024;
    internal const ulong DefaultDirectMemoryBytes = 12288UL * 1024 * 1024;
    // direct + flexible; constant while param.json changes the flexible share.
    internal const ulong TitleMemoryBytes = DefaultDirectMemoryBytes + DefaultFlexibleMemoryBytes;

    private const string DirectMemoryOverrideVariable = "SHARPEMU_DIRECT_MEMORY_MB";

    // The direct memory the title may allocate; the backing object may be larger.
    private static ulong _directMemoryBudget = DefaultDirectMemoryBytes;
    private static ulong _flexibleMemoryBudget = DefaultFlexibleMemoryBytes;

    internal static ulong DirectMemoryBudget
    {
        get { lock (_memoryGate) return _directMemoryBudget; }
    }

    internal static ulong FlexibleMemoryBudget
    {
        get { lock (_memoryGate) return _flexibleMemoryBudget; }
    }

    // Direct memory for a given flexible size: the title total less flexible, or the override
    // (MiB) when it parses to a nonzero value. Either way it leaves the flexible share of the
    // backing object free.
    internal static ulong ResolveDirectMemoryBytes(ulong flexibleBytes, string? overrideMegabytes)
    {
        var direct = TitleMemoryBytes - flexibleBytes;
        if (ulong.TryParse(overrideMegabytes, out var megabytes) && megabytes != 0 && megabytes <= (ulong.MaxValue >> 20))
            direct = megabytes << 20;
        direct = Math.Min(direct, GuestMemoryLayout.BackingBytes - flexibleBytes);
        return direct & ~(OrbisPageSize - 1);
    }

    // Applies the title budget before the title allocates anything. A null or zero size keeps
    // the default; a size the backing object cannot hold beside direct memory is clamped.
    public static void ConfigureMemoryBudget(ulong? flexibleMemorySize)
    {
        lock (_memoryGate)
        {
            if (_directAllocations.AvailableBytes != _directAllocations.Capacity || _flexibleBacking.Used != 0)
            {
                Console.Error.WriteLine("[LOADER][WARN] Memory budget unchanged: guest memory is already allocated.");
                return;
            }

            var flexible = flexibleMemorySize is { } requested && requested != 0
                ? AlignUp(requested, OrbisPageSize)
                : DefaultFlexibleMemoryBytes;
            var room = GuestMemoryLayout.BackingBytes - DefaultDirectMemoryBytes;
            if (flexible > room)
            {
                Console.Error.WriteLine(
                    $"[LOADER][WARN] param.json flexible memory 0x{flexible:X} exceeds the backing; using 0x{room:X}.");
                flexible = room & ~(OrbisPageSize - 1);
            }

            var direct = ResolveDirectMemoryBytes(
                flexible, Environment.GetEnvironmentVariable(DirectMemoryOverrideVariable));
            _directMemoryBudget = direct;
            _flexibleMemoryBudget = flexible;
            _directAllocations = new DirectMemoryAllocationMap(direct);
            _flexibleBacking = new FlexibleBackingPool(GuestMemoryLayout.BackingBytes - flexible, flexible);
            Console.Error.WriteLine(
                $"[LOADER] Memory budget: direct=0x{direct:X} flexible=0x{flexible:X}" +
                (flexibleMemorySize is > 0 ? " (param.json)" : " (default)"));
        }
    }
}
