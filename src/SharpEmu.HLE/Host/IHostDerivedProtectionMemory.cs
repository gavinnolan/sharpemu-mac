// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
namespace SharpEmu.HLE.Host;

/// <summary>Trusted ordinary/watch protection transport. Guest ledger rights are
/// separate from derived native rights. This is not fault recovery authority.</summary>
public interface IHostDerivedProtectionMemory
{
    bool ChangeDerivedAccess(ulong address, ulong size, GuestPageProtection guestAccess,
        HostPageProtection derivedHostAccess, bool ordinaryEnrollment);
}
