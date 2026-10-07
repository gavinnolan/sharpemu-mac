// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
using System.Buffers.Binary;
using SharpEmu.Fonts.Abstractions;
using SharpEmu.Fonts.CoreTextMac;
using SharpEmu.HLE;
using SharpEmu.Libs.Font;
using Xunit;
using Xunit.Abstractions;

namespace SharpEmu.Fonts.Tests;

public sealed class CoreTextGuestAbiTests(ITestOutputHelper output)
{
    private static readonly CoreTextFontFactory Factory = new();
    [Fact]
    public void NativeMemoryFontGuestInstanceAndMetricsSurviveSourceCloseAndGuestDataOverwrite()
    {
        if (Environment.GetEnvironmentVariable("SHARPEMU_TEST_REQUIRE_NATIVE_FONTS") != "1") return;
        Assert.True(OperatingSystem.IsMacOS());
        GuestFontServices.Initialize(Factory);
        var bytes = File.ReadAllBytes("/System/Library/Fonts/Supplemental/Arial.ttf");
        using var expected = Factory.CreateMemoryFace(bytes, 0);
        Assert.NotNull(expected);
        var memory = new Memory();
        var ctx = new CpuContext(memory, Generation.Gen5);
        Assert.True(memory.TryWrite(Memory.Base + 0x1000, bytes));
        ctx[CpuRegister.Rsi] = Memory.Base + 0x1000;
        ctx[CpuRegister.Rdx] = (uint)bytes.Length;
        ctx[CpuRegister.R8] = Memory.Base + 0x100;
        Assert.Equal(0, FontExports.OpenFontMemory(ctx));
        Assert.True(ctx.TryReadUInt64(Memory.Base + 0x100, out var source));
        ctx[CpuRegister.Rdi] = source;
        ctx[CpuRegister.Rsi] = 0;
        ctx[CpuRegister.Rdx] = Memory.Base + 0x108;
        Assert.Equal(0, FontExports.OpenFontInstance(ctx));
        Assert.True(ctx.TryReadUInt64(Memory.Base + 0x108, out var instance));
        Assert.NotEqual(source, instance);
        Assert.True(memory.TryWrite(Memory.Base + 0x1000, new byte[bytes.Length]));
        ctx[CpuRegister.Rdi] = source;
        Assert.Equal(0, FontExports.CloseFont(ctx));
        Assert.Equal(0, FontExports.CloseFont(ctx));
        ctx[CpuRegister.Rdi] = instance;
        ctx[CpuRegister.Rsi] = 'F';
        ctx[CpuRegister.Rdx] = Memory.Base + 0x200;
        Assert.Equal(0, FontExports.GetCharGlyphMetrics(ctx));
        var metrics = new byte[32]; Assert.True(memory.TryRead(Memory.Base + 0x200, metrics));
        var actual = expected.GetGlyph(FontCharacter.FromUnicode('F'), 16, 16);
        Assert.Equal((float)actual.Width, BinaryPrimitives.ReadSingleLittleEndian(metrics));
        Assert.Equal((float)actual.Height, BinaryPrimitives.ReadSingleLittleEndian(metrics.AsSpan(4)));
        Assert.Equal(actual.BearingX, BinaryPrimitives.ReadSingleLittleEndian(metrics.AsSpan(8)));
        Assert.Equal(actual.BearingY, BinaryPrimitives.ReadSingleLittleEndian(metrics.AsSpan(12)));
        Assert.Equal(actual.Advance, BinaryPrimitives.ReadSingleLittleEndian(metrics.AsSpan(16)));
        Assert.Equal(0, FontExports.CloseFont(ctx));
        Assert.Equal(0, FontExports.CloseFont(ctx));
        Assert.DoesNotContain(typeof(FontExports).Assembly.GetReferencedAssemblies(), reference => reference.Name == "SkiaSharp");
        output.WriteLine("Actual CoreText guest OpenFontMemory→allocated instance→guest byte overwrite→source double-close→exact32-byte glyph ABI metrics→instance double-close passed, no shared Skia reference.");
    }
    private sealed class Memory : ICpuMemory, IGuestMemoryAllocator
    {
        public const ulong Base = 0x100000000;
        private readonly byte[] bytes = new byte[4 * 1024 * 1024];
        private ulong cursor = Base + 3 * 1024 * 1024;
        public bool CanRead(ulong address, ulong count) => address >= Base && count <= (ulong)bytes.Length && address - Base <= (ulong)bytes.Length - count;
        public bool TryRead(ulong address, Span<byte> result)
        {
            if (!CanRead(address, (ulong)result.Length)) return false;
            bytes.AsSpan((int)(address - Base), result.Length).CopyTo(result); return true;
        }
        public bool TryWrite(ulong address, ReadOnlySpan<byte> input)
        {
            if (!CanRead(address, (ulong)input.Length)) return false;
            input.CopyTo(bytes.AsSpan((int)(address - Base), input.Length)); return true;
        }
        public bool TryFreeGuestMemory(ulong address) => false; // exports retain their existing opaque allocation policy
        public bool TryAllocateGuestMemory(ulong size, ulong alignment, out ulong address)
        {
            address = (cursor + alignment - 1) & ~(alignment - 1);
            if (!CanRead(address, size)) return false;
            cursor = address + size; return true;
        }
    }
}
