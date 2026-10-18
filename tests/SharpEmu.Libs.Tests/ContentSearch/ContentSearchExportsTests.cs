// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.ContentSearch;
using Xunit;

namespace SharpEmu.Libs.Tests.ContentSearch;

public sealed class ContentSearchExportsTests
{
    private const ulong Base = 0x1_0000_0000;
    private const int NotInitialized = unchecked((int)0x809D1001);
    private const int LimitTooBig = unchecked((int)0x809D100A);

    private readonly FakeCpuMemory _memory = new(Base, 0x10000);
    private readonly CpuContext _ctx;

    public ContentSearchExportsTests()
    {
        ContentSearchExports.ResetForTests();
        _ctx = new CpuContext(_memory, Generation.Gen5);
    }

    private void Poison(ulong address) =>
        Assert.True(_memory.TryWrite(address, Enumerable.Repeat((byte)0xEE, 8).ToArray()));

    private ulong ReadU64(ulong address)
    {
        _ctx.TryReadUInt64(address, out var value);
        return value;
    }

    [Fact]
    public void CallsBeforeInitReportNotInitialized_AndTermNeedsInit()
    {
        Assert.Equal(NotInitialized, ContentSearchExports.ContentSearchGetContentLastUpdateId(_ctx));
        Assert.Equal(NotInitialized, ContentSearchExports.ContentSearchGetNumOfContent(_ctx));
        Assert.Equal(NotInitialized, ContentSearchExports.ContentSearchSearchContent(_ctx));
        Assert.Equal(NotInitialized, ContentSearchExports.ContentSearchOpenMetadata(_ctx));
        Assert.Equal(NotInitialized, ContentSearchExports.ContentSearchTerm(_ctx));
    }

    [Fact]
    public void EmptyLibraryReportsZeroedOutputs()
    {
        Assert.Equal(0, ContentSearchExports.ContentSearchInit(_ctx));

        Poison(Base);
        Poison(Base + 0x10);
        _ctx[CpuRegister.Rdx] = Base;
        _ctx[CpuRegister.Rcx] = Base + 0x10;
        Assert.Equal(0, ContentSearchExports.ContentSearchGetNumOfContent(_ctx));
        Assert.Equal(0UL, ReadU64(Base));
        Assert.Equal(0UL, ReadU64(Base + 0x10));

        Poison(Base);
        _ctx[CpuRegister.Rdi] = Base;
        Assert.Equal(0, ContentSearchExports.ContentSearchGetContentLastUpdateId(_ctx));
        Assert.Equal(0UL, ReadU64(Base));

        Assert.Equal(0, ContentSearchExports.ContentSearchTerm(_ctx));
        Assert.Equal(NotInitialized, ContentSearchExports.ContentSearchTerm(_ctx));
    }

    [Fact]
    public void OpenMetadataFindsNothingAndZeroesTheId()
    {
        ContentSearchExports.ContentSearchInit(_ctx);
        Poison(Base);
        _ctx[CpuRegister.Rdi] = Base + 0x100;
        _ctx[CpuRegister.Rsi] = Base;
        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND, ContentSearchExports.ContentSearchOpenMetadata(_ctx));
        Assert.Equal(0u, (uint)ReadU64(Base));

        _ctx[CpuRegister.Rsi] = 0;
        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT, ContentSearchExports.ContentSearchOpenMetadata(_ctx));
    }

    [Fact]
    public void SearchContent_ValidatesLimitAndReadsStackOutputs()
    {
        ContentSearchExports.ContentSearchInit(_ctx);
        var stack = Base + 0x1000;
        Assert.True(_ctx.TryWriteUInt64(stack + 8, Base + 0x20));   // numOfContent
        Assert.True(_ctx.TryWriteUInt64(stack + 16, Base + 0x300)); // infos
        Assert.True(_ctx.TryWriteUInt64(stack + 24, Base + 0x30));  // lastUpdateId
        _ctx[CpuRegister.Rsp] = stack;
        Poison(Base + 0x20);
        Poison(Base + 0x30);

        _ctx[CpuRegister.R9] = 93;
        Assert.Equal(LimitTooBig, ContentSearchExports.ContentSearchSearchContent(_ctx));

        _ctx[CpuRegister.R9] = 92;
        Assert.Equal(0, ContentSearchExports.ContentSearchSearchContent(_ctx));
        Assert.Equal(0UL, ReadU64(Base + 0x20));
        Assert.Equal(0UL, ReadU64(Base + 0x30));
    }
}
