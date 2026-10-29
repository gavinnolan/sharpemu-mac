// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.InteropServices;

namespace SharpEmu.PerfAudit;

/// <summary>One native block seen as guest memory at <see cref="Base"/>; no tracking, no wrappers.</summary>
internal sealed unsafe class NativeGuestMemory : IDisposable
{
    public const ulong Base = 0x2_0000_0000;
    private readonly byte* _block;
    private readonly ulong _size;

    public NativeGuestMemory(ulong size)
    {
        _size = size;
        _block = (byte*)NativeMemory.AlignedAlloc((nuint)size, 16384);
        NativeMemory.Clear(_block, (nuint)size);
    }

    public byte* Raw(ulong address) => _block + (address - Base);
    public ulong Size => _size;
    public void Dispose() => NativeMemory.AlignedFree(_block);

    public bool TryRead(ulong address, Span<byte> destination)
    {
        if (address < Base || address - Base + (ulong)destination.Length > _size) return false;
        new ReadOnlySpan<byte>(_block + (address - Base), destination.Length).CopyTo(destination);
        return true;
    }

    public bool TryWrite(ulong address, ReadOnlySpan<byte> source)
    {
        if (address < Base || address - Base + (ulong)source.Length > _size) return false;
        source.CopyTo(new Span<byte>(_block + (address - Base), source.Length));
        return true;
    }

    public bool CanRead(ulong address, ulong size) => address >= Base && address - Base + size <= _size;

    public void WriteWords(ulong address, ReadOnlySpan<uint> words) => TryWrite(address, MemoryMarshal.AsBytes(words));
}
