// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.InteropServices;

namespace SharpEmu.GpuRemote;

/// <summary>Which process a <see cref="GpuRemoteChannel"/> view belongs to.</summary>
public enum GpuRemoteSide
{
    /// <summary>Process A: the emulator (guest CPU and HLE), the region's creator.</summary>
    Host,

    /// <summary>Process B: the native GPU host.</summary>
    Gpu,
}

/// <summary>Sizes of the parts of a region; the creator picks them, the header records them.</summary>
public sealed record GpuRemoteLayout
{
    /// <summary>Data bytes of the A → B ring (submissions, requests). Power of two.</summary>
    public int HostToGpuRingBytes { get; init; } = 1 << 20;

    /// <summary>Data bytes of the B → A ring (events, interrupts, input). Power of two.</summary>
    public int GpuToHostRingBytes { get; init; } = 256 << 10;

    public int SyncSlotCount { get; init; } = SyncCallTable.MaxSlots;

    public int SyncSlotPayloadBytes { get; init; } = 512;

    /// <summary>Slots of the B → A call table (B calls, A serves).</summary>
    public int ReverseSyncSlotCount { get; init; } = 32;

    public int ReverseSyncSlotPayloadBytes { get; init; } = 512;

    /// <summary>
    /// Bytes of the application block: shared memory the transport lays out but never reads,
    /// for state both processes keep in place (the split's page summary and acknowledgement
    /// counters). Rounded up to whole cache lines; 0 for none.
    /// </summary>
    public int ApplicationBytes { get; init; }
}

[StructLayout(LayoutKind.Explicit, Size = Size)]
internal struct RegionHeader
{
    public const int Size = 256;

    // "SEGPUSHM", little-endian.
    public const ulong ExpectedMagic = 0x4D48_5355_5047_4553;

    /// <summary>Written last by the creator (release), checked first by the attacher (acquire).</summary>
    [FieldOffset(0)]
    public ulong Magic;

    [FieldOffset(8)]
    public uint Version;

    [FieldOffset(12)]
    public uint HeaderSize;

    [FieldOffset(16)]
    public ulong TotalSize;

    [FieldOffset(24)]
    public ulong HostToGpuOffset;

    [FieldOffset(32)]
    public ulong GpuToHostOffset;

    [FieldOffset(40)]
    public ulong SyncOffset;

    [FieldOffset(48)]
    public int HostToGpuCapacity;

    [FieldOffset(52)]
    public int GpuToHostCapacity;

    [FieldOffset(56)]
    public int SyncSlotCount;

    [FieldOffset(60)]
    public int SyncSlotPayloadBytes;

    [FieldOffset(64)]
    public int HostProcessId;

    [FieldOffset(68)]
    public int GpuProcessId;

    [FieldOffset(72)]
    public ulong ReverseSyncOffset;

    [FieldOffset(80)]
    public int ReverseSyncSlotCount;

    [FieldOffset(84)]
    public int ReverseSyncSlotPayloadBytes;

    [FieldOffset(88)]
    public ulong ApplicationOffset;

    [FieldOffset(96)]
    public ulong ApplicationBytes;

    /// <summary>A <see cref="GpuRemoteChannel"/> peer state of process A.</summary>
    [FieldOffset(128)]
    public uint HostState;

    /// <summary>A <see cref="GpuRemoteChannel"/> peer state of process B.</summary>
    [FieldOffset(132)]
    public uint GpuState;
}

/// <summary>
/// One process's view of the shared region of the split: a header, the A → B ring, the B → A
/// ring, the two synchronous call tables (A → B and B → A) and an optional application block,
/// all in one <see cref="SharedMemoryRegion"/>.
/// </summary>
/// <remarks>
/// <para>Process A creates the region (<see cref="Create"/>) and passes its name to B, which
/// attaches (<see cref="Attach"/>). Each side sees the rings from its own end:
/// <see cref="Outbound"/> is the ring it writes, <see cref="Inbound"/> the one it reads; calls
/// go A → B through <see cref="SyncCalls"/> and B → A through <see cref="ReverseSyncCalls"/>.
/// Only the A → B table is signal-safe for its caller in practice: B's calls come from its
/// ordinary threads.</para>
///
/// <para>The header also carries each side's state (<see cref="PeerRunning"/>,
/// <see cref="PeerExited"/>). Every blocking wait in the transport checks the other side's
/// state, and <see cref="MarkPeerExited"/> - called by whoever watches the other process die -
/// sets it and wakes every waiter in this process, so a crash on one side never leaves the
/// other blocked forever.</para>
/// </remarks>
public sealed unsafe class GpuRemoteChannel : IDisposable
{
    public const uint PeerNotAttached = 0;
    public const uint PeerRunning = 1;
    public const uint PeerExited = 2;

    private const int BlockAlignment = 128;

    private readonly SharedMemoryRegion _region;
    private readonly RegionHeader* _header;
    // Orders the exit watch's MarkPeerExited against Dispose's unmap.
    private readonly Lock _gate = new();
    private int _disposed;

    private GpuRemoteChannel(SharedMemoryRegion region, GpuRemoteSide side)
    {
        _region = region;
        _header = (RegionHeader*)region.Pointer;
        Side = side;

        var start = region.Pointer;
        var hostToGpu = start + _header->HostToGpuOffset;
        var gpuToHost = start + _header->GpuToHostOffset;
        var peerState = side == GpuRemoteSide.Host ? &_header->GpuState : &_header->HostState;
        if (side == GpuRemoteSide.Host)
        {
            Outbound = new RingWriter(hostToGpu, _header->HostToGpuCapacity, peerState);
            Inbound = new RingReader(gpuToHost, _header->GpuToHostCapacity, peerState);
        }
        else
        {
            Outbound = new RingWriter(gpuToHost, _header->GpuToHostCapacity, peerState);
            Inbound = new RingReader(hostToGpu, _header->HostToGpuCapacity, peerState);
        }

        SyncCalls = new SyncCallTable(start + _header->SyncOffset, _header->SyncSlotCount, _header->SyncSlotPayloadBytes, peerState);
        ReverseSyncCalls = new SyncCallTable(
            start + _header->ReverseSyncOffset, _header->ReverseSyncSlotCount, _header->ReverseSyncSlotPayloadBytes, peerState);
    }

    public GpuRemoteSide Side { get; }

    /// <summary>The shared memory object's name, as passed to the GPU host.</summary>
    public string Name => _region.Name;

    public nuint Size => _region.Size;

    /// <summary>The ring this side writes (A: submissions and requests; B: events).</summary>
    public RingWriter Outbound { get; }

    /// <summary>The ring this side reads.</summary>
    public RingReader Inbound { get; }

    /// <summary>A → B synchronous calls; A calls, B serves.</summary>
    public SyncCallTable SyncCalls { get; }

    /// <summary>B → A synchronous calls; B calls, A serves.</summary>
    public SyncCallTable ReverseSyncCalls { get; }

    /// <summary>The application block (see <see cref="GpuRemoteLayout.ApplicationBytes"/>); null when it has none.</summary>
    public byte* Application => _header->ApplicationBytes == 0 ? null : _region.Pointer + _header->ApplicationOffset;

    public int ApplicationBytes => (int)_header->ApplicationBytes;

    /// <summary>The other side's state word, for primitives laid out in the application block.</summary>
    public uint* PeerStateAddress => PeerStateWord;

    public int HostProcessId => Volatile.Read(ref _header->HostProcessId);

    public int GpuProcessId => Volatile.Read(ref _header->GpuProcessId);

    /// <summary>The other side's state: <see cref="PeerNotAttached"/>, <see cref="PeerRunning"/> or <see cref="PeerExited"/>.</summary>
    public uint PeerState => Volatile.Read(ref *PeerStateWord);

    /// <summary>False once the other side has exited (cleanly or not).</summary>
    public bool IsPeerAlive => PeerState != PeerExited;

    private uint* PeerStateWord => Side == GpuRemoteSide.Host ? &_header->GpuState : &_header->HostState;

    private uint* OwnStateWord => Side == GpuRemoteSide.Host ? &_header->HostState : &_header->GpuState;

    /// <summary>Bytes a region with <paramref name="layout"/> occupies, before rounding to pages.</summary>
    public static int RegionSize(GpuRemoteLayout layout, out int hostToGpuOffset, out int gpuToHostOffset, out int syncOffset) =>
        RegionSize(layout, out hostToGpuOffset, out gpuToHostOffset, out syncOffset, out _, out _);

    /// <summary>
    /// <see cref="RegionSize(GpuRemoteLayout, out int, out int, out int)"/> with the B → A call
    /// table and the application block placed too.
    /// </summary>
    public static int RegionSize(
        GpuRemoteLayout layout, out int hostToGpuOffset, out int gpuToHostOffset, out int syncOffset, out int reverseSyncOffset,
        out int applicationOffset)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentOutOfRangeException.ThrowIfNegative(layout.ApplicationBytes);
        hostToGpuOffset = Align(RegionHeader.Size);
        gpuToHostOffset = Align(hostToGpuOffset + SpscRing.BlockSize(layout.HostToGpuRingBytes));
        syncOffset = Align(gpuToHostOffset + SpscRing.BlockSize(layout.GpuToHostRingBytes));
        reverseSyncOffset = Align(syncOffset + SyncCallTable.BlockSize(layout.SyncSlotCount, layout.SyncSlotPayloadBytes));
        applicationOffset = Align(reverseSyncOffset + SyncCallTable.BlockSize(layout.ReverseSyncSlotCount, layout.ReverseSyncSlotPayloadBytes));
        return Align(applicationOffset + layout.ApplicationBytes);
    }

    /// <summary>Process A: creates the region under <paramref name="name"/> and lays it out.</summary>
    public static GpuRemoteChannel Create(string name, GpuRemoteLayout? layout = null)
    {
        layout ??= new GpuRemoteLayout();
        var size = RegionSize(layout, out var hostToGpu, out var gpuToHost, out var sync, out var reverseSync, out var application);
        var region = SharedMemoryRegion.Create(name, (nuint)size);
        try
        {
            // The object starts zero-filled, which is the initial state of every ring and slot.
            var header = (RegionHeader*)region.Pointer;
            header->Version = GpuRemoteProtocol.Version;
            header->HeaderSize = RegionHeader.Size;
            header->TotalSize = region.Size;
            header->HostToGpuOffset = (ulong)hostToGpu;
            header->GpuToHostOffset = (ulong)gpuToHost;
            header->SyncOffset = (ulong)sync;
            header->HostToGpuCapacity = layout.HostToGpuRingBytes;
            header->GpuToHostCapacity = layout.GpuToHostRingBytes;
            header->SyncSlotCount = layout.SyncSlotCount;
            header->SyncSlotPayloadBytes = layout.SyncSlotPayloadBytes;
            header->ReverseSyncOffset = (ulong)reverseSync;
            header->ReverseSyncSlotCount = layout.ReverseSyncSlotCount;
            header->ReverseSyncSlotPayloadBytes = layout.ReverseSyncSlotPayloadBytes;
            header->ApplicationOffset = (ulong)application;
            header->ApplicationBytes = (ulong)Align(layout.ApplicationBytes);
            header->HostProcessId = Environment.ProcessId;
            header->HostState = PeerRunning;
            Volatile.Write(ref header->Magic, RegionHeader.ExpectedMagic);
            return new GpuRemoteChannel(region, GpuRemoteSide.Host);
        }
        catch
        {
            region.Dispose();
            throw;
        }
    }

    /// <summary>Process B: attaches to the region A created and marks itself running.</summary>
    public static GpuRemoteChannel Attach(string name)
    {
        var region = SharedMemoryRegion.Open(name);
        try
        {
            var header = (RegionHeader*)region.Pointer;
            if (region.Size < RegionHeader.Size || Volatile.Read(ref header->Magic) != RegionHeader.ExpectedMagic)
            {
                throw new InvalidDataException($"{name} is not a SharpEmu GPU region (bad magic).");
            }

            if (header->Version != GpuRemoteProtocol.Version)
            {
                throw new InvalidDataException(
                    $"{name} uses GPU protocol version {header->Version}; this process speaks {GpuRemoteProtocol.Version}.");
            }

            var layout = new GpuRemoteLayout
            {
                HostToGpuRingBytes = header->HostToGpuCapacity,
                GpuToHostRingBytes = header->GpuToHostCapacity,
                SyncSlotCount = header->SyncSlotCount,
                SyncSlotPayloadBytes = header->SyncSlotPayloadBytes,
                ReverseSyncSlotCount = header->ReverseSyncSlotCount,
                ReverseSyncSlotPayloadBytes = header->ReverseSyncSlotPayloadBytes,
                ApplicationBytes = (int)Math.Min(header->ApplicationBytes, int.MaxValue),
            };
            var size = RegionSize(layout, out var hostToGpu, out var gpuToHost, out var sync, out var reverseSync, out var application);
            if (header->HeaderSize != RegionHeader.Size || (ulong)size > region.Size || header->TotalSize != region.Size ||
                header->HostToGpuOffset != (ulong)hostToGpu || header->GpuToHostOffset != (ulong)gpuToHost || header->SyncOffset != (ulong)sync ||
                header->ReverseSyncOffset != (ulong)reverseSync || header->ApplicationOffset != (ulong)application)
            {
                throw new InvalidDataException($"{name} has an inconsistent layout.");
            }

            header->GpuProcessId = Environment.ProcessId;
            Volatile.Write(ref header->GpuState, PeerRunning);

            // Both sides have it mapped now, so the name has done its job. A unlinks it too
            // after the handshake; doing it here as well covers an A that died in between.
            _ = RemoteNative.ShmUnlink(name);
            return new GpuRemoteChannel(region, GpuRemoteSide.Gpu);
        }
        catch
        {
            region.Dispose();
            throw;
        }
    }

    /// <summary>Removes the region's name once both sides have it mapped (process A only).</summary>
    public void Unlink() => _region.Unlink();

    /// <summary>
    /// Records that the other process is gone and wakes every wait in this process, so each
    /// returns its "peer exited" result. Called by the process-exit watch.
    /// </summary>
    public void MarkPeerExited()
    {
        lock (_gate)
        {
            if (_disposed != 0)
            {
                return;
            }

            Volatile.Write(ref *PeerStateWord, PeerExited);
            WakeAll();
        }
    }

    /// <summary>Wakes every waiter on every doorbell and slot of the region, in both processes.</summary>
    public void WakeAll()
    {
        var start = _region.Pointer;
        foreach (var offset in stackalloc[] { _header->HostToGpuOffset, _header->GpuToHostOffset })
        {
            var control = (RingControl*)(start + offset);
            new Doorbell(&control->DataBell).WakeAll();
            new Doorbell(&control->SpaceBell).WakeAll();
        }

        SyncCalls.WakeAll();
        ReverseSyncCalls.WakeAll();
    }

    /// <summary>
    /// Marks this side exited, wakes the other side's waits, and unmaps the region. Every thread
    /// of this process must be done with the channel first: its rings and slots point into the
    /// mapping.
    /// </summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed != 0)
            {
                return;
            }

            _disposed = 1;
            Volatile.Write(ref *OwnStateWord, PeerExited);
            WakeAll();
            _region.Dispose();
        }
    }

    private static int Align(int value) => (value + BlockAlignment - 1) & ~(BlockAlignment - 1);
}
