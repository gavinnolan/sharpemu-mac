// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE.Host;
using SharpEmu.HLE.Host.Posix;
using SharpEmu.HLE.Host.Windows;
using Xunit;

namespace SharpEmu.Libs.Tests.Memory.HostViews;

internal static class HostViewTestSupport
{
    public const ulong Segment = 0x4000;
    public const ulong BackingSize = 1UL << 20;
    public const ulong Marker = 0x5348_5250_5649_4557;

    public static bool Supported => OperatingSystem.IsWindows() || OperatingSystem.IsLinux() || OperatingSystem.IsMacOS();

    public static IHostMemory PlatformMemory => OperatingSystem.IsWindows() ? new WindowsHostMemory() : new PosixHostMemory();

    public static ulong HoleSize(IHostViewMemory views) => AlignUp(4 * Segment, views.Granularity);

    // Keep search tests below the guest address limit (0xFC_0000_0000) and
    // outside reserved graphics memory.
    //
    // Three constraints fix where this window sits. It may not sit at the
    // address the host hands out for an "anywhere" mapping - 0x70_0000_0000 on
    // macOS - because a probe frees its range before the caller reserves it
    // again, so a background thread allocating anything at all could take the
    // range in between. It has to be roomy, because the whole-lock-coverage
    // cases in GuestSpaceOwnerTests probe for just over 1 GiB at a time. And it
    // has to stay clear of the lowest 512 GiB that
    // LargeVirtualReservationFallsBackToSparseGuestRange reserves, since the
    // guest address search hands out the lowest free range it finds. The top
    // 48 GiB of the guest range satisfies all three.
    private const ulong ProbeWindowStart = 0xF0_0000_0000UL;
    private const ulong ProbeWindowEnd = 0xFC_0000_0000UL;

    // Slot size for a small probe. A larger probe rounds up to a multiple of
    // this, so the window holds many small slots and few large ones.
    private const ulong ProbeSlotUnit = 0x100_0000UL;

    private static ulong _probeSlotCursor;

    // Hands out a free, granularity-aligned guest address.
    //
    // The probe reserves a range, frees it, and returns the address for the
    // caller to reserve again, so there is a window in which anyone else can
    // take it. Every caller used to start probing at the same address, which
    // made concurrent callers collide in exactly that window: the loser either
    // saw its own reservation fail or found the range already occupied. Give
    // each call its own slot instead, so they do not aim at the same address in
    // the first place.
    public static ulong ProbeGuestAddress(IHostViewMemory views, ulong size)
    {
        size = AlignUp(size, views.Granularity);
        var slot = AlignUp(Math.Max(size, ProbeSlotUnit), ProbeSlotUnit);
        Assert.True(slot <= ProbeWindowEnd - ProbeWindowStart,
            $"A probe of 0x{size:X} bytes does not fit the probe window.");
        var step = AlignUp(Math.Max(size, ProbeSlotUnit), views.Granularity);
        var slots = (ProbeWindowEnd - ProbeWindowStart) / slot;
        var firstSlot = Interlocked.Increment(ref _probeSlotCursor) - 1;

        // Walk every slot once, starting from this call's own, so a host mapping
        // that happens to sit in one slot does not fail the test.
        for (var attempt = 0UL; attempt < slots; attempt++)
        {
            var slotBase = ProbeWindowStart + (firstSlot + attempt) % slots * slot;
            for (var candidate = slotBase; candidate + size <= slotBase + slot; candidate += step)
            {
                if (views.ReserveHole(candidate, size) != candidate) continue;
                Assert.True(views.FreeHole(candidate, size));
                return candidate;
            }
        }

        Assert.Fail("No free guest test range was found.");
        return 0;
    }

    public static ulong AlignUp(ulong value, ulong alignment) => (value + alignment - 1) / alignment * alignment;

    // Reserves then frees a probe so the address is free and granularity-aligned.
    public static ulong ProbeFreeAddress(IHostViewMemory views, ulong size)
    {
        var memory = PlatformMemory;
        for (var attempt = 0; attempt < 8; attempt++)
        {
            var probe = memory.Reserve(0, size, HostPageProtection.ReadWrite);
            Assert.NotEqual(0UL, probe);
            Assert.True(memory.Free(probe));
            if (probe % views.Granularity == 0)
            {
                return probe;
            }
        }

        Assert.Fail("no aligned free address after 8 attempts");
        return 0;
    }

    // No fixed replacement may run unless ReserveHole returned this exact address.
    public static ulong ReserveFreeHole(IHostViewMemory views, ulong size)
    {
        for (var attempt = 0; attempt < 8; attempt++)
        {
            var probe = ProbeFreeAddress(views, size);
            if (views.ReserveHole(probe, size) == probe)
            {
                return probe;
            }
        }

        Assert.Fail("no free hole could be reserved after 8 attempts");
        return 0;
    }

    public static HostBackingObject CreateBacking(IHostViewMemory views)
    {
        Assert.True(views.TryCreateBacking(BackingSize, out var backing, out var failure));
        Assert.Equal(HostViewFailure.None, failure);
        return backing!;
    }
}
