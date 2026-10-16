// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using System.Buffers.Binary;

namespace SharpEmu.Libs.CommonDialog;

/// <summary>
/// libSceMsgDialog. There is no host popup, so the dialog is simulated:
/// NONE -> INITIALIZED (Initialize) -> RUNNING (Open) -> FINISHED -> NONE (Terminate).
/// User-message and system-message dialogs with at least one button finish on their own after
/// <see cref="PollsUntilAutoFinish"/> status polls, answering with a configurable default
/// button (<see cref="DefaultButtonEnvironmentVariable"/>). Progress-bar dialogs and
/// button-less user-message dialogs stay RUNNING until sceMsgDialogClose.
/// </summary>
public static class MsgDialogExports
{
    private const int StatusNone = 0;
    private const int StatusInitialized = 1;
    private const int StatusRunning = 2;
    private const int StatusFinished = 3;

    private const int ErrorOk = 0;
    private const int ErrorNotSystemInitialized = CommonDialogState.ErrorNotSystemInitialized;
    private const int ErrorNotInitialized = CommonDialogState.ErrorNotInitialized;
    private const int ErrorNotFinished = CommonDialogState.ErrorNotFinished;
    private const int ErrorBusy = CommonDialogState.ErrorBusy;
    private const int ErrorParamInvalid = CommonDialogState.ErrorParamInvalid;
    private const int ErrorNotRunning = CommonDialogState.ErrorNotRunning;
    private const int ErrorArgNull = CommonDialogState.ErrorArgNull;

    // SceMsgDialogMode.
    internal const int ModeUserMsg = 1;
    internal const int ModeProgressBar = 2;
    internal const int ModeSystemMsg = 3;

    // SceMsgDialogButtonType (values 0-4 as defined by both references).
    internal const int ButtonTypeOk = 0;
    internal const int ButtonTypeYesNo = 1;
    internal const int ButtonTypeNone = 2;
    internal const int ButtonTypeOkCancel = 3;
    internal const int ButtonTypeYesNoFocusNo = 4;

    // SceMsgDialogButtonId: INVALID = 0, OK/YES/BUTTON1 = 1, NO/BUTTON2 = 2.
    internal const int ButtonIdInvalid = 0;
    internal const int ButtonIdFirst = 1;
    internal const int ButtonIdSecond = 2;

    // SceMsgDialogResult { int32 mode @0; int32 result @4; int32 buttonId @8; uint8 reserved[32] }.
    // result: SCE_COMMON_DIALOG_RESULT_OK = 0.
    internal const int ResultSize = 0x2C;

    // SceMsgDialogParam: base SceCommonDialogBaseParam (0x30 bytes), then size_t size @0x30,
    // int mode @0x38, userMsgParam* @0x40, progBarParam* @0x48, sysMsgParam* @0x50.
    private const int ParamModeOffset = 0x38;
    private const int ParamUserMsgOffset = 0x40;
    private const int ParamHeadSize = 0x58;

    // Percentage bar range accepted by ProgressBarSetValue/Inc, and the only valid target
    // (SCE_MSG_DIALOG_PROGRESSBAR_TARGET_BAR_DEFAULT).
    private const uint ProgressMax = 100;
    private const int ProgressTargetBarDefault = 0;

    /// <summary>Status polls (GetStatus/UpdateStatus) after Open before an answerable dialog finishes.</summary>
    internal const int PollsUntilAutoFinish = 2;

    /// <summary>
    /// Selects which button an auto-finishing two-button dialog answers with. "1", "first",
    /// "ok", "yes" (default) pick the first/affirmative button; "2", "second", "no",
    /// "cancel" pick the second.
    /// </summary>
    internal const string DefaultButtonEnvironmentVariable = "SHARPEMU_MSGDIALOG_DEFAULT_BUTTON";

    private static readonly object Gate = new();
    private static int _status;
    private static int _mode = ModeUserMsg;
    private static int _buttonId = ButtonIdInvalid;
    private static bool _autoFinish;
    private static int _pollsLeft;
    private static bool _usedMarked;
    private static uint _progress;

    /// <summary>Current progress-bar percentage (0-100) of the open dialog.</summary>
    internal static uint ProgressValue
    {
        get
        {
            lock (Gate)
            {
                return _progress;
            }
        }
    }

    internal static void ResetForTests()
    {
        lock (Gate)
        {
            ReleaseUsedLocked();
            _status = StatusNone;
            _mode = ModeUserMsg;
            _buttonId = ButtonIdInvalid;
            _autoFinish = false;
            _pollsLeft = 0;
            _progress = 0;
        }
    }

    /// <summary>
    /// Button an auto-finishing dialog of <paramref name="buttonType"/> answers with. The first
    /// (affirmative) button is the default regardless of the initially focused one.
    /// </summary>
    internal static int ResolveDefaultButtonId(string? setting, int buttonType)
    {
        var twoButtons = buttonType is ButtonTypeYesNo or ButtonTypeOkCancel or ButtonTypeYesNoFocusNo;
        if (!twoButtons || string.IsNullOrWhiteSpace(setting))
        {
            return ButtonIdFirst;
        }

        return setting.Trim().ToLowerInvariant() switch
        {
            "2" or "second" or "no" or "cancel" => ButtonIdSecond,
            _ => ButtonIdFirst,
        };
    }

    [SysAbiExport(
        Nid = "lDqxaY1UbEo",
        ExportName = "sceMsgDialogInitialize",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceMsgDialog")]
    public static int MsgDialogInitialize(CpuContext ctx)
    {
        // sceCommonDialogInitialize must have succeeded first.
        if (!CommonDialogState.IsInitialized)
        {
            return ctx.SetReturn(ErrorNotSystemInitialized);
        }

        lock (Gate)
        {
            // Re-initializing while a dialog is open would clobber it: report BUSY (neither
            // reference defines a more specific code). Repeating Initialize while idle or
            // after a finished dialog stays a success, as in both references.
            if (_status == StatusRunning)
            {
                return ctx.SetReturn(ErrorBusy);
            }

            if (_status == StatusNone)
            {
                _status = StatusInitialized;
            }
        }

        return ctx.SetReturn(ErrorOk);
    }

    [SysAbiExport(
        Nid = "ePw-kqZmelo",
        ExportName = "sceMsgDialogTerminate",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceMsgDialog")]
    public static int MsgDialogTerminate(CpuContext ctx)
    {
        lock (Gate)
        {
            if (_status == StatusNone)
            {
                return ctx.SetReturn(ErrorNotInitialized);
            }

            // Terminating a running dialog tears it down (both references allow it).
            ReleaseUsedLocked();
            _status = StatusNone;
            _progress = 0;
        }

        return ctx.SetReturn(ErrorOk);
    }

    [SysAbiExport(
        Nid = "b06Hh0DPEaE",
        ExportName = "sceMsgDialogOpen",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceMsgDialog")]
    public static int MsgDialogOpen(CpuContext ctx)
    {
        var paramAddress = ctx[CpuRegister.Rdi];
        if (paramAddress == 0)
        {
            return ctx.SetReturn(ErrorArgNull);
        }

        lock (Gate)
        {
            if (_status == StatusNone)
            {
                return ctx.SetReturn(ErrorNotInitialized);
            }

            if (_status == StatusRunning)
            {
                return ctx.SetReturn(ErrorBusy);
            }

            var mode = ModeUserMsg;
            var buttonType = ButtonTypeOk;
            Span<byte> head = stackalloc byte[ParamHeadSize];
            if (ctx.Memory.TryRead(paramAddress, head))
            {
                mode = BinaryPrimitives.ReadInt32LittleEndian(head[ParamModeOffset..]);
                if (mode is < ModeUserMsg or > ModeSystemMsg)
                {
                    return ctx.SetReturn(ErrorParamInvalid);
                }

                if (mode == ModeUserMsg)
                {
                    buttonType = ReadButtonType(ctx, BinaryPrimitives.ReadUInt64LittleEndian(head[ParamUserMsgOffset..]));
                }
            }

            LogDialogMessage(ctx, paramAddress);

            _mode = mode;
            _progress = 0;
            _buttonId = ButtonIdInvalid;
            _pollsLeft = PollsUntilAutoFinish;
            // Progress bars and button-less dialogs are ended by the title (Close).
            _autoFinish = mode != ModeProgressBar && !(mode == ModeUserMsg && buttonType == ButtonTypeNone);
            if (_autoFinish)
            {
                _buttonId = mode == ModeUserMsg
                    ? ResolveDefaultButtonId(Environment.GetEnvironmentVariable(DefaultButtonEnvironmentVariable), buttonType)
                    : ButtonIdFirst;
            }

            _status = StatusRunning;
            if (!_usedMarked)
            {
                _usedMarked = true;
                CommonDialogState.MarkUsed(true);
            }
        }

        return ctx.SetReturn(ErrorOk);
    }

    // SceMsgDialogUserMessageParam { int buttonType @0; const char* msg @8; ... }.
    private static int ReadButtonType(CpuContext ctx, ulong userMsgAddress)
    {
        Span<byte> field = stackalloc byte[4];
        return userMsgAddress != 0 && ctx.Memory.TryRead(userMsgAddress, field)
            ? BinaryPrimitives.ReadInt32LittleEndian(field)
            : ButtonTypeOk;
    }

    // Callers hold Gate.
    private static void ReleaseUsedLocked()
    {
        if (_usedMarked)
        {
            _usedMarked = false;
            CommonDialogState.MarkUsed(false);
        }
    }

    // Callers hold Gate.
    private static void FinishLocked()
    {
        _status = StatusFinished;
        ReleaseUsedLocked();
    }

    // Best-effort extraction of the dialog text so fatal-error popups are visible in the
    // log even though no host dialog is shown. The PS5 SceMsgDialogParam layout is not
    // fully known, so chase every qword in the struct that points at readable text - one
    // level deep, then a second level for nested sub-param structs.
    private static void LogDialogMessage(CpuContext ctx, ulong paramAddress)
    {
        Console.Error.WriteLine($"[LOADER][INFO] sceMsgDialogOpen: param=0x{paramAddress:X12}");

        Span<byte> head = stackalloc byte[0xA0];
        if (ctx.Memory.TryRead(paramAddress, head))
        {
            DumpPointerStrings(ctx, paramAddress, head, "param", chaseNested: true);
        }
    }

    private static void DumpPointerStrings(CpuContext ctx, ulong baseAddress, ReadOnlySpan<byte> bytes, string label, bool chaseNested)
    {
        Span<byte> nested = stackalloc byte[0x40];
        for (var offset = 0; offset + 8 <= bytes.Length; offset += 8)
        {
            var value = System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(bytes[offset..]);
            if (value < 0x10000 || value == baseAddress)
            {
                continue;
            }

            var text = TryReadPrintableText(ctx, value);
            if (text is not null)
            {
                Console.Error.WriteLine($"[LOADER][INFO]   {label}+0x{offset:X2} -> 0x{value:X12} text=\"{text}\"");
            }
            else if (chaseNested && ctx.Memory.TryRead(value, nested))
            {
                DumpPointerStrings(ctx, value, nested, $"{label}+0x{offset:X2}", chaseNested: false);
            }
        }
    }

    private static string? TryReadPrintableText(CpuContext ctx, ulong address)
    {
        Span<byte> bytes = stackalloc byte[256];
        if (!ctx.Memory.TryRead(address, bytes))
        {
            return null;
        }

        var nullIndex = bytes.IndexOf((byte)0);
        if (nullIndex < 2)
        {
            return null;
        }

        var candidate = bytes[..nullIndex];
        foreach (var b in candidate)
        {
            if (b is < 0x20 or > 0x7E && b != (byte)'\n' && b != (byte)'\r' && b != (byte)'\t')
            {
                return null;
            }
        }

        return System.Text.Encoding.ASCII.GetString(candidate).Replace("\n", "\\n").Replace("\r", "\\r");
    }


    [SysAbiExport(
        Nid = "CWVW78Qc3fI",
        ExportName = "sceMsgDialogGetStatus",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceMsgDialog")]
    public static int MsgDialogGetStatus(CpuContext ctx) => ctx.SetReturn(PollStatus());

    [SysAbiExport(
        Nid = "6fIC3XKt2k0",
        ExportName = "sceMsgDialogUpdateStatus",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceMsgDialog")]
    public static int MsgDialogUpdateStatus(CpuContext ctx) => ctx.SetReturn(PollStatus());

    // With no host UI nobody can answer, so an answerable dialog is dismissed by itself on its
    // second poll (the first poll after Open still reports RUNNING). Advancing on both
    // UpdateStatus and GetStatus keeps every guest polling pattern free of infinite RUNNING
    // loops. Progress-bar and button-less dialogs only finish through sceMsgDialogClose.
    private static int PollStatus()
    {
        lock (Gate)
        {
            if (_status == StatusRunning && _autoFinish && --_pollsLeft <= 0)
            {
                FinishLocked();
            }

            return _status;
        }
    }

    [SysAbiExport(
        Nid = "Lr8ovHH9l6A",
        ExportName = "sceMsgDialogGetResult",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceMsgDialog")]
    public static int MsgDialogGetResult(CpuContext ctx)
    {
        var resultAddress = ctx[CpuRegister.Rdi];
        if (resultAddress == 0)
        {
            return ctx.SetReturn(ErrorArgNull);
        }

        Span<byte> result = stackalloc byte[ResultSize];
        result.Clear();
        lock (Gate)
        {
            if (_status != StatusFinished)
            {
                return ctx.SetReturn(ErrorNotFinished);
            }

            // Echo the opened mode and the button that ended the dialog. A dialog ended by
            // sceMsgDialogClose before any answer reports BUTTON_ID_INVALID (inferred; neither
            // reference documents it). The result code is always OK: no user can cancel.
            BinaryPrimitives.WriteInt32LittleEndian(result, _mode);
            BinaryPrimitives.WriteInt32LittleEndian(result[0x04..], 0);
            BinaryPrimitives.WriteInt32LittleEndian(result[0x08..], _buttonId);
        }

        if (!ctx.Memory.TryWrite(resultAddress, result))
        {
            return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        return ctx.SetReturn(ErrorOk);
    }

    [SysAbiExport(
        Nid = "HTrcDKlFKuM",
        ExportName = "sceMsgDialogClose",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceMsgDialog")]
    public static int MsgDialogClose(CpuContext ctx)
    {
        lock (Gate)
        {
            if (_status == StatusNone)
            {
                return ctx.SetReturn(ErrorNotInitialized);
            }

            if (_status != StatusRunning)
            {
                return ctx.SetReturn(ErrorNotRunning);
            }

            FinishLocked();
        }

        return ctx.SetReturn(ErrorOk);
    }

    // int sceMsgDialogProgressBarSetValue(SceMsgDialogProgressBarTarget target, uint32_t rate).
    [SysAbiExport(
        Nid = "wTpfglkmv34",
        ExportName = "sceMsgDialogProgressBarSetValue",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceMsgDialog")]
    public static int MsgDialogProgressBarSetValue(CpuContext ctx)
    {
        var target = unchecked((int)ctx[CpuRegister.Rdi]);
        var rate = unchecked((uint)ctx[CpuRegister.Rsi]);
        lock (Gate)
        {
            var state = CheckProgressStateLocked();
            if (state != ErrorOk)
            {
                return ctx.SetReturn(state);
            }

            if (target != ProgressTargetBarDefault || rate > ProgressMax)
            {
                return ctx.SetReturn(ErrorParamInvalid);
            }

            _progress = rate;
        }

        return ctx.SetReturn(ErrorOk);
    }

    // int sceMsgDialogProgressBarInc(SceMsgDialogProgressBarTarget target, uint32_t delta).
    // The bar saturates at 100.
    [SysAbiExport(
        Nid = "Gc5k1qcK4fs",
        ExportName = "sceMsgDialogProgressBarInc",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceMsgDialog")]
    public static int MsgDialogProgressBarInc(CpuContext ctx)
    {
        var target = unchecked((int)ctx[CpuRegister.Rdi]);
        var delta = unchecked((uint)ctx[CpuRegister.Rsi]);
        lock (Gate)
        {
            var state = CheckProgressStateLocked();
            if (state != ErrorOk)
            {
                return ctx.SetReturn(state);
            }

            if (target != ProgressTargetBarDefault || delta > ProgressMax)
            {
                return ctx.SetReturn(ErrorParamInvalid);
            }

            _progress = Math.Min(ProgressMax, _progress + delta);
        }

        return ctx.SetReturn(ErrorOk);
    }

    // int sceMsgDialogProgressBarSetMsg(SceMsgDialogProgressBarTarget target, const char *msg).
    // There is no visible bar, so the text is only validated.
    [SysAbiExport(
        Nid = "6H-71OdrpXM",
        ExportName = "sceMsgDialogProgressBarSetMsg",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceMsgDialog")]
    public static int MsgDialogProgressBarSetMsg(CpuContext ctx)
    {
        var target = unchecked((int)ctx[CpuRegister.Rdi]);
        var msgAddress = ctx[CpuRegister.Rsi];
        lock (Gate)
        {
            var state = CheckProgressStateLocked();
            if (state != ErrorOk)
            {
                return ctx.SetReturn(state);
            }

            if (msgAddress == 0)
            {
                return ctx.SetReturn(ErrorArgNull);
            }

            if (target != ProgressTargetBarDefault)
            {
                return ctx.SetReturn(ErrorParamInvalid);
            }
        }

        return ctx.SetReturn(ErrorOk);
    }

    // Progress-bar calls need a live dialog. Callers hold Gate.
    private static int CheckProgressStateLocked() => _status switch
    {
        StatusNone => ErrorNotInitialized,
        StatusRunning => ErrorOk,
        _ => ErrorNotRunning,
    };
}
