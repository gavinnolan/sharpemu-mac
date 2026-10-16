// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Libs.CommonDialog;

/// <summary>
/// Process-wide state of libSceCommonDialog, shared by the individual dialog libraries
/// (MsgDialog, ErrorDialog, and SaveDataDialog once it adopts it).
/// </summary>
/// <remarks>
/// <para>
/// <see cref="IsInitialized"/> is set by the first sceCommonDialogInitialize call. Dialog
/// libraries that require it report SCE_COMMON_DIALOG_ERROR_NOT_SYSTEM_INITIALIZED
/// (<see cref="ErrorNotSystemInitialized"/>) from their own Initialize when it is false.
/// </para>
/// <para>
/// <see cref="IsUsed"/> backs sceCommonDialogIsUsed: true while at least one dialog is on
/// screen. A dialog calls <c>MarkUsed(true)</c> exactly once when it enters RUNNING and
/// <c>MarkUsed(false)</c> exactly once when it leaves RUNNING (finished, closed or
/// terminated). The calls are reference counted so two dialogs cannot clear each other.
/// </para>
/// </remarks>
internal static class CommonDialogState
{
    // SCE_COMMON_DIALOG_ERROR_*. ALREADY_SYSTEM_INITIALIZED (anyps5), NOT_INITIALIZED,
    // NOT_FINISHED, BUSY, NOT_RUNNING and ARG_NULL (both references) are taken from the
    // references. NOT_SYSTEM_INITIALIZED, INVALID_STATE and PARAM_INVALID are not defined by
    // either reference and follow the SDK's sequential numbering (inferred).
    // Both references use 0x80B80007 for BUSY; kept as the conservative choice.
    internal const int ErrorNotSystemInitialized = unchecked((int)0x80B80001);
    internal const int ErrorAlreadySystemInitialized = unchecked((int)0x80B80002);
    internal const int ErrorNotInitialized = unchecked((int)0x80B80003);
    internal const int ErrorNotFinished = unchecked((int)0x80B80005);
    internal const int ErrorInvalidState = unchecked((int)0x80B80006);
    internal const int ErrorBusy = unchecked((int)0x80B80007);
    internal const int ErrorParamInvalid = unchecked((int)0x80B8000A);
    internal const int ErrorNotRunning = unchecked((int)0x80B8000B);
    internal const int ErrorArgNull = unchecked((int)0x80B8000D);

    private static readonly object Gate = new();
    private static bool _initialized;
    private static int _usedCount;

    internal static bool IsInitialized
    {
        get
        {
            lock (Gate)
            {
                return _initialized;
            }
        }
    }

    internal static bool IsUsed
    {
        get
        {
            lock (Gate)
            {
                return _usedCount > 0;
            }
        }
    }

    /// <summary>Marks the service initialized. Returns false when it already was.</summary>
    internal static bool TryInitialize()
    {
        lock (Gate)
        {
            if (_initialized)
            {
                return false;
            }

            _initialized = true;
            return true;
        }
    }

    internal static void MarkUsed(bool used)
    {
        lock (Gate)
        {
            if (used)
            {
                _usedCount++;
            }
            else if (_usedCount > 0)
            {
                _usedCount--;
            }
        }
    }

    internal static void ResetForTests()
    {
        lock (Gate)
        {
            _initialized = false;
            _usedCount = 0;
        }
    }
}
