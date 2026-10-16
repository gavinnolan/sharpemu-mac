// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Numerics;

namespace SharpEmu.Libs.Kernel;

/// <summary>
/// The guest's descriptor number space, with FreeBSD <c>fdalloc</c>
/// semantics: a new descriptor is the lowest free number at or above the
/// requested minimum, numbers are reused after close, and the table refuses
/// with EMFILE once full (<c>sys/kern/kern_descrip.c</c>). Files, directories,
/// devices and sockets all draw from this one table so two kinds of object
/// can never share a number.
/// </summary>
internal sealed class GuestFileDescriptorTable
{
    /// <summary>
    /// Per-process descriptor limit (RLIMIT_NOFILE). The console's exact value
    /// isn't documented; this is well above what a title keeps open and well
    /// below anything a host struggles with.
    /// </summary>
    public const int DefaultLimit = 4096;

    /// <summary>stdin, stdout and stderr: reserved, never handed out by open.</summary>
    public const int FirstAllocatable = 3;

    private readonly object _gate = new();
    private readonly ulong[] _words;
    private readonly int _limit;

    public GuestFileDescriptorTable(int limit = DefaultLimit)
    {
        if (limit <= FirstAllocatable)
        {
            throw new ArgumentOutOfRangeException(nameof(limit));
        }

        _limit = limit;
        _words = new ulong[(limit + 63) / 64];
        for (var fd = 0; fd < FirstAllocatable; fd++)
        {
            Mark(fd);
        }
    }

    public int Limit => _limit;

    /// <summary>
    /// Allocates the lowest free descriptor &gt;= <paramref name="minimum"/>
    /// (and &gt;= 3). False means EMFILE, or EINVAL when the minimum itself is
    /// outside the table.
    /// </summary>
    public bool TryAllocate(out int fd, int minimum = FirstAllocatable)
    {
        fd = -1;
        var start = Math.Max(minimum, FirstAllocatable);
        if (start >= _limit)
        {
            return false;
        }

        lock (_gate)
        {
            var word = start >> 6;
            var free = ~_words[word] & (ulong.MaxValue << (start & 63));
            while (true)
            {
                if (free != 0)
                {
                    var candidate = (word << 6) + BitOperations.TrailingZeroCount(free);
                    if (candidate >= _limit)
                    {
                        return false;
                    }

                    Mark(candidate);
                    fd = candidate;
                    return true;
                }

                if (++word >= _words.Length)
                {
                    return false;
                }

                free = ~_words[word];
            }
        }
    }

    /// <summary>
    /// Claims a specific number (dup2 target). True when it is in range; a
    /// number already in use stays in use.
    /// </summary>
    public bool TryClaim(int fd)
    {
        if (fd < 0 || fd >= _limit)
        {
            return false;
        }

        lock (_gate)
        {
            Mark(fd);
        }

        return true;
    }

    /// <summary>Returns a descriptor number to the free pool.</summary>
    public void Release(int fd)
    {
        if (fd < FirstAllocatable || fd >= _limit)
        {
            return;
        }

        lock (_gate)
        {
            _words[fd >> 6] &= ~(1UL << (fd & 63));
        }
    }

    public bool IsAllocated(int fd)
    {
        if (fd < 0 || fd >= _limit)
        {
            return false;
        }

        lock (_gate)
        {
            return (_words[fd >> 6] & (1UL << (fd & 63))) != 0;
        }
    }

    private void Mark(int fd) => _words[fd >> 6] |= 1UL << (fd & 63);
}
