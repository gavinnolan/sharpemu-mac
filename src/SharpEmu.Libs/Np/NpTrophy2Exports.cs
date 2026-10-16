// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using System.Buffers.Binary;

namespace SharpEmu.Libs.Np;

public static class NpTrophy2Exports
{
    private static int _nextContext = 1;
    private static int _nextHandle = 1;

    [SysAbiExport(
        Nid = "Bagshr7OQ6Q",
        ExportName = "sceNpTrophy2CreateContext",
        Target = Generation.Gen5,
        LibraryName = "libSceNpTrophy2")]
    public static int NpTrophy2CreateContext(CpuContext ctx)
    {
        var contextId = _nextContext;
        var result = WriteIdAndReturn(ctx, ctx[CpuRegister.Rdi], ref _nextContext);
        if (result == (int)OrbisGen2Result.ORBIS_GEN2_OK)
        {
            lock (ContextGate)
            {
                LiveContexts.Add(contextId);
            }
        }

        return result;
    }

    [SysAbiExport(
        Nid = "Gz1rmUZpROM",
        ExportName = "sceNpTrophy2CreateHandle",
        Target = Generation.Gen5,
        LibraryName = "libSceNpTrophy2")]
    public static int NpTrophy2CreateHandle(CpuContext ctx)
    {
        return WriteIdAndReturn(ctx, ctx[CpuRegister.Rdi], ref _nextHandle);
    }

    [SysAbiExport(
        Nid = "sysY2FHYff4",
        ExportName = "sceNpTrophy2DestroyContext",
        Target = Generation.Gen5,
        LibraryName = "libSceNpTrophy2")]
    public static int NpTrophy2DestroyContext(CpuContext ctx)
    {
        lock (ContextGate)
        {
            LiveContexts.Remove(unchecked((int)ctx[CpuRegister.Rdi]));
        }

        return ReturnOk(ctx);
    }

    [SysAbiExport(
        Nid = "d8P11CI40KE",
        ExportName = "sceNpTrophy2DestroyHandle",
        Target = Generation.Gen5,
        LibraryName = "libSceNpTrophy2")]
    public static int NpTrophy2DestroyHandle(CpuContext ctx) => ReturnOk(ctx);

    [SysAbiExport(
        Nid = "fYapWA9xVmA",
        ExportName = "sceNpTrophy2AbortHandle",
        Target = Generation.Gen5,
        LibraryName = "libSceNpTrophy2")]
    public static int NpTrophy2AbortHandle(CpuContext ctx) => ReturnOk(ctx);

    [SysAbiExport(
        Nid = "bIDov3wBu5Q",
        ExportName = "sceNpTrophy2RegisterContext",
        Target = Generation.Gen5,
        LibraryName = "libSceNpTrophy2")]
    public static int NpTrophy2RegisterContext(CpuContext ctx) => ReturnOk(ctx);

    [SysAbiExport(
        Nid = "sUXGfNMalIo",
        ExportName = "sceNpTrophy2RegisterUnlockCallback",
        Target = Generation.Gen5,
        LibraryName = "libSceNpTrophy2")]
    public static int NpTrophy2RegisterUnlockCallback(CpuContext ctx) => ReturnOk(ctx);

    [SysAbiExport(
        Nid = "wVqxM58sIKs",
        ExportName = "sceNpTrophy2UnregisterUnlockCallback",
        Target = Generation.Gen5,
        LibraryName = "libSceNpTrophy2")]
    public static int NpTrophy2UnregisterUnlockCallback(CpuContext ctx) => ReturnOk(ctx);

    [SysAbiExport(
        Nid = "EHQEDVXZ0TI",
        ExportName = "sceNpTrophy2ShowTrophyList",
        Target = Generation.Gen5,
        LibraryName = "libSceNpTrophy2")]
    public static int NpTrophy2ShowTrophyList(CpuContext ctx) => ReturnOk(ctx);

    /// <summary>
    /// Gen5 ABI: context, handle, trophy id, then SceNpTrophy2Details and
    /// SceNpTrophy2Data output pointers.
    /// </summary>
    /// <remarks>
    /// Reports "no such trophy" rather than succeeding. Succeeding would require
    /// filling both output structures, and their exact layouts are not confirmed
    /// here — a title that trusted zeroed details would read an empty name and a
    /// grade of zero as real data. NOT_FOUND is a documented outcome that callers
    /// must already handle, so it degrades along a path the game tests.
    /// </remarks>
    [SysAbiExport(
        Nid = "EwNylPdWUTM",
        ExportName = "sceNpTrophy2GetTrophyInfo",
        Target = Generation.Gen5,
        LibraryName = "libSceNpTrophy2")]
    public static int NpTrophy2GetTrophyInfo(CpuContext ctx) =>
        SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND);

    [SysAbiExport(
        Nid = "y3zHpdZO6ME",
        ExportName = "sceNpTrophy2GetTrophyInfoArray",
        Target = Generation.Gen5,
        LibraryName = "libSceNpTrophy2")]
    public static int NpTrophy2GetTrophyInfoArray(CpuContext ctx)
    {
        var offset = (uint)ctx[CpuRegister.Rdx];
        var limit = (uint)ctx[CpuRegister.Rcx];
        var detailsAddress = ctx[CpuRegister.R8];
        var dataAddress = ctx[CpuRegister.R9];
        if ((detailsAddress == 0 && dataAddress == 0) ||
            !ctx.TryGetImportStackArgument(0, out var countAddress) || countAddress == 0)
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        var trophies = TrophyService.Trophies;
        var start = (int)Math.Min(offset, (uint)trophies.Length);
        var count = (int)Math.Min(limit, (uint)(trophies.Length - start));
        Span<byte> details = stackalloc byte[TrophyService.DetailsSize];
        Span<byte> data = stackalloc byte[TrophyService.DataSize];
        for (var index = 0; index < count; index++)
        {
            var trophy = trophies[start + index];
            if (detailsAddress != 0)
            {
                trophy.WriteDetails(details);
                if (!ctx.Memory.TryWrite(detailsAddress + (ulong)index * TrophyService.DetailsSize, details))
                {
                    return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
                }
            }

            if (dataAddress != 0)
            {
                trophy.WriteData(data);
                if (!ctx.Memory.TryWrite(dataAddress + (ulong)index * TrophyService.DataSize, data))
                {
                    return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
                }
            }
        }

        Span<byte> countBytes = stackalloc byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(countBytes, (uint)count);
        return ctx.Memory.TryWrite(countAddress, countBytes)
            ? SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_OK)
            : SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }


    // SCE_NP_TROPHY_ERROR_* for the NpTrophy2 library (base 0x80553900). Neither reference
    // lists INSUFFICIENT_BUFFER; 0x80553905 follows the numbering of the neighbouring codes
    // the references do list (INVALID_ARGUMENT 04, INVALID_CONTEXT 09), which match the
    // NpTrophy v1 layout. The rest are listed by KytyPS5.
    internal const int ErrorInvalidArgument = unchecked((int)0x80553904);
    internal const int ErrorInsufficientBuffer = unchecked((int)0x80553905);
    internal const int ErrorInvalidContext = unchecked((int)0x80553909);
    internal const int ErrorInvalidTrophyId = unchecked((int)0x8055390A);
    internal const int ErrorInvalidGroupId = unchecked((int)0x8055390B);
    internal const int ErrorIconFileNotFound = unchecked((int)0x80553911);

    internal const int GameDetailsSize = 152;
    internal const int GameDataSize = 24;
    internal const int GroupDetailsSize = 152;
    internal const int GroupDataSize = 32;
    private const int TitleFieldSize = 128;

    private static readonly object ContextGate = new();
    private static readonly HashSet<int> LiveContexts = [];

    internal static void ResetForTests()
    {
        lock (ContextGate)
        {
            LiveContexts.Clear();
            _nextContext = 1;
            _nextHandle = 1;
        }
    }

    private static bool IsLiveContext(CpuContext ctx)
    {
        lock (ContextGate)
        {
            return LiveContexts.Contains(unchecked((int)ctx[CpuRegister.Rdi]));
        }
    }

    // Trophy grades are stored 1 = platinum, 2 = gold, 3 = silver, 4 = bronze.
    private readonly record struct GradeCounts(
        uint Trophies, uint Platinum, uint Gold, uint Silver, uint Bronze)
    {
        // Progress weighting: platinum is excluded; gold, silver and bronze weigh 6, 2 and 1
        // (the weighting KytyPS5 uses). The reference that returns a constant 0 is not
        // followed, because a title would never see progress move.
        public uint Points => Gold * 6 + Silver * 2 + Bronze;
    }

    private static (GradeCounts Total, GradeCounts Unlocked, uint Percentage) Summarize(
        IEnumerable<TrophyService.Trophy> trophies)
    {
        var total = new uint[5];
        var unlocked = new uint[5];
        uint totalCount = 0;
        uint unlockedCount = 0;
        foreach (var trophy in trophies)
        {
            totalCount++;
            if (trophy.Grade is >= 1 and <= 4)
            {
                total[trophy.Grade]++;
            }

            if (trophy.Unlocked)
            {
                unlockedCount++;
                if (trophy.Grade is >= 1 and <= 4)
                {
                    unlocked[trophy.Grade]++;
                }
            }
        }

        var totalCounts = new GradeCounts(totalCount, total[1], total[2], total[3], total[4]);
        var unlockedCounts = new GradeCounts(unlockedCount, unlocked[1], unlocked[2], unlocked[3], unlocked[4]);
        var points = totalCounts.Points;
        var percentage = points == 0 ? 0u : (uint)((ulong)unlockedCounts.Points * 100 / points);
        return (totalCounts, unlockedCounts, percentage);
    }

    private static void WriteCounts(Span<byte> destination, GradeCounts counts)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(destination, counts.Trophies);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[4..], counts.Platinum);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[8..], counts.Gold);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[12..], counts.Silver);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[16..], counts.Bronze);
    }

    private static void WriteTitle(Span<byte> field, string title)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(title);
        var length = Math.Min(bytes.Length, field.Length - 1);
        while (length > 0 && length < bytes.Length && (bytes[length] & 0xC0) == 0x80)
        {
            length--;
        }

        bytes.AsSpan(0, length).CopyTo(field);
    }

    private static IReadOnlyList<int> GroupIds(TrophyService.Trophy[] trophies) =>
        trophies.Select(t => t.GroupId).Distinct().OrderBy(id => id).ToArray();

    /// <summary>
    /// SceNpTrophy2GameDetails (152 bytes): group, trophy, platinum, gold, silver and bronze
    /// counts as uint32, then a 128-byte title. SceNpTrophy2GameData (24 bytes): unlocked
    /// total and per grade, then the progress percentage.
    /// </summary>
    internal static void WriteGameInfo(
        TrophyService.Trophy[] trophies,
        string title,
        Span<byte> details,
        Span<byte> data)
    {
        var (total, unlocked, percentage) = Summarize(trophies);
        details.Clear();
        BinaryPrimitives.WriteUInt32LittleEndian(details, (uint)GroupIds(trophies).Count);
        BinaryPrimitives.WriteUInt32LittleEndian(details[4..], total.Trophies);
        BinaryPrimitives.WriteUInt32LittleEndian(details[8..], total.Platinum);
        BinaryPrimitives.WriteUInt32LittleEndian(details[12..], total.Gold);
        BinaryPrimitives.WriteUInt32LittleEndian(details[16..], total.Silver);
        BinaryPrimitives.WriteUInt32LittleEndian(details[20..], total.Bronze);
        WriteTitle(details.Slice(24, TitleFieldSize), title);

        data.Clear();
        BinaryPrimitives.WriteUInt32LittleEndian(data, unlocked.Trophies);
        BinaryPrimitives.WriteUInt32LittleEndian(data[4..], unlocked.Platinum);
        BinaryPrimitives.WriteUInt32LittleEndian(data[8..], unlocked.Gold);
        BinaryPrimitives.WriteUInt32LittleEndian(data[12..], unlocked.Silver);
        BinaryPrimitives.WriteUInt32LittleEndian(data[16..], unlocked.Bronze);
        BinaryPrimitives.WriteUInt32LittleEndian(data[20..], percentage);
    }

    /// <summary>
    /// SceNpTrophy2GroupDetails (152 bytes): group id as int32, trophy, platinum, gold, silver
    /// and bronze counts, then a 128-byte title. SceNpTrophy2GroupData (32 bytes): group id,
    /// unlocked total and per grade, progress percentage, 4 reserved bytes.
    /// </summary>
    internal static void WriteGroupInfo(
        TrophyService.Trophy[] trophies,
        int groupId,
        string title,
        Span<byte> details,
        Span<byte> data)
    {
        var (total, unlocked, percentage) = Summarize(trophies.Where(t => t.GroupId == groupId));
        details.Clear();
        BinaryPrimitives.WriteInt32LittleEndian(details, groupId);
        WriteCounts(details[4..], total);
        WriteTitle(details.Slice(24, TitleFieldSize), title);

        data.Clear();
        BinaryPrimitives.WriteInt32LittleEndian(data, groupId);
        WriteCounts(data[4..], unlocked);
        BinaryPrimitives.WriteUInt32LittleEndian(data[24..], percentage);
    }

    private static string GroupTitle(int groupId) =>
        TrophyService.GroupNames.TryGetValue(groupId, out var name) ? name : string.Empty;

    /// <summary>Gen5 ABI: context, handle, then SceNpTrophy2GameDetails and GameData out pointers.</summary>
    [SysAbiExport(
        Nid = "4IzqhhUQ3nk",
        ExportName = "sceNpTrophy2GetGameInfo",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpTrophy2")]
    public static int NpTrophy2GetGameInfo(CpuContext ctx)
    {
        var detailsAddress = ctx[CpuRegister.Rdx];
        var dataAddress = ctx[CpuRegister.Rcx];
        if (detailsAddress == 0 && dataAddress == 0)
        {
            return SetCode(ctx, ErrorInvalidArgument);
        }

        if (!IsLiveContext(ctx))
        {
            return SetCode(ctx, ErrorInvalidContext);
        }

        Span<byte> details = stackalloc byte[GameDetailsSize];
        Span<byte> data = stackalloc byte[GameDataSize];
        WriteGameInfo(TrophyService.Trophies, TrophyService.GameTitle, details, data);
        if ((detailsAddress != 0 && !ctx.Memory.TryWrite(detailsAddress, details)) ||
            (dataAddress != 0 && !ctx.Memory.TryWrite(dataAddress, data)))
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        return ReturnOk(ctx);
    }

    /// <summary>Gen5 ABI: context, handle, group id, then GroupDetails and GroupData out pointers.</summary>
    [SysAbiExport(
        Nid = "DoZWauG8mu0",
        ExportName = "sceNpTrophy2GetGroupInfo",
        Target = Generation.Gen5,
        LibraryName = "libSceNpTrophy2")]
    public static int NpTrophy2GetGroupInfo(CpuContext ctx)
    {
        var groupId = unchecked((int)ctx[CpuRegister.Rdx]);
        var detailsAddress = ctx[CpuRegister.Rcx];
        var dataAddress = ctx[CpuRegister.R8];
        if (detailsAddress == 0 && dataAddress == 0)
        {
            return SetCode(ctx, ErrorInvalidArgument);
        }

        if (!IsLiveContext(ctx))
        {
            return SetCode(ctx, ErrorInvalidContext);
        }

        var trophies = TrophyService.Trophies;
        if (!trophies.Any(t => t.GroupId == groupId))
        {
            return SetCode(ctx, ErrorInvalidGroupId);
        }

        Span<byte> details = stackalloc byte[GroupDetailsSize];
        Span<byte> data = stackalloc byte[GroupDataSize];
        WriteGroupInfo(trophies, groupId, GroupTitle(groupId), details, data);
        if ((detailsAddress != 0 && !ctx.Memory.TryWrite(detailsAddress, details)) ||
            (dataAddress != 0 && !ctx.Memory.TryWrite(dataAddress, data)))
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        return ReturnOk(ctx);
    }

    /// <summary>
    /// Gen5 ABI: context, handle, offset, limit, details array, data array, then the count
    /// output on the stack. Groups are ordered by id.
    /// </summary>
    [SysAbiExport(
        Nid = "+PDSI6WgPRc",
        ExportName = "sceNpTrophy2GetGroupInfoArray",
        Target = Generation.Gen5,
        LibraryName = "libSceNpTrophy2")]
    public static int NpTrophy2GetGroupInfoArray(CpuContext ctx)
    {
        var offset = (uint)ctx[CpuRegister.Rdx];
        var limit = (uint)ctx[CpuRegister.Rcx];
        var detailsAddress = ctx[CpuRegister.R8];
        var dataAddress = ctx[CpuRegister.R9];
        if ((detailsAddress == 0 && dataAddress == 0) ||
            !ctx.TryGetImportStackArgument(0, out var countAddress) || countAddress == 0)
        {
            return SetCode(ctx, ErrorInvalidArgument);
        }

        if (!IsLiveContext(ctx))
        {
            return SetCode(ctx, ErrorInvalidContext);
        }

        var trophies = TrophyService.Trophies;
        var groups = GroupIds(trophies);
        var start = (int)Math.Min(offset, (uint)groups.Count);
        var count = (int)Math.Min(limit, (uint)(groups.Count - start));
        Span<byte> details = stackalloc byte[GroupDetailsSize];
        Span<byte> data = stackalloc byte[GroupDataSize];
        for (var index = 0; index < count; index++)
        {
            var groupId = groups[start + index];
            WriteGroupInfo(trophies, groupId, GroupTitle(groupId), details, data);
            if ((detailsAddress != 0 &&
                 !ctx.Memory.TryWrite(detailsAddress + (ulong)index * GroupDetailsSize, details)) ||
                (dataAddress != 0 &&
                 !ctx.Memory.TryWrite(dataAddress + (ulong)index * GroupDataSize, data)))
            {
                return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
            }
        }

        return ctx.TryWriteUInt32(countAddress, (uint)count)
            ? ReturnOk(ctx)
            : SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    [SysAbiExport(
        Nid = "2QgUy+xJqS0",
        ExportName = "sceNpTrophy2GetGameIcon",
        Target = Generation.Gen5,
        LibraryName = "libSceNpTrophy2")]
    public static int NpTrophy2GetGameIcon(CpuContext ctx) =>
        GetIcon(ctx, TrophyIconKind.Game, bufferRegister: CpuRegister.Rdx, sizeRegister: CpuRegister.Rcx);

    [SysAbiExport(
        Nid = "6IjXJUy6ZnA",
        ExportName = "sceNpTrophy2GetGroupIcon",
        Target = Generation.Gen5,
        LibraryName = "libSceNpTrophy2")]
    public static int NpTrophy2GetGroupIcon(CpuContext ctx) =>
        GetIcon(ctx, TrophyIconKind.Group, bufferRegister: CpuRegister.Rcx, sizeRegister: CpuRegister.R8);

    [SysAbiExport(
        Nid = "-9LLVU0uvs8",
        ExportName = "sceNpTrophy2GetTrophyIcon",
        Target = Generation.Gen5,
        LibraryName = "libSceNpTrophy2")]
    public static int NpTrophy2GetTrophyIcon(CpuContext ctx) =>
        GetIcon(ctx, TrophyIconKind.Trophy, bufferRegister: CpuRegister.Rcx, sizeRegister: CpuRegister.R8);

    [SysAbiExport(
        Nid = "BsE-m8JxIOg",
        ExportName = "sceNpTrophy2GetRewardIcon",
        Target = Generation.Gen5,
        LibraryName = "libSceNpTrophy2")]
    public static int NpTrophy2GetRewardIcon(CpuContext ctx) =>
        GetIcon(ctx, TrophyIconKind.Reward, bufferRegister: CpuRegister.Rcx, sizeRegister: CpuRegister.R8);

    private enum TrophyIconKind
    {
        Game,
        Group,
        Trophy,
        Reward,
    }

    /// <summary>
    /// Icon calls: (context, handle, [group or trophy id,] buffer, size*). A null buffer is a
    /// size query: the icon size is written to *size. Otherwise *size is the capacity on
    /// input; a capacity below the icon size writes the required size back and fails with
    /// INSUFFICIENT_BUFFER, leaving the buffer untouched. The package carries trophy icons
    /// only (trop%04d.png); game, group and reward icons are reported as ICON_FILE_NOT_FOUND
    /// with *size set to 0, the same answer both references give for every icon.
    /// </summary>
    private static int GetIcon(CpuContext ctx, TrophyIconKind kind, CpuRegister bufferRegister, CpuRegister sizeRegister)
    {
        var bufferAddress = ctx[bufferRegister];
        var sizeAddress = ctx[sizeRegister];
        if (sizeAddress == 0)
        {
            return SetCode(ctx, ErrorInvalidArgument);
        }

        if (!IsLiveContext(ctx))
        {
            return SetCode(ctx, ErrorInvalidContext);
        }

        var id = unchecked((int)ctx[CpuRegister.Rdx]);
        var trophies = TrophyService.Trophies;
        if (kind == TrophyIconKind.Group && !trophies.Any(t => t.GroupId == id))
        {
            return SetCode(ctx, ErrorInvalidGroupId);
        }

        if (kind is TrophyIconKind.Trophy or TrophyIconKind.Reward && !trophies.Any(t => t.Id == id))
        {
            return SetCode(ctx, ErrorInvalidTrophyId);
        }

        byte[] icon = [];
        var found = kind == TrophyIconKind.Trophy && TrophyService.TryReadTrophyIcon(id, out icon);
        if (!found)
        {
            return ctx.TryWriteUInt64(sizeAddress, 0)
                ? SetCode(ctx, ErrorIconFileNotFound)
                : SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        if (bufferAddress == 0)
        {
            return ctx.TryWriteUInt64(sizeAddress, (ulong)icon.Length)
                ? ReturnOk(ctx)
                : SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        if (!ctx.TryReadUInt64(sizeAddress, out var capacity))
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        if (capacity < (ulong)icon.Length)
        {
            return ctx.TryWriteUInt64(sizeAddress, (ulong)icon.Length)
                ? SetCode(ctx, ErrorInsufficientBuffer)
                : SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        return ctx.Memory.TryWrite(bufferAddress, icon) && ctx.TryWriteUInt64(sizeAddress, (ulong)icon.Length)
            ? ReturnOk(ctx)
            : SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    private static int SetCode(CpuContext ctx, int code)
    {
        ctx[CpuRegister.Rax] = unchecked((ulong)code);
        return code;
    }

    private static int WriteIdAndReturn(CpuContext ctx, ulong outAddress, ref int nextId)
    {
        if (outAddress == 0)
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        Span<byte> idBytes = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(idBytes, nextId);
        if (!ctx.Memory.TryWrite(outAddress, idBytes))
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        nextId++;
        return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_OK);
    }

    private static int ReturnOk(CpuContext ctx) => SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_OK);

    private static int SetReturn(CpuContext ctx, OrbisGen2Result result)
    {
        ctx[CpuRegister.Rax] = unchecked((ulong)(int)result);
        return (int)result;
    }
}
