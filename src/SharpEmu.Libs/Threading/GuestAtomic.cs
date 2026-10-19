// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.InteropServices;
using SharpEmu.HLE;

namespace SharpEmu.Libs.Threading;

/// <summary>
/// Compare-and-swap on a guest word, atomic with respect to guest code doing its own
/// <c>lock cmpxchg</c> on the same word (the umtx userland fast paths). It runs on the
/// host alias of the guest page via <see cref="ICpuMemory.TryFillInPlace{TState}"/>.
/// Memory that cannot hand out the range in place (test fakes, unbacked ranges) falls
/// back to read/compare/write under one process-wide lock: atomic against other HLE
/// callers only, which is all such memory can be reached by.
/// </summary>
public static class GuestAtomic
{
    private static readonly Lock FallbackGate = new();

    private sealed class CasState
    {
        public uint Expected;
        public uint Desired;
        public uint Observed;
    }

    [ThreadStatic]
    private static CasState? _state;

    private static readonly GuestSpanFill<CasState> CompareExchange32Fill = static (state, destination) =>
    {
        ref var word = ref MemoryMarshal.Cast<byte, uint>(destination)[0];
        state.Observed = Interlocked.CompareExchange(ref word, state.Desired, state.Expected);
        return state.Observed == state.Expected ? sizeof(uint) : 0;
    };

    /// <summary>
    /// Atomically replaces the 32-bit word at <paramref name="address"/> with
    /// <paramref name="desired"/> when it equals <paramref name="expected"/>. Returns false
    /// only when the address is not accessible; <paramref name="observed"/> is the value
    /// seen (equal to <paramref name="expected"/> exactly when the swap happened).
    /// </summary>
    public static bool TryCompareExchange32(CpuContext ctx, ulong address, uint expected, uint desired, out uint observed)
    {
        if ((address & 3) == 0)
        {
            var state = _state ??= new CasState();
            state.Expected = expected;
            state.Desired = desired;
            state.Observed = 0;
            if (ctx.Memory.TryFillInPlace(address, sizeof(uint), state, CompareExchange32Fill, out _))
            {
                observed = state.Observed;
                return true;
            }
        }

        lock (FallbackGate)
        {
            if (!ctx.TryReadUInt32(address, out observed))
            {
                return false;
            }

            if (observed == expected && !ctx.TryWriteUInt32(address, desired))
            {
                return false;
            }

            return true;
        }
    }

    /// <summary>Atomically ORs <paramref name="bits"/> into the word; returns the previous value.</summary>
    public static bool TryOr32(CpuContext ctx, ulong address, uint bits, out uint previous)
    {
        if (!ctx.TryReadUInt32(address, out previous))
        {
            return false;
        }

        while (true)
        {
            if ((previous & bits) == bits)
            {
                return true;
            }

            if (!TryCompareExchange32(ctx, address, previous, previous | bits, out var observed))
            {
                return false;
            }

            if (observed == previous)
            {
                return true;
            }

            previous = observed;
        }
    }

    /// <summary>Atomically clears <paramref name="bits"/> in the word; returns the previous value.</summary>
    public static bool TryAndNot32(CpuContext ctx, ulong address, uint bits, out uint previous)
    {
        if (!ctx.TryReadUInt32(address, out previous))
        {
            return false;
        }

        while (true)
        {
            if ((previous & bits) == 0)
            {
                return true;
            }

            if (!TryCompareExchange32(ctx, address, previous, previous & ~bits, out var observed))
            {
                return false;
            }

            if (observed == previous)
            {
                return true;
            }

            previous = observed;
        }
    }

    /// <summary>Atomically adds <paramref name="delta"/> (two's complement) to the word; returns the previous value.</summary>
    public static bool TryAdd32(CpuContext ctx, ulong address, int delta, out uint previous)
    {
        if (!ctx.TryReadUInt32(address, out previous))
        {
            return false;
        }

        while (true)
        {
            if (!TryCompareExchange32(ctx, address, previous, unchecked(previous + (uint)delta), out var observed))
            {
                return false;
            }

            if (observed == previous)
            {
                return true;
            }

            previous = observed;
        }
    }
}
