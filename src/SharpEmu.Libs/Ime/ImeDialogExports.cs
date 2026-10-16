// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System;
using System.Buffers.Binary;
using System.Text;
using SharpEmu.HLE;

namespace SharpEmu.Libs.Ime;

/// <summary>
/// libSceImeDialog. There is no host text-entry UI, so the dialog is simulated:
/// NONE -> RUNNING (Init) -> FINISHED (second status poll, or Abort) -> NONE (Term).
/// The returned text is the dialog's own initial text unless
/// <see cref="DefaultTextEnvironmentVariable"/> supplies one.
/// </summary>
public static class ImeDialogExports
{
    private const int StatusNone = 0;
    private const int StatusRunning = 1;
    private const int StatusFinished = 2;

    // SceImeDialogEndStatus.
    internal const uint EndStatusOk = 0;
    internal const uint EndStatusAborted = 2;

    // SCE_IME_ERROR_* as defined by kyty (anyps5 reports 0x80BC0002 NOT_OPENED for the
    // not-running / not-in-use cases instead; the finer-grained kyty codes are used here).
    internal const int ErrorBusy = unchecked((int)0x80BC0001);
    internal const int ErrorInvalidType = unchecked((int)0x80BC0011);
    internal const int ErrorInvalidAddress = unchecked((int)0x80BC0031);
    internal const int ErrorNotRunning = unchecked((int)0x80BC0105);
    internal const int ErrorNotFinished = unchecked((int)0x80BC0106);
    internal const int ErrorNotInUse = unchecked((int)0x80BC0107);

    // SceImeDialogParam (0x60 bytes): userId @0x00, type @0x04, supportedLanguages @0x08,
    // enterLabel @0x10, inputMethod @0x14, filter @0x18, option @0x20, maxTextLength @0x24,
    // inputTextBuffer @0x28, posx @0x30, posy @0x34, horizontalAlignment @0x38,
    // verticalAlignment @0x3C, placeholder @0x40, title @0x48, reserved[16] @0x50.
    private const int ParamSize = 0x60;
    private const int ParamTypeOffset = 0x04;
    private const int ParamOptionOffset = 0x20;
    private const int ParamMaxTextLengthOffset = 0x24;
    private const int ParamInputTextBufferOffset = 0x28;
    private const int ParamPosXOffset = 0x30;
    private const int ParamPosYOffset = 0x34;
    private const int ParamHAlignOffset = 0x38;
    private const int ParamVAlignOffset = 0x3C;

    private const uint TypeNumber = 4;
    private const uint OptionMultiline = 0x1;
    private const uint OptionUseOver2K = 0x4000;
    private const uint MaxTextLengthLimit = 2048;

    // SceImeDialogResult { SceImeDialogEndStatus endstatus @0; int8 reserved[12] } = 0x10 bytes.
    internal const int ResultSize = 0x10;

    // SceImePositionAndForm { uint32 type @0; float posx @4; float posy @8; uint32 halign @0xC;
    // uint32 valign @0x10; uint32 width @0x14; uint32 height @0x18 } = 0x1C bytes.
    internal const int PositionAndFormSize = 0x1C;
    private const uint PositionAndFormTypeDialog = 2;

    /// <summary>Status polls after Init before the dialog finishes.</summary>
    internal const int PollsUntilFinish = 2;

    /// <summary>
    /// Text returned to titles that ask for text input. Unset (the default) leaves the
    /// dialog's initial text untouched.
    /// </summary>
    internal const string DefaultTextEnvironmentVariable = "SHARPEMU_IME_DEFAULT_TEXT";

    private static readonly object _gate = new();
    private static int _status = StatusNone;
    private static int _pollsLeft;
    private static uint _endStatus = EndStatusOk;
    private static uint _type;
    private static uint _option;
    private static uint _posX;
    private static uint _posY;
    private static uint _hAlign;
    private static uint _vAlign;

    internal static void ResetForTests()
    {
        lock (_gate)
        {
            _status = StatusNone;
            _pollsLeft = 0;
            _endStatus = EndStatusOk;
        }
    }

    // Panel size for a given type/option (extended parameters are not applied).
    internal static (uint Width, uint Height) ComputePanelSize(uint type, uint option)
    {
        uint width;
        uint height;
        if (type == TypeNumber)
        {
            width = 370;
            height = 522;
        }
        else
        {
            width = 793;
            height = (option & OptionMultiline) != 0 ? 628u : 528u;
        }

        if ((option & OptionUseOver2K) != 0)
        {
            width *= 2;
            height *= 2;
        }

        return (width, height);
    }

    [SysAbiExport(
        Nid = "NUeBrN7hzf0",
        ExportName = "sceImeDialogInit",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceImeDialog")]
    public static int ImeDialogInit(CpuContext ctx)
    {
        var parameterAddress = ctx[CpuRegister.Rdi];
        if (parameterAddress == 0)
        {
            return SetReturn(ctx, ErrorInvalidAddress);
        }

        Span<byte> param = stackalloc byte[ParamSize];
        if (!ctx.Memory.TryRead(parameterAddress, param))
        {
            return SetReturn(ctx, ErrorInvalidAddress);
        }

        var type = BinaryPrimitives.ReadUInt32LittleEndian(param[ParamTypeOffset..]);
        if (type > TypeNumber)
        {
            return SetReturn(ctx, ErrorInvalidType);
        }

        lock (_gate)
        {
            if (_status != StatusNone)
            {
                return SetReturn(ctx, ErrorBusy);
            }

            _type = type;
            _option = BinaryPrimitives.ReadUInt32LittleEndian(param[ParamOptionOffset..]);
            _posX = BinaryPrimitives.ReadUInt32LittleEndian(param[ParamPosXOffset..]);
            _posY = BinaryPrimitives.ReadUInt32LittleEndian(param[ParamPosYOffset..]);
            _hAlign = BinaryPrimitives.ReadUInt32LittleEndian(param[ParamHAlignOffset..]);
            _vAlign = BinaryPrimitives.ReadUInt32LittleEndian(param[ParamVAlignOffset..]);
            _endStatus = EndStatusOk;
            _pollsLeft = PollsUntilFinish;
            _status = StatusRunning;
        }

        TryWriteDefaultText(ctx, param);
        return SetReturn(ctx, 0);
    }

    [SysAbiExport(
        Nid = "IADmD4tScBY",
        ExportName = "sceImeDialogGetStatus",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceImeDialog")]
    public static int ImeDialogGetStatus(CpuContext ctx)
    {
        int status;
        lock (_gate)
        {
            // Nobody can type, so the dialog ends on its own at the second poll.
            if (_status == StatusRunning && --_pollsLeft <= 0)
            {
                _status = StatusFinished;
            }

            status = _status;
        }

        ctx[CpuRegister.Rax] = unchecked((ulong)(long)status);
        return status;
    }

    [SysAbiExport(
        Nid = "x01jxu+vxlc",
        ExportName = "sceImeDialogGetResult",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceImeDialog")]
    public static int ImeDialogGetResult(CpuContext ctx)
    {
        var resultAddress = ctx[CpuRegister.Rdi];
        uint endStatus;
        lock (_gate)
        {
            if (_status == StatusNone)
            {
                return SetReturn(ctx, ErrorNotInUse);
            }

            if (resultAddress == 0)
            {
                return SetReturn(ctx, ErrorInvalidAddress);
            }

            if (_status != StatusFinished)
            {
                return SetReturn(ctx, ErrorNotFinished);
            }

            endStatus = _endStatus;
        }

        Span<byte> result = stackalloc byte[ResultSize];
        result.Clear();
        BinaryPrimitives.WriteUInt32LittleEndian(result, endStatus);
        return ctx.Memory.TryWrite(resultAddress, result)
            ? SetReturn(ctx, 0)
            : SetReturn(ctx, ErrorInvalidAddress);
    }

    [SysAbiExport(
        Nid = "oBmw4xrmfKs",
        ExportName = "sceImeDialogAbort",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceImeDialog")]
    public static int ImeDialogAbort(CpuContext ctx)
    {
        lock (_gate)
        {
            if (_status == StatusNone)
            {
                return SetReturn(ctx, ErrorNotInUse);
            }

            if (_status != StatusRunning)
            {
                return SetReturn(ctx, ErrorNotRunning);
            }

            _endStatus = EndStatusAborted;
            _status = StatusFinished;
        }

        return SetReturn(ctx, 0);
    }

    [SysAbiExport(
        Nid = "gyTyVn+bXMw",
        ExportName = "sceImeDialogTerm",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceImeDialog")]
    public static int ImeDialogTerm(CpuContext ctx)
    {
        lock (_gate)
        {
            if (_status == StatusNone)
            {
                return SetReturn(ctx, ErrorNotInUse);
            }

            if (_status != StatusFinished)
            {
                return SetReturn(ctx, ErrorNotFinished);
            }

            _status = StatusNone;
        }

        return SetReturn(ctx, 0);
    }

    // int sceImeDialogGetPanelSize(const SceImeDialogParam *param, uint32_t *width, uint32_t *height).
    [SysAbiExport(
        Nid = "wqsJvRXwl58",
        ExportName = "sceImeDialogGetPanelSize",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceImeDialog")]
    public static int ImeDialogGetPanelSize(CpuContext ctx)
    {
        var paramAddress = ctx[CpuRegister.Rdi];
        var widthAddress = ctx[CpuRegister.Rsi];
        var heightAddress = ctx[CpuRegister.Rdx];
        if (paramAddress == 0 || widthAddress == 0 || heightAddress == 0)
        {
            return SetReturn(ctx, ErrorInvalidAddress);
        }

        Span<byte> param = stackalloc byte[ParamSize];
        if (!ctx.Memory.TryRead(paramAddress, param))
        {
            return SetReturn(ctx, ErrorInvalidAddress);
        }

        var type = BinaryPrimitives.ReadUInt32LittleEndian(param[ParamTypeOffset..]);
        if (type > TypeNumber)
        {
            return SetReturn(ctx, ErrorInvalidType);
        }

        var (width, height) = ComputePanelSize(type, BinaryPrimitives.ReadUInt32LittleEndian(param[ParamOptionOffset..]));
        Span<byte> value = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(value, width);
        var ok = ctx.Memory.TryWrite(widthAddress, value);
        BinaryPrimitives.WriteUInt32LittleEndian(value, height);
        ok &= ctx.Memory.TryWrite(heightAddress, value);
        return SetReturn(ctx, ok ? 0 : ErrorInvalidAddress);
    }

    // int sceImeDialogGetPanelPositionAndForm(SceImePositionAndForm *form): describes the open dialog.
    [SysAbiExport(
        Nid = "8jqzzPioYl8",
        ExportName = "sceImeDialogGetPanelPositionAndForm",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceImeDialog")]
    public static int ImeDialogGetPanelPositionAndForm(CpuContext ctx)
    {
        var formAddress = ctx[CpuRegister.Rdi];
        Span<byte> form = stackalloc byte[PositionAndFormSize];
        form.Clear();
        lock (_gate)
        {
            if (_status == StatusNone)
            {
                return SetReturn(ctx, ErrorNotInUse);
            }

            if (formAddress == 0)
            {
                return SetReturn(ctx, ErrorInvalidAddress);
            }

            var (width, height) = ComputePanelSize(_type, _option);
            BinaryPrimitives.WriteUInt32LittleEndian(form, PositionAndFormTypeDialog);
            BinaryPrimitives.WriteUInt32LittleEndian(form[0x04..], _posX);
            BinaryPrimitives.WriteUInt32LittleEndian(form[0x08..], _posY);
            BinaryPrimitives.WriteUInt32LittleEndian(form[0x0C..], _hAlign);
            BinaryPrimitives.WriteUInt32LittleEndian(form[0x10..], _vAlign);
            BinaryPrimitives.WriteUInt32LittleEndian(form[0x14..], width);
            BinaryPrimitives.WriteUInt32LittleEndian(form[0x18..], height);
        }

        return ctx.Memory.TryWrite(formAddress, form)
            ? SetReturn(ctx, 0)
            : SetReturn(ctx, ErrorInvalidAddress);
    }

    // When the user configured a default text, write it as the dialog's answer. Without it the
    // buffer keeps the title's own initial text, which is what an unedited dialog returns.
    private static void TryWriteDefaultText(CpuContext ctx, ReadOnlySpan<byte> param)
    {
        var text = Environment.GetEnvironmentVariable(DefaultTextEnvironmentVariable);
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        var bufferAddress = BinaryPrimitives.ReadUInt64LittleEndian(param[ParamInputTextBufferOffset..]);
        var maxTextLength = BinaryPrimitives.ReadUInt32LittleEndian(param[ParamMaxTextLengthOffset..]);
        if (bufferAddress == 0 || maxTextLength == 0 || maxTextLength > MaxTextLengthLimit)
        {
            return;
        }

        if ((uint)text.Length > maxTextLength)
        {
            text = text[..(int)maxTextLength];
        }

        var encoded = Encoding.Unicode.GetBytes(text);
        var payload = new byte[encoded.Length + 2];
        encoded.CopyTo(payload, 0);

        if (ctx.Memory.TryWrite(bufferAddress, payload))
        {
            Console.Error.WriteLine(
                $"[LOADER][INFO] ime.dialog_default_text buf=0x{bufferAddress:X16} " +
                $"max={maxTextLength} text=\"{text}\"");
        }
    }

    private static int SetReturn(CpuContext ctx, int result)
    {
        ctx[CpuRegister.Rax] = unchecked((ulong)(long)result);
        return result;
    }
}
