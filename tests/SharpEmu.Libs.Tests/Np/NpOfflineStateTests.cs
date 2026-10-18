// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.Libs.Np;
using Xunit;

namespace SharpEmu.Libs.Tests.Np;

// The sign-in surface an online-aware title reads before it decides whether to try its
// own servers. These three answers have to agree, and they have to be the combination
// real titles are known to tolerate: fpPS4 and shadPS4 both report signed out,
// reachability unavailable, and SCE_NP_ERROR_SIGNED_OUT for a console with no PSN
// session. The SceNpState ordering is the trap — SIGNED_OUT is 1 and SIGNED_IN is 2, so
// a "signed in" reading of the value 1 is backwards.
public sealed class NpOfflineStateTests
{
    private const ulong MemoryBase = 0x1_0000_0000;
    private const int SignedOut = unchecked((int)0x80550006);

    private static uint ReadUInt32(CpuContext ctx, ulong address)
    {
        Span<byte> bytes = stackalloc byte[sizeof(uint)];
        Assert.True(ctx.Memory.TryRead(address, bytes));
        return BinaryPrimitives.ReadUInt32LittleEndian(bytes);
    }

    [Fact]
    public void GetState_ReportsSignedOut()
    {
        var ctx = new CpuContext(new FakeCpuMemory(MemoryBase, 0x1000), Generation.Gen5);
        var address = MemoryBase + 0x100;
        ctx[CpuRegister.Rdi] = 1;
        ctx[CpuRegister.Rsi] = address;

        Assert.Equal(0, NpManagerExports.NpGetState(ctx));
        Assert.Equal(NpManagerExports.NpStateSignedOut, ReadUInt32(ctx, address));
        Assert.NotEqual(NpManagerExports.NpStateSignedIn, ReadUInt32(ctx, address));
    }

    [Fact]
    public void GetNpReachabilityState_ReportsUnavailable()
    {
        var ctx = new CpuContext(new FakeCpuMemory(MemoryBase, 0x1000), Generation.Gen5);
        var address = MemoryBase + 0x100;
        ctx[CpuRegister.Rdi] = 1;
        ctx[CpuRegister.Rsi] = address;

        Assert.Equal(0, NpManagerExports.NpGetNpReachabilityState(ctx));
        Assert.Equal(NpManagerExports.NpReachabilityUnavailable, ReadUInt32(ctx, address));
    }

    [Fact]
    public void CheckNpReachability_FailsSignedOut()
    {
        var ctx = new CpuContext(new FakeCpuMemory(MemoryBase, 0x1000), Generation.Gen5);
        var requestId = NpManagerExports.NpCreateAsyncRequest(ctx);
        Assert.True(requestId > 0);

        ctx[CpuRegister.Rdi] = unchecked((uint)requestId);
        Assert.Equal(SignedOut, NpManagerExports.NpCheckNpReachability(ctx));
    }

    // Whatever a later change does to the rest of the surface, these three must keep
    // telling one story: a title that reads "signed out" and then finds PSN reachable
    // has every reason to retry for ever.
    [Fact]
    public void TheOfflineAnswersAgreeWithEachOther()
    {
        var ctx = new CpuContext(new FakeCpuMemory(MemoryBase, 0x1000), Generation.Gen5);
        var stateAddress = MemoryBase + 0x100;
        var reachAddress = MemoryBase + 0x200;

        ctx[CpuRegister.Rdi] = 1;
        ctx[CpuRegister.Rsi] = stateAddress;
        Assert.Equal(0, NpManagerExports.NpGetState(ctx));

        ctx[CpuRegister.Rdi] = 1;
        ctx[CpuRegister.Rsi] = reachAddress;
        Assert.Equal(0, NpManagerExports.NpGetNpReachabilityState(ctx));

        var signedIn = ReadUInt32(ctx, stateAddress) == NpManagerExports.NpStateSignedIn;
        var reachable = ReadUInt32(ctx, reachAddress) != NpManagerExports.NpReachabilityUnavailable;
        Assert.Equal(signedIn, reachable);
    }

    [Fact]
    public void HasSignedUp_ReportsNoOnlineAccount()
    {
        var ctx = new CpuContext(new FakeCpuMemory(MemoryBase, 0x1000), Generation.Gen5);
        var address = MemoryBase + 0x100;
        ctx[CpuRegister.Rdi] = 1;
        ctx[CpuRegister.Rsi] = address;

        Assert.Equal(0, NpManagerExports.NpHasSignedUp(ctx));
        Assert.Equal(0u, ReadUInt32(ctx, address));
    }
}
