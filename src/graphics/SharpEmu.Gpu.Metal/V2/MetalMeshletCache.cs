// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.IO.Hashing;
using SharpEmu.HLE;

namespace SharpEmu.Gpu.Metal.V2;

/// <summary>
/// Splits an indexed triangle list into meshlets the way the geometry engine packs NGG
/// subgroups: each holds a bounded number of unique vertices and triangles, so a mesh
/// threadgroup runs one without reading the index stream.
///
/// A built buffer is [meshlet count x uint4 {vertexOffset, primOffset, firstTriangle,
/// vertexCount | primCount &lt;&lt; 16}] [vertex list: the raw index values] [prim list:
/// one dword per triangle, local i0 | i1 &lt;&lt; 8 | i2 &lt;&lt; 16]. The offsets are
/// relative to the start of their list; the draw-info block carries the lists' bases.
/// </summary>
public static class MeshletBuilder
{
    /// <summary>Local indices are 8-bit, so a meshlet holds at most this many vertices.</summary>
    public const uint MaxLocalVertices = 255;

    public readonly record struct Result(uint[] Words, uint MeshletCount, uint VertexBase, uint PrimitiveBase);

    public static Result Build(ReadOnlySpan<uint> indices, uint maxVertices, uint maxPrimitives)
    {
        maxVertices = Math.Clamp(maxVertices, 3, MaxLocalVertices);
        maxPrimitives = Math.Clamp(maxPrimitives, 1, MaxLocalVertices);
        var triangles = indices.Length / 3;
        var headers = new List<uint>();
        var vertexList = new List<uint>(indices.Length);
        var primitiveList = new List<uint>(triangles);

        var current = new uint[maxVertices];
        var currentVertices = 0u;
        var currentPrimitives = 0u;
        var meshletVertexStart = 0u;
        var meshletPrimitiveStart = 0u;
        var meshletFirstTriangle = 0u;

        void Flush()
        {
            if (currentPrimitives == 0)
            {
                return;
            }

            headers.Add(meshletVertexStart);
            headers.Add(meshletPrimitiveStart);
            headers.Add(meshletFirstTriangle);
            headers.Add(currentVertices | (currentPrimitives << 16));
            for (var i = 0u; i < currentVertices; i++)
            {
                vertexList.Add(current[i]);
            }

            meshletVertexStart += currentVertices;
            meshletPrimitiveStart += currentPrimitives;
            currentVertices = 0;
            currentPrimitives = 0;
        }

        uint Find(uint value)
        {
            for (var i = 0u; i < currentVertices; i++)
            {
                if (current[i] == value)
                {
                    return i;
                }
            }

            return uint.MaxValue;
        }

        for (var triangle = 0; triangle < triangles; triangle++)
        {
            var a = indices[triangle * 3];
            var b = indices[(triangle * 3) + 1];
            var c = indices[(triangle * 3) + 2];

            // New unique vertices this triangle would add to the current meshlet.
            var added = 0u;
            if (Find(a) == uint.MaxValue)
            {
                added++;
            }

            if (b != a && Find(b) == uint.MaxValue)
            {
                added++;
            }

            if (c != a && c != b && Find(c) == uint.MaxValue)
            {
                added++;
            }

            if (currentPrimitives == maxPrimitives || currentVertices + added > maxVertices)
            {
                Flush();
            }

            if (currentPrimitives == 0)
            {
                meshletFirstTriangle = (uint)triangle;
            }

            var la = Local(a);
            var lb = Local(b);
            var lc = Local(c);
            primitiveList.Add(la | (lb << 8) | (lc << 16));
            currentPrimitives++;

            uint Local(uint value)
            {
                var found = Find(value);
                if (found != uint.MaxValue)
                {
                    return found;
                }

                current[currentVertices] = value;
                return currentVertices++;
            }
        }

        Flush();

        var meshlets = (uint)(headers.Count / 4);
        var vertexBase = meshlets * 4;
        var primitiveBase = vertexBase + (uint)vertexList.Count;
        var words = new uint[primitiveBase + primitiveList.Count];
        headers.CopyTo(words, 0);
        vertexList.CopyTo(words, (int)vertexBase);
        primitiveList.CopyTo(words, (int)primitiveBase);
        return new Result(words, meshlets, vertexBase, primitiveBase);
    }
}

/// <summary>
/// Meshlets of the indexed triangle-list draws a non-GS mesh pipeline runs, built once per
/// index range and validated against the guest bytes on every use: an index buffer the guest
/// rewrites in place hashes differently and is rebuilt. Entries live in shared Metal buffers
/// the residency set names, under a byte budget with least-recently-used eviction of entries
/// no in-flight submission can still read.
/// </summary>
internal sealed class MetalMeshletCache
{
    private const ulong BudgetBytes = 128UL * 1024 * 1024;
    private const int HashChunkBytes = 64 * 1024;

    public enum Fallback
    {
        Disabled,
        NotTriangleList,
        NoGuestAddress,
        GpuWrites,
        Unreadable,
        OverBudget,
    }

    public readonly record struct Meshlets(nint Buffer, uint Count, uint VertexBase, uint PrimitiveBase);

    private readonly record struct Key(
        ulong Address, uint IndexCount, bool Index32, uint MaxVertices, uint MaxPrimitives);

    private sealed class Entry
    {
        public required nint Buffer;
        public required nint Contents;
        public required ulong Bytes;
        public required ulong Hash;
        public required uint Count;
        public required uint VertexBase;
        public required uint PrimitiveBase;
        public ulong LastUsedSerial;
        public ulong LastUsedTick;
    }

    private static readonly bool Disabled =
        Environment.GetEnvironmentVariable("SHARPEMU_METAL_MESHLETS") == "0";

    private readonly MetalGuestMemory _guestMemory;
    private readonly ICpuMemory _memory;
    private readonly Dictionary<Key, Entry> _entries = [];
    private readonly byte[] _chunk = new byte[HashChunkBytes];
    private readonly List<(nint Buffer, ulong Serial)> _retired = [];
    private ulong _bytes;
    private ulong _tick;

    private long _draws;
    private readonly long[] _fallbacks = new long[Enum.GetValues<Fallback>().Length];
    private long _buildTicks;
    private long _hashTicks;
    private long _builds;

    public MetalMeshletCache(MetalGuestMemory guestMemory, ICpuMemory memory)
    {
        _guestMemory = guestMemory;
        _memory = memory;
    }

    public static bool IsDisabled => Disabled;

    public void NoteFallback(Fallback reason) => _fallbacks[(int)reason]++;

    /// <summary>Returns the meshlets of the index range, building them when the range is new
    /// or its bytes changed. The caller has already established that the draw is eligible
    /// (indexed triangle list, a guest-backed index buffer no GPU work is writing).</summary>
    public bool TryGet(
        ulong rangeAddress,
        uint indexCount,
        bool index32,
        uint maxVertices,
        uint maxPrimitives,
        ulong submissionSerial,
        ulong completedSerial,
        out Meshlets meshlets)
    {
        meshlets = default;
        ReleaseRetired(completedSerial);
        var elementBytes = index32 ? 4UL : 2UL;
        var rangeBytes = indexCount * elementBytes;
        var key = new Key(rangeAddress, indexCount, index32, maxVertices, maxPrimitives);

        var hashStarted = System.Diagnostics.Stopwatch.GetTimestamp();
        if (!TryHash(rangeAddress, rangeBytes, out var hash))
        {
            NoteFallback(Fallback.Unreadable);
            return false;
        }

        _hashTicks += System.Diagnostics.Stopwatch.GetTimestamp() - hashStarted;

        if (_entries.TryGetValue(key, out var entry))
        {
            if (entry.Hash == hash)
            {
                Touch(entry, submissionSerial);
                meshlets = new Meshlets(entry.Buffer, entry.Count, entry.VertexBase, entry.PrimitiveBase);
                _draws++;
                return true;
            }

            Remove(key, entry);
        }

        var buildStarted = System.Diagnostics.Stopwatch.GetTimestamp();
        var bytes = new byte[rangeBytes];
        if (!_memory.TryRead(rangeAddress, bytes))
        {
            NoteFallback(Fallback.Unreadable);
            return false;
        }

        var indices = new uint[indexCount];
        if (index32)
        {
            System.Buffer.BlockCopy(bytes, 0, indices, 0, bytes.Length);
        }
        else
        {
            for (var i = 0; i < indices.Length; i++)
            {
                indices[i] = BitConverter.ToUInt16(bytes, i * 2);
            }
        }

        var built = MeshletBuilder.Build(indices, maxVertices, maxPrimitives);
        var size = (ulong)built.Words.Length * sizeof(uint);
        if (!MakeRoom(size, completedSerial))
        {
            ReleaseRetired(completedSerial);
            NoteFallback(Fallback.OverBudget);
            return false;
        }

        var buffer = _guestMemory.CreateMeshletBuffer(size, out var contents);
        unsafe
        {
            built.Words.AsSpan().CopyTo(new Span<uint>((void*)contents, built.Words.Length));
        }

        entry = new Entry
        {
            Buffer = buffer,
            Contents = contents,
            Bytes = size,
            Hash = hash,
            Count = built.MeshletCount,
            VertexBase = built.VertexBase,
            PrimitiveBase = built.PrimitiveBase,
        };
        _entries[key] = entry;
        _bytes += size;
        Touch(entry, submissionSerial);
        _buildTicks += System.Diagnostics.Stopwatch.GetTimestamp() - buildStarted;
        _builds++;
        _draws++;
        meshlets = new Meshlets(buffer, entry.Count, entry.VertexBase, entry.PrimitiveBase);
        return true;
    }

    private void Touch(Entry entry, ulong serial)
    {
        entry.LastUsedSerial = serial;
        entry.LastUsedTick = ++_tick;
    }

    private bool TryHash(ulong address, ulong bytes, out ulong hash)
    {
        var hasher = new XxHash3();
        for (ulong offset = 0; offset < bytes;)
        {
            var count = (int)Math.Min((ulong)_chunk.Length, bytes - offset);
            var span = _chunk.AsSpan(0, count);
            if (!_memory.TryRead(address + offset, span))
            {
                hash = 0;
                return false;
            }

            hasher.Append(span);
            offset += (ulong)count;
        }

        hash = hasher.GetCurrentHashAsUInt64();
        return true;
    }

    // Evicts least-recently-used entries no in-flight submission can still read until the
    // new one fits. False when everything left is still in use.
    private bool MakeRoom(ulong size, ulong completedSerial)
    {
        if (size > BudgetBytes)
        {
            return false;
        }

        while (_bytes + size > BudgetBytes)
        {
            Key? victimKey = null;
            Entry? victim = null;
            foreach (var (key, entry) in _entries)
            {
                if (entry.LastUsedSerial <= completedSerial && (victim is null || entry.LastUsedTick < victim.LastUsedTick))
                {
                    victimKey = key;
                    victim = entry;
                }
            }

            if (victim is null)
            {
                return false;
            }

            Remove(victimKey!.Value, victim);
        }

        return true;
    }

    // The buffer may still be bound by recorded work, so it is released only once the
    // serial it was last used under has completed.
    private void Remove(Key key, Entry entry)
    {
        _entries.Remove(key);
        _bytes -= entry.Bytes;
        _retired.Add((entry.Buffer, entry.LastUsedSerial));
    }

    private void ReleaseRetired(ulong completedSerial)
    {
        for (var i = _retired.Count - 1; i >= 0; i--)
        {
            if (_retired[i].Serial <= completedSerial)
            {
                _guestMemory.ReleaseMeshletBuffer(_retired[i].Buffer);
                _retired.RemoveAt(i);
            }
        }
    }

    /// <summary>The window's counters as the PERF line's text, then reset.</summary>
    public string TakeCounters()
    {
        var culture = System.Globalization.CultureInfo.InvariantCulture;
        var frequency = (double)System.Diagnostics.Stopwatch.Frequency;
        var text =
            $"meshlet_draws={_draws} meshlet_builds={_builds} meshlet_mb={_bytes / (1024 * 1024)} " +
            $"meshlet_build_ms={(_buildTicks * 1000.0 / frequency).ToString("F1", culture)} " +
            $"meshlet_hash_ms={(_hashTicks * 1000.0 / frequency).ToString("F1", culture)} " +
            $"meshlet_fallbacks={string.Join('/', _fallbacks)}";
        _draws = 0;
        _builds = 0;
        _buildTicks = 0;
        _hashTicks = 0;
        Array.Clear(_fallbacks);
        return text;
    }
}
