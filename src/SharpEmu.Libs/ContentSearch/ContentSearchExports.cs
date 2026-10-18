// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;

namespace SharpEmu.Libs.ContentSearch;

// The host exports no media to the console's content library, so the library is empty: every
// search finds zero rows and no metadata id is ever handed out. The signatures and the two
// library error codes below are the only parts the references agree on (anyps5 and kyty);
// they throw for the cases whose console error is unknown, which an HLE export cannot do, so
// those answer with generic kernel codes instead (marked below).
public static class ContentSearchExports
{
    private const int ErrorNotInitialized = unchecked((int)0x809D1001);
    private const int ErrorLimitTooBig = unchecked((int)0x809D100A);
    private const uint MaxSearchLimit = 92;

    // The console's invalid-argument and unknown-content codes are not known; the kernel codes
    // are the conservative documented stand-ins.
    private const int ErrorInvalidArgument = (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
    private const int ErrorContentNotFound = (int)OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND;

    private static int _initialized;

    [SysAbiExport(
        Nid = "dPj4ZtRcIWk",
        ExportName = "sceContentSearchInit",
        Target = Generation.Gen5,
        LibraryName = "libSceContentSearch")]
    public static int ContentSearchInit(CpuContext ctx)
    {
        // Repeated initialization is accepted (anyps5 rejects it with an unknown code).
        Volatile.Write(ref _initialized, 1);
        return ctx.SetReturn(0);
    }

    [SysAbiExport(
        Nid = "1xSZodB2geA",
        ExportName = "sceContentSearchTerm",
        Target = Generation.Gen5,
        LibraryName = "libSceContentSearch")]
    public static int ContentSearchTerm(CpuContext ctx) =>
        Interlocked.Exchange(ref _initialized, 0) == 0
            ? ctx.SetReturn(ErrorNotInitialized)
            : ctx.SetReturn(0);

    [SysAbiExport(
        Nid = "-YbpaF0XS-I",
        ExportName = "sceContentSearchCloseMetadata",
        Target = Generation.Gen5,
        LibraryName = "libSceContentSearch")]
    public static int ContentSearchCloseMetadata(CpuContext ctx) =>
        !IsInitialized
            ? ctx.SetReturn(ErrorNotInitialized)
            : ctx.SetReturn(ErrorContentNotFound);

    [SysAbiExport(
        Nid = "EbNufIY0Zvc",
        ExportName = "sceContentSearchGetMetadataFieldInfo",
        Target = Generation.Gen5,
        LibraryName = "libSceContentSearch")]
    public static int ContentSearchGetMetadataFieldInfo(CpuContext ctx)
    {
        if (!IsInitialized)
            return ctx.SetReturn(ErrorNotInitialized);

        // (metadataId, field, int* metadataType, int* size)
        if (ctx[CpuRegister.Rsi] == 0 || ctx[CpuRegister.Rdx] == 0 || ctx[CpuRegister.Rcx] == 0)
            return ctx.SetReturn(ErrorInvalidArgument);

        return ctx.SetReturn(ErrorContentNotFound);
    }

    [SysAbiExport(
        Nid = "ruNe-FgCzO8",
        ExportName = "sceContentSearchGetMetadataValue",
        Target = Generation.Gen5,
        LibraryName = "libSceContentSearch")]
    public static int ContentSearchGetMetadataValue(CpuContext ctx)
    {
        if (!IsInitialized)
            return ctx.SetReturn(ErrorNotInitialized);

        // (metadataId, field, void* value)
        if (ctx[CpuRegister.Rsi] == 0 || ctx[CpuRegister.Rdx] == 0)
            return ctx.SetReturn(ErrorInvalidArgument);

        return ctx.SetReturn(ErrorContentNotFound);
    }

    [SysAbiExport(
        Nid = "G13HF1z-e4o",
        ExportName = "sceContentSearchOpenMetadata",
        Target = Generation.Gen5,
        LibraryName = "libSceContentSearch")]
    public static int ContentSearchOpenMetadata(CpuContext ctx)
    {
        if (!IsInitialized)
            return ctx.SetReturn(ErrorNotInitialized);

        // (const char* filePath, int* metadataId): the library is empty, no id is produced.
        if (ctx[CpuRegister.Rdi] == 0 || ctx[CpuRegister.Rsi] == 0)
            return ctx.SetReturn(ErrorInvalidArgument);

        return ZeroInt32(ctx, ctx[CpuRegister.Rsi], ErrorContentNotFound);
    }

    [SysAbiExport(
        Nid = "bjAlYWwRTJA",
        ExportName = "sceContentSearchOpenMetadataByContentId",
        Target = Generation.Gen5,
        LibraryName = "libSceContentSearch")]
    public static int ContentSearchOpenMetadataByContentId(CpuContext ctx)
    {
        if (!IsInitialized)
            return ctx.SetReturn(ErrorNotInitialized);

        // (int64 contentId, int* metadataId)
        if (ctx[CpuRegister.Rsi] == 0)
            return ctx.SetReturn(ErrorInvalidArgument);

        return ZeroInt32(ctx, ctx[CpuRegister.Rsi], ErrorContentNotFound);
    }

    [SysAbiExport(
        Nid = "PD5bnBTsv6Y",
        ExportName = "sceContentSearchGetContentLastUpdateId",
        Target = Generation.Gen5,
        LibraryName = "libSceContentSearch")]
    public static int ContentSearchGetContentLastUpdateId(CpuContext ctx)
    {
        if (!IsInitialized)
            return ctx.SetReturn(ErrorNotInitialized);

        return ctx[CpuRegister.Rdi] == 0
            ? ctx.SetReturn(ErrorInvalidArgument)
            : ZeroInt64s(ctx, 0, ctx[CpuRegister.Rdi]);
    }

    [SysAbiExport(
        Nid = "o-RBPV0qr8c",
        ExportName = "sceContentSearchGetNumOfContent",
        Target = Generation.Gen5,
        LibraryName = "libSceContentSearch")]
    public static int ContentSearchGetNumOfContent(CpuContext ctx)
    {
        if (!IsInitialized)
            return ctx.SetReturn(ErrorNotInitialized);

        // (columnSet, columnSetLength, int64* numOfContent, int64* lastUpdateId)
        var count = ctx[CpuRegister.Rdx];
        var updateId = ctx[CpuRegister.Rcx];
        return count == 0 || updateId == 0
            ? ctx.SetReturn(ErrorInvalidArgument)
            : ZeroInt64s(ctx, 0, count, updateId);
    }

    [SysAbiExport(
        Nid = "i2ZMZKwZpWs",
        ExportName = "sceContentSearchGetTotalContentSize",
        Target = Generation.Gen5,
        LibraryName = "libSceContentSearch")]
    public static int ContentSearchGetTotalContentSize(CpuContext ctx)
    {
        if (!IsInitialized)
            return ctx.SetReturn(ErrorNotInitialized);

        // (columnSet, columnSetLength, int64* size, int64* lastUpdateId)
        var size = ctx[CpuRegister.Rdx];
        var updateId = ctx[CpuRegister.Rcx];
        return size == 0 || updateId == 0
            ? ctx.SetReturn(ErrorInvalidArgument)
            : ZeroInt64s(ctx, 0, size, updateId);
    }

    [SysAbiExport(
        Nid = "TEW3IKxYfXc",
        ExportName = "sceContentSearchSearchContent",
        Target = Generation.Gen5,
        LibraryName = "libSceContentSearch")]
    public static int ContentSearchSearchContent(CpuContext ctx)
    {
        if (!IsInitialized)
            return ctx.SetReturn(ErrorNotInitialized);

        // (columnSet, columnSetLength, orderBy, orderByLength, offset, limit,
        //  int64* numOfContent, void* infos, int64* lastUpdateId): the last three are on the stack.
        var limit = (uint)ctx[CpuRegister.R9];
        if (!TryGetStackArgument(ctx, 0, out var numOfContent)
            || !TryGetStackArgument(ctx, 1, out var infos)
            || !TryGetStackArgument(ctx, 2, out var lastUpdateId))
        {
            return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        if (numOfContent == 0 || lastUpdateId == 0 || (infos == 0 && limit != 0))
            return ctx.SetReturn(ErrorInvalidArgument);

        if (limit > MaxSearchLimit)
            return ctx.SetReturn(ErrorLimitTooBig);

        return ZeroInt64s(ctx, 0, numOfContent, lastUpdateId);
    }

    private static bool IsInitialized => Volatile.Read(ref _initialized) != 0;

    private static bool TryGetStackArgument(CpuContext ctx, int index, out ulong value) =>
        ctx.TryGetImportStackArgument(index, out value)
        || ctx.TryReadUInt64(ctx[CpuRegister.Rsp] + (ulong)(sizeof(ulong) * (index + 1)), out value);

    private static int ZeroInt32(CpuContext ctx, ulong address, int result)
    {
        Span<byte> zero = stackalloc byte[sizeof(int)];
        zero.Clear();
        return ctx.Memory.TryWrite(address, zero)
            ? ctx.SetReturn(result)
            : ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    private static int ZeroInt64s(CpuContext ctx, int result, params ulong[] addresses)
    {
        Span<byte> zero = stackalloc byte[sizeof(long)];
        zero.Clear();
        foreach (var address in addresses)
        {
            if (!ctx.Memory.TryWrite(address, zero))
                return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        return ctx.SetReturn(result);
    }

    internal static void ResetForTests() => Volatile.Write(ref _initialized, 0);
}
