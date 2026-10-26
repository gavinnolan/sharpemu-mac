// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Kernel;
using Xunit;

namespace SharpEmu.Libs.Tests.Kernel;

public sealed class MemsetInPlaceTests
{
    private const ulong Base = 0x2_7000_0000;

    [Theory]
    [InlineData(0x10000UL, 0x7Fu)]
    [InlineData(0x30001UL, 0x00u)]
    [InlineData(0x1000UL, 0xA5u)]
    public void LargeAndSmallFillsWriteTheSameBytesInPlaceOrStaged(ulong length, uint value)
    {
        var inPlace = Run(length, (byte)value, supportsInPlace: true, out var usedInPlace);
        var staged = Run(length, (byte)value, supportsInPlace: false, out _);

        Assert.Equal(staged, inPlace);
        Assert.Equal(length >= 0x10000, usedInPlace);
        // The fill covers exactly [dst, dst + length) and leaves the neighbours alone.
        Assert.Equal(0x11, inPlace[0x1FF]);
        Assert.Equal((byte)value, inPlace[0x200]);
        Assert.Equal((byte)value, inPlace[0x200 + (int)length - 1]);
        Assert.Equal(0x11, inPlace[0x200 + (int)length]);
    }

    private static byte[] Run(ulong length, byte value, bool supportsInPlace, out bool usedInPlace)
    {
        var memory = new InPlaceMemory(Base, 0x40000, supportsInPlace);
        memory.Storage.AsSpan().Fill(0x11);
        var context = new CpuContext(memory, Generation.Gen5)
        {
            [CpuRegister.Rdi] = Base + 0x200,
            [CpuRegister.Rsi] = value,
            [CpuRegister.Rdx] = length,
        };
        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, KernelMemoryCompatExports.Memset(context));
        Assert.Equal(Base + 0x200, context[CpuRegister.Rax]);
        usedInPlace = memory.InPlaceFills == 1;
        return memory.Storage;
    }

    private sealed class InPlaceMemory(ulong baseAddress, int size, bool supportsInPlace) : ICpuMemory
    {
        public byte[] Storage { get; } = new byte[size];

        public int InPlaceFills { get; private set; }

        public bool TryRead(ulong address, Span<byte> destination)
        {
            if (address < baseAddress || address - baseAddress + (ulong)destination.Length > (ulong)Storage.Length)
            {
                return false;
            }

            Storage.AsSpan((int)(address - baseAddress), destination.Length).CopyTo(destination);
            return true;
        }

        public bool TryWrite(ulong address, ReadOnlySpan<byte> source)
        {
            if (address < baseAddress || address - baseAddress + (ulong)source.Length > (ulong)Storage.Length)
            {
                return false;
            }

            source.CopyTo(Storage.AsSpan((int)(address - baseAddress)));
            return true;
        }

        public bool TryFillInPlace<TState>(ulong address, int length, TState state, GuestSpanFill<TState> fill, out int filled)
        {
            filled = 0;
            if (!supportsInPlace || address < baseAddress || address - baseAddress + (ulong)length > (ulong)Storage.Length)
            {
                return false;
            }

            InPlaceFills++;
            filled = fill(state, Storage.AsSpan((int)(address - baseAddress), length));
            return true;
        }
    }
}
