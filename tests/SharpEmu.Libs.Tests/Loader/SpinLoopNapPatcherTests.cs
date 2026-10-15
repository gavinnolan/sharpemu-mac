// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Runtime.InteropServices;
using SharpEmu.Core.Loader;
using SharpEmu.Core.Memory;
using SharpEmu.HLE;
using Xunit;

namespace SharpEmu.Libs.Tests.Loader;

[Collection(GuestRedZonePatcherCollection.Name)]
public sealed unsafe class SpinLoopNapPatcherTests
{
    // A threshold far longer than any test loop, so the probe tracks but never sleeps: the patched
    // code must run exactly as the original. The runtime caches the probe, so this must win the
    // first EnsureProbe of the process; the collection serializes these tests to keep it so.
    static SpinLoopNapPatcherTests()
    {
        Environment.SetEnvironmentVariable(SpinNapRuntime.IdleEnvironmentVariable, "100000");
    }

    [Fact]
    public void ProbeIsPositionIndependentAndNonZero()
    {
        if (RuntimeInformation.ProcessArchitecture != Architecture.X64)
            return;

        Assert.NotEmpty(SpinNapRuntime.EnsureProbeBytes(1_000_000_000));
        Assert.Same(SpinNapRuntime.EnsureProbeBytes(1_000_000_000), SpinNapRuntime.EnsureProbeBytes(1_000_000_000));
    }

    [Fact]
    public void TicksForScalesWithFrequencyAndClamps()
    {
        Assert.Equal(500_000L, SpinNapRuntime.TicksFor(500, 1_000_000_000));
        Assert.Equal(1_500_000L, SpinNapRuntime.TicksFor(500, 3_000_000_000));
        Assert.Equal((long)int.MaxValue, SpinNapRuntime.TicksFor(100_000, 100_000_000_000));
    }

    [Theory]
    [InlineData(5, 0, 10, true)]
    [InlineData(15, 0, 10, false)]
    [InlineData(25, 0, 10, true)]
    [InlineData(125, 100, 10, true)]
    public void AbPhaseAlternatesFromTheEpoch(long now, long epoch, long period, bool expected)
    {
        Assert.Equal(expected, SpinNapRuntime.IsNapPhase(now, epoch, period));
    }

    [Fact]
    public void PatchesAPauseInABackwardLoopAndStaysTransparent()
    {
        if (RuntimeInformation.ProcessArchitecture != Architecture.X64)
            return;

        // mov ecx, 4 ; loop: pause ; dec ecx ; jnz loop ; mov eax, 0x11223344 ; ret
        byte[] function =
        [
            0xB9, 0x04, 0x00, 0x00, 0x00, // mov ecx, 4
            0xF3, 0x90,                   // loop: pause
            0xFF, 0xC9,                   // dec ecx
            0x75, 0xFB,                   // jnz loop (-5 -> the pause)
            0xB8, 0x44, 0x33, 0x22, 0x11, // mov eax, 0x11223344
            0xC3,                         // ret
        ];

        using var image = SpinImage.Create(function);
        Assert.Equal(1, image.Result.PauseSites);
        Assert.Equal(1, image.Result.PatchedSites);
        Assert.Equal(0, image.Result.FailedSites);
        Assert.Equal(0, image.Result.NonLoopSites);

        // The pause (offset 5) is now the start of a jmp into the trampoline.
        Assert.Equal(0xE9, image.ReadByte(5));
        var trampoline = (ulong)((long)image.Base + 5 + 5 + BinaryPrimitives.ReadInt32LittleEndian(image.Read(6, 4)));

        // Trampoline lead: lea rsp,[rsp-256]; call rel32; lea rsp,[rsp+256].
        var lead = image.ReadAbsolute(trampoline, 21);
        Assert.Equal(new byte[] { 0x48, 0x8D, 0xA4, 0x24, 0x00, 0xFF, 0xFF, 0xFF }, lead[..8]);
        Assert.Equal(0xE8, lead[8]);
        var probe = (ulong)((long)trampoline + 8 + 5 + BinaryPrimitives.ReadInt32LittleEndian(lead.AsSpan(9, 4)));
        // The probe is copied to the arena start, below the first trampoline; the call must reach it.
        Assert.True(probe < trampoline);
        Assert.Equal(SpinNapRuntime.EnsureProbeBytes(1_000_000_000), image.ReadAbsolute(probe, SpinNapRuntime.EnsureProbeBytes(1_000_000_000).Length));
        Assert.Equal(new byte[] { 0x48, 0x8D, 0xA4, 0x24, 0x00, 0x01, 0x00, 0x00 }, lead[13..21]);

        Assert.Equal(0x11223344u, ((delegate* unmanaged<uint>)image.Base)());
    }

    [Fact]
    public void LeavesAPauseThatDoesNotCloseALoopAlone()
    {
        if (RuntimeInformation.ProcessArchitecture != Architecture.X64)
            return;

        // mov eax, 7 ; pause ; ret  (no backward branch)
        byte[] function = [0xB8, 0x07, 0x00, 0x00, 0x00, 0xF3, 0x90, 0xC3];

        using var image = SpinImage.Create(function);
        Assert.Equal(1, image.Result.PauseSites);
        Assert.Equal(0, image.Result.PatchedSites);
        Assert.Equal(1, image.Result.NonLoopSites);
        Assert.Equal(0xF3, image.ReadByte(5));
        Assert.Equal(7u, ((delegate* unmanaged<uint>)image.Base)());
    }

    private sealed class SpinImage : IDisposable
    {
        private const ulong ImageSize = 0x10000;
        private readonly PhysicalVirtualMemory _memory;

        private SpinImage(PhysicalVirtualMemory memory, ulong imageBase, GuestRedZonePatcher.SpinPatchResult result)
        {
            _memory = memory;
            Base = imageBase;
            Result = result;
        }

        public ulong Base { get; }

        public GuestRedZonePatcher.SpinPatchResult Result { get; }

        public static SpinImage Create(byte[] function)
        {
            var memory = new PhysicalVirtualMemory();
            const ulong holeSize = 0x4000000;
            var hole = (ulong)HostMemory.Alloc(null, (nuint)holeSize, HostMemory.MEM_RESERVE, HostMemory.PAGE_NOACCESS);
            Assert.NotEqual(0UL, hole);
            Assert.True(HostMemory.Free((void*)hole, 0, HostMemory.MEM_RELEASE));
            var imageBase = memory.AllocateAt(hole, ImageSize);
            Assert.True(memory.TryWrite(imageBase, function));

            var exceptionFrameHeader = new byte[32];
            exceptionFrameHeader[0] = 1;
            exceptionFrameHeader[2] = 3;
            BinaryPrimitives.WriteUInt32LittleEndian(exceptionFrameHeader.AsSpan(12), 1);
            BinaryPrimitives.WriteUInt64LittleEndian(exceptionFrameHeader.AsSpan(16), imageBase);
            Assert.True(memory.TryWrite(imageBase + 0x1000, exceptionFrameHeader));
            ProgramHeader[] headers =
            [
                CreateProgramHeader(ProgramHeaderType.Load, ProgramHeaderFlags.Read | ProgramHeaderFlags.Execute,
                    0, (ulong)function.Length),
                CreateProgramHeader(ProgramHeaderType.GnuEhFrame, ProgramHeaderFlags.Read, 0x1000, (ulong)exceptionFrameHeader.Length),
            ];

            var probe = SpinNapRuntime.EnsureProbeBytes(1_000_000_000);
            var result = GuestRedZonePatcher.PatchSpinLoops(memory, memory, headers, imageBase, ImageSize, probe, "test");
            return new SpinImage(memory, imageBase, result);
        }

        public byte ReadByte(ulong offset) => Read(offset, 1)[0];

        public byte[] Read(ulong offset, int length)
        {
            var value = new byte[length];
            Assert.True(_memory.TryRead(Base + offset, value));
            return value;
        }

        public byte[] ReadAbsolute(ulong address, int length)
        {
            var value = new byte[length];
            Assert.True(_memory.TryRead(address, value));
            return value;
        }

        public void Dispose() => _memory.Dispose();
    }

    private static ProgramHeader CreateProgramHeader(
        ProgramHeaderType type, ProgramHeaderFlags flags, ulong address, ulong size)
    {
        Span<byte> bytes = stackalloc byte[56];
        bytes.Clear();
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, (uint)type);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes[4..], (uint)flags);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes[16..], address);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes[32..], size);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes[40..], size);
        return MemoryMarshal.Read<ProgramHeader>(bytes);
    }
}
