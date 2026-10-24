// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.HLE;

public static class GuestMemoryLayout
{
    public const ulong DirectBytes = 16384UL * 1024 * 1024;
    public const ulong FlexibleBytes = 448UL * 1024 * 1024;
    public const ulong FlexibleOffset = DirectBytes;
    public const ulong BackingBytes = DirectBytes + FlexibleBytes;
    public const ulong GuestPage = 0x4000;

    // The host's guest allocator arena (IGuestMemoryAllocator): HLE objects the guest is handed
    // pointers to, the VideoOut buffer labels among them. Outside the guest's own address range.
    public const ulong AllocatorArenaAddress = 0x00006000_0000_0000;
    public const ulong AllocatorArenaBytes = 0x2000_0000;
}
