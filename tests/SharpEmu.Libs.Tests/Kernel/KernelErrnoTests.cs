// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Kernel;
using Xunit;

namespace SharpEmu.Libs.Tests.Kernel;

public sealed class KernelErrnoTests
{
    private const ulong BaseAddress = 0x1_0000;
    private const ulong TlsBase = BaseAddress + 0x100;

    private static CpuContext NewContext(out FakeCpuMemory memory)
    {
        memory = new FakeCpuMemory(BaseAddress, 0x2000);
        return new CpuContext(memory, Generation.Gen5) { FsBase = TlsBase };
    }

    // FreeBSD 11 sys/sys/errno.h: EPERM 1, ENOENT 2, ESRCH 3, EFAULT 14,
    // EEXIST 17, EINVAL 22; SCE_KERNEL_ERROR_* = 0x80020000 | errno.
    [Theory]
    [InlineData(OrbisGen2Result.ORBIS_GEN2_ERROR_PERMISSION_DENIED, 0x80020001u)]
    [InlineData(OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND, 0x80020002u)]
    [InlineData(OrbisGen2Result.ORBIS_GEN2_ERROR_ESRCH, 0x80020003u)]
    [InlineData(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT, 0x8002000Eu)]
    [InlineData(OrbisGen2Result.ORBIS_GEN2_ERROR_ALREADY_EXISTS, 0x80020011u)]
    [InlineData(OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT, 0x80020016u)]
    [InlineData(OrbisGen2Result.ORBIS_GEN2_ERROR_TRY_AGAIN, 0x80020023u)]
    [InlineData(OrbisGen2Result.ORBIS_GEN2_ERROR_TIMED_OUT, 0x8002003Cu)]
    public void EnumMembersAreKernelErrnoCodes(OrbisGen2Result result, uint expected)
    {
        Assert.Equal(expected, unchecked((uint)(int)result));
    }

    [Fact]
    public void EveryErrnoBackedEnumMemberRoundTrips()
    {
        foreach (var value in Enum.GetValues<OrbisGen2Result>())
        {
            if (value is OrbisGen2Result.ORBIS_GEN2_OK
                or OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_IMPLEMENTED
                or OrbisGen2Result.ORBIS_GEN2_ERROR_CPU_TRAP)
            {
                continue;
            }

            Assert.True(OrbisErrno.TryGetErrno((int)value, out var errno), value.ToString());
            Assert.Equal((int)value, OrbisErrno.ToSceKernelError(errno));
        }
    }

    [Fact]
    public void EnumHasNoAliasedValues()
    {
        var values = Enum.GetValues<OrbisGen2Result>();
        Assert.Equal(values.Length, values.Distinct().Count());
    }

    [Theory]
    [InlineData(unchecked((int)0x80920001))] // another facility (Pad)
    [InlineData(unchecked((int)0x80020000))] // facility base, no errno
    [InlineData(unchecked((int)0x80020061))] // past ELAST
    [InlineData(unchecked((int)0x80020102))] // CPU_TRAP
    [InlineData(-1)]
    public void NonErrnoResultsAreNotDecoded(int result)
    {
        Assert.False(OrbisErrno.TryGetErrno(result, out _));
        Assert.Equal(OrbisErrno.EIO, OrbisErrno.ToErrno(result, OrbisErrno.EIO));
    }

    [Fact]
    public void NotImplementedBecomesEnosys()
    {
        Assert.Equal(
            OrbisErrno.ENOSYS,
            OrbisErrno.ToErrno((int)OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_IMPLEMENTED));
    }

    [Fact]
    public void SceFlavorReturnsCodeAndLeavesErrnoAlone()
    {
        var ctx = NewContext(out _);
        KernelRuntimeCompatExports.TrySetErrno(ctx, 0);

        var result = KernelErrno.Fail(ctx, KernelAbiFlavor.Sce, OrbisErrno.EBADF);

        Assert.Equal(unchecked((int)0x80020009), result);
        Assert.Equal(0xFFFFFFFF80020009UL, ctx[CpuRegister.Rax]);
        Assert.Equal(0, KernelErrno.Current(ctx));
    }

    [Fact]
    public void PosixFlavorReturnsMinusOneAndSetsErrno()
    {
        var ctx = NewContext(out _);

        var result = KernelErrno.Fail(ctx, KernelAbiFlavor.Posix, OrbisErrno.ENOTDIR);

        Assert.Equal(-1, result);
        Assert.Equal(ulong.MaxValue, ctx[CpuRegister.Rax]);
        Assert.Equal(OrbisErrno.ENOTDIR, KernelErrno.Current(ctx));
    }

    [Fact]
    public void FromSceResultTranslatesOnlyForPosix()
    {
        var ctx = NewContext(out _);
        var eexist = (int)OrbisGen2Result.ORBIS_GEN2_ERROR_ALREADY_EXISTS;

        Assert.Equal(eexist, KernelErrno.FromSceResult(ctx, KernelAbiFlavor.Sce, eexist));
        Assert.Equal(-1, KernelErrno.FromSceResult(ctx, KernelAbiFlavor.Posix, eexist));
        Assert.Equal(OrbisErrno.EEXIST, KernelErrno.Current(ctx));
    }

    [Fact]
    public void FromSceResultPassesSuccessThroughUntouched()
    {
        var ctx = NewContext(out _);
        ctx[CpuRegister.Rax] = 7; // e.g. an fd an open core already wrote

        Assert.Equal(0, KernelErrno.FromSceResult(ctx, KernelAbiFlavor.Posix, 0));
        Assert.Equal(7UL, ctx[CpuRegister.Rax]);
    }

    [Fact]
    public void PosixUnlinkOfMissingPathReportsEnoent()
    {
        var ctx = NewContext(out var memory);
        const ulong pathAddress = BaseAddress + 0x1000;
        var path = "/temp0/sharpemu-errno-test-does-not-exist\0"u8.ToArray();
        Assert.True(memory.TryWrite(pathAddress, path));
        ctx[CpuRegister.Rdi] = pathAddress;

        var result = KernelMemoryCompatExports.PosixUnlink(ctx);

        Assert.Equal(-1, result);
        Assert.Equal(OrbisErrno.ENOENT, KernelErrno.Current(ctx));
    }
}
