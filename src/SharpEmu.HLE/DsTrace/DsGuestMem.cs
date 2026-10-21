// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.InteropServices;

namespace SharpEmu.HLE.DsTrace;

/// <summary>
/// Guest-memory reads for DSTRACE. Guest virtual addresses are host addresses.
///
/// Default (safe) mode reads through <see cref="ICpuMemory.TryRead"/>: never a raw
/// dereference, so a page whose protection changes at runtime (page watch for GPU
/// dirty tracking) cannot fault the sampler or host threads. The cost is that reads
/// that miss the lock-free single-view path take the address-space read lock.
///
/// SHARPEMU_DSTRACE_RAW_READS=1 switches to the spec's validated raw reads: a page
/// is validated once through TryRead into a 64 Ki-entry direct-mapped page cache,
/// then read with a raw load. Never use either mode on execute-only guest code.
/// </summary>
public static unsafe class DsGuestMem
{
    private const int PageCacheMask = (1 << 16) - 1;
    private static ICpuMemory? _memory;
    private static readonly ulong[]? _pageCache = DsTrace.RawReads ? new ulong[PageCacheMask + 1] : null;

    public static void Init(ICpuMemory? memory)
    {
        if (DsTrace.On && memory is not null)
        {
            _memory = memory;
        }
    }

    public static bool Ready => _memory is not null;

    public static bool TryRead64(ulong address, out ulong value)
    {
        value = 0;
        if ((address & 7) != 0 || address < 0x10000)
        {
            return false;
        }

        var memory = _memory;
        if (memory is null)
        {
            return false;
        }

        if (_pageCache is { } cache && IsValidatedPage(cache, memory, address))
        {
            value = *(ulong*)address;
            return true;
        }

        Span<byte> buffer = stackalloc byte[8];
        if (!memory.TryRead(address, buffer))
        {
            return false;
        }

        value = MemoryMarshal.Read<ulong>(buffer);
        return true;
    }

    public static bool TryRead32(ulong address, out uint value)
    {
        value = 0;
        if ((address & 3) != 0 || address < 0x10000)
        {
            return false;
        }

        var memory = _memory;
        if (memory is null)
        {
            return false;
        }

        if (_pageCache is { } cache && IsValidatedPage(cache, memory, address))
        {
            value = *(uint*)address;
            return true;
        }

        Span<byte> buffer = stackalloc byte[4];
        if (!memory.TryRead(address, buffer))
        {
            return false;
        }

        value = MemoryMarshal.Read<uint>(buffer);
        return true;
    }

    /// <summary>Bulk read (one TryRead call in safe mode).</summary>
    public static bool TryReadBlock(ulong address, Span<byte> destination)
    {
        var memory = _memory;
        if (memory is null || address < 0x10000 || destination.IsEmpty)
        {
            return false;
        }

        if (_pageCache is { } cache &&
            ((address ^ (address + (ulong)destination.Length - 1)) >> 12) == 0 &&
            IsValidatedPage(cache, memory, address))
        {
            new ReadOnlySpan<byte>((void*)address, destination.Length).CopyTo(destination);
            return true;
        }

        return memory.TryRead(address, destination);
    }

    public static ulong R64(ulong address) => TryRead64(address, out var value) ? value : 0;

    public static uint R32(ulong address) => TryRead32(address, out var value) ? value : 0;

    private static bool IsValidatedPage(ulong[] cache, ICpuMemory memory, ulong address)
    {
        var page = address >> 12;
        var slot = (int)(page & PageCacheMask);
        if (cache[slot] == page + 1)
        {
            return true;
        }

        Span<byte> probe = stackalloc byte[8];
        if (!memory.TryRead(address & ~7UL, probe))
        {
            return false;
        }

        cache[slot] = page + 1;
        return true;
    }

    /// <summary>
    /// Frame-pointer return-address chain. Fills <paramref name="returns"/> with
    /// RA2, RA3, … from <paramref name="rbp"/>; missing values stay 0. Returns the count.
    /// </summary>
    public static int ReturnChain(ulong rbp, ulong rsp, Span<ulong> returns)
    {
        returns.Clear();
        var count = 0;
        for (var k = 0; k < returns.Length; k++)
        {
            if (rbp == 0 || (rbp & 7) != 0 || !TryRead64(rbp + 8, out var ra))
            {
                break;
            }

            returns[k] = ra;
            count++;
            if (k + 1 == returns.Length || !TryRead64(rbp, out var next) ||
                next <= rbp || next - rsp >= 0x20000 || (next & 7) != 0)
            {
                break;
            }

            rbp = next;
        }

        return count;
    }
}
