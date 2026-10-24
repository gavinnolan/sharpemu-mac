// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.HLE.GpuMemory;

/// <summary>
/// An address space that wants to know which pages a <see cref="PageGuard"/> watches. The
/// two-process split's GPU process keeps a page summary in memory both processes share, so the
/// guest process reports a managed write only for pages a GPU cache watches; the guard finds
/// this interface on the address space it is made with.
/// </summary>
public interface IPageWatchObserver
{
    /// <summary>
    /// The page at <paramref name="page"/> got its first watch (read or write) or lost its last.
    /// Called under the guard's block lock, before the protection that follows from the change is
    /// applied: it must not block, and it must not allocate.
    /// </summary>
    void OnPageWatchChanged(ulong page, bool watched);
}

/// <summary>
/// Told about every span a <see cref="GuestGpuMemory"/> registers and every guest protection
/// change of one (<see cref="GuestGpuMemory.AttachSpanObserver"/>). The two-process split's guest
/// process keeps the GPU process's spans and permission ledger equal to its own this way.
/// </summary>
public interface IGuestSpanObserver
{
    /// <summary>After <see cref="GuestGpuMemory.Register"/> applied the span here.</summary>
    void OnSpanRegistered(ulong address, ulong size, GuestPageProtection protection);

    /// <summary>After <see cref="GuestGpuMemory.NoteProtected"/> applied the change here.</summary>
    void OnSpanProtected(ulong address, ulong size, GuestPageProtection protection);
}
