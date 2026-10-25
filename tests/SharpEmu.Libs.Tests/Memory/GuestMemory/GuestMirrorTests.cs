// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE.GuestMemory;
using SharpEmu.HLE.Host;
using Xunit;
using static SharpEmu.Libs.Tests.Memory.HostViews.HostViewTestSupport;

namespace SharpEmu.Libs.Tests.Memory.GuestMemory;

/// <summary>
/// A shareable guest backing (the two-process split, SHARPEMU_GPU_PROCESS=1) mapped a second time
/// as an offset mirror in this process, as the GPU process would map it: bytes written through a
/// guest view read back through the mirror and the other way round, for the shm and the chunked
/// backing, and the shared view table, read through its own read-only mapping, turns guest
/// addresses into the right mirror pointers as views and protections change.
/// </summary>
[Collection(GuestMemoryStateCollection.Name)]
public sealed unsafe class GuestMirrorTests : IDisposable
{
    private const ulong Chunk = 0x10000;
    private const ulong StoreBytes = 64 * Chunk;
    private readonly bool _shareable = HostBackendUnderTest.SetShareableBacking(true);

    public void Dispose()
    {
        HostBackendUnderTest.SetShareableBacking(null);
        HostBackendUnderTest.SetChunkedBacking(null);
    }

    private static IHostGuestMirrorMemory MirrorHost(IHostViewMemory host) => Assert.IsAssignableFrom<IHostGuestMirrorMemory>(host);

    private static HostBackingObject MapMirror(IHostViewMemory host, GuestBackingExport export)
    {
        Assert.True(MirrorHost(host).TryMapMirror(export, out var mirror));
        return mirror!;
    }

    // The view table as the other process sees it: its own read-only mapping of the shared region.
    private static HostSharedRegion MapTableReadOnly(IHostViewMemory host, GuestViewTable table)
    {
        Assert.True(MirrorHost(host).TryMapSharedRegion(table.Region.Descriptor, table.Region.Size, writable: false, out var region));
        Assert.NotEqual(table.Region.Address, region!.Address);
        return region;
    }

    [Fact]
    public void BackingsAreNotShareableUnlessAsked()
    {
        if (!_shareable) return;
        HostBackendUnderTest.SetShareableBacking(false);
        foreach (var chunked in new[] { false, true })
        {
            HostBackendUnderTest.SetChunkedBacking(chunked);
            using var store = new SharedBackingViews(HostViewMemory.Create(), StoreBytes);
            Assert.True(store.IsAvailable);
            Assert.Null(store.BackingExport);
            Assert.Null(store.ViewTable);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MirrorAndGuestViewsShareEveryByte(bool chunked)
    {
        if (!_shareable) return;
        HostBackendUnderTest.SetChunkedBacking(chunked);
        var host = HostViewMemory.Create();
        using var store = new SharedBackingViews(host, StoreBytes);
        var export = Assert.IsType<GuestBackingExport>(store.BackingExport);
        Assert.Equal(chunked ? GuestBackingExportKind.MemoryEntries : GuestBackingExportKind.SharedMemoryObject, export.Kind);
        Assert.Equal(StoreBytes, export.Size);
        if (chunked)
        {
            // One kept port per 2 MiB object.
            Assert.Equal((int)((export.MappedSize + export.ObjectBytes - 1) / export.ObjectBytes), export.Entries.Count);
            Assert.Equal(-1, export.Descriptor);
        }

        var holeBytes = AlignUp(4 * Chunk, host.Granularity);
        var view = ReserveFreeHole(host, holeBytes);
        const ulong viewOffset = 9 * Chunk;
        Assert.True(store.TryMapReservedRange(view, 4 * Chunk, viewOffset, HostPageProtection.ReadWrite, out _));
        var mirror = MapMirror(host, export);
        try
        {
            Assert.NotEqual(store.AliasBase, mirror.AliasBase);
            Assert.Equal(0UL, mirror.AliasBase % Chunk);
            Assert.Null(mirror.DiscardChunks);
            Assert.Null(mirror.Export);
            Assert.Equal(chunked ? Chunk : 0, mirror.ChunkBytes);

            // Guest view -> mirror, across a chunk edge.
            *(ulong*)(view + Chunk - 8) = Marker;
            *(ulong*)(view + Chunk) = ~Marker;
            Assert.Equal(Marker, *(ulong*)(mirror.AliasBase + viewOffset + Chunk - 8));
            Assert.Equal(~Marker, *(ulong*)(mirror.AliasBase + viewOffset + Chunk));

            // Mirror -> guest view and alias.
            *(ulong*)(mirror.AliasBase + viewOffset + 3 * Chunk + 0x40) = Marker ^ 0x55;
            Assert.Equal(Marker ^ 0x55, *(ulong*)(view + 3 * Chunk + 0x40));
            Assert.Equal(Marker ^ 0x55, *(ulong*)(store.AliasBase + viewOffset + 3 * Chunk + 0x40));

            // The last byte of the backing.
            *(byte*)(store.AliasBase + StoreBytes - 1) = 0xC3;
            Assert.Equal(0xC3, *(byte*)(mirror.AliasBase + StoreBytes - 1));
        }
        finally
        {
            mirror.Dispose();
            Assert.True(store.Unmap(view, 4 * Chunk, out _));
            Assert.True(host.FreeHole(view, holeBytes));
        }
    }

    [Fact]
    public void ChunksTheOwnerReleasesReadZeroThroughTheMirror()
    {
        if (!_shareable) return;
        HostBackendUnderTest.SetChunkedBacking(true);
        var host = HostViewMemory.Create();
        using var store = new SharedBackingViews(host, StoreBytes);
        using var mirror = MapMirror(host, store.BackingExport!);
        new Span<byte>((void*)(mirror.AliasBase + 4 * Chunk), (int)(4 * Chunk)).Fill(0xE7);
        Assert.Equal(0xE7, *(byte*)(store.AliasBase + 5 * Chunk));

        // No view maps them, so the owner gives their pages back; the mirror maps the same objects.
        Assert.Equal(2 * Chunk, store.Release(5 * Chunk, 2 * Chunk));
        Assert.False(new ReadOnlySpan<byte>((void*)(mirror.AliasBase + 5 * Chunk), (int)(2 * Chunk)).ContainsAnyExcept((byte)0));
        Assert.Equal(0xE7, *(byte*)(mirror.AliasBase + 4 * Chunk));
        Assert.Equal(0xE7, *(byte*)(mirror.AliasBase + 7 * Chunk));

        // The mirror makes its own GPU alias, over the same objects.
        using var gpuAlias = mirror.CreateGpuAlias!(mirror);
        Assert.NotNull(gpuAlias);
        Assert.Equal(0xE7, *(byte*)(gpuAlias!.Base + 4 * Chunk));
        *(byte*)(gpuAlias.Base + 6 * Chunk) = 0x3C;
        Assert.Equal(0x3C, *(byte*)(store.AliasBase + 6 * Chunk));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ViewTableTranslatesGuestAddressesIntoTheMirror(bool chunked)
    {
        if (!_shareable) return;
        HostBackendUnderTest.SetChunkedBacking(chunked);
        var host = HostViewMemory.Create();
        using var owner = new GuestSpaceOwner(host, StoreBytes);
        var table = Assert.IsType<GuestViewTable>(owner.ViewTable);
        var page = GuestSpaceOwner.GuestPage;
        var size = AlignUp(8 * Chunk, Math.Max(host.Granularity, page));
        var address = ProbeGuestAddress(host, size);
        Assert.True(owner.TryReserveAddressRange(address, size));
        using var tableMapping = MapTableReadOnly(host, table);
        using var mirror = MapMirror(host, owner.BackingExport!);
        var translator = new GuestMirrorTranslator(tableMapping, mirror);

        // Two views of the same bytes, the second at a different guest address.
        Assert.True(owner.MapShared(address, 4 * Chunk, 16 * Chunk, HostPageProtection.ReadWrite, out _));
        Assert.True(owner.MapShared(address + 4 * Chunk, 2 * Chunk, 17 * Chunk, HostPageProtection.ReadWrite, out _));
        Assert.Equal(
            [new GuestViewEntry(address, 4 * Chunk, 16 * Chunk, HostPageProtection.ReadWrite),
             new GuestViewEntry(address + 4 * Chunk, 2 * Chunk, 17 * Chunk, HostPageProtection.ReadWrite)],
            translator.Snapshot());

        *(ulong*)(address + Chunk + 0x18) = Marker;
        Assert.True(translator.TryTranslate(address + Chunk + 0x18, 8, out var pointer));
        Assert.Equal(mirror.AliasBase + 17 * Chunk + 0x18, pointer);
        Assert.Equal(Marker, *(ulong*)pointer);
        Assert.True(translator.TryTranslate(address + 4 * Chunk + 0x18, 8, out pointer));
        Assert.Equal(Marker, *(ulong*)pointer);

        // A guest protection change is logged; a shareable backing's entries keep the mapping protection.
        var generation = translator.MappingGeneration;
        var logged = table.LogSequence;
        Assert.False(table.TracksProtection);
        Assert.True(owner.SetAccess(address + Chunk, page, HostPageProtection.ReadOnly));
        Assert.True(table.TryReadLog(logged, out var record));
        Assert.Equal(new GuestViewLogEntry(logged, GuestViewLogKind.Protect, address + Chunk, page, 0, HostPageProtection.ReadOnly), record);
        Assert.True(translator.TryGetEntry(address + Chunk, out var entry));
        Assert.Equal(HostPageProtection.ReadWrite, entry.Protection);
        Assert.Equal(generation, translator.MappingGeneration);
        Assert.True(translator.TryTranslate(address + Chunk - 8, 16, out pointer));
        Assert.Equal(mirror.AliasBase + 17 * Chunk - 8, pointer);
        Assert.True(owner.SetAccess(address + Chunk, page, HostPageProtection.ReadWrite));
        Assert.Equal(2, translator.Snapshot().Length);

        // A partial unmap removes exactly that range; the restored sides keep translating.
        Assert.True(owner.UnmapShared(address + Chunk, Chunk));
        Assert.False(translator.TryTranslate(address + Chunk + 0x18, 8, out _));
        Assert.True(translator.TryTranslate(address + 2 * Chunk, 8, out pointer));
        Assert.Equal(mirror.AliasBase + 18 * Chunk, pointer);
        Assert.True(translator.TryTranslate(address, 8, out pointer));
        Assert.Equal(mirror.AliasBase + 16 * Chunk, pointer);

        Assert.True(owner.UnmapShared(address, Chunk));
        Assert.True(owner.UnmapShared(address + 2 * Chunk, 4 * Chunk));
        Assert.Empty(translator.Snapshot());
        Assert.True(translator.IsComplete);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReplicaAtAConstantSharesEveryByteWithTheGuestViews(bool chunked)
    {
        if (!_shareable) return;
        HostBackendUnderTest.SetChunkedBacking(chunked);
        var host = HostViewMemory.Create();
        using var owner = new GuestSpaceOwner(host, StoreBytes);
        var size = AlignUp(8 * Chunk, Math.Max(host.Granularity, GuestSpaceOwner.GuestPage));
        var address = ProbeGuestAddress(host, size);
        Assert.True(owner.TryReserveAddressRange(address, size));
        Assert.True(owner.MapShared(address, 2 * Chunk, 20 * Chunk, HostPageProtection.ReadWrite, out _));
        Assert.True(owner.MapShared(address + 2 * Chunk, 2 * Chunk, 30 * Chunk, HostPageProtection.ReadWrite, out _));
        *(ulong*)(address + Chunk) = Marker;
        using var tableMapping = MapTableReadOnly(host, owner.ViewTable!);
        using var mirror = MapMirror(host, owner.BackingExport!);
        using var replica = GuestReplica.TryAttach(MirrorHost(host), mirror, tableMapping, GuestReplica.DefaultBase, address, address + size);
        Assert.NotNull(replica);
        Assert.Equal(GuestReplica.DefaultBase + address, replica!.HostAddress(address));
        Assert.True(replica.IsComplete);
        Assert.Equal(owner.ViewTable!.LogSequence, replica.ReplayedSequence);

        // Guest view -> replica, and replica -> guest view, across the two views' edge.
        Assert.Equal(Marker, *(ulong*)replica.HostAddress(address + Chunk));
        *(ulong*)replica.HostAddress(address + 2 * Chunk - 8) = ~Marker;
        *(ulong*)replica.HostAddress(address + 2 * Chunk) = Marker ^ 0x77;
        Assert.Equal(~Marker, *(ulong*)(address + 2 * Chunk - 8));
        Assert.Equal(Marker ^ 0x77, *(ulong*)(address + 2 * Chunk));
        Assert.Equal(Marker ^ 0x77, *(ulong*)(mirror.AliasBase + 30 * Chunk));

        // Page watches change protection; the replica neither needs nor takes them.
        Assert.True(owner.SetAccess(address, GuestSpaceOwner.GuestPage, HostPageProtection.ReadOnly));
        var published = replica.PublishedSequence;
        Assert.Equal(published, replica.Sync());
        *(ulong*)replica.HostAddress(address + 8) = Marker;
        Assert.Equal(Marker, *(ulong*)(address + 8));
        Assert.True(owner.SetAccess(address, GuestSpaceOwner.GuestPage, HostPageProtection.ReadWrite));

        // A remap is seen only from the sync after it, as at the split's sync points.
        new Span<byte>((void*)(mirror.AliasBase + 40 * Chunk), (int)Chunk).Fill(0x6E);
        Assert.True(owner.UnmapShared(address + 2 * Chunk, Chunk));
        Assert.True(owner.MapShared(address + 2 * Chunk, Chunk, 40 * Chunk, HostPageProtection.ReadWrite, out _));
        Assert.Equal(Marker ^ 0x77, *(ulong*)replica.HostAddress(address + 2 * Chunk));
        var replayed = replica.Sync();
        Assert.Equal(owner.ViewTable!.LogSequence, replayed);
        Assert.Equal(0x6E6E_6E6E_6E6E_6E6EUL, *(ulong*)replica.HostAddress(address + 2 * Chunk));
        Assert.Equal(0x6E6E_6E6E_6E6E_6E6EUL, *(ulong*)(address + 2 * Chunk));
        Assert.Equal(1, replica.Resyncs);
        Assert.True(replica.IsComplete);

        Assert.True(owner.UnmapShared(address, 4 * Chunk));
    }

    [Fact]
    public void ReplicaAWholeLogBehindStartsAgainFromTheTable()
    {
        if (!_shareable) return;
        HostBackendUnderTest.SetChunkedBacking(true);
        var host = HostViewMemory.Create();
        using var store = new SharedBackingViews(host, StoreBytes);
        for (var chunk = 0; chunk < (int)(StoreBytes / Chunk); chunk++)
            new Span<byte>((void*)(store.AliasBase + (ulong)chunk * Chunk), (int)Chunk).Fill((byte)(chunk + 1));

        // An owner's table with a four-record log, driven by hand: guest addresses need no views here.
        Assert.True(MirrorHost(host).TryCreateSharedRegion(GuestViewTable.RegionBytes(16, 4), out var region));
        using var table = new GuestViewTable(region!, StoreBytes, logCapacity: 4);
        using var tableMapping = MapTableReadOnly(host, table);
        using var mirror = MapMirror(host, store.BackingExport!);
        const ulong guest = 0x30_0000_0000;
        table.Map(guest, Chunk, 3 * Chunk, HostPageProtection.ReadWrite);
        using var replica = GuestReplica.TryAttach(MirrorHost(host), mirror, tableMapping, GuestReplica.DefaultBase, guest, guest + 8 * Chunk);
        Assert.NotNull(replica);
        Assert.Equal(4, *(byte*)replica!.HostAddress(guest));

        table.Map(guest + Chunk, Chunk, 5 * Chunk, HostPageProtection.ReadWrite);
        table.Unmap(guest, Chunk);
        Assert.Equal(table.LogSequence, replica.Sync());
        Assert.Equal(1, replica.Resyncs);
        Assert.Equal(6, *(byte*)replica.HostAddress(guest + Chunk));

        // Ten more changes: the ring holds four of them, so the replica maps the table again.
        for (ulong index = 0; index < 10; index++) table.Map(guest + (index % 4) * Chunk, Chunk, (10 + index) * Chunk, HostPageProtection.ReadWrite);
        Assert.Equal(table.LogSequence, replica.Sync());
        Assert.Equal(2, replica.Resyncs);
        Assert.True(replica.IsComplete);
        for (ulong slot = 0; slot < 4; slot++)
            Assert.Equal((byte)(10 + (slot < 2 ? slot + 8 : slot + 4) + 1), *(byte*)replica.HostAddress(guest + slot * Chunk));

        // A view outside the window cannot be replicated, and the replica says so.
        table.Map(guest + 9 * Chunk, Chunk, 0, HostPageProtection.ReadWrite);
        replica.Sync();
        Assert.False(replica.IsComplete);
    }
}
