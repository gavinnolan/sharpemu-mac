// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Text;
using SharpEmu.HLE;
using SharpEmu.Libs.SaveData;
using Xunit;

namespace SharpEmu.Libs.Tests.SaveData;

// Status machine and result selection of the save data dialog against a temp save root.
[Collection("SaveDataMemoryState")]
public sealed class SaveDataDialogExportsTests : IDisposable
{
    private const ulong Base = 0x4_0000_0000;
    private const string TitleId = "SDDIALOGTEST";
    private const int UserId = 0x1001;
    private const ulong UserData = 0x1122_3344_5566_7788;

    private const ulong ParamAddress = Base + 0x100;
    private const ulong ItemsAddress = Base + 0x200;
    private const ulong UserMsgAddress = Base + 0x280;
    private const ulong SysMsgAddress = Base + 0x2C0;
    private const ulong NewItemAddress = Base + 0x300;
    private const ulong DirNamesAddress = Base + 0x400;
    private const ulong ResultAddress = Base + 0x800;
    private const ulong ResultDirName = Base + 0x880;
    private const ulong ResultParam = Base + 0x900;

    private const int StatusNone = 0;
    private const int StatusInitialized = 1;
    private const int StatusRunning = 2;
    private const int StatusFinished = 3;
    private const int ErrorNotInitialized = unchecked((int)0x80B80003);
    private const int ErrorAlreadyInitialized = unchecked((int)0x80B80004);
    private const int ErrorNotFinished = unchecked((int)0x80B80005);
    private const int ErrorBusy = unchecked((int)0x80B80008);
    private const int ErrorParamInvalid = unchecked((int)0x80B8000A);
    private const int ErrorNotRunning = unchecked((int)0x80B8000B);
    private const int ErrorArgNull = unchecked((int)0x80B8000D);

    private const int ModeList = 1;
    private const int ModeUserMsg = 2;
    private const int ModeSystemMsg = 3;
    private const int ModeProgressBar = 5;
    private const int DispSave = 1;
    private const int DispLoad = 2;
    private const uint FocusListHead = 0;
    private const uint FocusDataLatest = 4;

    private readonly FakeCpuMemory _memory = new(Base, 0x10000);
    private readonly CpuContext _ctx;
    private readonly string _root;
    private readonly string? _previousRoot;

    public SaveDataDialogExportsTests()
    {
        _ctx = new CpuContext(_memory, Generation.Gen5);
        _root = Path.Combine(Path.GetTempPath(), $"sharpemu-sddialog-{Guid.NewGuid():N}");
        _previousRoot = Environment.GetEnvironmentVariable("SHARPEMU_SAVEDATA_DIR");
        Environment.SetEnvironmentVariable("SHARPEMU_SAVEDATA_DIR", _root);
        SaveDataExports.ConfigureApplicationInfo(TitleId);
        SaveDataDialogExports.DefaultChoiceOverride = SaveDataDialogExports.DefaultChoice.Affirmative;
        SaveDataDialogExports.SaveDataDialogTerminate(_ctx);
    }

    public void Dispose()
    {
        SaveDataDialogExports.SaveDataDialogTerminate(_ctx);
        SaveDataDialogExports.DefaultChoiceOverride = null;
        SaveDataExports.ConfigureApplicationInfo(null);
        Environment.SetEnvironmentVariable("SHARPEMU_SAVEDATA_DIR", _previousRoot);
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private int Status() => SaveDataDialogExports.SaveDataDialogUpdateStatus(_ctx);

    private int Initialize() => SaveDataDialogExports.SaveDataDialogInitialize(_ctx);

    private int Open(ulong param = ParamAddress)
    {
        _ctx[CpuRegister.Rdi] = param;
        return SaveDataDialogExports.SaveDataDialogOpen(_ctx);
    }

    private int Close()
    {
        _ctx[CpuRegister.Rdi] = 0;
        return SaveDataDialogExports.SaveDataDialogClose(_ctx);
    }

    private int GetResult(ulong address = ResultAddress)
    {
        _ctx[CpuRegister.Rdi] = address;
        return SaveDataDialogExports.SaveDataDialogGetResult(_ctx);
    }

    private void CreateSave(string dirName, string title, DateTime lastWriteUtc)
    {
        var slot = Path.Combine(_root, TitleId, dirName);
        Directory.CreateDirectory(slot);
        SaveDataStorage.WriteMetadata(slot, SaveDataMetadata.CreateDefault(dirName) with { Title = title });
        Directory.SetLastWriteTimeUtc(slot, lastWriteUtc);
    }

    private void WriteParam(
        int mode,
        int dispType = DispLoad,
        string[]? dirNames = null,
        bool newItem = false,
        uint focusPos = FocusListHead,
        uint? userButtonType = null,
        uint? sysMsgType = null)
    {
        var param = new byte[0x98];
        BinaryPrimitives.WriteInt32LittleEndian(param.AsSpan(0x30), param.Length);
        BinaryPrimitives.WriteInt32LittleEndian(param.AsSpan(0x34), mode);
        BinaryPrimitives.WriteInt32LittleEndian(param.AsSpan(0x38), dispType);
        BinaryPrimitives.WriteUInt64LittleEndian(param.AsSpan(0x70), UserData);

        if (dirNames is not null)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(param.AsSpan(0x48), ItemsAddress);
            var items = new byte[0x60];
            BinaryPrimitives.WriteInt32LittleEndian(items, UserId);
            BinaryPrimitives.WriteUInt64LittleEndian(items.AsSpan(0x10), DirNamesAddress);
            BinaryPrimitives.WriteUInt32LittleEndian(items.AsSpan(0x18), (uint)dirNames.Length);
            BinaryPrimitives.WriteUInt64LittleEndian(items.AsSpan(0x20), newItem ? NewItemAddress : 0);
            BinaryPrimitives.WriteUInt32LittleEndian(items.AsSpan(0x28), focusPos);
            Assert.True(_memory.TryWrite(ItemsAddress, items));

            var names = new byte[Math.Max(1, dirNames.Length) * 32];
            for (var i = 0; i < dirNames.Length; i++)
            {
                Encoding.ASCII.GetBytes(dirNames[i]).CopyTo(names, i * 32);
            }

            Assert.True(_memory.TryWrite(DirNamesAddress, names));
        }

        if (userButtonType is { } buttonType)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(param.AsSpan(0x50), UserMsgAddress);
            Assert.True(_memory.TryWrite(UserMsgAddress, BitConverter.GetBytes(buttonType)));
        }

        if (sysMsgType is { } type)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(param.AsSpan(0x58), SysMsgAddress);
            Assert.True(_memory.TryWrite(SysMsgAddress, BitConverter.GetBytes(type)));
        }

        Assert.True(_memory.TryWrite(ParamAddress, param));
    }

    private void PrepareResult()
    {
        var result = new byte[0x48];
        BinaryPrimitives.WriteUInt64LittleEndian(result.AsSpan(0x10), ResultDirName);
        BinaryPrimitives.WriteUInt64LittleEndian(result.AsSpan(0x18), ResultParam);
        Assert.True(_memory.TryWrite(ResultAddress, result));
        Assert.True(_memory.TryWrite(ResultDirName, Enumerable.Repeat((byte)0xCD, 32).ToArray()));
        Assert.True(_memory.TryWrite(ResultParam, Enumerable.Repeat((byte)0xCD, 0x530).ToArray()));
    }

    private (int Mode, int Result, int ButtonId, ulong DirNamePtr, ulong ParamPtr, ulong UserData) ReadResult()
    {
        var bytes = new byte[0x48];
        Assert.True(_memory.TryRead(ResultAddress, bytes));
        return (
            BinaryPrimitives.ReadInt32LittleEndian(bytes),
            BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(0x04)),
            BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(0x08)),
            BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(0x10)),
            BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(0x18)),
            BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(0x20)));
    }

    private string ReadAscii(ulong address, int length)
    {
        var bytes = new byte[length];
        Assert.True(_memory.TryRead(address, bytes));
        var end = Array.IndexOf(bytes, (byte)0);
        return Encoding.ASCII.GetString(bytes, 0, end < 0 ? length : end);
    }

    private void RunToFinished()
    {
        Assert.Equal(StatusRunning, Status());
        Assert.Equal(StatusFinished, Status());
    }

    [Fact]
    public void StatusMachine_FollowsTheCommonDialogLifecycle()
    {
        Assert.Equal(StatusNone, Status());
        Assert.Equal(ErrorNotInitialized, Open());
        Assert.Equal(ErrorNotInitialized, SaveDataDialogExports.SaveDataDialogTerminate(_ctx));

        Assert.Equal(0, Initialize());
        Assert.Equal(StatusInitialized, Status());
        Assert.Equal(ErrorAlreadyInitialized, Initialize());
        Assert.Equal(ErrorNotFinished, GetResult());
        Assert.Equal(ErrorNotRunning, Close());
        Assert.Equal(ErrorArgNull, Open(0));

        WriteParam(mode: 9);
        Assert.Equal(ErrorParamInvalid, Open());

        WriteParam(ModeUserMsg, userButtonType: 0);
        Assert.Equal(0, Open());
        Assert.Equal(ErrorBusy, Open());
        Assert.Equal(ErrorNotFinished, GetResult());
        RunToFinished();
        Assert.Equal(StatusFinished, Status());
        Assert.Equal(ErrorAlreadyInitialized, Initialize());
        Assert.Equal(ErrorArgNull, GetResult(0));

        // A finished dialog can be opened again without re-initializing.
        Assert.Equal(0, Open());
        Assert.Equal(StatusRunning, Status());

        Assert.Equal(0, SaveDataDialogExports.SaveDataDialogTerminate(_ctx));
        Assert.Equal(StatusNone, Status());
        Assert.Equal(0, Initialize());
    }

    [Fact]
    public void UserMessage_EchoesModeAndUserData_WithAffirmativeButton()
    {
        Assert.Equal(0, Initialize());
        WriteParam(ModeUserMsg, userButtonType: 1);
        Assert.Equal(0, Open());
        RunToFinished();
        PrepareResult();

        Assert.Equal(0, GetResult());
        var result = ReadResult();
        Assert.Equal(ModeUserMsg, result.Mode);
        Assert.Equal(0, result.Result);
        Assert.Equal(1, result.ButtonId);
        Assert.Equal(UserData, result.UserData);
        Assert.Equal(ResultDirName, result.DirNamePtr);
        Assert.Equal(ResultParam, result.ParamPtr);
    }

    [Fact]
    public void FixedSaveQuestion_WithNegativeDefault_AnswersNoForTheRequestedDirectory()
    {
        SaveDataDialogExports.DefaultChoiceOverride = SaveDataDialogExports.DefaultChoice.Negative;
        CreateSave("SLOT1", "Slot one", new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        Assert.Equal(0, Initialize());
        WriteParam(ModeSystemMsg, DispSave, ["SLOT1"], sysMsgType: 3);
        Assert.Equal(0, Open());
        RunToFinished();
        PrepareResult();

        Assert.Equal(0, GetResult());
        var result = ReadResult();
        Assert.Equal(0, result.Result);
        Assert.Equal(2, result.ButtonId);
        Assert.Equal("SLOT1", ReadAscii(ResultDirName, 32));
        Assert.Equal("Slot one", ReadAscii(ResultParam, 128));
    }

    [Fact]
    public void OkCancelMessage_WithNegativeDefault_IsUserCanceled()
    {
        SaveDataDialogExports.DefaultChoiceOverride = SaveDataDialogExports.DefaultChoice.Negative;
        Assert.Equal(0, Initialize());
        WriteParam(ModeUserMsg, userButtonType: 3);
        Assert.Equal(0, Open());
        RunToFinished();
        PrepareResult();

        Assert.Equal(0, GetResult());
        var result = ReadResult();
        Assert.Equal(1, result.Result);
        Assert.Equal(0, result.ButtonId);
    }

    [Fact]
    public void ProgressBar_StaysRunningUntilClosed()
    {
        Assert.Equal(0, Initialize());
        WriteParam(ModeProgressBar, DispSave, ["SLOT1"]);
        Assert.Equal(0, Open());
        for (var i = 0; i < 5; i++)
        {
            Assert.Equal(StatusRunning, Status());
            Assert.Equal(StatusRunning, SaveDataDialogExports.SaveDataDialogGetStatus(_ctx));
        }

        Assert.Equal(0, Close());
        Assert.Equal(ErrorNotRunning, Close());
        Assert.Equal(StatusFinished, Status());
        PrepareResult();

        Assert.Equal(0, GetResult());
        var result = ReadResult();
        Assert.Equal(ModeProgressBar, result.Mode);
        Assert.Equal(0, result.Result);
        Assert.Equal(0, result.ButtonId);
        Assert.Equal("SLOT1", ReadAscii(ResultDirName, 32));
        // SLOT1 does not exist on the host: its parameters are reported zeroed.
        Assert.All(ReadBytes(ResultParam, 0x530), b => Assert.Equal(0, b));
    }

    [Fact]
    public void LoadList_WithNoSavesOnHost_IsTheNoDataScreen()
    {
        Assert.Equal(0, Initialize());
        WriteParam(ModeList, DispLoad, ["SLOT1", "SLOT2"]);
        Assert.Equal(0, Open());
        RunToFinished();
        PrepareResult();

        Assert.Equal(0, GetResult());
        var result = ReadResult();
        Assert.Equal(ModeList, result.Mode);
        Assert.Equal(1, result.Result);
        Assert.Equal(0, result.ButtonId);
        Assert.Equal(UserData, result.UserData);
        Assert.Equal(string.Empty, ReadAscii(ResultDirName, 32));
        Assert.All(ReadBytes(ResultParam, 0x530), b => Assert.Equal(0, b));
    }

    [Fact]
    public void LoadList_SelectsTheFocusedExistingSave()
    {
        CreateSave("OLD", "Old save", new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        CreateSave("NEW", "New save", new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc));
        Assert.Equal(0, Initialize());

        // List head skips the listed directory that does not exist.
        WriteParam(ModeList, DispLoad, ["MISSING", "OLD", "NEW"]);
        Assert.Equal(0, Open());
        RunToFinished();
        PrepareResult();
        Assert.Equal(0, GetResult());
        Assert.Equal(0, ReadResult().Result);
        Assert.Equal(1, ReadResult().ButtonId);
        Assert.Equal("OLD", ReadAscii(ResultDirName, 32));
        Assert.Equal("Old save", ReadAscii(ResultParam, 128));

        WriteParam(ModeList, DispLoad, ["MISSING", "OLD", "NEW"], focusPos: FocusDataLatest);
        Assert.Equal(0, Open());
        RunToFinished();
        PrepareResult();
        Assert.Equal(0, GetResult());
        Assert.Equal("NEW", ReadAscii(ResultDirName, 32));
        Assert.Equal("New save", ReadAscii(ResultParam, 128));
    }

    [Fact]
    public void SaveList_FocusedOnTheNewItem_ReturnsAnEmptyDirectory()
    {
        CreateSave("OLD", "Old save", new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        Assert.Equal(0, Initialize());
        WriteParam(ModeList, DispSave, ["OLD"], newItem: true);
        Assert.Equal(0, Open());
        RunToFinished();
        PrepareResult();

        Assert.Equal(0, GetResult());
        var result = ReadResult();
        Assert.Equal(0, result.Result);
        Assert.Equal(1, result.ButtonId);
        Assert.Equal(string.Empty, ReadAscii(ResultDirName, 32));
    }

    [Fact]
    public void List_WithNegativeDefault_IsUserCanceled()
    {
        SaveDataDialogExports.DefaultChoiceOverride = SaveDataDialogExports.DefaultChoice.Negative;
        CreateSave("OLD", "Old save", new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        Assert.Equal(0, Initialize());
        WriteParam(ModeList, DispLoad, ["OLD"]);
        Assert.Equal(0, Open());
        RunToFinished();
        PrepareResult();

        Assert.Equal(0, GetResult());
        Assert.Equal(1, ReadResult().Result);
        Assert.Equal(string.Empty, ReadAscii(ResultDirName, 32));
    }

    private byte[] ReadBytes(ulong address, int length)
    {
        var bytes = new byte[length];
        Assert.True(_memory.TryRead(address, bytes));
        return bytes;
    }
}
