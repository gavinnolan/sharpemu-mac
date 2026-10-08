// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.HLE.Host;

/// <summary>Legacy scalar operations implemented by the canonical memory provider.</summary>
/// <remarks>
/// Raw flags/protection values retain the platform's existing compatibility
/// semantics. They are not neutral permissions, transferable between OSes,
/// or a native ownership receipt. Query preserves the complete legacy layout.
/// </remarks>
public interface IHostMemoryRaw : IHostMemory
{
    ulong AllocateRaw(ulong desiredAddress, ulong size, uint allocationType, uint rawProtection);
    bool FreeRaw(ulong address, ulong size, uint freeType);
    nuint QueryRaw(ulong address, out HostMemory.BasicInfo info);
}
