// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Libs.Kernel;

// macOS keeps [0x10_0000_0000, 0x70_0000_0000) for graphics memory, which is the first
// 0x60_0000_0000 bytes of the PS5 app map area. Direct execution uses guest addresses as host
// addresses, so nothing can live there: kernel-chosen placements search around the window on
// every macOS, and from macOS 27 on, where the host refuses even fixed mappings in it, a fixed
// guest request there fails up front with ENOMEM, the error of an occupied fixed range, before
// it touches any state. Translating guest addresses is the ARM64 path's job, not this one's.
internal static class GuestVaHole
{
    public const ulong Start = 0x10_0000_0000UL;
    public const ulong End = 0x70_0000_0000UL;

    private static int _blockedFixedRequests;

    // Tests pin the host behaviour; null follows the running OS.
    internal static bool? SkipsSearchOverride { get; set; }
    internal static bool? BlocksFixedOverride { get; set; }

    public static bool SkipsSearch => SkipsSearchOverride ?? OperatingSystem.IsMacOS();
    public static bool BlocksFixed => BlocksFixedOverride ?? OperatingSystem.IsMacOSVersionAtLeast(27);

    public static int BlockedFixedRequests => Volatile.Read(ref _blockedFixedRequests);

    public static bool Overlaps(ulong address, ulong length) =>
        length != 0 && address < End && (address > ulong.MaxValue - length || address + length > Start);

    // SHARPEMU_VA_HOLE_VIRTUAL=1 (experimental): the host keeps a fixed request there as a view record
    // alone (MacHostViews), so it is not refused; only the guest's own CPU accesses still fault.
    private static readonly bool VirtualViews = Environment.GetEnvironmentVariable("SHARPEMU_VA_HOLE_VIRTUAL") == "1";

    public static bool IsFixedRequestBlocked(ulong address, ulong length)
    {
        if (!BlocksFixed || !Overlaps(address, length) || (VirtualViews && address >= Start && length <= End - address))
            return false;
        if (Interlocked.Increment(ref _blockedFixedRequests) <= 8)
            Console.Error.WriteLine(
                $"[LOADER][WARN] Fixed guest mapping in the host's blocked window refused: address=0x{address:X16} size=0x{length:X} " +
                $"window=[0x{Start:X}, 0x{End:X}).");
        return true;
    }

    // Moves a kernel-chosen search start past the window when the range would touch it.
    public static ulong SkipForSearch(ulong desired, ulong length) =>
        SkipsSearch && Overlaps(desired, length) ? End : desired;
}
