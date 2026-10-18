// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Runtime.InteropServices;
using SharpEmu.Core.Cpu.Native;
using SharpEmu.Core.Loader;
using SharpEmu.Core.Memory;
using SharpEmu.HLE;
using Xunit;

namespace SharpEmu.Libs.Tests.Loader;

public sealed class GuestSyscallPatcherTests
{
    private const int MaxNumber = 0x2D2;

    public static TheoryData<byte[], int, int> Idioms => new()
    {
        // FreeBSD RSYSCALL: mov rax, 3; mov r10, rcx; syscall; jb rel32
        { [0x48, 0xC7, 0xC0, 0x03, 0x00, 0x00, 0x00, 0x49, 0x89, 0xCA, 0x0F, 0x05, 0x0F, 0x82, 0, 0, 0, 0], 10, 3 },
        // mov eax, 4; mov r10, rcx; syscall
        { [0xB8, 0x04, 0x00, 0x00, 0x00, 0x49, 0x89, 0xCA, 0x0F, 0x05], 8, 4 },
        // mov eax, 0x1DD; mov r10, rcx (4C 8B D1 form); syscall
        { [0xB8, 0xDD, 0x01, 0x00, 0x00, 0x4C, 0x8B, 0xD1, 0x0F, 0x05], 8, 0x1DD },
        // mov r10, rcx; mov eax, 0x2D2; syscall
        { [0x49, 0x89, 0xCA, 0xB8, 0xD2, 0x02, 0x00, 0x00, 0x0F, 0x05], 8, 0x2D2 },
        // mov eax, 20; syscall; ret
        { [0xB8, 0x14, 0x00, 0x00, 0x00, 0x0F, 0x05, 0xC3], 5, 0x14 },
        // mov eax, 0; syscall; jb rel8
        { [0x90, 0xB8, 0x00, 0x00, 0x00, 0x00, 0x0F, 0x05, 0x72, 0x05], 6, 0 },
    };

    [Theory]
    [MemberData(nameof(Idioms))]
    public void FindsTheWrapperIdioms(byte[] code, int syscallOffset, int number)
    {
        var site = Assert.Single(GuestSyscallScanner.Scan(code, MaxNumber));
        Assert.Equal(syscallOffset, site.Offset);
        Assert.Equal(number, site.Number);
    }

    public static TheoryData<byte[]> NonSites => new()
    {
        // 0F 05 alone.
        new byte[] { 0x90, 0x0F, 0x05, 0xC3 },
        // Inside an immediate: mov eax, 0x0000050F.
        new byte[] { 0xB8, 0x0F, 0x05, 0x00, 0x00, 0xC3 },
        // Inside a displacement: mov eax, [rax+0x50F].
        new byte[] { 0x8B, 0x80, 0x0F, 0x05, 0x00, 0x00 },
        // Number out of range: mov eax, 0x2D3; mov r10, rcx; syscall.
        new byte[] { 0xB8, 0xD3, 0x02, 0x00, 0x00, 0x49, 0x89, 0xCA, 0x0F, 0x05 },
        // Sign-extended negative: mov rax, -1; mov r10, rcx; syscall.
        new byte[] { 0x48, 0xC7, 0xC0, 0xFF, 0xFF, 0xFF, 0xFF, 0x49, 0x89, 0xCA, 0x0F, 0x05 },
        // Bare load with nothing a stub does next.
        new byte[] { 0xB8, 0x14, 0x00, 0x00, 0x00, 0x0F, 0x05, 0x90 },
        // Bare load at the very end of the segment (trailer unknown).
        new byte[] { 0xB8, 0x14, 0x00, 0x00, 0x00, 0x0F, 0x05 },
        // Load truncated at the start of the buffer.
        new byte[] { 0x00, 0x00, 0x00, 0x49, 0x89, 0xCA, 0x0F, 0x05 },
        // r10 move from the wrong register (mov r10, rdx).
        new byte[] { 0xB8, 0x04, 0x00, 0x00, 0x00, 0x49, 0x89, 0xD2, 0x0F, 0x05 },
    };

    [Theory]
    [MemberData(nameof(NonSites))]
    public void IgnoresBytesOutsideTheIdiom(byte[] code)
    {
        Assert.Empty(GuestSyscallScanner.Scan(code, MaxNumber));
    }

    [Fact]
    public void FindsEverySiteInAStubRun()
    {
        byte[] stub = [0x48, 0xC7, 0xC0, 0, 0, 0, 0, 0x49, 0x89, 0xCA, 0x0F, 0x05, 0x72, 0x01, 0xC3, 0xCC];
        var code = new byte[stub.Length * 3];
        for (var i = 0; i < 3; i++)
        {
            stub.CopyTo(code, i * stub.Length);
            code[i * stub.Length + 3] = (byte)(i + 1);
        }

        var sites = GuestSyscallScanner.Scan(code, MaxNumber);

        Assert.Equal([1, 2, 3], sites.Select(s => s.Number));
        Assert.Equal([10, 26, 42], sites.Select(s => s.Offset));
    }

    [Fact]
    public void PatchesOnlyExecutableSegmentsAndBindsTheTrapStub()
    {
        const ulong imageBase = 0x40_0000;
        byte[] code = [0xCC, 0xB8, 0x14, 0x00, 0x00, 0x00, 0x49, 0x89, 0xCA, 0x0F, 0x05, 0x72, 0x00, 0xC3];
        var memory = new VirtualMemory();
        // The live loader writes code through PhysicalVirtualMemory's alias, which ignores guest
        // protection; this managed view checks it, so map the code writable. Whether a segment
        // is scanned is decided by its program header alone.
        memory.Map(imageBase + 0x1000, 0x1000, 0, code,
            ProgramHeaderFlags.Read | ProgramHeaderFlags.Write | ProgramHeaderFlags.Execute);
        memory.Map(imageBase + 0x2000, 0x1000, 0, code, ProgramHeaderFlags.Read | ProgramHeaderFlags.Write);
        ProgramHeader[] headers =
        [
            CreateProgramHeader(ProgramHeaderFlags.Read | ProgramHeaderFlags.Execute, 0x1000, (ulong)code.Length),
            CreateProgramHeader(ProgramHeaderFlags.Read | ProgramHeaderFlags.Write, 0x2000, (ulong)code.Length),
        ];
        var importStubs = new Dictionary<ulong, string>();

        var patched = GuestSyscallPatcher.Patch(memory, headers, imageBase, importStubs, moduleManager: null);

        Assert.Equal(1, patched);
        var executable = new byte[code.Length];
        Assert.True(memory.TryRead(imageBase + 0x1000, executable));
        Assert.Equal(0x0F, executable[9]);
        Assert.Equal(0x0B, executable[10]);
        Assert.Equal(code[..9], executable[..9]);
        Assert.Equal(code[11..], executable[11..]);
        var data = new byte[code.Length];
        Assert.True(memory.TryRead(imageBase + 0x2000, data));
        Assert.Equal(code, data);

        Assert.True(GuestSyscallTrap.IsRegisteredSite(imageBase + 0x1009));
        Assert.False(GuestSyscallTrap.IsRegisteredSite(imageBase + 0x2009));
        Assert.Equal(GuestSyscallFrame.TrapNid, importStubs[GuestSyscallTrap.StubAddress]);
    }

    private static ProgramHeader CreateProgramHeader(ProgramHeaderFlags flags, ulong address, ulong size)
    {
        Span<byte> bytes = stackalloc byte[56];
        bytes.Clear();
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, (uint)ProgramHeaderType.Load);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes[4..], (uint)flags);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes[16..], address);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes[32..], size);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes[40..], size);
        return MemoryMarshal.Read<ProgramHeader>(bytes);
    }
}
