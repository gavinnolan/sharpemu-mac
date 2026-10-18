// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE.GuestMemory;
using SharpEmu.HLE.Host;
using Xunit;
using static SharpEmu.Libs.Tests.Memory.HostViews.HostViewTestSupport;

namespace SharpEmu.Libs.Tests.Memory.GuestMemory;

/// <summary>
/// The chunked guest backing (macOS SHARPEMU_GUEST_BACKING=chunked): 64 KiB purgeable chunks
/// under the canonical alias, views remapped from it. Views that cross chunk edges or cover
/// part of a chunk must alias the alias and each other exactly as shm views do; a clear empties
/// whole chunks and writes only the partial ones at its ends; a release empties only whole
/// chunks no view maps. Every test forces the chunked backing for the backings it creates.
/// </summary>
[Collection(GuestMemoryStateCollection.Name)]
public sealed unsafe class ChunkedBackingTests : IDisposable
{
    private const ulong Chunk = 0x10000;
    private const ulong StoreBytes = 16 * Chunk;
    private readonly bool _chunked = HostBackendUnderTest.SetChunkedBacking(true);

    public void Dispose() => HostBackendUnderTest.SetChunkedBacking(null);

    private static void Fill(SharedBackingViews store, ulong offset, ulong size, byte value) =>
        new Span<byte>((void*)(store.AliasBase + offset), (int)size).Fill(value);

    private static bool AllEqual(ulong address, ulong size, byte value) =>
        !new ReadOnlySpan<byte>((void*)address, (int)size).ContainsAnyExcept(value);

    [Fact]
    public void BackingReportsItsChunks()
    {
        if (!_chunked) return;
        using var store = new SharedBackingViews(HostViewMemory.Create(), StoreBytes);
        Assert.True(store.IsAvailable);
        Assert.Equal(Chunk, store.ChunkBytes);
        Assert.Equal(0UL, store.AliasBase % Chunk);
        Assert.True(AllEqual(store.AliasBase, StoreBytes, 0));
    }

    [Fact]
    public void ViewsAcrossChunkEdgesAliasTheAliasAndEachOther()
    {
        if (!_chunked) return;
        var host = HostViewMemory.Create();
        using var store = new SharedBackingViews(host, StoreBytes);
        var holeBytes = AlignUp(4 * Chunk, host.Granularity);
        var whole = ReserveFreeHole(host, holeBytes);
        var part = ReserveFreeHole(host, holeBytes);
        // whole maps chunks 0-3; part maps 16 KiB into chunk 1 through 16 KiB into chunk 3.
        const ulong partOffset = Chunk + 0x4000;
        const ulong partSize = 2 * Chunk;
        Assert.True(store.TryMapReservedRange(whole, 4 * Chunk, 0, HostPageProtection.ReadWrite, out _));
        Assert.True(store.TryMapReservedRange(part, partSize, partOffset, HostPageProtection.ReadWrite, out _));
        try
        {
            // Bytes on both sides of the chunk 2 edge, written through the partial view.
            *(ulong*)(part + (2 * Chunk - partOffset) - 8) = Marker;
            *(ulong*)(part + (2 * Chunk - partOffset)) = ~Marker;
            Assert.Equal(Marker, *(ulong*)(store.AliasBase + 2 * Chunk - 8));
            Assert.Equal(~Marker, *(ulong*)(whole + 2 * Chunk));

            // Written through the alias at the partial view's last byte and first byte.
            *(byte*)(store.AliasBase + partOffset + partSize - 1) = 0x5A;
            *(byte*)(store.AliasBase + partOffset) = 0xA5;
            Assert.Equal(0x5A, *(byte*)(part + partSize - 1));
            Assert.Equal(0xA5, *(byte*)part);
            Assert.Equal(0xA5, *(byte*)(whole + partOffset));

            // A read spanning the edge through the locked path.
            var bytes = new byte[16];
            Assert.True(store.TryReadBacking(whole + 2 * Chunk - 8, bytes));
            Assert.Equal(Marker, BitConverter.ToUInt64(bytes, 0));
            Assert.Equal(~Marker, BitConverter.ToUInt64(bytes, 8));
        }
        finally
        {
            Assert.True(store.Unmap(whole, 4 * Chunk, out _));
            Assert.True(store.Unmap(part, partSize, out _));
            Assert.True(host.FreeHole(whole, holeBytes));
            Assert.True(host.FreeHole(part, holeBytes));
        }
    }

    [Fact]
    public void ProtectionHoleInsideAChunkAndPartialUnmapKeepTheRestCoherent()
    {
        if (!_chunked) return;
        var host = HostViewMemory.Create();
        using var store = new SharedBackingViews(host, StoreBytes);
        var holeBytes = AlignUp(3 * Chunk, host.Granularity);
        var view = ReserveFreeHole(host, holeBytes);
        Assert.True(store.TryMapReservedRange(view, 3 * Chunk, 0, HostPageProtection.ReadWrite, out _));
        try
        {
            // A PROT_NONE page in the middle of chunk 1, then back: the view and alias agree.
            var page = Chunk + 0x8000;
            Assert.True(host.ChangeAccess(view + page, host.PageSize, HostPageProtection.NoAccess));
            *(ulong*)(store.AliasBase + page) = Marker;
            Assert.True(host.ChangeAccess(view + page, host.PageSize, HostPageProtection.ReadWrite));
            Assert.Equal(Marker, *(ulong*)(view + page));

            // Unmap 16 KiB in the middle of chunk 1: the two remapped sides still alias.
            Assert.True(store.Unmap(view + Chunk + 0x4000, 0x4000, out var preserved));
            Assert.True(preserved);
            Assert.True(store.Contains(view, Chunk + 0x4000));
            Assert.True(store.Contains(view + Chunk + 0x8000, 2 * Chunk - 0x8000));
            *(ulong*)(view + Chunk) = ~Marker;
            *(ulong*)(view + 2 * Chunk + 8) = Marker;
            Assert.Equal(~Marker, *(ulong*)(store.AliasBase + Chunk));
            Assert.Equal(Marker, *(ulong*)(store.AliasBase + 2 * Chunk + 8));
            Assert.Equal(Marker, *(ulong*)(view + page));
        }
        finally
        {
            Assert.True(store.Unmap(view, Chunk + 0x4000, out _));
            Assert.True(store.Unmap(view + Chunk + 0x8000, 2 * Chunk - 0x8000, out _));
            Assert.True(host.FreeHole(view, holeBytes));
        }
    }

    [Fact]
    public void ClearEmptiesWholeChunksAndWritesOnlyThePartialEnds()
    {
        if (!_chunked) return;
        var host = HostViewMemory.Create();
        using var store = new SharedBackingViews(host, StoreBytes);
        var holeBytes = AlignUp(4 * Chunk, host.Granularity);
        var view = ReserveFreeHole(host, holeBytes);
        Assert.True(store.TryMapReservedRange(view, 4 * Chunk, 0, HostPageProtection.ReadWrite, out _));
        try
        {
            Fill(store, 0, 4 * Chunk, 0xAB);
            var purged = GuestBackingTelemetry.ClearPurgedBytes;
            var zeroFilled = GuestBackingTelemetry.ZeroFilledBytes;
            // [16 KiB, 208 KiB): chunks 1 and 2 whole, parts of chunks 0 and 3.
            Assert.True(store.Clear(0x4000, 3 * Chunk));
            Assert.True(AllEqual(view, 0x4000, 0xAB));
            Assert.True(AllEqual(view + 0x4000, 3 * Chunk, 0));
            Assert.True(AllEqual(view + 0x4000 + 3 * Chunk, Chunk - 0x4000, 0xAB));
            Assert.True(AllEqual(store.AliasBase + 0x4000, 3 * Chunk, 0));
            // Counters are process-wide: other chunked backings may add to them meanwhile.
            Assert.True(GuestBackingTelemetry.ClearPurgedBytes - purged + GuestBackingTelemetry.ZeroFilledBytes - zeroFilled >=
                (long)(2 * Chunk));

            // An emptied chunk is ordinary memory again.
            *(ulong*)(view + Chunk + 8) = Marker;
            Assert.Equal(Marker, *(ulong*)(store.AliasBase + Chunk + 8));
        }
        finally
        {
            Assert.True(store.Unmap(view, 4 * Chunk, out _));
            Assert.True(host.FreeHole(view, holeBytes));
        }
    }

    [Fact]
    public void ReleaseEmptiesOnlyWholeChunksNoViewMaps()
    {
        if (!_chunked) return;
        var host = HostViewMemory.Create();
        using var store = new SharedBackingViews(host, StoreBytes);
        var holeBytes = AlignUp(Chunk, host.Granularity);
        var view = ReserveFreeHole(host, holeBytes);
        Fill(store, 0, 6 * Chunk, 0xCD);
        // A 16 KiB view into chunk 2 keeps the whole chunk.
        Assert.True(store.TryMapReservedRange(view, 0x4000, 2 * Chunk + 0x8000, HostPageProtection.ReadWrite, out _));
        try
        {
            // [32 KiB, 352 KiB) holds chunks 1-4 whole; chunk 0 and 5 only in part.
            // Chunks 1, 3 and 4. Nothing wires them here, so all three are emptied, none zero-filled.
            Assert.Equal(3 * Chunk, store.Release(0x8000, 5 * Chunk));
            Assert.True(AllEqual(store.AliasBase, Chunk, 0xCD));
            Assert.True(AllEqual(store.AliasBase + Chunk, Chunk, 0));
            Assert.True(AllEqual(store.AliasBase + 2 * Chunk, Chunk, 0xCD));
            Assert.True(AllEqual(store.AliasBase + 3 * Chunk, 2 * Chunk, 0));
            Assert.True(AllEqual(store.AliasBase + 5 * Chunk, Chunk, 0xCD));
            Assert.Equal(0xCD, *(byte*)view);

            // Nothing whole inside: nothing released.
            Assert.Equal(0UL, store.Release(0x4000, Chunk));
            Assert.True(AllEqual(store.AliasBase, Chunk, 0xCD));

            Assert.True(store.Unmap(view, 0x4000, out _));
            Assert.Equal(Chunk, store.Release(2 * Chunk, Chunk));
            Assert.True(AllEqual(store.AliasBase + 2 * Chunk, Chunk, 0));
        }
        finally
        {
            if (store.Contains(view, 0x4000)) Assert.True(store.Unmap(view, 0x4000, out _));
            Assert.True(host.FreeHole(view, holeBytes));
        }
    }

    // A chunk released out of a 2 MiB object while another thread keeps writing the rest of
    // the object: the chunk is emptied (its pages go back, it reads zero) and no write to the
    // rest is lost, because the rest stays wired through the EMPTY. A release of a whole
    // object empties all of it.
    [Fact]
    public void ReleaseOutOfAnObjectKeepsConcurrentWritesToTheRest()
    {
        if (!_chunked) return;
        const ulong objectBytes = 32 * Chunk;
        using var store = new SharedBackingViews(HostViewMemory.Create(), 2 * objectBytes);
        Fill(store, 0, 2 * objectBytes, 0x5A);
        var slots = (int)(objectBytes / 2 / 0x1000);
        var last = new uint[slots];
        var stop = 0;
        var writer = new Thread(() =>
        {
            // Chunks 16-31 of object 0, one 4 KiB slot at a time.
            var basePointer = store.AliasBase + objectBytes / 2;
            for (uint sequence = 1; Volatile.Read(ref stop) == 0; sequence++)
            {
                var slot = (int)(sequence * 2654435761u % (uint)slots);
                *(uint*)(basePointer + (ulong)slot * 0x1000) = sequence;
                last[slot] = sequence;
            }
        });
        writer.Start();
        try
        {
            for (var round = 0; round < 32; round++)
            {
                for (ulong chunk = 0; chunk < 16; chunk++)
                {
                    Fill(store, chunk * Chunk, Chunk, 0xEE);
                    Assert.Equal(Chunk, store.Release(chunk * Chunk, Chunk));
                    Assert.True(AllEqual(store.AliasBase + chunk * Chunk, Chunk, 0));
                }
            }
        }
        finally
        {
            Volatile.Write(ref stop, 1);
            writer.Join();
        }

        for (var slot = 0; slot < slots; slot++)
        {
            var slotAddress = store.AliasBase + objectBytes / 2 + (ulong)slot * 0x1000;
            Assert.Equal(last[slot] != 0 ? last[slot] : 0x5A5A5A5Au, *(uint*)slotAddress);
            Assert.True(AllEqual(slotAddress + 4, 0x1000 - 4, 0x5A));
        }

        Assert.Equal(objectBytes, store.Release(objectBytes, objectBytes));
        Assert.True(AllEqual(store.AliasBase + objectBytes, objectBytes, 0));
    }

    [Fact]
    public void SingleObjectBackingReleasesNothing()
    {
        HostBackendUnderTest.SetChunkedBacking(false);
        using var store = new SharedBackingViews(HostViewMemory.Create(), StoreBytes);
        Assert.Equal(0UL, store.ChunkBytes);
        Fill(store, 0, 2 * Chunk, 0x11);
        Assert.Equal(0UL, store.Release(0, 2 * Chunk));
        Assert.True(AllEqual(store.AliasBase, 2 * Chunk, 0x11));
    }
}
