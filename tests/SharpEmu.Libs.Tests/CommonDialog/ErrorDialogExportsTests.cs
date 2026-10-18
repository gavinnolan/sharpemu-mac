// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.Libs.CommonDialog;
using Xunit;

namespace SharpEmu.Libs.Tests.CommonDialog;

[Collection(MsgDialogStateCollection.Name)]
public sealed class ErrorDialogExportsTests : IDisposable
{
    private const int ErrorNotInitialized = unchecked((int)0x80ED0001);
    private const int ErrorAlreadyInitialized = unchecked((int)0x80ED0002);
    private const int ErrorParamInvalid = unchecked((int)0x80ED0003);
    private const int ErrorInvalidState = unchecked((int)0x80ED0005);
    private const int StatusInitialized = 1;
    private const int StatusRunning = 2;
    private const int StatusFinished = 3;
    private const ulong MemoryBase = 0x1_0000_0000;
    private const ulong ParamAddress = MemoryBase + 0x100;
    private const ulong FaultingAddress = MemoryBase + 0x1000;

    private readonly FakeCpuMemory _memory = new(MemoryBase, 0x1000);
    private readonly CpuContext _ctx;

    public ErrorDialogExportsTests()
    {
        _ctx = new CpuContext(_memory, Generation.Gen5);
        ResetAll();
    }

    public void Dispose() => ResetAll();

    [Fact]
    public void CommonDialogInitializeSucceedsOnceThenReportsAlreadyInitialized()
    {
        Assert.Equal(0, CommonDialogExports.CommonDialogInitialize(_ctx));
        Assert.Equal(0UL, _ctx[CpuRegister.Rax]);
        Assert.True(CommonDialogState.IsInitialized);

        var already = unchecked((int)0x80B80002);
        Assert.Equal(already, CommonDialogExports.CommonDialogInitialize(_ctx));
        Assert.Equal(unchecked((ulong)(long)already), _ctx[CpuRegister.Rax]);
        Assert.True(CommonDialogState.IsInitialized);
    }

    [Fact]
    public void CommonDialogIsUsedFollowsMarkUsed()
    {
        Assert.Equal(0, CommonDialogExports.CommonDialogIsUsed(_ctx));
        Assert.Equal(0UL, _ctx[CpuRegister.Rax]);

        CommonDialogState.MarkUsed(true);
        CommonDialogState.MarkUsed(true);
        CommonDialogExports.CommonDialogIsUsed(_ctx);
        Assert.Equal(1UL, _ctx[CpuRegister.Rax]);

        // Two users: one releasing must not clear the other.
        CommonDialogState.MarkUsed(false);
        Assert.True(CommonDialogState.IsUsed);
        CommonDialogState.MarkUsed(false);
        CommonDialogState.MarkUsed(false);
        CommonDialogExports.CommonDialogIsUsed(_ctx);
        Assert.Equal(0UL, _ctx[CpuRegister.Rax]);
    }

    [Fact]
    public void LifecycleWalksNoneInitializedRunningFinishedNone()
    {
        Assert.Equal(0, ErrorDialogExports.ErrorDialogGetStatus(_ctx));

        Assert.Equal(0, ErrorDialogExports.ErrorDialogInitialize(_ctx));
        Assert.Equal(StatusInitialized, ErrorDialogExports.ErrorDialogGetStatus(_ctx));

        Open();
        // Open must not jump straight to FINISHED.
        Assert.Equal(StatusRunning, ErrorDialogExports.ErrorDialogGetStatus(_ctx));
        Assert.True(CommonDialogState.IsUsed);
        Assert.Equal(StatusFinished, ErrorDialogExports.ErrorDialogUpdateStatus(_ctx));
        Assert.False(CommonDialogState.IsUsed);
        Assert.Equal((ulong)StatusFinished, _ctx[CpuRegister.Rax]);

        Assert.Equal(0, ErrorDialogExports.ErrorDialogTerminate(_ctx));
        Assert.Equal(0, ErrorDialogExports.ErrorDialogGetStatus(_ctx));
    }

    [Fact]
    public void InitializeTwiceReportsAlreadyInitialized()
    {
        Assert.Equal(0, ErrorDialogExports.ErrorDialogInitialize(_ctx));
        Assert.Equal(ErrorAlreadyInitialized, ErrorDialogExports.ErrorDialogInitialize(_ctx));
        Assert.Equal(StatusInitialized, ErrorDialogExports.ErrorDialogGetStatus(_ctx));
    }

    [Fact]
    public void OpenValidatesStateAndParameter()
    {
        _ctx[CpuRegister.Rdi] = ParamAddress;
        Assert.Equal(ErrorInvalidState, ErrorDialogExports.ErrorDialogOpen(_ctx));

        Assert.Equal(0, ErrorDialogExports.ErrorDialogInitialize(_ctx));

        _ctx[CpuRegister.Rdi] = 0;
        Assert.Equal(ErrorParamInvalid, ErrorDialogExports.ErrorDialogOpen(_ctx));

        _ctx[CpuRegister.Rdi] = FaultingAddress;
        Assert.Equal(ErrorParamInvalid, ErrorDialogExports.ErrorDialogOpen(_ctx));

        // Wrong size field.
        WriteParam(size: 0x20);
        _ctx[CpuRegister.Rdi] = ParamAddress;
        Assert.Equal(ErrorParamInvalid, ErrorDialogExports.ErrorDialogOpen(_ctx));
        Assert.Equal(StatusInitialized, ErrorDialogExports.ErrorDialogGetStatus(_ctx));

        Open();
        // A second Open while the dialog is on screen is an invalid state.
        Assert.Equal(ErrorInvalidState, ErrorDialogExports.ErrorDialogOpen(_ctx));
    }

    [Fact]
    public void CloseNeedsARunningDialog()
    {
        Assert.Equal(ErrorInvalidState, ErrorDialogExports.ErrorDialogClose(_ctx));

        Assert.Equal(0, ErrorDialogExports.ErrorDialogInitialize(_ctx));
        Assert.Equal(ErrorInvalidState, ErrorDialogExports.ErrorDialogClose(_ctx));

        Open();
        Assert.Equal(0, ErrorDialogExports.ErrorDialogClose(_ctx));
        Assert.Equal(StatusFinished, ErrorDialogExports.ErrorDialogGetStatus(_ctx));
        Assert.False(CommonDialogState.IsUsed);
        Assert.Equal(ErrorInvalidState, ErrorDialogExports.ErrorDialogClose(_ctx));
    }

    [Fact]
    public void TerminateNeedsAnInitializedDialogAndReleasesARunningOne()
    {
        Assert.Equal(ErrorNotInitialized, ErrorDialogExports.ErrorDialogTerminate(_ctx));
        Assert.Equal(unchecked((ulong)(long)ErrorNotInitialized), _ctx[CpuRegister.Rax]);

        Assert.Equal(0, ErrorDialogExports.ErrorDialogInitialize(_ctx));
        Open();
        Assert.True(CommonDialogState.IsUsed);
        Assert.Equal(0, ErrorDialogExports.ErrorDialogTerminate(_ctx));
        Assert.False(CommonDialogState.IsUsed);
        Assert.Equal(0, ErrorDialogExports.ErrorDialogGetStatus(_ctx));

        // The dialog can be initialized again after Terminate.
        Assert.Equal(0, ErrorDialogExports.ErrorDialogInitialize(_ctx));
    }

    [Fact]
    public void ReopenIsAllowedAfterFinished()
    {
        Assert.Equal(0, ErrorDialogExports.ErrorDialogInitialize(_ctx));
        Open();
        Assert.Equal(0, ErrorDialogExports.ErrorDialogClose(_ctx));

        Open();
        Assert.Equal(StatusRunning, ErrorDialogExports.ErrorDialogGetStatus(_ctx));
    }

    private void Open()
    {
        WriteParam(size: 0x10);
        _ctx[CpuRegister.Rdi] = ParamAddress;
        Assert.Equal(0, ErrorDialogExports.ErrorDialogOpen(_ctx));
    }

    // SceErrorDialogParam { int32 size; int32 errorCode; int32 userId; int32 reserved }.
    private void WriteParam(int size)
    {
        var param = new byte[0x10];
        BinaryPrimitives.WriteInt32LittleEndian(param, size);
        BinaryPrimitives.WriteUInt32LittleEndian(param.AsSpan(4), 0x80020001);
        BinaryPrimitives.WriteInt32LittleEndian(param.AsSpan(8), 0x10000000);
        Assert.True(_memory.TryWrite(ParamAddress, param));
    }

    private static void ResetAll()
    {
        ErrorDialogExports.ResetForTests();
        CommonDialogState.ResetForTests();
    }
}
