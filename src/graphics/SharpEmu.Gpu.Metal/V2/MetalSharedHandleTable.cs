// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Gpu.Metal.V2;

/// <summary>
/// Native objects (libraries, render pipeline states) that several owners use, found by what
/// they were made from. The table owns the one native reference the creator got and counts
/// the owners on top of it: the object is released when the last owner lets go, so an owner
/// that is evicted never releases what another still draws with. Managed bookkeeping only;
/// the caller does the native release when <see cref="Release"/> says so. Any thread.
/// </summary>
internal sealed class MetalSharedHandleTable<TKey> where TKey : notnull
{
    private sealed class Record(TKey key, nint handle, UInt128 aux)
    {
        public readonly TKey Key = key;
        public readonly nint Handle = handle;
        public readonly UInt128 Aux = aux;
        public int Owners = 1;
    }

    private readonly object _gate = new();
    private readonly Dictionary<TKey, Record> _byKey = new();
    private readonly Dictionary<nint, Record> _byHandle = new();

    /// <summary>The objects alive in the table.</summary>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _byKey.Count;
            }
        }
    }

    /// <summary>The owners of <paramref name="handle"/>, zero when it is not shared.</summary>
    public int OwnersOf(nint handle)
    {
        lock (_gate)
        {
            return _byHandle.TryGetValue(handle, out var record) ? record.Owners : 0;
        }
    }

    /// <summary>Takes one more ownership of the object made from <paramref name="key"/>, if there is one.</summary>
    public bool TryAcquire(TKey key, out nint handle, out UInt128 aux)
    {
        lock (_gate)
        {
            if (_byKey.TryGetValue(key, out var record))
            {
                record.Owners++;
                handle = record.Handle;
                aux = record.Aux;
                return true;
            }
        }

        handle = 0;
        aux = default;
        return false;
    }

    /// <summary>
    /// Hands a new object, with the one native reference its creator holds, to the table
    /// with that creator as its first owner. False when another creator got there first:
    /// <paramref name="shared"/> is then the table's object, this caller owns it too, and the
    /// object it made has to be released by the caller.
    /// </summary>
    public bool Register(TKey key, nint handle, UInt128 aux, out nint shared)
    {
        lock (_gate)
        {
            if (_byKey.TryGetValue(key, out var existing))
            {
                existing.Owners++;
                shared = existing.Handle;
                return false;
            }

            var record = new Record(key, handle, aux);
            _byKey.Add(key, record);
            _byHandle.Add(handle, record);
            shared = handle;
            return true;
        }
    }

    /// <summary>
    /// One owner lets go of <paramref name="handle"/>. True when the native object is to be
    /// released now: it was the last owner, or the object was never shared.
    /// </summary>
    public bool Release(nint handle)
    {
        lock (_gate)
        {
            if (!_byHandle.TryGetValue(handle, out var record))
            {
                return true;
            }

            if (--record.Owners > 0)
            {
                return false;
            }

            _byHandle.Remove(handle);
            _byKey.Remove(record.Key);
            return true;
        }
    }
}
