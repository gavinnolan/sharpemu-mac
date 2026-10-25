// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.HLE;

public interface IGuestMemoryAllocator
{
    bool TryAllocateGuestMemory(ulong size, ulong alignment, out ulong address);

    bool TryFreeGuestMemory(ulong address);

    // True when allocations are views of the shared guest backing (the two-process split's
    // layout, SplitBackingLayout), so the GPU process sees them; host data the GPU reads is
    // placed here then.
    bool AllocatesFromBacking => false;
}
