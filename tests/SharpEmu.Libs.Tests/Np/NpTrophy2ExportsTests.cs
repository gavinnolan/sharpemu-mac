// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Text;
using SharpEmu.HLE;
using SharpEmu.Libs.Np;
using Xunit;

namespace SharpEmu.Libs.Tests.Np;

// Shares the UDS collection: posting a UDS event reaches TrophyService, so both
// groups must not run against its static package concurrently.
[Collection("NpUniversalDataSystem")]
public sealed class NpTrophy2ExportsTests : IDisposable
{
    private const ulong MemoryBase = 0x1_0000_0000;
    private static readonly byte[] IconPng = [0x89, 0x50, 0x4E, 0x47, 1, 2, 3, 4, 5, 6];
    private readonly FakeCpuMemory _memory = new(MemoryBase, 0x1_0000);
    private readonly CpuContext _ctx;
    private readonly int _context;

    public NpTrophy2ExportsTests()
    {
        NpTrophy2Exports.ResetForTests();
        // Group 0: platinum, gold (unlocked), silver, bronze (unlocked), bronze.
        // Group 1: gold, bronze (unlocked). Only trophy 3 has an icon.
        TrophyService.SetPackageForTests(
            [
                Trophy(0, grade: 1, group: 0, unlocked: false),
                Trophy(1, grade: 2, group: 0, unlocked: true),
                Trophy(2, grade: 3, group: 0, unlocked: false),
                Trophy(3, grade: 4, group: 0, unlocked: true, icon: IconPng),
                Trophy(4, grade: 4, group: 0, unlocked: false),
                Trophy(5, grade: 2, group: 1, unlocked: false),
                Trophy(6, grade: 4, group: 1, unlocked: true),
            ],
            "Test Game",
            new Dictionary<int, string> { [0] = "Base Game", [1] = "Expansion" });
        _ctx = new CpuContext(_memory, Generation.Gen5);
        _ctx[CpuRegister.Rdi] = MemoryBase + 0x10;
        Assert.Equal(0, NpTrophy2Exports.NpTrophy2CreateContext(_ctx));
        _context = ReadInt32(MemoryBase + 0x10);
    }

    public void Dispose()
    {
        TrophyService.ResetForTests();
        NpTrophy2Exports.ResetForTests();
    }

    private static TrophyService.Trophy Trophy(int id, int grade, int group, bool unlocked, byte[]? icon = null) =>
        new() { Id = id, Grade = grade, GroupId = group, Unlocked = unlocked, IconPng = icon, Name = $"T{id}" };

    private int ReadInt32(ulong address)
    {
        Assert.True(_ctx.TryReadInt32(address, out var value));
        return value;
    }

    private byte[] Read(ulong address, int length)
    {
        var bytes = new byte[length];
        Assert.True(_memory.TryRead(address, bytes));
        return bytes;
    }

    private static uint U32(byte[] bytes, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset));

    private void Args(ulong rdi = 0, ulong rsi = 0, ulong rdx = 0, ulong rcx = 0, ulong r8 = 0, ulong r9 = 0)
    {
        _ctx[CpuRegister.Rdi] = rdi;
        _ctx[CpuRegister.Rsi] = rsi;
        _ctx[CpuRegister.Rdx] = rdx;
        _ctx[CpuRegister.Rcx] = rcx;
        _ctx[CpuRegister.R8] = r8;
        _ctx[CpuRegister.R9] = r9;
    }

    [Fact]
    public void GetGameInfo_FillsCountsPerGradeProgressAndTitle()
    {
        var details = MemoryBase + 0x100;
        var data = MemoryBase + 0x300;
        Args((ulong)_context, 1, details, data);
        Assert.Equal(0, NpTrophy2Exports.NpTrophy2GetGameInfo(_ctx));

        var d = Read(details, NpTrophy2Exports.GameDetailsSize);
        Assert.Equal(2U, U32(d, 0)); // groups
        Assert.Equal(7U, U32(d, 4)); // trophies
        Assert.Equal(1U, U32(d, 8)); // platinum
        Assert.Equal(2U, U32(d, 12)); // gold
        Assert.Equal(1U, U32(d, 16)); // silver
        Assert.Equal(3U, U32(d, 20)); // bronze
        Assert.Equal("Test Game", Encoding.UTF8.GetString(d.AsSpan(24, 128)).TrimEnd('\0'));

        var g = Read(data, NpTrophy2Exports.GameDataSize);
        Assert.Equal(3U, U32(g, 0)); // unlocked
        Assert.Equal(0U, U32(g, 4));
        Assert.Equal(1U, U32(g, 8));
        Assert.Equal(0U, U32(g, 12));
        Assert.Equal(2U, U32(g, 16));
        Assert.Equal(47U, U32(g, 20)); // (6 + 1 + 1) * 100 / (12 + 2 + 3)
    }

    [Fact]
    public void GetGameInfo_WritesOnlyTheRequestedStructure()
    {
        var data = MemoryBase + 0x300;
        Args((ulong)_context, 1, 0, data);
        Assert.Equal(0, NpTrophy2Exports.NpTrophy2GetGameInfo(_ctx));
        Assert.Equal(3U, U32(Read(data, NpTrophy2Exports.GameDataSize), 0));
    }

    [Fact]
    public void GetGameInfo_ValidatesArgumentsAndContext()
    {
        Args((ulong)_context, 1, 0, 0);
        Assert.Equal(NpTrophy2Exports.ErrorInvalidArgument, NpTrophy2Exports.NpTrophy2GetGameInfo(_ctx));

        Args(99, 1, MemoryBase + 0x100, 0);
        Assert.Equal(NpTrophy2Exports.ErrorInvalidContext, NpTrophy2Exports.NpTrophy2GetGameInfo(_ctx));

        Args((ulong)_context, 1, MemoryBase + 0x100, 0);
        Assert.Equal(0, NpTrophy2Exports.NpTrophy2GetGameInfo(_ctx));
        _ctx[CpuRegister.Rdi] = (ulong)_context;
        Assert.Equal(0, NpTrophy2Exports.NpTrophy2DestroyContext(_ctx));
        Args((ulong)_context, 1, MemoryBase + 0x100, 0);
        Assert.Equal(NpTrophy2Exports.ErrorInvalidContext, NpTrophy2Exports.NpTrophy2GetGameInfo(_ctx));
    }

    [Fact]
    public void GetGameInfo_WithoutTrophiesReportsZeroCounts()
    {
        TrophyService.SetPackageForTests([], string.Empty, []);
        var details = MemoryBase + 0x100;
        var data = MemoryBase + 0x300;
        Args((ulong)_context, 1, details, data);
        Assert.Equal(0, NpTrophy2Exports.NpTrophy2GetGameInfo(_ctx));
        Assert.All(Read(details, NpTrophy2Exports.GameDetailsSize), b => Assert.Equal(0, b));
        Assert.All(Read(data, NpTrophy2Exports.GameDataSize), b => Assert.Equal(0, b));
    }

    [Fact]
    public void GetGroupInfo_FillsGroupCountsAndRejectsUnknownGroups()
    {
        var details = MemoryBase + 0x100;
        var data = MemoryBase + 0x300;
        Args((ulong)_context, 1, 0, details, data);
        Assert.Equal(0, NpTrophy2Exports.NpTrophy2GetGroupInfo(_ctx));
        var d = Read(details, NpTrophy2Exports.GroupDetailsSize);
        Assert.Equal(0, BinaryPrimitives.ReadInt32LittleEndian(d));
        Assert.Equal(5U, U32(d, 4));
        Assert.Equal(1U, U32(d, 8));
        Assert.Equal(1U, U32(d, 12));
        Assert.Equal(1U, U32(d, 16));
        Assert.Equal(2U, U32(d, 20));
        Assert.Equal("Base Game", Encoding.UTF8.GetString(d.AsSpan(24, 128)).TrimEnd('\0'));
        var g = Read(data, NpTrophy2Exports.GroupDataSize);
        Assert.Equal(0, BinaryPrimitives.ReadInt32LittleEndian(g));
        Assert.Equal(2U, U32(g, 4));
        Assert.Equal(1U, U32(g, 12)); // gold
        Assert.Equal(1U, U32(g, 20)); // bronze
        Assert.Equal(70U, U32(g, 24)); // 7 * 100 / 10
        Assert.Equal(0U, U32(g, 28)); // reserved

        Args((ulong)_context, 1, 1, details, data);
        Assert.Equal(0, NpTrophy2Exports.NpTrophy2GetGroupInfo(_ctx));
        Assert.Equal(14U, U32(Read(data, NpTrophy2Exports.GroupDataSize), 24)); // 1 * 100 / 7

        Args((ulong)_context, 1, 7, details, data);
        Assert.Equal(NpTrophy2Exports.ErrorInvalidGroupId, NpTrophy2Exports.NpTrophy2GetGroupInfo(_ctx));
        Args((ulong)_context, 1, 0, 0, 0);
        Assert.Equal(NpTrophy2Exports.ErrorInvalidArgument, NpTrophy2Exports.NpTrophy2GetGroupInfo(_ctx));
    }

    [Fact]
    public void GetGroupInfoArray_PagesGroupsAndWritesTheCountFromTheStack()
    {
        var details = MemoryBase + 0x100;
        var data = MemoryBase + 0x500;
        var count = MemoryBase + 0x700;
        _ctx.SetImportStackArguments(count, 0, 0, 0, 0, 0);
        Args((ulong)_context, 1, 0, 8, details, data);
        Assert.Equal(0, NpTrophy2Exports.NpTrophy2GetGroupInfoArray(_ctx));
        Assert.Equal(2, ReadInt32(count));
        Assert.Equal(0, BinaryPrimitives.ReadInt32LittleEndian(Read(details, 4)));
        Assert.Equal(1, BinaryPrimitives.ReadInt32LittleEndian(Read(details + NpTrophy2Exports.GroupDetailsSize, 4)));
        Assert.Equal(1, BinaryPrimitives.ReadInt32LittleEndian(Read(data + NpTrophy2Exports.GroupDataSize, 4)));
        Assert.Equal(
            "Expansion",
            Encoding.UTF8.GetString(Read(details + NpTrophy2Exports.GroupDetailsSize + 24, 128)).TrimEnd('\0'));

        Args((ulong)_context, 1, 1, 8, details, data);
        Assert.Equal(0, NpTrophy2Exports.NpTrophy2GetGroupInfoArray(_ctx));
        Assert.Equal(1, ReadInt32(count));

        Args((ulong)_context, 1, 5, 8, details, data);
        Assert.Equal(0, NpTrophy2Exports.NpTrophy2GetGroupInfoArray(_ctx));
        Assert.Equal(0, ReadInt32(count));

        _ctx.SetImportStackArguments(0, 0, 0, 0, 0, 0);
        Args((ulong)_context, 1, 0, 8, details, data);
        Assert.Equal(NpTrophy2Exports.ErrorInvalidArgument, NpTrophy2Exports.NpTrophy2GetGroupInfoArray(_ctx));
    }

    [Fact]
    public void GetTrophyIcon_ReportsSizeThenFillsBufferAndRejectsSmallBuffers()
    {
        var buffer = MemoryBase + 0x100;
        var size = MemoryBase + 0x200;

        // Size query: null buffer.
        Assert.True(_ctx.TryWriteUInt64(size, 0xFFFF));
        Args((ulong)_context, 1, 3, 0, size);
        Assert.Equal(0, NpTrophy2Exports.NpTrophy2GetTrophyIcon(_ctx));
        Assert.True(_ctx.TryReadUInt64(size, out var reported));
        Assert.Equal((ulong)IconPng.Length, reported);

        // Capacity below the icon: required size comes back, buffer stays untouched.
        Assert.True(_ctx.TryWriteUInt64(size, 4));
        Args((ulong)_context, 1, 3, buffer, size);
        Assert.Equal(NpTrophy2Exports.ErrorInsufficientBuffer, NpTrophy2Exports.NpTrophy2GetTrophyIcon(_ctx));
        Assert.True(_ctx.TryReadUInt64(size, out reported));
        Assert.Equal((ulong)IconPng.Length, reported);
        Assert.All(Read(buffer, IconPng.Length), b => Assert.Equal(0, b));

        // Exact capacity.
        Assert.True(_ctx.TryWriteUInt64(size, (ulong)IconPng.Length));
        Args((ulong)_context, 1, 3, buffer, size);
        Assert.Equal(0, NpTrophy2Exports.NpTrophy2GetTrophyIcon(_ctx));
        Assert.Equal(IconPng, Read(buffer, IconPng.Length));
        Assert.True(_ctx.TryReadUInt64(size, out reported));
        Assert.Equal((ulong)IconPng.Length, reported);
    }

    [Fact]
    public void IconCalls_ReportMissingIconsAndInvalidIdentifiers()
    {
        var buffer = MemoryBase + 0x100;
        var size = MemoryBase + 0x200;

        // Trophy 4 exists but has no icon in the package.
        Assert.True(_ctx.TryWriteUInt64(size, 99));
        Args((ulong)_context, 1, 4, buffer, size);
        Assert.Equal(NpTrophy2Exports.ErrorIconFileNotFound, NpTrophy2Exports.NpTrophy2GetTrophyIcon(_ctx));
        Assert.True(_ctx.TryReadUInt64(size, out var reported));
        Assert.Equal(0UL, reported);

        // Game, group and reward icons are never present in the package.
        Assert.True(_ctx.TryWriteUInt64(size, 99));
        Args((ulong)_context, 1, buffer, size);
        Assert.Equal(NpTrophy2Exports.ErrorIconFileNotFound, NpTrophy2Exports.NpTrophy2GetGameIcon(_ctx));
        Assert.True(_ctx.TryReadUInt64(size, out reported));
        Assert.Equal(0UL, reported);
        Args((ulong)_context, 1, 0, buffer, size);
        Assert.Equal(NpTrophy2Exports.ErrorIconFileNotFound, NpTrophy2Exports.NpTrophy2GetGroupIcon(_ctx));
        Args((ulong)_context, 1, 3, buffer, size);
        Assert.Equal(NpTrophy2Exports.ErrorIconFileNotFound, NpTrophy2Exports.NpTrophy2GetRewardIcon(_ctx));

        Args((ulong)_context, 1, 42, buffer, size);
        Assert.Equal(NpTrophy2Exports.ErrorInvalidTrophyId, NpTrophy2Exports.NpTrophy2GetTrophyIcon(_ctx));
        Assert.Equal(NpTrophy2Exports.ErrorInvalidTrophyId, NpTrophy2Exports.NpTrophy2GetRewardIcon(_ctx));
        Args((ulong)_context, 1, 42, buffer, size);
        Assert.Equal(NpTrophy2Exports.ErrorInvalidGroupId, NpTrophy2Exports.NpTrophy2GetGroupIcon(_ctx));

        Args((ulong)_context, 1, 3, buffer, 0);
        Assert.Equal(NpTrophy2Exports.ErrorInvalidArgument, NpTrophy2Exports.NpTrophy2GetTrophyIcon(_ctx));
        Args(77, 1, 3, buffer, size);
        Assert.Equal(NpTrophy2Exports.ErrorInvalidContext, NpTrophy2Exports.NpTrophy2GetTrophyIcon(_ctx));
    }

    [Fact]
    public void Exports_AreRegisteredWithTheTrophy2Library()
    {
        var manager = new ModuleManager();
        manager.RegisterExports(SharpEmu.Generated.SysAbiExportRegistry.CreateExports(Generation.Gen5));
        foreach (var (nid, name) in new[]
                 {
                     ("4IzqhhUQ3nk", "sceNpTrophy2GetGameInfo"),
                     ("2QgUy+xJqS0", "sceNpTrophy2GetGameIcon"),
                     ("DoZWauG8mu0", "sceNpTrophy2GetGroupInfo"),
                     ("+PDSI6WgPRc", "sceNpTrophy2GetGroupInfoArray"),
                     ("-9LLVU0uvs8", "sceNpTrophy2GetTrophyIcon"),
                     ("6IjXJUy6ZnA", "sceNpTrophy2GetGroupIcon"),
                     ("BsE-m8JxIOg", "sceNpTrophy2GetRewardIcon"),
                 })
        {
            Assert.True(manager.TryGetExport(nid, out var export), $"NID {nid} did not register.");
            Assert.Equal(name, export.Name);
            Assert.Equal("libSceNpTrophy2", export.LibraryName);
        }
    }
}
