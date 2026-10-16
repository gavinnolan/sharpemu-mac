// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Kernel;
using SharpEmu.Libs.Kernel.Syscalls;
using Xunit;

namespace SharpEmu.Libs.Tests.Kernel.Syscalls;

[CollectionDefinition(KernelSyscallDispatchCollection.Name, DisableParallelization = true)]
public sealed class KernelSyscallDispatchCollection
{
    public const string Name = "KernelSyscallDispatch";
}

// KernelSyscallDispatcher.ExportLookup is process-wide.
[Collection(KernelSyscallDispatchCollection.Name)]
public sealed class KernelSyscallDispatchTests : IDisposable
{
    private const ulong BaseAddress = 0x1_0000;
    private const ulong TlsBase = BaseAddress + 0x100;
    private const ulong FrameAddress = BaseAddress + 0x1000;
    private const ulong GuestRsp = BaseAddress + 0x2000;
    private const ulong ReturnRip = 0x0000_0008_1234_5678;
    private const int GuestErrno = 99;

    // FreeBSD sys/sys/errno.h
    private const int EBADF = 9;
    private const int ENOSYS = 78;

    private const string ReadNid = "AqBioC2vF3I";      // read
    private const string GetpidNid = "HoLVWNanBBc";    // getpid
    private const string MmapNid = "BPE9s9vQQXo";      // mmap

    private readonly Func<string, ExportedFunction?>? _previousLookup = KernelSyscallDispatcher.ExportLookup;
    private readonly ModuleManager _manager = new();
    private readonly List<ulong[]> _calls = new();

    public KernelSyscallDispatchTests()
    {
        KernelSyscallDispatcher.ExportLookup = nid => _manager.TryGetExport(nid, out var export) ? export : null;
    }

    public void Dispose() => KernelSyscallDispatcher.ExportLookup = _previousLookup;

    [Fact]
    public void RawSuccessClearsCarryAndReturnsTheValue()
    {
        Register(ReadNid, "read", ctx => 7);
        var ctx = NewContext(out _);
        WriteFrame(ctx, number: 3, rdi: 5, rsi: 0x2_0000, rdx: 16, rflags: 0x203);

        KernelSyscallExports.RawSyscallTrap(ctx);

        Assert.Equal(7UL, Frame(ctx, GuestSyscallFrame.Rax));
        Assert.Equal(0x202UL, Frame(ctx, GuestSyscallFrame.Rflags));
        Assert.Equal(0x202UL, Frame(ctx, GuestSyscallFrame.R11));
        Assert.Equal(ReturnRip, Frame(ctx, GuestSyscallFrame.Rcx));
        // No second value: rdx keeps the caller's value.
        Assert.Equal(16UL, Frame(ctx, GuestSyscallFrame.Rdx));
        Assert.Equal([5UL, 0x2_0000, 16, 0x44, 0x55, 0x66], _calls.Single()[..6]);
        AssertGuestErrnoUntouched(ctx);
    }

    [Fact]
    public void RawFailureSetsCarryAndReturnsErrnoWithoutTouchingTlsErrno()
    {
        Register(ReadNid, "read", ctx => KernelErrno.Posix(ctx, EBADF));
        var ctx = NewContext(out _);
        WriteFrame(ctx, number: 3, rdi: 1234, rflags: 0x202);

        KernelSyscallExports.RawSyscallTrap(ctx);

        Assert.Equal((ulong)EBADF, Frame(ctx, GuestSyscallFrame.Rax));
        Assert.Equal(0x203UL, Frame(ctx, GuestSyscallFrame.Rflags));
        Assert.Equal(0x203UL, Frame(ctx, GuestSyscallFrame.R11));
        AssertGuestErrnoUntouched(ctx);
    }

    [Fact]
    public void RawSixtyFourBitResultSurvives()
    {
        Register(MmapNid, "mmap", ctx =>
        {
            ctx[CpuRegister.Rax] = 0x0000_0010_0000_0000;
            return 0;
        });
        var ctx = NewContext(out _);
        WriteFrame(ctx, number: 0x1DD, rflags: 0x203);

        KernelSyscallExports.RawSyscallTrap(ctx);

        Assert.Equal(0x0000_0010_0000_0000UL, Frame(ctx, GuestSyscallFrame.Rax));
        Assert.Equal(0x202UL, Frame(ctx, GuestSyscallFrame.Rflags));
    }

    [Theory]
    [InlineData(0x011)]  // sys_obsolete17: nosys
    [InlineData(0x0D2)]  // sys_lkmnosys
    [InlineData(0x2D3)]  // past the table: sysent[0], nosys
    [InlineData(0x013)]  // sys_compat.lseek
    [InlineData(0x2D2)]  // sys_workspace_ctrl: no HLE equivalent
    [InlineData(0x003)]  // sys_read with no read export registered
    public void UnservicedSlotsFailWithEnosys(long number)
    {
        var ctx = NewContext(out _);
        WriteFrame(ctx, number: (ulong)number, rflags: 0x202);

        KernelSyscallExports.RawSyscallTrap(ctx);

        Assert.Equal((ulong)ENOSYS, Frame(ctx, GuestSyscallFrame.Rax));
        Assert.Equal(0x203UL, Frame(ctx, GuestSyscallFrame.Rflags));
        AssertGuestErrnoUntouched(ctx);
    }

    [Fact]
    public void ExportForAnotherGenerationIsNotUsed()
    {
        _manager.RegisterExports([new ExportedFunction("libKernel", ReadNid, "read", Generation.Gen4, _ => 7)]);
        var ctx = NewContext(out _);
        WriteFrame(ctx, number: 3, rflags: 0x202);

        KernelSyscallExports.RawSyscallTrap(ctx);

        Assert.Equal((ulong)ENOSYS, Frame(ctx, GuestSyscallFrame.Rax));
    }

    [Fact]
    public void IndirectSyscallShiftsArgumentsAndReadsTheStack()
    {
        Register(MmapNid, "mmap", _ => 0);
        var ctx = NewContext(out _);
        // syscall(SYS_mmap, a0..a5): number in rdi, a0..a4 in rsi, rdx, r10,
        // r8, r9, a5 at [rsp+8] above the wrapper's return address.
        WriteFrame(ctx, number: 0, rdi: 0x1DD, rsi: 0xA0, rdx: 0xA1, rflags: 0x202);
        ctx.TryWriteUInt64(GuestRsp + 8, 0xA5);
        ctx.TryWriteUInt64(GuestRsp + 16, 0xA6);

        KernelSyscallExports.RawSyscallTrap(ctx);

        Assert.Equal([0xA0UL, 0xA1, 0x44, 0x55, 0x66, 0xA5, 0xA6], _calls.Single()[..7]);
        Assert.Equal(0x202UL, Frame(ctx, GuestSyscallFrame.Rflags));
    }

    [Fact]
    public void NestedIndirectSyscallIsNosys()
    {
        var ctx = NewContext(out _);
        WriteFrame(ctx, number: 0, rdi: 0xC6, rflags: 0x202);

        KernelSyscallExports.RawSyscallTrap(ctx);

        Assert.Equal((ulong)ENOSYS, Frame(ctx, GuestSyscallFrame.Rax));
        Assert.Equal(0x203UL, Frame(ctx, GuestSyscallFrame.Rflags));
    }

    [Fact]
    public void ForwardedCallLeavesTheCallersContextAsItWas()
    {
        Register(GetpidNid, "getpid", _ => 42);
        var ctx = NewContext(out _);
        ctx[CpuRegister.Rdi] = 0x11;
        ctx[CpuRegister.Rsi] = 0x22;
        ctx[CpuRegister.Rdx] = 0x33;
        ctx[CpuRegister.Rcx] = 0x44;
        ctx[CpuRegister.R8] = 0x55;
        ctx[CpuRegister.R9] = 0x66;
        ctx.SetImportStackArguments(1, 2, 3, 4, 5, 6);
        Span<ulong> arguments = stackalloc ulong[KernelSyscallDispatcher.ArgumentCount];

        var outcome = KernelSyscallDispatcher.Invoke(ctx, 0x14, arguments, incomingRdx: 0x77);

        Assert.False(outcome.Failed);
        Assert.Equal(42UL, outcome.Value);
        Assert.Equal(0x77UL, outcome.SecondValue);
        Assert.Equal(0x11UL, ctx[CpuRegister.Rdi]);
        Assert.Equal(0x22UL, ctx[CpuRegister.Rsi]);
        Assert.Equal(0x33UL, ctx[CpuRegister.Rdx]);
        Assert.Equal(0x44UL, ctx[CpuRegister.Rcx]);
        Assert.Equal(0x55UL, ctx[CpuRegister.R8]);
        Assert.Equal(0x66UL, ctx[CpuRegister.R9]);
        Assert.True(ctx.TryGetImportStackArgument(5, out var sixth));
        Assert.Equal(6UL, sixth);
    }

    [Fact]
    public void LibcSyscallSucceedsWithTheValue()
    {
        Register(GetpidNid, "getpid", _ => 42);
        var ctx = NewContext(out _);
        ctx[CpuRegister.Rdi] = 0x14;

        var returned = KernelSyscallExports.Syscall(ctx);

        Assert.Equal(42, returned);
        Assert.Equal(42UL, ctx[CpuRegister.Rax]);
        AssertGuestErrnoUntouched(ctx);
    }

    [Fact]
    public void LibcSyscallShiftsCAbiArguments()
    {
        Register(MmapNid, "mmap", _ => 0);
        var ctx = NewContext(out _);
        ctx[CpuRegister.Rdi] = 0x1DD;
        ctx[CpuRegister.Rsi] = 0xA0;
        ctx[CpuRegister.Rdx] = 0xA1;
        ctx[CpuRegister.Rcx] = 0xA2;
        ctx[CpuRegister.R8] = 0xA3;
        ctx[CpuRegister.R9] = 0xA4;
        ctx.SetImportStackArguments(0xA5, 0xA6, 0, 0, 0, 0);

        KernelSyscallExports.Syscall(ctx);

        Assert.Equal([0xA0UL, 0xA1, 0xA2, 0xA3, 0xA4, 0xA5, 0xA6], _calls.Single()[..7]);
    }

    [Fact]
    public void LibcSyscallFailureIsMinusOneWithErrno()
    {
        Register(ReadNid, "read", ctx => KernelErrno.Posix(ctx, EBADF));
        var ctx = NewContext(out _);
        ctx[CpuRegister.Rdi] = 3;

        var returned = KernelSyscallExports.Syscall(ctx);

        Assert.Equal(-1, returned);
        Assert.Equal(ulong.MaxValue, ctx[CpuRegister.Rax]);
        Assert.True(KernelRuntimeCompatExports.TryGetErrno(ctx, out var errno));
        Assert.Equal(EBADF, errno);
    }

    [Fact]
    public void LibcSyscallOfNosysSlotSetsEnosys()
    {
        var ctx = NewContext(out _);
        ctx[CpuRegister.Rdi] = 0x11;

        Assert.Equal(-1, KernelSyscallExports.Syscall(ctx));
        Assert.True(KernelRuntimeCompatExports.TryGetErrno(ctx, out var errno));
        Assert.Equal(ENOSYS, errno);
    }

    [Theory]
    [InlineData(KernelAbiFlavor.Posix, -1L, EBADF, true, EBADF)]
    [InlineData(KernelAbiFlavor.Posix, -1L, 0, true, 22)]                  // -1 with no errno: EINVAL
    [InlineData(KernelAbiFlavor.Posix, -2147352567L, 0, true, EBADF)]       // (int)0x80020009: mis-flavoured SCE code
    [InlineData(KernelAbiFlavor.Posix, -20L, 0, false, 0)]                 // getpriority-style negative value
    [InlineData(KernelAbiFlavor.Posix, 5L, 0, false, 0)]
    [InlineData(KernelAbiFlavor.Sce, -2147352554L, 0, true, 22)]             // (int)0x80020016
    [InlineData(KernelAbiFlavor.Sce, -1L, 0, true, 22)]                    // no errno in it: EINVAL
    [InlineData(KernelAbiFlavor.Sce, 0L, 0, false, 0)]
    [InlineData(KernelAbiFlavor.Sce, 0x0000_0010_0000_0000L, 0, false, 0)] // 64-bit value
    public void DecodesFailuresPerFlavour(KernelAbiFlavor flavor, long value, int stored, bool failed, int errno)
    {
        Assert.Equal(failed, KernelSyscallDispatcher.TryDecodeFailure(flavor, unchecked((ulong)value), stored, out var decoded));
        Assert.Equal(errno, decoded);
    }

    private void Register(string nid, string name, SysAbiFunction body)
    {
        _manager.RegisterExports([new ExportedFunction("libKernel", nid, name, Generation.Gen4 | Generation.Gen5, ctx =>
        {
            ulong[] seen =
            [
                ctx[CpuRegister.Rdi], ctx[CpuRegister.Rsi], ctx[CpuRegister.Rdx],
                ctx[CpuRegister.Rcx], ctx[CpuRegister.R8], ctx[CpuRegister.R9],
                ctx.TryGetImportStackArgument(0, out var s0) ? s0 : ulong.MaxValue,
            ];
            _calls.Add(seen);
            return body(ctx);
        })]);
    }

    private static CpuContext NewContext(out FakeCpuMemory memory)
    {
        memory = new FakeCpuMemory(BaseAddress, 0x4000);
        var ctx = new CpuContext(memory, Generation.Gen5) { FsBase = TlsBase };
        Assert.True(KernelRuntimeCompatExports.TrySetErrno(ctx, GuestErrno));
        ctx[CpuRegister.Rdi] = FrameAddress;
        return ctx;
    }

    private static void WriteFrame(
        CpuContext ctx,
        ulong number,
        ulong rdi = 0x11,
        ulong rsi = 0x22,
        ulong rdx = 0x33,
        ulong rflags = 0x202)
    {
        void Set(int offset, ulong value) => Assert.True(ctx.TryWriteUInt64(FrameAddress + (ulong)offset, value));
        Set(GuestSyscallFrame.Rax, number);
        Set(GuestSyscallFrame.Rdi, rdi);
        Set(GuestSyscallFrame.Rsi, rsi);
        Set(GuestSyscallFrame.Rdx, rdx);
        Set(GuestSyscallFrame.R10, 0x44);
        Set(GuestSyscallFrame.R8, 0x55);
        Set(GuestSyscallFrame.R9, 0x66);
        Set(GuestSyscallFrame.Rcx, ReturnRip);
        Set(GuestSyscallFrame.R11, GuestRsp);
        Set(GuestSyscallFrame.Rflags, rflags);
        Set(GuestSyscallFrame.ReturnRip, ReturnRip);
        Set(GuestSyscallFrame.GuestRsp, GuestRsp);
    }

    private static ulong Frame(CpuContext ctx, int offset)
    {
        Assert.True(ctx.TryReadUInt64(FrameAddress + (ulong)offset, out var value));
        return value;
    }

    private static void AssertGuestErrnoUntouched(CpuContext ctx)
    {
        Assert.True(KernelRuntimeCompatExports.TryGetErrno(ctx, out var errno));
        Assert.Equal(GuestErrno, errno);
    }
}
