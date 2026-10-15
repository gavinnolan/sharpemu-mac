// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections.Generic;

namespace SharpEmu.ShaderCompiler.Resources;

/// <summary>
/// The data-only scalar loads, per shader, that the planner leaves to the device instead of
/// flattening into the host-evaluated table.
/// </summary>
/// <remarks>
/// A flattened data word is read by the host when the draw is bound. When the GPU is still
/// writing it — a title that copies its constant table on the GPU every frame — the host has
/// to wait for that submission, and the value is only ever a shader operand: the device can
/// read it itself, after the writer, through the page table. A host learns such a load at
/// bind (<see cref="Learn"/>); the next bind of that shader re-plans it with the load left
/// unflattened. Binding reads are never learned, so descriptors stay host-evaluated.
/// Nothing is persisted.
/// </remarks>
public static class DeviceDataLoads
{
    private static readonly object Gate = new();
    private static readonly Dictionary<ulong, HashSet<int>> MemoryByHash = new();
    private static int _generation;
    private static long _slots;
    private static long _replans;

    // Changes whenever any shader learns a load, so a cached program can tell cheaply that
    // nothing it depends on moved.
    public static int Generation => Volatile.Read(ref _generation);

    public static long LearnedSlots => Interlocked.Read(ref _slots);

    public static long Replans => Interlocked.Read(ref _replans);

    public static void CountReplan() => Interlocked.Increment(ref _replans);

    // The memory records of one shader the planner must leave to the device; null when none.
    public static IReadOnlySet<int>? For(ulong hash)
    {
        lock (Gate)
        {
            return MemoryByHash.TryGetValue(hash, out var set) ? new HashSet<int>(set) : null;
        }
    }

    public static int CountFor(ulong hash)
    {
        lock (Gate)
        {
            return MemoryByHash.TryGetValue(hash, out var set) ? set.Count : 0;
        }
    }

    public static bool Contains(ulong hash, int memoryIndex)
    {
        lock (Gate)
        {
            return MemoryByHash.TryGetValue(hash, out var set) && set.Contains(memoryIndex);
        }
    }

    // Records one flattened slot's memory records; true when any of them is new.
    public static bool Learn(ulong hash, IReadOnlyList<int> memoryIndices)
    {
        lock (Gate)
        {
            if (!MemoryByHash.TryGetValue(hash, out var set))
            {
                MemoryByHash[hash] = set = [];
            }

            var added = false;
            foreach (var index in memoryIndices)
            {
                added |= set.Add(index);
            }

            if (!added)
            {
                return false;
            }

            _slots++;
            _generation++;
            return true;
        }
    }
}

/// <summary>The shader whose resources the current thread is materializing, for wait reports.</summary>
public static class MaterializingShader
{
    [ThreadStatic]
    private static ulong _hash;

    public static ulong Hash
    {
        get => _hash;
        set => _hash = value;
    }
}
