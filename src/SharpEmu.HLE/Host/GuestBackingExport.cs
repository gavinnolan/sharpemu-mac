// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.HLE.Host;

/// <summary>How a <see cref="GuestBackingExport"/> names the storage behind the guest backing.</summary>
public enum GuestBackingExportKind
{
    /// <summary>One shared-memory object, named by <see cref="GuestBackingExport.Descriptor"/>.</summary>
    SharedMemoryObject,

    /// <summary>
    /// Memory objects laid end to end, each named by a handle in <see cref="GuestBackingExport.Entries"/>
    /// (macOS: send rights to the chunked backing's purgeable named entries).
    /// </summary>
    MemoryEntries,
}

/// <summary>One memory object of a <see cref="GuestBackingExportKind.MemoryEntries"/> export.</summary>
public readonly record struct GuestBackingEntry(ulong Offset, ulong Size, uint Handle);

/// <summary>
/// What another process needs to map the whole guest backing: the two-process split's GPU process
/// maps it as an offset mirror, at an address of its own choosing, and finds guest addresses in it
/// through the shared <see cref="GuestMemory.GuestViewTable"/>.
/// </summary>
/// <remarks>
/// Host-package contract. The descriptor and handles are names in the process that holds this
/// object and are never owned by it: a backing's export borrows them from the backing (released
/// with it), and an export received from another process borrows them from the
/// <see cref="GuestMemoryShare"/> it came in.
/// </remarks>
public sealed class GuestBackingExport
{
    public GuestBackingExport(GuestBackingExportKind kind, ulong size, ulong mappedSize, int descriptor,
        IReadOnlyList<GuestBackingEntry> entries, ulong chunkBytes, ulong objectBytes)
    {
        ArgumentNullException.ThrowIfNull(entries);
        if (size == 0 || mappedSize < size)
            throw new ArgumentOutOfRangeException(nameof(mappedSize));
        if (kind == GuestBackingExportKind.SharedMemoryObject ? descriptor < 0 || entries.Count != 0 : entries.Count == 0)
            throw new ArgumentException("The export does not name its storage the way its kind does.", nameof(entries));
        Kind = kind;
        Size = size;
        MappedSize = mappedSize;
        Descriptor = descriptor;
        Entries = entries;
        ChunkBytes = chunkBytes;
        ObjectBytes = objectBytes;
    }

    public GuestBackingExportKind Kind { get; }

    /// <summary>Bytes of guest backing: backing offsets run from 0 to here.</summary>
    public ulong Size { get; }

    /// <summary>Bytes a mirror maps: the owner's native alias size (the last object may reach past <see cref="Size"/>).</summary>
    public ulong MappedSize { get; }

    /// <summary>The shared-memory object's descriptor; -1 for <see cref="GuestBackingExportKind.MemoryEntries"/>.</summary>
    public int Descriptor { get; }

    /// <summary>The memory objects in offset order, end to end from offset 0; empty for one shared-memory object.</summary>
    public IReadOnlyList<GuestBackingEntry> Entries { get; }

    /// <summary>The owner's <see cref="HostBackingObject.ChunkBytes"/>; 0 for one object.</summary>
    public ulong ChunkBytes { get; }

    /// <summary>The owner's <see cref="HostBackingObject.ObjectBytes"/>; 0 for one object.</summary>
    public ulong ObjectBytes { get; }

    /// <summary>The same storage named by handles that are valid in another process (a received export).</summary>
    public GuestBackingExport WithNames(int descriptor, IReadOnlyList<GuestBackingEntry> entries) =>
        new(Kind, Size, MappedSize, descriptor, entries, ChunkBytes, ObjectBytes);
}

/// <summary>
/// A block of plain shared memory another process can map (the guest view table). The mapping is
/// owned by this object; the descriptor is owned by it too when it created the block, and borrowed
/// when it mapped a descriptor someone else holds.
/// </summary>
/// <remarks>Host-package contract; made by an <see cref="IHostGuestMirrorMemory"/> backend.</remarks>
public sealed class HostSharedRegion : IDisposable
{
    private readonly Action<HostSharedRegion> _release;
    private int _disposed;

    public HostSharedRegion(ulong address, ulong size, int descriptor, bool writable, Action<HostSharedRegion> release)
    {
        ArgumentNullException.ThrowIfNull(release);
        if (!HostViewMemory.IsValidRange(address, size))
            throw new ArgumentOutOfRangeException(nameof(address));
        Address = address;
        Size = size;
        Descriptor = descriptor;
        IsWritable = writable;
        _release = release;
    }

    public ulong Address { get; }

    public ulong Size { get; }

    /// <summary>The descriptor another process maps the block from (sent to it, never named by path).</summary>
    public int Descriptor { get; }

    public bool IsWritable { get; }

    public bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0) _release(this);
    }
}

/// <summary>
/// The guest backing and its view table as one process received them from another: the export's
/// descriptor or handles and the view table's descriptor are owned here until <see cref="Dispose"/>.
/// A mirror or region mapped from them stays valid after they are released.
/// </summary>
public sealed class GuestMemoryShare : IDisposable
{
    private readonly Action<GuestMemoryShare> _release;
    private int _disposed;

    public GuestMemoryShare(GuestBackingExport backing, int viewTableDescriptor, ulong viewTableBytes,
        Action<GuestMemoryShare> release)
    {
        ArgumentNullException.ThrowIfNull(backing);
        ArgumentNullException.ThrowIfNull(release);
        Backing = backing;
        ViewTableDescriptor = viewTableDescriptor;
        ViewTableBytes = viewTableBytes;
        _release = release;
    }

    public GuestBackingExport Backing { get; }

    public int ViewTableDescriptor { get; }

    public ulong ViewTableBytes { get; }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0) _release(this);
    }
}

/// <summary>
/// Optional capability of an <see cref="IHostViewMemory"/> backend whose backings can be shared
/// with another process (macOS: <c>SHARPEMU_GUEST_BACKING_SHAREABLE=1</c> or
/// <c>SHARPEMU_GPU_PROCESS=1</c>, which set <see cref="HostBackingObject.Export"/>).
/// </summary>
public interface IHostGuestMirrorMemory
{
    /// <summary>Creates a zeroed, read-write block of shared memory another process can map.</summary>
    bool TryCreateSharedRegion(ulong size, out HostSharedRegion? region);

    /// <summary>Maps <paramref name="size"/> bytes of a shared block another process created; the descriptor stays the caller's.</summary>
    bool TryMapSharedRegion(int descriptor, ulong size, bool writable, out HostSharedRegion? region);

    /// <summary>
    /// Maps the whole backing <paramref name="export"/> names, read-write, at an address the host picks:
    /// an offset mirror whose byte at <c>AliasBase + offset</c> is backing offset <c>offset</c>, coherent
    /// with the owner's alias and every guest view. The result owns only the mirror mapping: it never
    /// releases backing chunks (<see cref="HostBackingObject.DiscardChunks"/> is null) and has no export.
    /// </summary>
    bool TryMapMirror(GuestBackingExport export, out HostBackingObject? mirror);

    /// <summary>
    /// Reserves exactly [address, address + size), inaccessible, for a replica of guest views
    /// (<see cref="GuestMemory.GuestReplica"/>); false when any of it is taken. Never replaces a mapping.
    /// </summary>
    bool TryReserveReplica(ulong address, ulong size);

    /// <summary>
    /// Maps backing [offset, offset + size) of <paramref name="mirror"/> read-write at
    /// <paramref name="address"/>, inside a replica reservation, replacing what was there: the
    /// same objects, so it shares every byte with the mirror, the owner and its guest views.
    /// </summary>
    bool TryMapReplicaView(HostBackingObject mirror, ulong address, ulong offset, ulong size);

    /// <summary>Puts the inaccessible reservation back over [address, address + size) of a replica.</summary>
    bool TryUnmapReplicaView(ulong address, ulong size);

    /// <summary>Releases a replica reservation and every view in it.</summary>
    bool TryReleaseReplica(ulong address, ulong size);
}
