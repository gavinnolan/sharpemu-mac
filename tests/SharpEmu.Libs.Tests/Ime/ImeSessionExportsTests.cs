// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Text;
using SharpEmu.HLE;
using SharpEmu.Libs.Ime;
using Xunit;

namespace SharpEmu.Libs.Tests.Ime;

[Collection(ImeStateCollection.Name)]
public sealed class ImeSessionExportsTests : IDisposable
{
    private const int NotOpened = unchecked((int)0x80BC0002);
    private const int Busy = unchecked((int)0x80BC0001);
    private const int InvalidUserId = unchecked((int)0x80BC0010);
    private const int InvalidType = unchecked((int)0x80BC0011);
    private const int InvalidMaxTextLength = unchecked((int)0x80BC0016);
    private const int InvalidTextBuffer = unchecked((int)0x80BC0017);
    private const int InvalidWork = unchecked((int)0x80BC0020);
    private const int InvalidHandler = unchecked((int)0x80BC0022);
    private const int InvalidMode = unchecked((int)0x80BC0024);
    private const int InvalidParam = unchecked((int)0x80BC0030);
    private const int InvalidAddress = unchecked((int)0x80BC0031);
    private const ulong Base = 0x1_0000_0000;
    private const ulong ParamAddress = Base + 0x100;
    private const ulong BufferAddress = Base + 0x200;
    private const ulong TextAddress = Base + 0x300;
    private const ulong CaretAddress = Base + 0x400;
    private const ulong FaultingAddress = Base + 0x1000;
    private const int UserId = 0x10000000;

    private readonly FakeCpuMemory _memory = new(Base, 0x1000);
    private readonly CpuContext _ctx;

    public ImeSessionExportsTests()
    {
        _ctx = new CpuContext(_memory, Generation.Gen5);
        ImeExports.ResetForTests();
    }

    public void Dispose() => ImeExports.ResetForTests();

    [Fact]
    public void ParamInitZeroesTheStructAndMarksTheUserInvalid()
    {
        Assert.True(_memory.TryWrite(ParamAddress, Enumerable.Repeat((byte)0x6B, 0x70).ToArray()));
        _ctx[CpuRegister.Rdi] = ParamAddress;

        Assert.Equal(0, ImeExports.ImeParamInit(_ctx));

        var bytes = new byte[0x70];
        Assert.True(_memory.TryRead(ParamAddress, bytes));
        Assert.Equal(-1, BinaryPrimitives.ReadInt32LittleEndian(bytes));
        Assert.All(bytes.Skip(4).Take(0x5C), b => Assert.Equal(0, b));
        // sizeof(SceImeParam) == 0x60: nothing past it is touched.
        Assert.Equal(0x6B, bytes[0x60]);
    }

    [Fact]
    public void ParamInitToleratesANullPointer()
    {
        _ctx[CpuRegister.Rdi] = 0;
        Assert.Equal(0, ImeExports.ImeParamInit(_ctx));
    }

    [Fact]
    public void OpenValidatesTheParamStruct()
    {
        _ctx[CpuRegister.Rdi] = 0;
        Assert.Equal(InvalidAddress, ImeExports.ImeOpen(_ctx));

        _ctx[CpuRegister.Rdi] = FaultingAddress;
        Assert.Equal(InvalidAddress, ImeExports.ImeOpen(_ctx));

        Assert.Equal(InvalidUserId, OpenWith(userId: -1));
        Assert.Equal(InvalidType, OpenWith(type: 5));
        Assert.Equal(InvalidMaxTextLength, OpenWith(maxTextLength: 0));
        Assert.Equal(InvalidMaxTextLength, OpenWith(maxTextLength: 2049));
        Assert.Equal(InvalidTextBuffer, OpenWith(buffer: 0));
        Assert.Equal(InvalidWork, OpenWith(work: 0));
        Assert.Equal(InvalidHandler, OpenWith(handler: 0));

        // None of the failed opens left a session behind.
        Assert.Equal(NotOpened, ImeExports.ImeClose(_ctx));
    }

    [Fact]
    public void OpenCloseWalkTheSessionState()
    {
        Assert.Equal(NotOpened, ImeExports.ImeClose(_ctx));

        Assert.Equal(0, OpenWith());
        Assert.Equal(Busy, OpenWith());

        Assert.Equal(0, ImeExports.ImeClose(_ctx));
        Assert.Equal(NotOpened, ImeExports.ImeClose(_ctx));
        Assert.Equal(0, OpenWith());
    }

    [Fact]
    public void SetTextAndSetCaretNeedAnOpenSession()
    {
        _ctx[CpuRegister.Rdi] = TextAddress;
        _ctx[CpuRegister.Rsi] = 1;
        Assert.Equal(NotOpened, ImeExports.ImeSetText(_ctx));

        WriteCaret(0);
        _ctx[CpuRegister.Rdi] = CaretAddress;
        Assert.Equal(NotOpened, ImeExports.ImeSetCaret(_ctx));
    }

    [Fact]
    public void CaretIndexIsBoundedByTheCurrentText()
    {
        WriteBuffer("abc");
        Assert.Equal(0, OpenWith(maxTextLength: 8));

        // Initial text "abc": indices 0..3 are valid.
        Assert.Equal(0, SetCaret(3));
        Assert.Equal(InvalidParam, SetCaret(4));

        WriteText("hello world");
        _ctx[CpuRegister.Rdi] = TextAddress;
        _ctx[CpuRegister.Rsi] = 11;
        Assert.Equal(0, ImeExports.ImeSetText(_ctx));

        // Clamped to maxTextLength (8).
        Assert.Equal(0, SetCaret(8));
        Assert.Equal(InvalidParam, SetCaret(9));

        _ctx[CpuRegister.Rsi] = 0;
        Assert.Equal(0, ImeExports.ImeSetText(_ctx));
        Assert.Equal(0, SetCaret(0));
        Assert.Equal(InvalidParam, SetCaret(1));
    }

    [Fact]
    public void SetTextAndSetCaretRejectBadPointers()
    {
        Assert.Equal(0, OpenWith());

        _ctx[CpuRegister.Rdi] = 0;
        _ctx[CpuRegister.Rsi] = 1;
        Assert.Equal(InvalidAddress, ImeExports.ImeSetText(_ctx));
        _ctx[CpuRegister.Rdi] = FaultingAddress;
        Assert.Equal(InvalidAddress, ImeExports.ImeSetText(_ctx));

        _ctx[CpuRegister.Rdi] = 0;
        Assert.Equal(InvalidAddress, ImeExports.ImeSetCaret(_ctx));
        _ctx[CpuRegister.Rdi] = FaultingAddress;
        Assert.Equal(InvalidAddress, ImeExports.ImeSetCaret(_ctx));
    }

    [Fact]
    public void KeyboardCloseAndSetModeFollowTheKeyboardState()
    {
        _ctx[CpuRegister.Rdi] = UserId;
        Assert.Equal(NotOpened, ImeExports.ImeKeyboardClose(_ctx));
        _ctx[CpuRegister.Rsi] = 1;
        Assert.Equal(NotOpened, ImeExports.ImeKeyboardSetMode(_ctx));

        Assert.Equal(0, ImeExports.ImeKeyboardOpen(_ctx));

        _ctx[CpuRegister.Rdi] = UserId;
        _ctx[CpuRegister.Rsi] = 0x7F;
        Assert.Equal(0, ImeExports.ImeKeyboardSetMode(_ctx));
        _ctx[CpuRegister.Rsi] = 0x80;
        Assert.Equal(InvalidMode, ImeExports.ImeKeyboardSetMode(_ctx));

        _ctx[CpuRegister.Rdi] = unchecked((ulong)-1L);
        _ctx[CpuRegister.Rsi] = 1;
        Assert.Equal(InvalidUserId, ImeExports.ImeKeyboardSetMode(_ctx));
        Assert.Equal(InvalidUserId, ImeExports.ImeKeyboardClose(_ctx));

        _ctx[CpuRegister.Rdi] = UserId;
        Assert.Equal(0, ImeExports.ImeKeyboardClose(_ctx));
        Assert.Equal(NotOpened, ImeExports.ImeKeyboardClose(_ctx));
    }

    private int SetCaret(uint index)
    {
        WriteCaret(index);
        _ctx[CpuRegister.Rdi] = CaretAddress;
        return ImeExports.ImeSetCaret(_ctx);
    }

    // SceImeCaret { float x; float y; uint32 height; uint32 index }.
    private void WriteCaret(uint index)
    {
        var caret = new byte[0x10];
        BinaryPrimitives.WriteUInt32LittleEndian(caret.AsSpan(0x0C), index);
        Assert.True(_memory.TryWrite(CaretAddress, caret));
    }

    private void WriteBuffer(string text) =>
        Assert.True(_memory.TryWrite(BufferAddress, Encoding.Unicode.GetBytes(text + "\0")));

    private void WriteText(string text) =>
        Assert.True(_memory.TryWrite(TextAddress, Encoding.Unicode.GetBytes(text)));

    // SceImeParam: userId @0, type @4, maxTextLength @0x24, inputTextBuffer @0x28, work @0x40, handler @0x50.
    private int OpenWith(
        int userId = UserId,
        uint type = 0,
        uint maxTextLength = 16,
        ulong buffer = BufferAddress,
        ulong work = Base + 0x600,
        ulong handler = 0x4000)
    {
        var param = new byte[0x60];
        BinaryPrimitives.WriteInt32LittleEndian(param, userId);
        BinaryPrimitives.WriteUInt32LittleEndian(param.AsSpan(0x04), type);
        BinaryPrimitives.WriteUInt32LittleEndian(param.AsSpan(0x24), maxTextLength);
        BinaryPrimitives.WriteUInt64LittleEndian(param.AsSpan(0x28), buffer);
        BinaryPrimitives.WriteUInt64LittleEndian(param.AsSpan(0x40), work);
        BinaryPrimitives.WriteUInt64LittleEndian(param.AsSpan(0x50), handler);
        Assert.True(_memory.TryWrite(ParamAddress, param));
        _ctx[CpuRegister.Rdi] = ParamAddress;
        _ctx[CpuRegister.Rsi] = 0;
        return ImeExports.ImeOpen(_ctx);
    }
}
