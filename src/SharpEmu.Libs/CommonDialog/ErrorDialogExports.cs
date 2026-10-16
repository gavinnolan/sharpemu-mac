// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using System.Buffers.Binary;

namespace SharpEmu.Libs.CommonDialog;

/// <summary>
/// libSceErrorDialog. There is no host error dialog, so the dialog is simulated:
/// NONE -> INITIALIZED (Initialize) -> RUNNING (Open) -> FINISHED -> NONE (Terminate).
/// An opened dialog finishes by itself on its second status poll, so a guest that blocks on
/// the dialog status (a common fatal-error path) cannot spin forever.
/// </summary>
public static class ErrorDialogExports
{
    // SCE_ERROR_DIALOG_ERROR_*; both references agree on these values.
    private const int ErrorNotInitialized = unchecked((int)0x80ED0001);
    private const int ErrorAlreadyInitialized = unchecked((int)0x80ED0002);
    private const int ErrorParamInvalid = unchecked((int)0x80ED0003);
    private const int ErrorInvalidState = unchecked((int)0x80ED0005);

    private const int StatusNone = 0;
    private const int StatusInitialized = 1;
    private const int StatusRunning = 2;
    private const int StatusFinished = 3;

    // SceErrorDialogParam { int32 size @0 (== 0x10); int32 errorCode @4; int32 userId @8; int32 reserved @0xC }.
    private const int ParamSize = 0x10;

    /// <summary>Status polls after Open before the dialog finishes.</summary>
    internal const int PollsUntilFinish = 2;

    private static readonly object Gate = new();
    private static int _status;
    private static int _pollsLeft;
    private static bool _usedMarked;

    internal static void ResetForTests()
    {
        lock (Gate)
        {
            ReleaseUsedLocked();
            _status = StatusNone;
            _pollsLeft = 0;
        }
    }

    [SysAbiExport(
        Nid = "I88KChlynSs",
        ExportName = "sceErrorDialogInitialize",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceErrorDialog")]
    public static int ErrorDialogInitialize(CpuContext ctx)
    {
        lock (Gate)
        {
            if (_status != StatusNone)
            {
                return SetReturn(ctx, ErrorAlreadyInitialized);
            }

            _status = StatusInitialized;
        }

        return SetReturn(ctx, 0);
    }

    [SysAbiExport(
        Nid = "M2ZF-ClLhgY",
        ExportName = "sceErrorDialogOpen",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceErrorDialog")]
    public static int ErrorDialogOpen(CpuContext ctx)
    {
        var paramAddress = ctx[CpuRegister.Rdi];
        lock (Gate)
        {
            // Both references report INVALID_STATE for every state but INITIALIZED/FINISHED,
            // including a dialog that was never initialized.
            if (_status is not (StatusInitialized or StatusFinished))
            {
                return SetReturn(ctx, ErrorInvalidState);
            }

            Span<byte> param = stackalloc byte[ParamSize];
            if (paramAddress == 0
                || !ctx.Memory.TryRead(paramAddress, param)
                || BinaryPrimitives.ReadInt32LittleEndian(param) != ParamSize)
            {
                return SetReturn(ctx, ErrorParamInvalid);
            }

            Console.Error.WriteLine(
                $"[LOADER][INFO] sceErrorDialogOpen: errorCode=0x{BinaryPrimitives.ReadUInt32LittleEndian(param[4..]):X8} " +
                $"userId={BinaryPrimitives.ReadInt32LittleEndian(param[8..])}");

            _status = StatusRunning;
            _pollsLeft = PollsUntilFinish;
            if (!_usedMarked)
            {
                _usedMarked = true;
                CommonDialogState.MarkUsed(true);
            }
        }

        return SetReturn(ctx, 0);
    }

    [SysAbiExport(
        Nid = "t2FvHRXzgqk",
        ExportName = "sceErrorDialogGetStatus",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceErrorDialog")]
    public static int ErrorDialogGetStatus(CpuContext ctx) => SetReturn(ctx, PollStatus());

    [SysAbiExport(
        Nid = "WWiGuh9XfgQ",
        ExportName = "sceErrorDialogUpdateStatus",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceErrorDialog")]
    public static int ErrorDialogUpdateStatus(CpuContext ctx) => SetReturn(ctx, PollStatus());

    // Nobody can dismiss the dialog, so the second poll after Open finishes it. Advancing on
    // GetStatus as well as UpdateStatus (kyty advances only on UpdateStatus) avoids a hang in
    // guests that poll with GetStatus alone.
    private static int PollStatus()
    {
        lock (Gate)
        {
            if (_status == StatusRunning && --_pollsLeft <= 0)
            {
                _status = StatusFinished;
                ReleaseUsedLocked();
            }

            return _status;
        }
    }

    [SysAbiExport(
        Nid = "ekXHb1kDBl0",
        ExportName = "sceErrorDialogClose",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceErrorDialog")]
    public static int ErrorDialogClose(CpuContext ctx)
    {
        lock (Gate)
        {
            if (_status != StatusRunning)
            {
                return SetReturn(ctx, ErrorInvalidState);
            }

            _status = StatusFinished;
            ReleaseUsedLocked();
        }

        return SetReturn(ctx, 0);
    }

    [SysAbiExport(
        Nid = "9XAxK2PMwk8",
        ExportName = "sceErrorDialogTerminate",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceErrorDialog")]
    public static int ErrorDialogTerminate(CpuContext ctx)
    {
        lock (Gate)
        {
            if (_status == StatusNone)
            {
                return SetReturn(ctx, ErrorNotInitialized);
            }

            ReleaseUsedLocked();
            _status = StatusNone;
        }

        return SetReturn(ctx, 0);
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

    private static int SetReturn(CpuContext ctx, int result)
    {
        ctx[CpuRegister.Rax] = unchecked((ulong)(long)result);
        return result;
    }
}
