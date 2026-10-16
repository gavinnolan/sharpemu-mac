// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;

namespace SharpEmu.Libs.CommonDialog;

public static class CommonDialogExports
{
    [SysAbiExport(
        Nid = "uoUpLGNkygk",
        ExportName = "sceCommonDialogInitialize",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceCommonDialog")]
    public static int CommonDialogInitialize(CpuContext ctx)
    {
        // A second call returns SCE_COMMON_DIALOG_ERROR_ALREADY_SYSTEM_INITIALIZED (0x80B80002)
        // as anyps5 does; kyty defines no common-dialog error codes.
        return ctx.SetReturn(CommonDialogState.TryInitialize()
            ? (int)OrbisGen2Result.ORBIS_GEN2_OK
            : CommonDialogState.ErrorAlreadySystemInitialized);
    }

    // bool sceCommonDialogIsUsed(void): true while any dialog is RUNNING.
    [SysAbiExport(
        Nid = "BQ3tey0JmQM",
        ExportName = "sceCommonDialogIsUsed",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceCommonDialog")]
    public static int CommonDialogIsUsed(CpuContext ctx)
    {
        ctx[CpuRegister.Rax] = CommonDialogState.IsUsed ? 1UL : 0UL;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }
}
