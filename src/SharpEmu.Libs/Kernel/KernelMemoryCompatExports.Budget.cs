// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;

namespace SharpEmu.Libs.Kernel;

public static partial class KernelMemoryCompatExports
{
    // The direct memory the title may allocate; the backing object may be larger.
    private static ulong _directMemoryBudget = GuestMemoryLayout.DirectBytes;
}
