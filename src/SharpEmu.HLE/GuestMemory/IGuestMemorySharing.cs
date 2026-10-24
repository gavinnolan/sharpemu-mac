// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE.Host;

namespace SharpEmu.HLE.GuestMemory;

/// <summary>
/// A guest address space whose backing another process can map: what the two-process split's
/// guest process serves to its GPU process (docs/macos/split-shared-memory.md). Both are null
/// unless the backing was created shareable (<c>SHARPEMU_GPU_PROCESS=1</c>).
/// </summary>
public interface IGuestMemorySharing
{
    /// <summary>What another process needs to map the whole backing.</summary>
    GuestBackingExport? GuestBackingExport { get; }

    /// <summary>The shared table of guest views into the backing, and its log.</summary>
    GuestViewTable? GuestViewTable { get; }
}
