// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE.Host;

namespace SharpEmu.HLE.GuestMemory;

/// <summary>Why a released chunk is wanted back.</summary>
public enum GuestSingleCopyAccess : byte
{
    /// <summary>The host reads the bytes (a syscall, the command processor, a staged upload).</summary>
    HostRead,

    /// <summary>The host writes part of a chunk; the rest must stay what it was.</summary>
    HostWrite,

    /// <summary>A guest address is about to map the chunk.</summary>
    Map,

    /// <summary>A guest thread touched the chunk through its mapping.</summary>
    Fault,

    /// <summary>The GPU is about to read or write the bytes as a buffer.</summary>
    GpuBinding,

    /// <summary>The texture that holds the bytes is going away.</summary>
    Teardown,

    /// <summary>
    /// A device-address program may read the bytes through the page table: a range the host
    /// evaluated for it covers the chunk, or the shader's trap saw it read the chunk.
    /// </summary>
    DeviceAddress,
}

/// <summary>
/// The owner of released chunks (the Metal backend's single-copy manager). Every call can
/// come from any thread, and the host-access calls come with the views lock and the PVM
/// gate possibly held: an implementation takes neither, nor anything that waits for them.
/// </summary>
public interface IGuestSingleCopyHandler
{
    /// <summary>Gives every released chunk overlapping backing bytes [offset, offset + size) its bytes again.</summary>
    void Restore(ulong offset, ulong size, GuestSingleCopyAccess access);

    /// <summary>
    /// The whole chunks [offset, offset + size) are about to be overwritten by a fill that
    /// may stop short: they stop being released now, and <see cref="EndOverwrite"/> says how
    /// far the fill got. Null when none of them was released.
    /// </summary>
    object? BeginOverwrite(ulong offset, ulong size);

    /// <summary>
    /// The fill begun with <see cref="BeginOverwrite"/> wrote [offset, offset + written): the
    /// bytes of those chunks it did not reach get what they held before.
    /// </summary>
    void EndOverwrite(object token, ulong offset, ulong written);

    /// <summary>
    /// The whole chunks [offset, offset + size) are being overwritten in full, cleared or
    /// freed: what they held no longer matters, so they are forgotten without a restore.
    /// </summary>
    void Drop(ulong offset, ulong size);

    /// <summary>
    /// A guest access (a store when <paramref name="write"/>) faulted at <paramref name="address"/>.
    /// True when the fault was the handler's (a released chunk, now restored, or a watch it
    /// left behind or another thread just took away) and the access should be retried.
    /// </summary>
    bool TryResolveFault(ulong address, bool write);
}

/// <summary>
/// Which 64 KiB chunks of a chunked guest backing are released: their guest copy may have
/// been emptied because a texture holds the same bytes (single-copy textures,
/// artifacts/memopt/single-copy-textures.md). Host code reaches guest memory through the
/// canonical alias, which a page watch cannot guard, so every alias access of
/// <see cref="SharedBackingViews"/> asks here first and hands a released chunk to the
/// <see cref="Handler"/> before the bytes are used. With nothing released the check is one
/// volatile read.
/// <para>
/// Only the handler changes the state: it marks a chunk with <see cref="TryMarkReleased"/>
/// (under the views lock, so a new mapping of the chunk cannot slip past it) and clears it
/// with <see cref="ClearReleased"/> under its own lock.
/// </para>
/// </summary>
public sealed class GuestSingleCopy
{
    private readonly SharedBackingViews _views;
    private readonly byte[] _released;
    private readonly byte[] _pinned;
    private IGuestSingleCopyHandler? _handler;
    private int _releasedChunks;

    // Mappings between their released-chunk check and their view record (SharedBackingViews):
    // a chunk is never marked released while one is in flight.
    internal int MapsInFlight;

    internal GuestSingleCopy(SharedBackingViews views, ulong chunkBytes, ulong size)
    {
        _views = views;
        ChunkBytes = chunkBytes;
        Size = size;
        _released = new byte[(size + chunkBytes - 1) / chunkBytes];
        _pinned = new byte[_released.Length];
    }

    public ulong ChunkBytes { get; }

    /// <summary>The backing's size; the last chunk may be short.</summary>
    public ulong Size { get; }

    public int ChunkCount => _released.Length;

    /// <summary>Host address of backing offset 0 in the canonical alias.</summary>
    public ulong AliasBase => _views.AliasBase;

    public IGuestSingleCopyHandler? Handler
    {
        get => Volatile.Read(ref _handler);
        set => Volatile.Write(ref _handler, value);
    }

    public int ReleasedChunks => Volatile.Read(ref _releasedChunks);

    public bool IsReleased(int chunk) => (uint)chunk < (uint)_released.Length && Volatile.Read(ref _released[chunk]) != 0;

    /// <summary>Whether any chunk overlapping backing bytes [offset, offset + size) is released.</summary>
    public bool AnyReleased(ulong offset, ulong size)
    {
        if (Volatile.Read(ref _releasedChunks) == 0 || size == 0 || offset >= Size)
        {
            return false;
        }

        var first = offset / ChunkBytes;
        var last = (Math.Min(offset + size, Size) - 1) / ChunkBytes;
        return _released.AsSpan((int)first, (int)(last - first + 1)).IndexOfAnyExcept((byte)0) >= 0;
    }

    /// <summary>
    /// Marks a chunk released if exactly one view maps it, at <paramref name="address"/> and
    /// whole, and no mapping is in flight. Takes the views lock; the caller holds none of
    /// the handler's locks.
    /// </summary>
    public bool TryMarkReleased(int chunk, ulong address) => _views.TryMarkSingleCopyReleased(this, chunk, address);

    // Under the views lock. A pinned chunk is refused; the mark and the pin are each written
    // before the other is read, both behind a full fence, so of a mark and a pin racing at
    // least one sees the other (Pin's caller restores a chunk it finds released).
    internal bool MarkReleasedLocked(int chunk)
    {
        if ((uint)chunk >= (uint)_released.Length || _released[chunk] != 0 || Volatile.Read(ref _pinned[chunk]) != 0)
        {
            return false;
        }

        Volatile.Write(ref _released[chunk], 1);
        Interlocked.Increment(ref _releasedChunks);
        if (Volatile.Read(ref _pinned[chunk]) != 0)
        {
            Volatile.Write(ref _released[chunk], 0);
            Interlocked.Decrement(ref _releasedChunks);
            return false;
        }

        return true;
    }

    /// <summary>Whether a chunk is pinned: it is never marked released again.</summary>
    public bool IsPinned(int chunk) => (uint)chunk < (uint)_pinned.Length && Volatile.Read(ref _pinned[chunk]) != 0;

    /// <summary>
    /// Pins a chunk for good (a device-address program may read it): it is never marked
    /// released from now on. True when it was not pinned before. The caller then gives back a
    /// chunk it finds released (<see cref="IsReleased"/>), which a mark racing the pin may
    /// have left.
    /// </summary>
    public bool Pin(int chunk) =>
        (uint)chunk < (uint)_pinned.Length && Interlocked.CompareExchange(ref _pinned[chunk], 1, 0) == 0;

    /// <summary>Clears a chunk's released mark; true when it was set. The handler's lock only.</summary>
    public bool ClearReleased(int chunk)
    {
        if (!IsReleased(chunk))
        {
            return false;
        }

        Volatile.Write(ref _released[chunk], 0);
        Interlocked.Decrement(ref _releasedChunks);
        return true;
    }

    /// <summary>How many view records reach into a chunk now (unlocked; a hint).</summary>
    public int ChunkViews(int chunk) => _views.ChunkViewCount(chunk);

    /// <summary>Copies restored bytes into the canonical alias at a backing offset, past every hook.</summary>
    public void WriteRestored(ulong offset, ReadOnlySpan<byte> bytes) => _views.WriteSingleCopyBytes(offset, bytes);

    /// <summary>Whether the canonical alias holds exactly these bytes at a backing offset (no hook).</summary>
    public bool Holds(ulong offset, ReadOnlySpan<byte> bytes) => _views.CanonicalBytesEqual(offset, bytes);

    /// <summary>Empties whole chunks, past every hook: their pages go back to the OS and they read zero.</summary>
    public HostBackingDiscard Empty(ulong offset, ulong size) => _views.EmptySingleCopyChunks(offset, size);

    /// <summary>Fills bytes of the canonical alias past every hook (a debug sentinel in place of an empty).</summary>
    public void Fill(ulong offset, ulong size, byte value) => _views.FillSingleCopyBytes(offset, size, value);

    /// <summary>A new GPU mapping of the backing (<see cref="IHostBackingGpuAlias"/>), or null.</summary>
    public IHostBackingGpuAlias? CreateGpuAlias() => _views.CreateGpuAlias();

    // The hooks SharedBackingViews calls before it touches alias bytes [offset, offset + size).

    internal void BeforeRead(ulong offset, ulong size)
    {
        if (AnyReleased(offset, size) && Handler is { } handler)
        {
            handler.Restore(offset, size, GuestSingleCopyAccess.HostRead);
        }
    }

    // A write that lands every byte of the range: the chunks it covers whole are dropped,
    // the ones it only touches are restored so the bytes it leaves keep their value.
    internal void BeforeWrite(ulong offset, ulong size)
    {
        if (!AnyReleased(offset, size) || Handler is not { } handler)
        {
            return;
        }

        var (start, end) = WholeChunks(offset, size);
        if (end <= start)
        {
            handler.Restore(offset, size, GuestSingleCopyAccess.HostWrite);
            return;
        }

        if (start > offset)
        {
            handler.Restore(offset, start - offset, GuestSingleCopyAccess.HostWrite);
        }

        if (offset + size > end)
        {
            handler.Restore(end, offset + size - end, GuestSingleCopyAccess.HostWrite);
        }

        handler.Drop(start, end - start);
    }

    // A fill that may stop short (a file read): the whole chunks are taken over until it
    // says how far it got.
    internal object? BeforeFill(ulong offset, ulong size)
    {
        if (!AnyReleased(offset, size) || Handler is not { } handler)
        {
            return null;
        }

        var (start, end) = WholeChunks(offset, size);
        if (end <= start)
        {
            handler.Restore(offset, size, GuestSingleCopyAccess.HostWrite);
            return null;
        }

        if (start > offset)
        {
            handler.Restore(offset, start - offset, GuestSingleCopyAccess.HostWrite);
        }

        if (offset + size > end)
        {
            handler.Restore(end, offset + size - end, GuestSingleCopyAccess.HostWrite);
        }

        return handler.BeginOverwrite(start, end - start);
    }

    internal void AfterFill(object? token, ulong offset, ulong written)
    {
        if (token is not null)
        {
            Handler?.EndOverwrite(token, offset, written);
        }
    }

    // Whole chunks emptied for good (a clear or a free).
    internal void BeforeDiscard(ulong offset, ulong size)
    {
        if (AnyReleased(offset, size) && Handler is { } handler)
        {
            handler.Drop(offset, size);
        }
    }

    internal void BeforeMap(ulong offset, ulong size)
    {
        if (AnyReleased(offset, size) && Handler is { } handler)
        {
            handler.Restore(offset, size, GuestSingleCopyAccess.Map);
        }
    }

    // The whole chunks inside [offset, offset + size); the last chunk ends at Size.
    private (ulong Start, ulong End) WholeChunks(ulong offset, ulong size)
    {
        var start = (offset + ChunkBytes - 1) / ChunkBytes * ChunkBytes;
        var rangeEnd = Math.Min(offset + size, Size);
        var end = rangeEnd == Size ? Size : rangeEnd / ChunkBytes * ChunkBytes;
        return (start, Math.Max(start, end));
    }
}
