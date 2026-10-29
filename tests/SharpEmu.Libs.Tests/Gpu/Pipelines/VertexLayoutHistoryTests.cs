// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.CompilerServices;
using SharpEmu.HLE;
using SharpEmu.Libs.Gpu.Pipelines;
using SharpEmu.Libs.Gpu.Rendering;
using SharpEmu.Libs.Tests.Gpu.Scheduling;
using Xunit;

namespace SharpEmu.Libs.Tests.Gpu.Pipelines;

public sealed partial class VertexInputResolverTests
{
    private (RegisteredShader Shader, uint[] UserData) PrepareHistoryFixture()
    {
        var shader = RegisterTables(semanticCount: 2);
        WriteWord(Semantics, SemanticWord(0, 0, 2));
        WriteWord(Semantics + 4, SemanticWord(1, 4, 2));
        WriteWord(AttributeTable, AttributeWord(0, AttributeFormat32x2Float, 0));
        WriteWord(AttributeTable + 4, AttributeWord(0, AttributeFormat32x2Float, 8));
        WriteBuffer(0, StreamBase, 16, 3);
        var userData = new uint[12];
        userData[8] = unchecked((uint)AttributeTable);
        userData[9] = (uint)(AttributeTable >> 32);
        userData[10] = unchecked((uint)BufferTable);
        userData[11] = (uint)(BufferTable >> 32);
        return (shader, userData);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AlternatingLayoutsPreserveSnapshotsAndReuseOnlyWhenEnabled(bool reuse)
    {
        var (shader, userData) = PrepareHistoryFixture();
        var cache = new VertexTableMetadataCache(reuse, collectStatistics: true);
        VertexInputInfo Resolve() => VertexInputResolver.ResolveVertexInputs(_context, shader, userData, metadataCache: cache, codeHash: 1);
        var first = Resolve();
        WriteBuffer(0, StreamBase + 0x100, 16, 3);
        var second = Resolve();
        WriteBuffer(0, StreamBase, 16, 3);
        var returned = Resolve();
        Assert.NotSame(first, returned);
        Assert.NotSame(first.Attributes, second.Attributes);
        Assert.Equal(StreamBase + 0x100, second.Buffers[0].Address);
        Assert.Equal(StreamBase, first.Buffers[0].Address);
        Assert.Equal(first.Attributes, returned.Attributes);
        Assert.Equal(first.Buffers, returned.Buffers);
        if (reuse)
        {
            Assert.Same(first.Attributes, returned.Attributes);
            Assert.Same(first.Buffers, returned.Buffers);
        }
        else Assert.NotSame(first.Attributes, returned.Attributes);
        var repeated = Resolve();
        Assert.Same(returned.Attributes, repeated.Attributes);
        Assert.Equal(reuse ? 1 : 0, cache.Statistics.HistoryHits);
        Assert.Equal(reuse ? 2 : 3, cache.Statistics.Decoded);
        Assert.Equal(1, cache.Statistics.LastHits);
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("1", true)]
    [InlineData("0", false)]
    public void EnvironmentSelectionControlsReturningLayoutReuse(string? value, bool reuse)
    {
        var prior = Environment.GetEnvironmentVariable("SHARPEMU_VERTEX_LAYOUT_HISTORY");
        try
        {
            Environment.SetEnvironmentVariable("SHARPEMU_VERTEX_LAYOUT_HISTORY", value);
            var (shader, userData) = PrepareHistoryFixture();
            var cache = new VertexTableMetadataCache();
            VertexInputInfo Resolve() => VertexInputResolver.ResolveVertexInputs(_context, shader, userData, metadataCache: cache, codeHash: 1);
            var first = Resolve();
            WriteBuffer(0, StreamBase + 256, 16, 3);
            Resolve();
            WriteBuffer(0, StreamBase, 16, 3);
            Assert.Equal(reuse, ReferenceEquals(first.Attributes, Resolve().Attributes));
        }
        finally { Environment.SetEnvironmentVariable("SHARPEMU_VERTEX_LAYOUT_HISTORY", prior); }
    }

    [Fact]
    public void EmptyAndFailedHeadersKeepNonNullDefaultMetadata()
    {
        var shader = RegisterTables(directCount: 0);
        Assert.True(VertexInputResolver.TryReadTables(_context, shader, 32, out var empty, out _));
        Assert.NotNull(empty);
        Assert.Equal(VertexTableMetadata.NoRegister, empty.VertexBufferRegister);
        Assert.False(VertexInputResolver.TryReadTables(_context, shader with { UserDataAddress = 0 }, 32, out var failed, out _));
        Assert.NotNull(failed);
        Assert.Empty(failed.InputSemantics);
    }

    private sealed class ReadGate(ICpuMemory inner) : ICpuMemory
    {
        internal bool DenyTables;
        internal int TableReads;
        public bool TryRead(ulong address, Span<byte> destination)
        {
            var table = address >= AttributeTable && address < BufferTable + 16;
            if (table) TableReads++;
            return !(DenyTables && table) && inner.TryRead(address, destination);
        }
        public bool TryWrite(ulong address, ReadOnlySpan<byte> source) => inner.TryWrite(address, source);
    }

    [Fact]
    public void HistoryHitsRereadGuestTablesAndCannotHideAnUnreadableTable()
    {
        var (shader, userData) = PrepareHistoryFixture();
        var memory = new ReadGate(_memory);
        var context = new CpuContext(memory, Generation.Gen5);
        var cache = new VertexTableMetadataCache(true, collectStatistics: true);
        VertexInputInfo Resolve() => VertexInputResolver.ResolveVertexInputs(context, shader, userData, metadataCache: cache, codeHash: 1);
        var first = Resolve();
        WriteBuffer(0, StreamBase + 0x100, 16, 3);
        Resolve();
        WriteBuffer(0, StreamBase, 16, 3);
        memory.TableReads = 0;
        Assert.Same(first.Attributes, Resolve().Attributes);
        Assert.Equal(2, memory.TableReads);
        memory.DenyTables = true;
        Assert.Throws<SchedulerFatalException>(() => Resolve());
        Assert.Equal(1, cache.Statistics.Refused);
    }

    [Fact]
    public void MovingEqualTablesReusesTheirLayoutButChangesAtTheNewAddressAreObserved()
    {
        var (shader, userData) = PrepareHistoryFixture();
        var cache = new VertexTableMetadataCache(true);
        VertexInputInfo Resolve() => VertexInputResolver.ResolveVertexInputs(_context, shader, userData, metadataCache: cache, codeHash: 1);
        var first = Resolve();
        WriteBuffer(0, StreamBase + 0x100, 16, 3);
        Resolve();
        WriteBuffer(0, StreamBase, 16, 3);
        Span<byte> bytes = stackalloc byte[16];
        Assert.True(_memory.TryRead(AttributeTable, bytes[..8]));
        Assert.True(_memory.TryWrite(MemoryBase + 0x2000, bytes[..8]));
        Assert.True(_memory.TryRead(BufferTable, bytes));
        Assert.True(_memory.TryWrite(MemoryBase + 0x2100, bytes));
        userData[8] = 0x2000;
        userData[10] = 0x2100;
        Assert.Same(first.Attributes, Resolve().Attributes);
        WriteWord(MemoryBase + 0x2108, 7);
        var changed = Resolve();
        Assert.NotSame(first.Attributes, changed.Attributes);
        Assert.Equal(7u, changed.Buffers[0].RecordCount);
        Assert.Equal(3u, first.Buffers[0].RecordCount);
    }

    [Fact]
    public void ANewCodeIdentityCannotRecallAnotherShadersSemanticMapping()
    {
        var (shader, userData) = PrepareHistoryFixture();
        var cache = new VertexTableMetadataCache(true);
        var first = VertexInputResolver.ResolveVertexInputs(_context, shader, userData, metadataCache: cache, codeHash: 1);
        WriteBuffer(0, StreamBase + 0x100, 16, 3);
        VertexInputResolver.ResolveVertexInputs(_context, shader, userData, metadataCache: cache, codeHash: 1);
        WriteBuffer(0, StreamBase, 16, 3);
        WriteWord(Semantics, SemanticWord(0, 12, 2));
        var changed = VertexInputResolver.ResolveVertexInputs(_context, shader, userData, metadataCache: cache, codeHash: 2);
        Assert.NotSame(first.Attributes, changed.Attributes);
        Assert.Equal(12, changed.Attributes[0].RegisterStart);
        Assert.Equal(0, first.Attributes[0].RegisterStart);
    }

    [Fact]
    public void HashCollisionsAndDifferentOwnersCannotSubstituteLayoutBytes()
    {
        var owner = new VertexTableMetadata();
        var history = new VertexLayoutHistory();
        byte[] wanted = [1, 2, 3, 4];
        byte[] buffers = [5, 6, 7, 8];
        var hash = VertexLayoutHistory.Hash(wanted, buffers);
        var wrong = new VertexLayoutMemo([9, 2, 3, 4], buffers, [], []);
        history.Remember(owner, wrong, hash);
        Assert.Null(history.Find(owner, wanted, buffers, out _));
        var right = new VertexLayoutMemo(wanted, buffers, [], []);
        history.Remember(owner, right, hash);
        Assert.Same(right, history.Find(owner, wanted, buffers, out _));
        VertexTableMetadata? other = null;
        for (var attempt = 0; attempt < 100_000; attempt++)
        {
            var candidate = new VertexTableMetadata();
            if ((RuntimeHelpers.GetHashCode(candidate) & (VertexLayoutHistory.Capacity - 1)) !=
                (RuntimeHelpers.GetHashCode(owner) & (VertexLayoutHistory.Capacity - 1))) continue;
            other = candidate;
            break;
        }
        Assert.NotNull(other);
        Assert.Null(history.Find(other, wanted, buffers, out _));
        Assert.Null(history.Find(owner, wanted, [5, 6, 7, 9], out _));
    }

    [Fact]
    public void HistoryBoundsRetentionAndRejectsAnOversizedEntry()
    {
        var history = new VertexLayoutHistory();
        var owner = new VertexTableMetadata();
        for (var index = 0; index < 2000; index++)
        {
            var bytes = new byte[8192];
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(bytes, index);
            history.Remember(owner, new VertexLayoutMemo(bytes, [], [], []), 0);
            Assert.InRange(history.Count, 1, VertexLayoutHistory.Capacity);
            Assert.InRange(history.RetainedBytes, 0, VertexLayoutHistory.ByteBudget);
        }
        Assert.True(history.Evictions > 0);
        var before = (history.Count, history.RetainedBytes);
        history.Remember(owner, new VertexLayoutMemo(new byte[VertexLayoutHistory.ByteBudget], [], [], []), 0);
        Assert.Equal(before, (history.Count, history.RetainedBytes));
        history.Clear();
        Assert.Equal(0, history.Count);
        Assert.Equal(0, history.RetainedBytes);
    }

    [Fact]
    public void LowReuseBacksOffAndLaterRepeatedLayoutsRecover()
    {
        var (shader, userData) = PrepareHistoryFixture();
        var cache = new VertexTableMetadataCache(true, collectStatistics: true);
        VertexInputInfo Resolve() => VertexInputResolver.ResolveVertexInputs(_context, shader, userData, metadataCache: cache, codeHash: 1);
        for (uint index = 0; index < 1100; index++)
        {
            WriteBuffer(0, StreamBase + index * 16, 16, 3);
            Assert.Equal(StreamBase + index * 16, Resolve().Buffers[0].Address);
        }
        Assert.True(cache.Statistics.Suppressed >= 1024);
        var hitsBefore = cache.Statistics.HistoryHits;
        for (uint index = 0; index < 4500; index++)
        {
            var address = StreamBase + (index & 1) * 256;
            WriteBuffer(0, address, 16, 3);
            Assert.Equal(address, Resolve().Buffers[0].Address);
        }
        Assert.True(cache.Statistics.HistoryHits > hitsBefore + 50);
    }

    [Fact]
    public void MetadataLimitClearsTheHistoryAsWellAsHeaderEntries()
    {
        var (shader, userData) = PrepareHistoryFixture();
        var cache = new VertexTableMetadataCache(true, collectStatistics: true);
        VertexInputResolver.ResolveVertexInputs(_context, shader, userData, metadataCache: cache, codeHash: 1);
        WriteBuffer(0, StreamBase + 0x100, 16, 3);
        VertexInputResolver.ResolveVertexInputs(_context, shader, userData, metadataCache: cache, codeHash: 1);
        Assert.True(cache.Statistics.Entries > 0);
        for (ulong hash = 2; hash <= 4097; hash++) cache.Add(shader, hash, new VertexTableMetadata());
        Assert.Equal(0, cache.Statistics.Entries);
        Assert.Equal(0, cache.Statistics.RetainedBytes);
    }
}
