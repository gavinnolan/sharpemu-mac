// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
using System.Runtime.InteropServices;
using SharpEmu.HLE.Host;

namespace SharpEmu.Host.MacOS;

internal static partial class MacMappedCopyState
{
    // Constructed only from ordinary composition, before guest entry. There is
    // no lazy native query from a fault, copy or IsCurrent call site.
    internal static HostMappedCopyLedger? Ledger { get; private set; }
    private static bool initialized;
    internal static int GeometryError { get; private set; }
    internal static int HostRightReleaseError { get; private set; }

    internal static void Initialize()
    {
        lock (HostViewRegions.Gate)
        {
            if (initialized) return;
            if (!OperatingSystem.IsMacOS()) { initialized = true; return; }
            var host = mach_host_self();
            nuint page = 0;
            try { GeometryError = _host_page_size(host, out page); }
            finally { HostRightReleaseError = mach_port_deallocate(mach_task_self(), host); }
            if (GeometryError == 0 && HostRightReleaseError == 0 && page != 0 &&
                ((ulong)page & ((ulong)page - 1)) == 0 &&
                (ulong)page >= (ulong)Environment.SystemPageSize &&
                (ulong)page % (ulong)Environment.SystemPageSize == 0)
                Ledger = new(HostViewRegions.Gate, (ulong)page);
            // Failed geometry leaves the OPTIONAL capability unavailable; no
            // invented 4K/16K authority or change to legacy provider semantics.
            initialized = true;
        }
    }

    [LibraryImport("/usr/lib/libSystem.B.dylib")]
    private static partial uint mach_host_self();
    [LibraryImport("/usr/lib/libSystem.B.dylib")]
    private static partial uint mach_task_self();
    [LibraryImport("/usr/lib/libSystem.B.dylib")]
    private static partial int _host_page_size(uint host, out nuint size);
    [LibraryImport("/usr/lib/libSystem.B.dylib")]
    private static partial int mach_port_deallocate(uint task, uint port);
}
