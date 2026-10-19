// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Text;
using SharpEmu.HLE;
using SharpEmu.Libs.Kernel;
using Xunit;

namespace SharpEmu.Libs.Tests.Kernel;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class KernelProcParamStateCollection
{
    public const string Name = "KernelProcParamState";
}

[Collection(KernelProcParamStateCollection.Name)]
public sealed class KernelSystemInfoExportsTests
{
    private const ulong BaseAddress = 0x1_0000;
    private const ulong TlsBase = BaseAddress + 0x100;
    private const ulong NameAddress = BaseAddress + 0x400;
    private const ulong OldAddress = BaseAddress + 0x600;
    private const ulong OldLengthAddress = BaseAddress + 0x700;
    private const ulong NewAddress = BaseAddress + 0x780;
    private const ulong StructAddress = BaseAddress + 0x800;
    private const ulong ProcParamAddress = BaseAddress + 0xC00;

    private static CpuContext NewContext(out FakeCpuMemory memory)
    {
        memory = new FakeCpuMemory(BaseAddress, 0x2000);
        return new CpuContext(memory, Generation.Gen5) { FsBase = TlsBase };
    }

    private static void WriteString(FakeCpuMemory memory, ulong address, string value) =>
        Assert.True(memory.TryWrite(address, Encoding.ASCII.GetBytes(value + "\0")));

    private static void WriteUInt64(FakeCpuMemory memory, ulong address, ulong value)
    {
        Span<byte> bytes = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(bytes, value);
        Assert.True(memory.TryWrite(address, bytes));
    }

    private static ulong ReadUInt64(FakeCpuMemory memory, ulong address)
    {
        Span<byte> bytes = stackalloc byte[8];
        Assert.True(memory.TryRead(address, bytes));
        return BinaryPrimitives.ReadUInt64LittleEndian(bytes);
    }

    private static byte[] ReadBytes(FakeCpuMemory memory, ulong address, int length)
    {
        var bytes = new byte[length];
        Assert.True(memory.TryRead(address, bytes));
        return bytes;
    }

    private static int SysctlByName(CpuContext ctx, FakeCpuMemory memory, string name, ulong capacity, bool withOld = true)
    {
        WriteString(memory, NameAddress, name);
        WriteUInt64(memory, OldLengthAddress, capacity);
        ctx[CpuRegister.Rdi] = NameAddress;
        ctx[CpuRegister.Rsi] = withOld ? OldAddress : 0;
        ctx[CpuRegister.Rdx] = OldLengthAddress;
        ctx[CpuRegister.Rcx] = 0;
        ctx[CpuRegister.R8] = 0;
        return KernelSystemInfoExports.PosixSysctlByName(ctx);
    }

    internal static ulong SysctlByNameUInt64(CpuContext ctx, FakeCpuMemory memory, string name)
    {
        Assert.Equal(0, SysctlByName(ctx, memory, name, 8));
        Assert.Equal(8UL, ReadUInt64(memory, OldLengthAddress));
        return ReadUInt64(memory, OldAddress);
    }

    private static int SysctlMib(CpuContext ctx, FakeCpuMemory memory, int[] mib, ulong capacity, ulong newAddress = 0)
    {
        var raw = new byte[mib.Length * 4];
        for (var i = 0; i < mib.Length; i++)
        {
            BinaryPrimitives.WriteInt32LittleEndian(raw.AsSpan(i * 4), mib[i]);
        }

        Assert.True(memory.TryWrite(NameAddress, raw));
        WriteUInt64(memory, OldLengthAddress, capacity);
        ctx[CpuRegister.Rdi] = NameAddress;
        ctx[CpuRegister.Rsi] = (ulong)mib.Length;
        ctx[CpuRegister.Rdx] = OldAddress;
        ctx[CpuRegister.Rcx] = OldLengthAddress;
        ctx[CpuRegister.R8] = newAddress;
        ctx[CpuRegister.R9] = newAddress == 0 ? 0UL : 4UL;
        return KernelSystemInfoExports.PosixSysctl(ctx);
    }

    [Fact]
    public void HwNcpuByMibIsThirteen()
    {
        var ctx = NewContext(out var memory);
        Assert.Equal(0, SysctlMib(ctx, memory, [6, 3], 4));
        Assert.Equal(13, BinaryPrimitives.ReadInt32LittleEndian(ReadBytes(memory, OldAddress, 4)));
        Assert.Equal(4UL, ReadUInt64(memory, OldLengthAddress));
    }

    [Fact]
    public void HwPagesizeAndPhysmemByName()
    {
        var ctx = NewContext(out var memory);
        Assert.Equal(0, SysctlByName(ctx, memory, "hw.pagesize", 4));
        Assert.Equal(16384, BinaryPrimitives.ReadInt32LittleEndian(ReadBytes(memory, OldAddress, 4)));
        Assert.Equal(16UL << 30, SysctlByNameUInt64(ctx, memory, "hw.physmem"));
    }

    [Fact]
    public void SizeProbeReportsLengthWithoutCopying()
    {
        var ctx = NewContext(out var memory);
        Assert.Equal(0, SysctlByName(ctx, memory, "kern.ostype", 0, withOld: false));
        Assert.Equal((ulong)"FreeBSD\0".Length, ReadUInt64(memory, OldLengthAddress));
    }

    [Fact]
    public void ShortBufferCopiesPrefixAndFailsWithEnomem()
    {
        var ctx = NewContext(out var memory);
        Assert.Equal(-1, SysctlByName(ctx, memory, "kern.ostype", 4));
        Assert.Equal(OrbisErrno.ENOMEM, KernelErrno.Current(ctx));
        Assert.Equal(4UL, ReadUInt64(memory, OldLengthAddress));
        Assert.Equal("Free"u8.ToArray(), ReadBytes(memory, OldAddress, 4));
    }

    [Fact]
    public void UnknownNameAndMibAreEnoent()
    {
        var ctx = NewContext(out var memory);
        Assert.Equal(-1, SysctlByName(ctx, memory, "kern.no_such_node", 8));
        Assert.Equal(OrbisErrno.ENOENT, KernelErrno.Current(ctx));
        Assert.Equal(-1, SysctlMib(ctx, memory, [6, 99], 8));
        Assert.Equal(OrbisErrno.ENOENT, KernelErrno.Current(ctx));
    }

    [Fact]
    public void WritingAReadOnlyNodeIsEperm()
    {
        var ctx = NewContext(out var memory);
        Assert.Equal(-1, SysctlMib(ctx, memory, [6, 3], 4, newAddress: NewAddress));
        Assert.Equal(OrbisErrno.EPERM, KernelErrno.Current(ctx));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(25)]
    public void MibLengthOutOfRangeIsEinval(int length)
    {
        var ctx = NewContext(out var memory);
        Assert.Equal(-1, SysctlMib(ctx, memory, new int[length], 4));
        Assert.Equal(OrbisErrno.EINVAL, KernelErrno.Current(ctx));
    }

    [Fact]
    public void Name2OidResolvesDottedNames()
    {
        var ctx = NewContext(out var memory);
        WriteString(memory, NewAddress, "hw.ncpu");
        Assert.True(memory.TryWrite(NameAddress, new byte[] { 0, 0, 0, 0, 3, 0, 0, 0 }));
        WriteUInt64(memory, OldLengthAddress, 24 * 4);
        ctx[CpuRegister.Rdi] = NameAddress;
        ctx[CpuRegister.Rsi] = 2;
        ctx[CpuRegister.Rdx] = OldAddress;
        ctx[CpuRegister.Rcx] = OldLengthAddress;
        ctx[CpuRegister.R8] = NewAddress;
        ctx[CpuRegister.R9] = (ulong)"hw.ncpu".Length;

        Assert.Equal(0, KernelSystemInfoExports.PosixSysctl(ctx));
        Assert.Equal(8UL, ReadUInt64(memory, OldLengthAddress));
        var oid = ReadBytes(memory, OldAddress, 8);
        Assert.Equal(6, BinaryPrimitives.ReadInt32LittleEndian(oid));
        Assert.Equal(3, BinaryPrimitives.ReadInt32LittleEndian(oid.AsSpan(4)));
    }

    [Fact]
    public void ProcPathnameAcceptsOnlyTheGuestPid()
    {
        var ctx = NewContext(out var memory);
        Assert.Equal(0, SysctlMib(ctx, memory, [1, 14, 12, -1], 64));
        Assert.Equal("/app0/eboot.bin\0"u8.ToArray(), ReadBytes(memory, OldAddress, 16));
        Assert.Equal(0, SysctlMib(ctx, memory, [1, 14, 12, 74], 64));
        Assert.Equal(-1, SysctlMib(ctx, memory, [1, 14, 12, 12345], 64));
        Assert.Equal(OrbisErrno.ESRCH, KernelErrno.Current(ctx));
    }

    [Fact]
    public void SdkVersionComesFromProcParam()
    {
        var ctx = NewContext(out var memory);
        WriteUInt64(memory, ProcParamAddress, 0x40);
        WriteUInt64(memory, ProcParamAddress + 0x10, 0x0400_0031);
        KernelRuntimeCompatExports.ConfigureProcessProcParamAddress(ProcParamAddress);
        try
        {
            Assert.Equal(0, SysctlByName(ctx, memory, "kern.sdk_version", 4));
            Assert.Equal(0x0400_0031u, BinaryPrimitives.ReadUInt32LittleEndian(ReadBytes(memory, OldAddress, 4)));

            ctx[CpuRegister.Rdi] = StructAddress;
            Assert.Equal(0, KernelExports.KernelGetCompiledSdkVersion(ctx));
            Assert.Equal(0x0400_0031u, BinaryPrimitives.ReadUInt32LittleEndian(ReadBytes(memory, StructAddress, 4)));

            ctx[CpuRegister.Rdi] = 0;
            Assert.Equal(
                (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT,
                KernelExports.KernelGetCompiledSdkVersion(ctx));
        }
        finally
        {
            KernelRuntimeCompatExports.ConfigureProcessProcParamAddress(0);
        }
    }

    [Fact]
    public void CompiledSdkVersionWithoutProcParamIsEinval()
    {
        var ctx = NewContext(out _);
        KernelRuntimeCompatExports.ConfigureProcessProcParamAddress(0);
        ctx[CpuRegister.Rdi] = StructAddress;
        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT,
            KernelExports.KernelGetCompiledSdkVersion(ctx));
    }

    [Fact]
    public void ProcessIdentityIsFixed()
    {
        var ctx = NewContext(out _);
        Assert.Equal(74, KernelRuntimeCompatExports.GetProcessId(ctx));
        Assert.Equal(1, KernelSystemInfoExports.GetParentProcessId(ctx));
        Assert.Equal(0, KernelSystemInfoExports.GetUserId(ctx));
        Assert.Equal(0, KernelSystemInfoExports.GetEffectiveGroupId(ctx));
        Assert.Equal(0, KernelSystemInfoExports.KernelIsDevelopmentMode(ctx));
        Assert.Equal(0, KernelSystemInfoExports.KernelGetCpumode(ctx));
    }

    [Fact]
    public void SystemSwVersionFillsTheStruct()
    {
        var ctx = NewContext(out var memory);
        ctx[CpuRegister.Rdi] = StructAddress;
        Assert.Equal(0, KernelSystemInfoExports.KernelGetSystemSwVersion(ctx));
        Assert.Equal(0x28UL, ReadUInt64(memory, StructAddress));
        Assert.Equal("01.000.000\0"u8.ToArray(), ReadBytes(memory, StructAddress + 8, 11));
        Assert.Equal(0x0100_0000u, BinaryPrimitives.ReadUInt32LittleEndian(ReadBytes(memory, StructAddress + 0x24, 4)));

        ctx[CpuRegister.Rdi] = 0;
        Assert.Equal(KernelErrno.Sce(OrbisErrno.EFAULT), KernelSystemInfoExports.KernelGetSystemSwVersion(ctx));
    }

    [Fact]
    public void CurrentCpuIsStablePerThread()
    {
        var first = KernelSystemInfoExports.GetCurrentCpu();
        Assert.InRange(first, 0, 12);
        Assert.Equal(first, KernelSystemInfoExports.GetCurrentCpu());

        var cpus = new int[26];
        var threads = Enumerable.Range(0, cpus.Length)
            .Select(i => new Thread(() => cpus[i] = KernelSystemInfoExports.GetCurrentCpu()))
            .ToArray();
        foreach (var thread in threads) thread.Start();
        foreach (var thread in threads) thread.Join();
        Assert.All(cpus, cpu => Assert.InRange(cpu, 0, 12));
        Assert.True(cpus.Distinct().Count() > 1);
    }

    [Fact]
    public void ThreadNameRoundTripsForTheCallingThread()
    {
        var ctx = NewContext(out var memory);
        WriteString(memory, NameAddress, "RenderWorker");
        ctx[CpuRegister.Rdi] = 0;
        ctx[CpuRegister.Rsi] = NameAddress;
        Assert.Equal(0, KernelSystemInfoExports.KernelSetThreadName(ctx));

        ctx[CpuRegister.Rdi] = 0;
        ctx[CpuRegister.Rsi] = StructAddress;
        Assert.Equal(0, KernelSystemInfoExports.KernelGetThreadName(ctx));
        Assert.Equal("RenderWorker\0"u8.ToArray(), ReadBytes(memory, StructAddress, 13));

        ctx[CpuRegister.Rdi] = 0x7FFF_FFF0;
        ctx[CpuRegister.Rsi] = StructAddress;
        Assert.Equal(KernelErrno.Sce(OrbisErrno.ESRCH), KernelSystemInfoExports.KernelGetThreadName(ctx));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(-1)]
    public void GetrusageFillsTheStruct(int who)
    {
        var ctx = NewContext(out var memory);
        ctx[CpuRegister.Rdi] = unchecked((ulong)who);
        ctx[CpuRegister.Rsi] = StructAddress;
        Assert.Equal(0, KernelSystemInfoExports.PosixGetrusage(ctx));
        var usec = (long)ReadUInt64(memory, StructAddress + 8);
        Assert.InRange(usec, 0, 999_999);
    }

    [Fact]
    public void GetrusageRejectsUnknownWho()
    {
        var ctx = NewContext(out _);
        ctx[CpuRegister.Rdi] = 7;
        ctx[CpuRegister.Rsi] = StructAddress;
        Assert.Equal(-1, KernelSystemInfoExports.PosixGetrusage(ctx));
        Assert.Equal(OrbisErrno.EINVAL, KernelErrno.Current(ctx));
    }

    [Fact]
    public void SigactionRoundTripsAndRejectsSigkill()
    {
        KernelSystemInfoExports.ResetSignalActionsForTests();
        var ctx = NewContext(out var memory);
        var action = new byte[32];
        BinaryPrimitives.WriteUInt64LittleEndian(action, 0x1234_5678);
        BinaryPrimitives.WriteInt32LittleEndian(action.AsSpan(8), 0x40); // SA_SIGINFO
        Assert.True(memory.TryWrite(NewAddress, action.AsSpan(0, 28)));

        ctx[CpuRegister.Rdi] = 11; // SIGSEGV
        ctx[CpuRegister.Rsi] = NewAddress;
        ctx[CpuRegister.Rdx] = OldAddress;
        Assert.Equal(0, KernelSystemInfoExports.PosixSigaction(ctx));
        Assert.Equal(0UL, ReadUInt64(memory, OldAddress)); // SIG_DFL before

        ctx[CpuRegister.Rdi] = 11;
        ctx[CpuRegister.Rsi] = 0;
        ctx[CpuRegister.Rdx] = OldAddress;
        Assert.Equal(0, KernelSystemInfoExports.PosixSigaction(ctx));
        Assert.Equal(0x1234_5678UL, ReadUInt64(memory, OldAddress));
        Assert.Equal(0x1234_5678UL, KernelSystemInfoExports.GetSignalHandler(11));

        ctx[CpuRegister.Rdi] = 9; // SIGKILL
        ctx[CpuRegister.Rsi] = NewAddress;
        ctx[CpuRegister.Rdx] = 0;
        Assert.Equal(-1, KernelSystemInfoExports.PosixSigaction(ctx));
        Assert.Equal(OrbisErrno.EINVAL, KernelErrno.Current(ctx));

        ctx[CpuRegister.Rdi] = 0;
        Assert.Equal(-1, KernelSystemInfoExports.PosixSigaction(ctx));
        KernelSystemInfoExports.ResetSignalActionsForTests();
    }

    [Fact]
    public void MsyncRejectsAsyncWithInvalidate()
    {
        var ctx = NewContext(out _);
        ctx[CpuRegister.Rdi] = 0x10_0000_0000;
        ctx[CpuRegister.Rsi] = 0x4000;
        ctx[CpuRegister.Rdx] = 0;
        Assert.Equal(0, KernelSystemInfoExports.KernelMsync(ctx));
        ctx[CpuRegister.Rdx] = 3;
        Assert.Equal(KernelErrno.Sce(OrbisErrno.EINVAL), KernelSystemInfoExports.KernelMsync(ctx));

        ctx[CpuRegister.Rdi] = ulong.MaxValue - 0x100;
        ctx[CpuRegister.Rsi] = 0x10000;
        Assert.Equal(KernelErrno.Sce(OrbisErrno.EINVAL), KernelSystemInfoExports.KernelMlock(ctx));
        ctx[CpuRegister.Rdi] = 0x10_0000_0000;
        Assert.Equal(0, KernelSystemInfoExports.KernelMlock(ctx));
    }
}
