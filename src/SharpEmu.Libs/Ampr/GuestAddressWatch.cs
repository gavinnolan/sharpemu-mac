// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Threading;

namespace SharpEmu.Libs.Ampr;

/// <summary>
/// Parks host-side waiters on guest memory words. Writers the emulator
/// performs itself (AMPR address writes) call <see cref="NotifyWrite"/>, which
/// wakes the waiters at once; stores the emulator cannot see (guest code,
/// GPU) are picked up by the waiters' bounded re-check.
/// </summary>
internal static class GuestAddressWatch
{
    /// <summary>The longest a waiter sleeps before re-reading its word.</summary>
    public static readonly TimeSpan RecheckInterval = TimeSpan.FromMilliseconds(1);

    private static readonly object _gate = new();
    private static long _version;

    /// <summary>Changes after every notified write.</summary>
    public static long Version => Interlocked.Read(ref _version);

    public static void NotifyWrite(ulong address, int length)
    {
        _ = address;
        _ = length;
        lock (_gate)
        {
            _version++;
            Monitor.PulseAll(_gate);
        }
    }

    /// <summary>
    /// Sleeps until a write is notified after <paramref name="observedVersion"/>
    /// was read, or until <paramref name="timeout"/> passes.
    /// </summary>
    public static void WaitForWrite(long observedVersion, TimeSpan timeout)
    {
        lock (_gate)
        {
            if (_version == observedVersion)
            {
                Monitor.Wait(_gate, timeout);
            }
        }
    }
}
