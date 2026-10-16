// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;

namespace SharpEmu.Libs.Kernel;

// The title's memory budget. Flexible memory comes from param.json (kernel.flexibleMemorySize),
// as the SDK documents for SCE_KERNEL_FLEXIBLE_MEMORY_SIZE; without one the SDK default
// applies. No header in reach states either default, so they follow the reference emulators:
// 13824 MiB of direct memory and 448 MiB of flexible memory. Both live in the one backing
// object, direct from offset 0 and flexible at its top.
public static partial class KernelMemoryCompatExports
{
    internal const ulong DefaultDirectMemoryBytes = 13824UL * 1024 * 1024;
    internal const ulong DefaultFlexibleMemoryBytes = 448UL * 1024 * 1024;

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

            var direct = Math.Min(DefaultDirectMemoryBytes, GuestMemoryLayout.BackingBytes);
            var flexible = flexibleMemorySize is { } requested && requested != 0
                ? AlignUp(requested, OrbisPageSize)
                : DefaultFlexibleMemoryBytes;
            var room = GuestMemoryLayout.BackingBytes - direct;
            if (flexible > room)
            {
                Console.Error.WriteLine(
                    $"[LOADER][WARN] param.json flexible memory 0x{flexible:X} exceeds the backing; using 0x{room:X}.");
                flexible = room & ~(OrbisPageSize - 1);
            }

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
