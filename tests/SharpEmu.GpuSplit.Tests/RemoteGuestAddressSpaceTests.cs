// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.GpuRemote;
using SharpEmu.HLE;
using SharpEmu.HLE.GpuMemory;
using SharpEmu.HLE.GuestMemory;
using SharpEmu.HLE.Host;
using Xunit;

namespace SharpEmu.GpuSplit.Tests;

[Collection(SplitCollection.Name)]
public sealed class SplitSharedStateTests
{
    [MacOSFact]
    public void TheSummaryAnswersForEveryGroupARangeTouches()
    {
        using var channel = GpuRemoteChannel.Create(SharedMemoryRegion.CreateUniqueName("sesplit"), SplitSharedState.Layout);
        var state = new SplitSharedState(channel);
        const ulong Group = 1UL << SplitSharedState.GroupShift;
        Assert.False(state.MayInterest(0, TrackerLayout.SpaceBytes));

        state.SetGroup(0x40_0000 + 3 * Group + 0x123);
        Assert.True(state.MayInterest(0x40_0000 + 3 * Group, 8));
        Assert.True(state.MayInterest(0x40_0000 + 4 * Group - 1, 1));
        Assert.False(state.MayInterest(0x40_0000 + 4 * Group, Group));
        Assert.False(state.MayInterest(0x40_0000, 3 * Group));
        // Across words: one 4 MiB block per word.
        Assert.True(state.MayInterest(0x10_0000, 0x40_0000));
        Assert.True(state.MayInterest(0, TrackerLayout.SpaceBytes));
        Assert.False(state.MayInterest(TrackerLayout.SpaceBytes, 8));
        Assert.False(state.MayInterest(0x40_0000 + 3 * Group, 0));

        state.SetGroup(TrackerLayout.SpaceBytes - 1);
        Assert.True(state.MayInterest(TrackerLayout.SpaceBytes - Group, ulong.MaxValue / 2));

        state.ClearGroup(0x40_0000 + 3 * Group);
        Assert.False(state.MayInterest(0x40_0000, 0x40_0000));
    }

    [MacOSFact]
    public void AProcessWithoutTheBlockIsRefused()
    {
        using var channel = GpuRemoteChannel.Create(SharedMemoryRegion.CreateUniqueName("sesplit"));
        Assert.Throws<InvalidDataException>(() => new SplitSharedState(channel));
    }
}

/// <summary>The GPU process's guest address space over a real shareable backing, mirror and replica.</summary>
[Collection(SplitCollection.Name)]
public sealed unsafe class RemoteGuestAddressSpaceTests : IDisposable
{
    private const GuestPageProtection ReadWrite = GuestPageProtection.Read | GuestPageProtection.Write;
    private const ulong Marker = 0x5350_4C49_5447_5055;

    private readonly SharedGuestMemory _guest = new();
    private readonly HostBackingObject _mirror;
    private readonly HostSharedRegion _table;
    private readonly GuestReplica _replica;
    private readonly RecordingProtections _protections = new();
    private readonly RecordingSummary _summary = new();
    private readonly RemoteGuestAddressSpace _space;

    public RemoteGuestAddressSpaceTests()
    {
        (_mirror, _table, _replica) = _guest.MapLikeTheGpuProcess();
        _space = new RemoteGuestAddressSpace(
            _mirror, new GuestMirrorTranslator(_table, _mirror), _replica, _guest.Export.ChunkBytes, _guest.Export.ObjectBytes,
            _protections, _summary);
    }

    public void Dispose()
    {
        _replica.Dispose();
        _table.Dispose();
        _mirror.Dispose();
        _guest.Dispose();
    }

    [MacOSFact]
    public void BytesCrossBothWaysThroughTheMirror()
    {
        *_guest.Pointer(SharedGuestMemory.Chunk - 8) = Marker;
        *_guest.Pointer(SharedGuestMemory.Chunk) = ~Marker;
        Span<byte> read = stackalloc byte[16];
        Assert.True(_space.TryRead(_guest.Address + SharedGuestMemory.Chunk - 8, read));
        Assert.Equal(Marker, BitConverter.ToUInt64(read));
        Assert.Equal(~Marker, BitConverter.ToUInt64(read[8..]));
        Assert.True(_space.TryCompare(_guest.Address + SharedGuestMemory.Chunk - 8, read, out var equal));
        Assert.True(equal);

        Assert.True(_space.TryWrite(_guest.Address + 3 * SharedGuestMemory.Chunk, BitConverter.GetBytes(Marker ^ 0x55)));
        Assert.Equal(Marker ^ 0x55, *_guest.Pointer(3 * SharedGuestMemory.Chunk));
        Assert.True(_space.CanRead(_guest.Address, SharedGuestMemory.ViewBytes));
        Assert.True(_space.IsBackedRange(_guest.Address, SharedGuestMemory.ViewBytes));
        Assert.True(_space.IsBackedView(_guest.Address));
    }

    [MacOSFact]
    public void MemoryTheBackingDoesNotHoldFailsAndIsCounted()
    {
        Span<byte> read = stackalloc byte[8];
        Assert.False(_space.TryRead(_guest.Address + SharedGuestMemory.ViewBytes, read));
        Assert.False(_space.TryRead(_guest.Address + SharedGuestMemory.ViewBytes - 4, read));
        Assert.False(_space.TryWrite(0x1000, read));
        Assert.False(_space.CanRead(_guest.Address, SharedGuestMemory.ViewBytes + 1));
        Assert.Equal(3, _space.PrivateMemoryMisses);
        Assert.Contains("not in the shared backing", _space.DescribeReadRange(0x1000, 8));
    }

    [MacOSFact]
    public void AliasesAreMirrorAddressesAndHostViewsReplicaAddresses()
    {
        Assert.True(_space.TryResolveBackingAlias(_guest.Address + 0x40, 0x100, out var alias));
        Assert.Equal(_mirror.AliasBase + SharedGuestMemory.ViewOffset + 0x40, alias);
        Assert.Equal((_mirror.AliasBase, _space.BackingAliasRange.Size), _space.BackingAliasRange);
        Assert.False(_space.TryResolveBackingAlias(_guest.Address + SharedGuestMemory.ViewBytes - 8, 16, out _));

        *_guest.Pointer(0x80) = Marker;
        Assert.True(_space.TryResolveBackedHostView(_guest.Address, SharedGuestMemory.ViewBytes, out var host));
        Assert.Equal(_replica.HostAddress(_guest.Address), host);
        Assert.Equal(Marker, *(ulong*)(host + 0x80));
    }

    [MacOSFact]
    public void AHostViewSyncsTheReplicaToViewsMappedAfterIt()
    {
        // A second view the replica has not replayed yet: the table already has it.
        var second = _guest.Address + SharedGuestMemory.ViewBytes;
        var hole = _guest.Host.ReserveHole(second, SharedGuestMemory.Chunk);
        if (hole != second)
        {
            return;
        }

        try
        {
            Assert.True(_guest.Views.TryMapReservedRange(second, SharedGuestMemory.Chunk, 40 * SharedGuestMemory.Chunk,
                HostPageProtection.ReadWrite, out _));
            *(ulong*)second = Marker;
            var before = _replica.ReplayedSequence;
            Assert.True(_space.TryResolveBackedHostView(second, SharedGuestMemory.Chunk, out var host));
            Assert.True(_replica.ReplayedSequence > before);
            Assert.Equal(Marker, *(ulong*)host);
        }
        finally
        {
            _ = _guest.Views.Unmap(second, SharedGuestMemory.Chunk, out _);
            _ = _guest.Host.FreeHole(second, SharedGuestMemory.Chunk);
        }
    }

    [MacOSFact]
    public void ProtectionsGoToTheGuestAndOnlyTakingRightsWaits()
    {
        Assert.Equal(TrackerLayout.PageBytes, _space.ProtectionPageSize);
        Assert.True(_space.TryApplyDerivedProtection(_guest.Address, 0x2000, ReadWrite, GuestPageProtection.Read, ordinaryEnrollment: true));
        Assert.True(_space.TryApplyDerivedProtection(_guest.Address, 0x2000, ReadWrite, ReadWrite, ordinaryEnrollment: false));
        Assert.False(_space.TryApplyDerivedProtection(_guest.Address, 0x2000, GuestPageProtection.Read, ReadWrite, ordinaryEnrollment: false));
        Assert.Equal(
            new[]
            {
                (_guest.Address, 0x2000UL, ReadWrite, GuestPageProtection.Read, ProtectRequestFlags.Derived | ProtectRequestFlags.OrdinaryEnrollment),
                (_guest.Address, 0x2000UL, ReadWrite, ReadWrite, ProtectRequestFlags.Derived),
            },
            _protections.Requests);
        Assert.Equal(new ulong[] { 1 }, _protections.Waits);
    }

    [MacOSFact]
    public void WatchesOfTheGpuMemoryManagerKeepTheInterestSummary()
    {
        using var memory = new GuestGpuMemory(_space);
        memory.Register(_guest.Address, SharedGuestMemory.ViewBytes, ReadWrite);
        _protections.Requests.Clear();

        memory.Pages.AddWatch(_guest.Address, 0x1000, blockReads: false);
        memory.Pages.AddWatch(_guest.Address + 0x1000, 0x1000, blockReads: false);
        memory.Pages.AddWatch(_guest.Address + SharedGuestMemory.Chunk, 0x1000, blockReads: false);
        Assert.Equal(new[] { _guest.Address, _guest.Address + SharedGuestMemory.Chunk }, _summary.Set);
        // Each watch takes write access from the guest, and waits for the guest to apply it.
        Assert.Equal(3, _protections.Requests.Count(request => request.Access == GuestPageProtection.Read));
        Assert.Equal(3, _protections.Waits.Count);

        memory.Pages.RemoveWatch(_guest.Address, 0x1000, blockReads: false);
        Assert.Empty(_summary.Cleared);
        memory.Pages.RemoveWatch(_guest.Address + 0x1000, 0x1000, blockReads: false);
        memory.Pages.RemoveWatch(_guest.Address + SharedGuestMemory.Chunk, 0x1000, blockReads: false);
        Assert.Equal(new[] { _guest.Address + 0x1000, _guest.Address + SharedGuestMemory.Chunk }, _summary.Cleared);
        Assert.Equal(3, _protections.Waits.Count);
    }

    [MacOSFact]
    public void TheGpuSideNeverAllocatesOrMaps()
    {
        Assert.Throws<NotSupportedException>(() => _space.AllocateAt(0x1000, 0x1000));
        Assert.False(_space.TryAllocateGuestMemory(0x1000, 0x1000, out _));
        Assert.False(_space.TryMapBacked(_guest.Address, 0x1000, 0, ReadWrite, out _));
        Assert.False(_space.TryUnmapBacked(_guest.Address, 0x1000));
        Assert.Null(((IGuestBackedSpace)_space).SingleCopy);
    }

    private sealed class RecordingProtections : IProtectionRequests
    {
        private ulong _last;

        public List<(ulong Address, ulong Size, GuestPageProtection Guest, GuestPageProtection Access, ProtectRequestFlags Flags)> Requests { get; } = new();

        public List<ulong> Waits { get; } = new();

        public ulong LastRequested => _last;

        public ulong Request(ulong address, ulong size, GuestPageProtection guestAccess, GuestPageProtection access, ProtectRequestFlags flags)
        {
            Requests.Add((address, size, guestAccess, access, flags));
            return ++_last;
        }

        public bool WaitApplied(ulong sequence)
        {
            Waits.Add(sequence);
            return true;
        }
    }

    private sealed class RecordingSummary : IInterestSummary
    {
        public List<ulong> Set { get; } = new();

        public List<ulong> Cleared { get; } = new();

        public void SetGroup(ulong address) => Set.Add(address);

        public void ClearGroup(ulong address) => Cleared.Add(address);
    }
}
