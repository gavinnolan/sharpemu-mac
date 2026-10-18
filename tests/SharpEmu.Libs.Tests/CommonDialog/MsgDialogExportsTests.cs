// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.Libs.CommonDialog;
using Xunit;

namespace SharpEmu.Libs.Tests.CommonDialog;

/// <summary>Serializes every test that touches the process-global common-dialog state.</summary>
[CollectionDefinition("MsgDialogState", DisableParallelization = true)]
public sealed class MsgDialogStateCollection
{
    public const string Name = "MsgDialogState";
}

[Collection(MsgDialogStateCollection.Name)]
public sealed class MsgDialogExportsTests : IDisposable
{
    private const int ErrorNotSystemInitialized = unchecked((int)0x80B80001);
    private const int ErrorNotInitialized = unchecked((int)0x80B80003);
    private const int ErrorNotFinished = unchecked((int)0x80B80005);
    private const int ErrorBusy = unchecked((int)0x80B80007);
    private const int ErrorParamInvalid = unchecked((int)0x80B8000A);
    private const int ErrorNotRunning = unchecked((int)0x80B8000B);
    private const int ErrorArgNull = unchecked((int)0x80B8000D);
    private const int ResultSize = 0x2C;
    private const int StatusInitialized = 1;
    private const int StatusRunning = 2;
    private const int StatusFinished = 3;
    private const int ModeUserMsg = 1;
    private const int ModeProgressBar = 2;
    private const int ModeSystemMsg = 3;
    private const ulong MemoryBase = 0x1_0000_0000;
    private const ulong ParamAddress = MemoryBase + 0x100;
    private const ulong UserMsgAddress = MemoryBase + 0x200;
    private const ulong ResultAddress = MemoryBase + 0x300;
    private const ulong TextAddress = MemoryBase + 0x400;
    private const ulong FaultingAddress = MemoryBase + 0x1000;

    private readonly FakeCpuMemory _memory = new(MemoryBase, 0x1000);
    private readonly CpuContext _ctx;
    private readonly string? _savedButtonSetting;

    public MsgDialogExportsTests()
    {
        _ctx = new CpuContext(_memory, Generation.Gen5);
        _savedButtonSetting = Environment.GetEnvironmentVariable(MsgDialogExports.DefaultButtonEnvironmentVariable);
        Environment.SetEnvironmentVariable(MsgDialogExports.DefaultButtonEnvironmentVariable, null);
        ResetAll();
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(MsgDialogExports.DefaultButtonEnvironmentVariable, _savedButtonSetting);
        ResetAll();
    }

    [Theory]
    [InlineData("lDqxaY1UbEo", "sceMsgDialogInitialize")]
    [InlineData("ePw-kqZmelo", "sceMsgDialogTerminate")]
    [InlineData("b06Hh0DPEaE", "sceMsgDialogOpen")]
    [InlineData("CWVW78Qc3fI", "sceMsgDialogGetStatus")]
    [InlineData("6fIC3XKt2k0", "sceMsgDialogUpdateStatus")]
    [InlineData("Lr8ovHH9l6A", "sceMsgDialogGetResult")]
    [InlineData("HTrcDKlFKuM", "sceMsgDialogClose")]
    public void RegistryResolvesDialogLifecycleExports(string nid, string exportName)
    {
        var manager = new ModuleManager();
        manager.RegisterExports(SharpEmu.Generated.SysAbiExportRegistry.CreateExports(
            Generation.Gen4 | Generation.Gen5));

        Assert.True(manager.TryGetExport(nid, out var export));
        Assert.Equal(exportName, export.Name);
        Assert.Equal("libSceMsgDialog", export.LibraryName);
    }

    [Fact]
    public void InitializeRequiresCommonDialogInitialize()
    {
        CommonDialogState.ResetForTests();

        Assert.Equal(ErrorNotSystemInitialized, MsgDialogExports.MsgDialogInitialize(_ctx));
        AssertReturn(ErrorNotSystemInitialized);

        Assert.Equal(0, CommonDialogExports.CommonDialogInitialize(_ctx));
        Assert.Equal(0, MsgDialogExports.MsgDialogInitialize(_ctx));
    }

    [Fact]
    public void InitializeWhileRunningReportsBusyAndKeepsTheDialog()
    {
        Initialize();
        OpenUserMsg(ButtonOk);

        Assert.Equal(ErrorBusy, MsgDialogExports.MsgDialogInitialize(_ctx));
        AssertReturn(ErrorBusy);
        Assert.Equal(StatusRunning, MsgDialogExports.MsgDialogGetStatus(_ctx));
    }

    [Fact]
    public void RepeatedInitializeWhileIdleStaysSuccessful()
    {
        Initialize();

        Assert.Equal(0, MsgDialogExports.MsgDialogInitialize(_ctx));
        Assert.Equal(StatusInitialized, MsgDialogExports.MsgDialogGetStatus(_ctx));
    }

    [Fact]
    public void UserMessageWalksNoneInitializedRunningFinished()
    {
        Assert.Equal(0, MsgDialogExports.MsgDialogGetStatus(_ctx));
        Initialize();
        Assert.Equal(StatusInitialized, MsgDialogExports.MsgDialogGetStatus(_ctx));

        OpenUserMsg(ButtonOk);
        Assert.Equal(StatusRunning, MsgDialogExports.MsgDialogGetStatus(_ctx));
        Assert.Equal(StatusFinished, MsgDialogExports.MsgDialogUpdateStatus(_ctx));
        Assert.Equal((ulong)StatusFinished, _ctx[CpuRegister.Rax]);

        Assert.Equal(0, MsgDialogExports.MsgDialogTerminate(_ctx));
        Assert.Equal(0, MsgDialogExports.MsgDialogGetStatus(_ctx));
    }

    [Fact]
    public void GetResultEchoesModeAndAffirmativeButton()
    {
        Initialize();
        OpenUserMsg(ButtonYesNo);
        FinishByPolling();

        var result = ReadResult();
        Assert.Equal(ModeUserMsg, BinaryPrimitives.ReadInt32LittleEndian(result));
        Assert.Equal(0, BinaryPrimitives.ReadInt32LittleEndian(result.AsSpan(4)));
        Assert.Equal(1, BinaryPrimitives.ReadInt32LittleEndian(result.AsSpan(8)));
        Assert.All(result.Skip(0x0C), b => Assert.Equal(0, b));
    }

    [Theory]
    [InlineData("2", ButtonYesNo, 2)]
    [InlineData("no", ButtonYesNo, 2)]
    [InlineData("cancel", ButtonOkCancel, 2)]
    [InlineData("second", ButtonYesNoFocusNo, 2)]
    [InlineData("1", ButtonYesNo, 1)]
    [InlineData("garbage", ButtonYesNo, 1)]
    [InlineData(null, ButtonYesNoFocusNo, 1)]
    // A single-button dialog can only answer with its one button.
    [InlineData("2", ButtonOk, 1)]
    public void DefaultButtonFollowsTheConfiguredSetting(string? setting, int buttonType, int expectedButtonId)
    {
        Environment.SetEnvironmentVariable(MsgDialogExports.DefaultButtonEnvironmentVariable, setting);
        Initialize();
        OpenUserMsg(buttonType);
        FinishByPolling();

        Assert.Equal(expectedButtonId, BinaryPrimitives.ReadInt32LittleEndian(ReadResult().AsSpan(8)));
    }

    [Fact]
    public void ProgressBarStaysRunningUntilClose()
    {
        Initialize();
        OpenProgressBar();

        for (var i = 0; i < 10; i++)
        {
            Assert.Equal(StatusRunning, MsgDialogExports.MsgDialogGetStatus(_ctx));
        }

        Assert.Equal(ErrorNotFinished, GetResultCode());

        Assert.Equal(0, MsgDialogExports.MsgDialogClose(_ctx));
        Assert.Equal(StatusFinished, MsgDialogExports.MsgDialogGetStatus(_ctx));

        var result = ReadResult();
        Assert.Equal(ModeProgressBar, BinaryPrimitives.ReadInt32LittleEndian(result));
        Assert.Equal(0, BinaryPrimitives.ReadInt32LittleEndian(result.AsSpan(4)));
        // Closed by the title before any answer: BUTTON_ID_INVALID.
        Assert.Equal(0, BinaryPrimitives.ReadInt32LittleEndian(result.AsSpan(8)));
    }

    [Fact]
    public void ButtonlessUserMessageStaysRunningUntilClose()
    {
        Initialize();
        OpenUserMsg(ButtonNone);

        for (var i = 0; i < 5; i++)
        {
            Assert.Equal(StatusRunning, MsgDialogExports.MsgDialogUpdateStatus(_ctx));
        }

        Assert.Equal(0, MsgDialogExports.MsgDialogClose(_ctx));
        Assert.Equal(StatusFinished, MsgDialogExports.MsgDialogGetStatus(_ctx));
    }

    [Fact]
    public void SystemMessageAutoFinishesWithTheFirstButtonAndEchoesItsMode()
    {
        Initialize();
        WriteParam(ModeSystemMsg, userMsgAddress: 0);
        OpenParam();
        FinishByPolling();

        var result = ReadResult();
        Assert.Equal(ModeSystemMsg, BinaryPrimitives.ReadInt32LittleEndian(result));
        Assert.Equal(1, BinaryPrimitives.ReadInt32LittleEndian(result.AsSpan(8)));
    }

    [Fact]
    public void ProgressBarSetValueAndIncTrackThePercentage()
    {
        Initialize();
        OpenProgressBar();

        Assert.Equal(0, SetValue(0, 30));
        Assert.Equal(30u, MsgDialogExports.ProgressValue);
        Assert.Equal(0, Inc(0, 25));
        Assert.Equal(55u, MsgDialogExports.ProgressValue);
        // Saturates at 100.
        Assert.Equal(0, Inc(0, 100));
        Assert.Equal(100u, MsgDialogExports.ProgressValue);
        Assert.Equal(0, SetValue(0, 0));
        Assert.Equal(0u, MsgDialogExports.ProgressValue);
        Assert.Equal(0, SetValue(0, 100));
    }

    [Fact]
    public void ProgressBarRejectsOutOfRangeArguments()
    {
        Initialize();
        OpenProgressBar();
        Assert.Equal(0, SetValue(0, 40));

        Assert.Equal(ErrorParamInvalid, SetValue(0, 101));
        Assert.Equal(ErrorParamInvalid, SetValue(1, 10));
        Assert.Equal(ErrorParamInvalid, Inc(0, 101));
        Assert.Equal(ErrorParamInvalid, Inc(7, 1));
        // Rejected calls leave the bar untouched.
        Assert.Equal(40u, MsgDialogExports.ProgressValue);

        _ctx[CpuRegister.Rdi] = 0;
        _ctx[CpuRegister.Rsi] = 0;
        Assert.Equal(ErrorArgNull, MsgDialogExports.MsgDialogProgressBarSetMsg(_ctx));
        _ctx[CpuRegister.Rdi] = 3;
        _ctx[CpuRegister.Rsi] = TextAddress;
        Assert.Equal(ErrorParamInvalid, MsgDialogExports.MsgDialogProgressBarSetMsg(_ctx));
        _ctx[CpuRegister.Rdi] = 0;
        Assert.Equal(0, MsgDialogExports.MsgDialogProgressBarSetMsg(_ctx));
    }

    [Fact]
    public void ProgressBarCallsNeedARunningDialog()
    {
        Assert.Equal(ErrorNotInitialized, SetValue(0, 1));
        Assert.Equal(ErrorNotInitialized, Inc(0, 1));

        Initialize();
        Assert.Equal(ErrorNotRunning, SetValue(0, 1));
        Assert.Equal(ErrorNotRunning, Inc(0, 1));

        OpenProgressBar();
        Assert.Equal(0, MsgDialogExports.MsgDialogClose(_ctx));
        Assert.Equal(ErrorNotRunning, SetValue(0, 1));
    }

    [Fact]
    public void CloseAndTerminateFollowTheDocumentedErrorRules()
    {
        Assert.Equal(ErrorNotInitialized, MsgDialogExports.MsgDialogClose(_ctx));
        Assert.Equal(ErrorNotInitialized, MsgDialogExports.MsgDialogTerminate(_ctx));

        Initialize();
        Assert.Equal(ErrorNotRunning, MsgDialogExports.MsgDialogClose(_ctx));

        OpenUserMsg(ButtonOk);
        Assert.Equal(0, MsgDialogExports.MsgDialogClose(_ctx));
        Assert.Equal(ErrorNotRunning, MsgDialogExports.MsgDialogClose(_ctx));
        AssertReturn(ErrorNotRunning);

        Assert.Equal(0, MsgDialogExports.MsgDialogTerminate(_ctx));
        Assert.Equal(ErrorNotInitialized, MsgDialogExports.MsgDialogTerminate(_ctx));
    }

    [Fact]
    public void OperationsRejectInvalidStatesAndNullPointers()
    {
        _ctx[CpuRegister.Rdi] = ParamAddress;
        Assert.Equal(ErrorNotInitialized, MsgDialogExports.MsgDialogOpen(_ctx));
        AssertReturn(ErrorNotInitialized);

        _ctx[CpuRegister.Rdi] = ResultAddress;
        Assert.Equal(ErrorNotFinished, MsgDialogExports.MsgDialogGetResult(_ctx));

        _ctx[CpuRegister.Rdi] = 0;
        Assert.Equal(ErrorArgNull, MsgDialogExports.MsgDialogGetResult(_ctx));

        Initialize();
        _ctx[CpuRegister.Rdi] = 0;
        Assert.Equal(ErrorArgNull, MsgDialogExports.MsgDialogOpen(_ctx));

        OpenUserMsg(ButtonOk);
        Assert.Equal(ErrorBusy, MsgDialogExports.MsgDialogOpen(_ctx));
        AssertReturn(ErrorBusy);
        Assert.Equal(ErrorNotFinished, GetResultCode());
    }

    [Fact]
    public void OpenRejectsAnUnknownModeWithoutStartingTheDialog()
    {
        Initialize();
        WriteParam(mode: 9, userMsgAddress: 0);

        _ctx[CpuRegister.Rdi] = ParamAddress;
        Assert.Equal(ErrorParamInvalid, MsgDialogExports.MsgDialogOpen(_ctx));
        Assert.Equal(StatusInitialized, MsgDialogExports.MsgDialogGetStatus(_ctx));
    }

    [Fact]
    public void OpenWithUnreadableNonNullParameterStillRunsDialog()
    {
        Initialize();

        _ctx[CpuRegister.Rdi] = FaultingAddress;
        Assert.Equal(0, MsgDialogExports.MsgDialogOpen(_ctx));
        Assert.Equal(StatusRunning, MsgDialogExports.MsgDialogGetStatus(_ctx));
        Assert.Equal(StatusFinished, MsgDialogExports.MsgDialogGetStatus(_ctx));
    }

    [Fact]
    public void GetResultWithUnreadableOutputReturnsMemoryFault()
    {
        Initialize();
        OpenUserMsg(ButtonOk);
        FinishByPolling();

        _ctx[CpuRegister.Rdi] = FaultingAddress;
        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT,
            MsgDialogExports.MsgDialogGetResult(_ctx));
        AssertReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    [Fact]
    public void GetResultWritesExactlyTheResultStruct()
    {
        Initialize();
        OpenUserMsg(ButtonOk);
        FinishByPolling();
        Assert.True(_memory.TryWrite(ResultAddress, Enumerable.Repeat((byte)0xA5, ResultSize + 4).ToArray()));

        _ctx[CpuRegister.Rdi] = ResultAddress;
        Assert.Equal(0, MsgDialogExports.MsgDialogGetResult(_ctx));

        var bytes = new byte[ResultSize + 4];
        Assert.True(_memory.TryRead(ResultAddress, bytes));
        Assert.Equal(0xA5, bytes[ResultSize]);
        Assert.Equal(0xA5, bytes[ResultSize + 3]);
        Assert.Equal(0, bytes[ResultSize - 1]);
    }

    [Fact]
    public void IsUsedReflectsAnOpenDialog()
    {
        Initialize();
        Assert.False(CommonDialogState.IsUsed);

        OpenUserMsg(ButtonOk);
        Assert.True(CommonDialogState.IsUsed);
        Assert.Equal(0, CommonDialogExports.CommonDialogIsUsed(_ctx));
        Assert.Equal(1UL, _ctx[CpuRegister.Rax]);

        FinishByPolling();
        Assert.False(CommonDialogState.IsUsed);

        OpenProgressBar();
        Assert.True(CommonDialogState.IsUsed);
        Assert.Equal(0, MsgDialogExports.MsgDialogTerminate(_ctx));
        Assert.False(CommonDialogState.IsUsed);
    }

    private const int ButtonOk = 0;
    private const int ButtonYesNo = 1;
    private const int ButtonNone = 2;
    private const int ButtonOkCancel = 3;
    private const int ButtonYesNoFocusNo = 4;

    private void ResetAll()
    {
        MsgDialogExports.ResetForTests();
        CommonDialogState.ResetForTests();
    }

    private void Initialize()
    {
        Assert.Equal(0, CommonDialogExports.CommonDialogInitialize(_ctx));
        Assert.Equal(0, MsgDialogExports.MsgDialogInitialize(_ctx));
        Assert.Equal(0UL, _ctx[CpuRegister.Rax]);
    }

    // SceMsgDialogParam: mode @0x38, userMsgParam* @0x40.
    private void WriteParam(int mode, ulong userMsgAddress)
    {
        var param = new byte[0xA0];
        BinaryPrimitives.WriteInt32LittleEndian(param.AsSpan(0x38), mode);
        BinaryPrimitives.WriteUInt64LittleEndian(param.AsSpan(0x40), userMsgAddress);
        Assert.True(_memory.TryWrite(ParamAddress, param));
    }

    private void OpenParam()
    {
        _ctx[CpuRegister.Rdi] = ParamAddress;
        Assert.Equal(0, MsgDialogExports.MsgDialogOpen(_ctx));
        Assert.Equal(0UL, _ctx[CpuRegister.Rax]);
    }

    private void OpenUserMsg(int buttonType)
    {
        var userMsg = new byte[0x20];
        BinaryPrimitives.WriteInt32LittleEndian(userMsg, buttonType);
        Assert.True(_memory.TryWrite(UserMsgAddress, userMsg));
        WriteParam(ModeUserMsg, UserMsgAddress);
        OpenParam();
    }

    private void OpenProgressBar()
    {
        WriteParam(ModeProgressBar, userMsgAddress: 0);
        OpenParam();
    }

    private void FinishByPolling()
    {
        Assert.Equal(StatusRunning, MsgDialogExports.MsgDialogGetStatus(_ctx));
        Assert.Equal(StatusFinished, MsgDialogExports.MsgDialogGetStatus(_ctx));
    }

    private byte[] ReadResult()
    {
        Assert.True(_memory.TryWrite(ResultAddress, new byte[ResultSize]));
        _ctx[CpuRegister.Rdi] = ResultAddress;
        Assert.Equal(0, MsgDialogExports.MsgDialogGetResult(_ctx));
        var result = new byte[ResultSize];
        Assert.True(_memory.TryRead(ResultAddress, result));
        return result;
    }

    private int GetResultCode()
    {
        _ctx[CpuRegister.Rdi] = ResultAddress;
        return MsgDialogExports.MsgDialogGetResult(_ctx);
    }

    private int SetValue(int target, uint rate)
    {
        _ctx[CpuRegister.Rdi] = unchecked((ulong)target);
        _ctx[CpuRegister.Rsi] = rate;
        return MsgDialogExports.MsgDialogProgressBarSetValue(_ctx);
    }

    private int Inc(int target, uint delta)
    {
        _ctx[CpuRegister.Rdi] = unchecked((ulong)target);
        _ctx[CpuRegister.Rsi] = delta;
        return MsgDialogExports.MsgDialogProgressBarInc(_ctx);
    }

    private void AssertReturn(int expected)
    {
        Assert.Equal(unchecked((ulong)(long)expected), _ctx[CpuRegister.Rax]);
    }
}
