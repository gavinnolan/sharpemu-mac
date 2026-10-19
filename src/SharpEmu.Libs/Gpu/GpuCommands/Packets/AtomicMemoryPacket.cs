// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;

namespace SharpEmu.Libs.Gpu.GpuCommands.Packets;

public readonly record struct AtomicMemoryPacket(
    uint RawOperation,
    uint Command,
    uint CachePolicy,
    ulong Address,
    ulong SourceData,
    ulong CompareData,
    uint LoopIntervalCycles,
    uint EngineSelection)
{
    public uint BaseOperation => RawOperation & 0x1Fu;

    public bool Is64Bit => (RawOperation & 0x20u) != 0;

    public bool ReturnsData => (RawOperation & 0x40u) == 0;

    public int ByteCount => Is64Bit ? sizeof(ulong) : sizeof(uint);

    public bool IsCompareSwap => BaseOperation == 8;

    public bool IsIntegerOperation =>
        BaseOperation is 7 or 8 || BaseOperation is >= 15 and <= 25;

    public bool HasValidCommand => Command switch
    {
        0 => ReturnsData,
        1 => ReturnsData && IsCompareSwap,
        2 or 3 => !ReturnsData,
        _ => false,
    };

    public bool IsAligned =>
        Address != 0 && (Address & (ulong)(ByteCount - 1)) == 0;

    public bool IsSupported =>
        IsIntegerOperation && HasValidCommand && IsAligned;

    public bool HasValidEngine(bool usesAsyncEncoding) =>
        usesAsyncEncoding ? EngineSelection == 0 : EngineSelection <= 1;

    public ulong LoopReferenceValue => Is64Bit ? CompareData : (uint)CompareData;

    public static AtomicMemoryPacket Decode(
        uint control,
        uint addressLow,
        uint addressHigh,
        uint sourceLow,
        uint sourceHigh,
        uint compareLow,
        uint compareHigh,
        uint loopControl) =>
        new(
            control & 0x7Fu,
            (control >> 8) & 0xFu,
            (control >> 25) & 0x3u,
            addressLow | ((ulong)addressHigh << 32),
            sourceLow | ((ulong)sourceHigh << 32),
            compareLow | ((ulong)compareHigh << 32),
            loopControl & 0x1FFFu,
            (control >> 30) & 0x3u);

    /// <summary>ATOMIC_MEM ADD_32 without a return (operation 15, no-return bit, command 2):
    /// what RELEASE_MEM data select 7 does to its destination at end of pipe.</summary>
    public static AtomicMemoryPacket Add32(ulong address, uint addend) =>
        Decode(0x4Fu | (2u << 8), (uint)address, (uint)(address >> 32), addend, 0, 0, 0, 0);

    /// <summary>Adds to a guest dword under the shared atomic gate, as an end-of-pipe completion does.</summary>
    public static bool TryAdd32(ICpuMemory memory, ulong address, uint addend) =>
        Add32(address, addend).TryApply(memory.TryRead, memory, out _, out _, out _);

    // Applies the operation once, read-modify-write, under the shared atomic gate.
    public bool TryApply(
        GuestReader read,
        ICpuMemory memory,
        out ulong priorValue,
        out ulong newValue,
        out bool comparePassed)
    {
        priorValue = 0;
        newValue = 0;
        comparePassed = false;
        if (!IsSupported)
        {
            return false;
        }

        lock (GuestAtomicGate.Lock)
        {
            Span<byte> bytes = stackalloc byte[sizeof(ulong)];
            var valueBytes = bytes[..ByteCount];
            if (!read(Address, valueBytes))
            {
                return false;
            }

            if (Is64Bit)
            {
                priorValue = BinaryPrimitives.ReadUInt64LittleEndian(valueBytes);
                newValue = Apply64(BaseOperation, priorValue, SourceData, CompareData, out comparePassed);
                BinaryPrimitives.WriteUInt64LittleEndian(valueBytes, newValue);
            }
            else
            {
                var prior32 = BinaryPrimitives.ReadUInt32LittleEndian(valueBytes);
                priorValue = prior32;
                newValue = Apply32(BaseOperation, prior32, (uint)SourceData, (uint)CompareData, out comparePassed);
                BinaryPrimitives.WriteUInt32LittleEndian(valueBytes, (uint)newValue);
            }

            return memory.TryWrite(Address, valueBytes);
        }
    }

    private static uint Apply32(uint operation, uint current, uint source, uint compare, out bool comparePassed)
    {
        comparePassed = operation != 8 || current == compare;
        return operation switch
        {
            7 => source,
            8 => comparePassed ? source : current,
            15 => unchecked(current + source),
            16 => unchecked(current - source),
            17 => (int)source < (int)current ? source : current,
            18 => Math.Min(source, current),
            19 => (int)source > (int)current ? source : current,
            20 => Math.Max(source, current),
            21 => current & source,
            22 => current | source,
            23 => current ^ source,
            24 => current >= source ? 0 : unchecked(current + 1),
            25 => current == 0 || current > source ? source : current - 1,
            _ => current,
        };
    }

    private static ulong Apply64(uint operation, ulong current, ulong source, ulong compare, out bool comparePassed)
    {
        comparePassed = operation != 8 || current == compare;
        return operation switch
        {
            7 => source,
            8 => comparePassed ? source : current,
            15 => unchecked(current + source),
            16 => unchecked(current - source),
            17 => (long)source < (long)current ? source : current,
            18 => Math.Min(source, current),
            19 => (long)source > (long)current ? source : current,
            20 => Math.Max(source, current),
            21 => current & source,
            22 => current | source,
            23 => current ^ source,
            24 => current >= source ? 0 : unchecked(current + 1),
            25 => current == 0 || current > source ? source : current - 1,
            _ => current,
        };
    }
}

// One gate for every guest atomic and semaphore update, so concurrent producers serialize.
public static class GuestAtomicGate
{
    public static readonly object Lock = new();
}
