// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.InteropServices;
using Xunit;

namespace SharpEmu.Libs.Tests;

// How much of the guest address range a host actually hands out differs by
// architecture, and a few tests reserve almost all of it.
//
// The guest range is 0x10_0000_0000-0xFC_0000_0000, which is 944 GiB. An x64
// process - the only configuration the emulator supports, under Rosetta on
// Apple Silicon - can place a fixed mapping from 0x70_0000_0000 up, leaving one
// contiguous 560 GiB hole. An arm64 process on macOS cannot place anything
// below the kernel's own allocation base, and its own mappings sit just above
// it: measured on macOS 27 / arm64, the largest contiguous hole inside the
// guest range is 525 GiB starting at 0x78_8E40_0000.
//
// A test that reserves 512 GiB therefore has 13 GiB of headroom on an arm64
// host and none to spare for anything else the process has mapped, so it
// passes or fails depending on what ran before it. There is nothing to fix in
// the product: the emulator runs as an x64 process, where the room exists.
internal static class GuestAddressSpaceRequirements
{
    public const string SkipReason =
        "This test reserves nearly the whole guest address range, which needs the " +
        "contiguous space only an x64 host process has.";

    // macOS is the only host that withholds the low guest range; Linux and
    // Windows honour a fixed placement request there on either architecture.
    public static bool HasRoomForLargeReservations =>
        !OperatingSystem.IsMacOS() || RuntimeInformation.ProcessArchitecture == Architecture.X64;
}

public sealed class RoomyGuestAddressSpaceFactAttribute : FactAttribute
{
    public RoomyGuestAddressSpaceFactAttribute()
    {
        if (!GuestAddressSpaceRequirements.HasRoomForLargeReservations)
            Skip = GuestAddressSpaceRequirements.SkipReason;
    }
}
