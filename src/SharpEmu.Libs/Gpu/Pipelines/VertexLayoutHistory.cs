// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.IO.Hashing;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace SharpEmu.Libs.Gpu.Pipelines;

// A bounded history of immutable CPU layouts. Guest tables must be reread before
// probing, and hashes only select candidates: ownership and every byte are checked.
// Like the metadata cache, this is owned by ShaderPipelineCache's resolve lock.
internal sealed class VertexLayoutHistory
{
    internal const int Capacity = 512;
    internal const long ByteBudget = 2 * 1024 * 1024;
    private readonly Slot[] _slots = new Slot[Capacity];
    private int _evictionCursor;
    internal int Count { get; private set; }
    internal long RetainedBytes { get; private set; }
    internal long Evictions { get; private set; }

    private struct Slot
    {
        internal VertexTableMetadata? Owner;
        internal VertexLayoutMemo? Layout;
        internal ulong Hash;
    }

    internal static ulong Hash(ReadOnlySpan<byte> attributes, ReadOnlySpan<byte> buffers) =>
        XxHash3.HashToUInt64(attributes) ^ BitOperations.RotateLeft(XxHash3.HashToUInt64(buffers), 17);

    private static int Index(VertexTableMetadata owner, ulong hash) =>
        (int)((hash ^ (uint)RuntimeHelpers.GetHashCode(owner)) & (Capacity - 1));

    internal VertexLayoutMemo? Find(VertexTableMetadata owner, ReadOnlySpan<byte> attributes,
        ReadOnlySpan<byte> buffers, out ulong hash)
    {
        hash = Hash(attributes, buffers);
        ref var slot = ref _slots[Index(owner, hash)];
        var layout = slot.Layout;
        return ReferenceEquals(slot.Owner, owner) && layout is not null && slot.Hash == hash &&
            attributes.SequenceEqual(layout.AttributeBytes) && buffers.SequenceEqual(layout.BufferBytes)
            ? layout : null;
    }

    internal void Remember(VertexTableMetadata owner, VertexLayoutMemo layout, ulong hash)
    {
        var bytes = layout.RetainedBytes;
        if (bytes > ByteBudget) return;
        if (hash == 0) hash = Hash(layout.AttributeBytes, layout.BufferBytes);
        var index = Index(owner, hash);
        ref var slot = ref _slots[index];
        if (ReferenceEquals(slot.Owner, owner) && ReferenceEquals(slot.Layout, layout)) return;
        Evict(index);
        while (RetainedBytes + bytes > ByteBudget)
        {
            Evict(_evictionCursor);
            _evictionCursor = (_evictionCursor + 1) & (Capacity - 1);
        }
        slot.Owner = owner;
        slot.Layout = layout;
        slot.Hash = hash;
        RetainedBytes += bytes;
        Count++;
    }

    private void Evict(int index)
    {
        ref var slot = ref _slots[index];
        if (slot.Layout is not { } layout) return;
        RetainedBytes -= layout.RetainedBytes;
        Count--;
        Evictions++;
        slot = default;
    }

    internal void Clear()
    {
        Array.Clear(_slots);
        Count = 0;
        RetainedBytes = 0;
        _evictionCursor = 0;
    }
}

internal enum VertexLayoutLookup { LastHit, HistoryHit, Decoded, Refused }

internal readonly record struct VertexLayoutStatistics(long Queries, long LastHits, long HistoryHits,
    long Decoded, long Refused, int Entries, long RetainedBytes, long Evictions, long Suppressed);

public sealed partial class VertexTableMetadataCache
{
    private VertexLayoutHistory? _history;
    internal readonly bool ReuseLayouts;
    internal readonly bool CollectStatistics;
    private readonly bool _logStatistics;
    private long _queries, _lastHits, _historyHits, _decoded, _refused, _suppressed;

    // Component-validated default; =0 retains only the existing last-layout memo.
    public VertexTableMetadataCache() : this(
        Environment.GetEnvironmentVariable("SHARPEMU_VERTEX_LAYOUT_HISTORY") != "0",
        Environment.GetEnvironmentVariable("SHARPEMU_PROFILE_VERTEX_LAYOUTS") == "1")
    {
        _logStatistics = CollectStatistics;
        if (_logStatistics)
            Console.Error.WriteLine($"[VERTEX-LAYOUT] enabled={(ReuseLayouts ? 1 : 0)} capacity={VertexLayoutHistory.Capacity} budget={VertexLayoutHistory.ByteBudget}");
    }

    internal VertexTableMetadataCache(bool reuseLayouts, bool collectStatistics = false)
    {
        ReuseLayouts = reuseLayouts;
        CollectStatistics = collectStatistics;
    }

    // A shader with little reuse probes for 32 changed layouts, then skips 4096.
    // Probation resumes automatically so a later stable working set can recover.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal bool ShouldProbeHistory(VertexTableMetadata owner)
    {
        if (owner.HistorySkipsRemaining == 0) return true;
        owner.HistorySkipsRemaining--;
        if (CollectStatistics) _suppressed++;
        return false;
    }

    internal VertexLayoutMemo? FindPrevious(VertexTableMetadata owner, ReadOnlySpan<byte> attributes,
        ReadOnlySpan<byte> buffers, out ulong hash)
    {
        var result = (_history ??= new()).Find(owner, attributes, buffers, out hash);
        owner.HistoryProbes++;
        if (result is not null) owner.HistoryHits++;
        if (owner.HistoryProbes == 32)
        {
            if (owner.HistoryHits < 16) owner.HistorySkipsRemaining = 4096;
            owner.HistoryProbes = owner.HistoryHits = 0;
        }
        return result;
    }

    internal void Remember(VertexTableMetadata owner, VertexLayoutMemo layout, ulong hash) => _history!.Remember(owner, layout, hash);

    internal VertexLayoutStatistics Statistics => new(_queries, _lastHits, _historyHits, _decoded, _refused,
        _history?.Count ?? 0, _history?.RetainedBytes ?? 0, _history?.Evictions ?? 0, _suppressed);

    internal void RecordLookup(VertexLayoutLookup result)
    {
        if (!CollectStatistics) return;
        _queries++;
        switch (result)
        {
            case VertexLayoutLookup.LastHit: _lastHits++; break;
            case VertexLayoutLookup.HistoryHit: _historyHits++; break;
            case VertexLayoutLookup.Decoded: _decoded++; break;
            case VertexLayoutLookup.Refused: _refused++; break;
        }
        if (_logStatistics && (_queries & 4095) == 0)
        {
            var stats = Statistics;
            Console.Error.WriteLine($"[VERTEX-LAYOUT] enabled={(ReuseLayouts ? 1 : 0)} queries={stats.Queries} last_hits={stats.LastHits} history_hits={stats.HistoryHits} decoded={stats.Decoded} refused={stats.Refused} entries={stats.Entries} bytes={stats.RetainedBytes} evictions={stats.Evictions} suppressed={stats.Suppressed}");
        }
    }
}
