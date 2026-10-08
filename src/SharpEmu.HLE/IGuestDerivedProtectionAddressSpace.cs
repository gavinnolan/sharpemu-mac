// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
namespace SharpEmu.HLE;

/// <summary>Explicit authoritative guest ledger and watcher-derived host bits.
/// Nonparticipants keep the existing derived syscall through IGuestAddressSpace.
/// Ordinary enrollment may allocate; watcher/fault transport must not.</summary>
public interface IGuestDerivedProtectionAddressSpace
{
    bool TryApplyDerivedProtection(ulong address, ulong size, GuestPageProtection guestAccess,
        GuestPageProtection derivedAccess, bool ordinaryEnrollment);
}
