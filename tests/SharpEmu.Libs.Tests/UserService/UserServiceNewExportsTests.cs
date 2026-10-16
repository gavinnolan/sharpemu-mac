// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.Libs.Mouse;
using SharpEmu.Libs.UserService;
using Xunit;

namespace SharpEmu.Libs.Tests.UserService;

public sealed class UserServiceNewExportsTests
{
    private const int PrimaryUserId = 0x10000000;
    private const ulong Base = 0x1_0000_0000UL;

    private readonly FakeCpuMemory _memory = new(Base, 0x1000);
    private readonly CpuContext _ctx;

    public UserServiceNewExportsTests() => _ctx = new CpuContext(_memory, Generation.Gen5);

    [Fact]
    public void GetForegroundUser_WritesPrimaryUser_AndRejectsNull()
    {
        _ctx[CpuRegister.Rdi] = 0;
        Assert.Equal(unchecked((int)0x80960005), UserServiceExports.UserServiceGetForegroundUser(_ctx));

        _ctx[CpuRegister.Rdi] = Base;
        Assert.Equal(0, UserServiceExports.UserServiceGetForegroundUser(_ctx));
        Assert.Equal(PrimaryUserId, ReadInt32(Base));
    }

    [Fact]
    public void GetRegisteredUserIdList_WritesSixteenSlots()
    {
        _ctx[CpuRegister.Rdi] = 0;
        Assert.Equal(unchecked((int)0x80960005), UserServiceExports.UserServiceGetRegisteredUserIdList(_ctx));

        _ctx[CpuRegister.Rdi] = Base;
        Assert.Equal(0, UserServiceExports.UserServiceGetRegisteredUserIdList(_ctx));
        Assert.Equal(PrimaryUserId, ReadInt32(Base));
        for (var slot = 1; slot < 16; slot++)
            Assert.Equal(-1, ReadInt32(Base + (ulong)(slot * 4)));
    }

    [Fact]
    public void GetNpAccountId_ReportsNoAccountAndValidatesArguments()
    {
        Assert.True(_memory.TryWrite(Base, Enumerable.Repeat((byte)0xFF, 8).ToArray()));

        _ctx[CpuRegister.Rdi] = PrimaryUserId;
        _ctx[CpuRegister.Rsi] = 0;
        Assert.Equal(unchecked((int)0x80960005), UserServiceExports.UserServiceGetNpAccountId(_ctx));

        _ctx[CpuRegister.Rdi] = 0x1234;
        _ctx[CpuRegister.Rsi] = Base;
        Assert.Equal(unchecked((int)0x80960009), UserServiceExports.UserServiceGetNpAccountId(_ctx));
        Span<byte> untouched = stackalloc byte[8];
        Assert.True(_memory.TryRead(Base, untouched));
        Assert.All(untouched.ToArray(), b => Assert.Equal(0xFF, b));

        _ctx[CpuRegister.Rdi] = PrimaryUserId;
        Assert.Equal(0, UserServiceExports.UserServiceGetNpAccountId(_ctx));
        Assert.True(_memory.TryRead(Base, untouched));
        Assert.Equal(0UL, BinaryPrimitives.ReadUInt64LittleEndian(untouched));
    }

    [Fact]
    public void GetUserColor_WritesFirstColorAndValidatesArguments()
    {
        _ctx[CpuRegister.Rdi] = 0x1234;
        _ctx[CpuRegister.Rsi] = Base;
        Assert.Equal(unchecked((int)0x80960009), UserServiceExports.UserServiceGetUserColor(_ctx));

        _ctx[CpuRegister.Rdi] = PrimaryUserId;
        _ctx[CpuRegister.Rsi] = 0;
        Assert.Equal(unchecked((int)0x80960005), UserServiceExports.UserServiceGetUserColor(_ctx));

        _ctx[CpuRegister.Rsi] = Base;
        Assert.Equal(0, UserServiceExports.UserServiceGetUserColor(_ctx));
        Assert.Equal(0, ReadInt32(Base));
    }

    [Fact]
    public void Initialize2_IsAcceptedRepeatedly()
    {
        Assert.Equal(0, UserServiceExports.UserServiceInitialize2(_ctx));
        Assert.Equal(0, UserServiceExports.UserServiceInitialize2(_ctx));
    }

    [Fact]
    public void MouseResetAbsolutePosition_IsAccepted()
    {
        Assert.Equal(0, MouseExports.MouseResetAbsolutePosition(_ctx));
    }

    private int ReadInt32(ulong address)
    {
        Span<byte> bytes = stackalloc byte[4];
        Assert.True(_memory.TryRead(address, bytes));
        return BinaryPrimitives.ReadInt32LittleEndian(bytes);
    }
}
