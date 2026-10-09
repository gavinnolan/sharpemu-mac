// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
using System.Buffers.Binary;
using System.Runtime.ExceptionServices;
using SharpEmu.HLE;
using SharpEmu.HLE.GpuMemory;
using SharpEmu.Libs.Gpu.GpuCommands.Packets;

namespace SharpEmu.Libs.Gpu.GpuCommands;

/// <summary>A closed, owned packet operation. An arbitrary Action cannot claim its effects.</summary>
public sealed class CommandMemoryOperation
{
    public enum OperationKind { Write, Fill, Copy, Atomic, SemaphoreSignal, SemaphoreConsume }
    private readonly byte[]? payload;
    private readonly AtomicMemoryPacket atomic;
    private readonly MemorySemaphorePacket semaphore;
    private int execution;
    private CpuMappedPublication? retainedUnknownWrite;
    public const int MaximumOriginalWriteBytes = 4 * 1024 * 1024;
    // One existing ordinary FillBuffer chunk; larger/GDS/unaligned fills keep
    // their legacy transfer path and cannot claim this original transaction.
    public const int MaximumOriginalFillBytes = 64 * 1024;
    private CommandMemoryOperation(OperationKind kind, ulong address, ulong bytes, ulong value,
        ulong source = 0, bool destinationIsGds = false, bool sourceIsGds = false,
        byte[]? payload = null, AtomicMemoryPacket atomic = default, MemorySemaphorePacket semaphore = default,
        string? debugName = null)
    {
        Kind = kind; Address = address; ByteCount = bytes; Value = value; SourceAddress = source;
        DestinationIsGds = destinationIsGds; SourceIsGds = sourceIsGds;
        this.payload = payload; this.atomic = atomic; this.semaphore = semaphore;
        DebugName = debugName ?? kind.ToString();
    }
    public OperationKind Kind { get; }
    public ulong Address { get; }
    public ulong ByteCount { get; }
    public ulong Value { get; }
    public ulong SourceAddress { get; }
    public bool DestinationIsGds { get; }
    public bool SourceIsGds { get; }
    public string DebugName { get; }
    public ulong OwnedByteCount => (ulong)(payload?.LongLength ?? 0);
    public bool HasOriginalWritePayload => payload is { Length: > 0 } &&
        (Kind == OperationKind.Write && payload.Length <= MaximumOriginalWriteBytes ||
         Kind == OperationKind.Fill && !DestinationIsGds && !SourceIsGds &&
         (Address & 3) == 0 && (ByteCount & 3) == 0 && payload.Length <= MaximumOriginalFillBytes) &&
        OwnedByteCount == ByteCount && Address != 0 && Address <= ulong.MaxValue - ByteCount;
    public bool RetainsUncertainOriginalWrite => retainedUnknownWrite is not null;
    public ulong PriorValue { get; private set; }
    public bool ComparePassed { get; private set; }

    // Closed byte writes and bounded constant non-GDS fills own their complete
    // little-endian bytes before admission. The
    // caller cannot inspect or replace the owned bytes, or substitute a younger
    // mapping after provider preparation refuses the first original.
    public void ExecuteOriginalWrite(ICpuMappedPublicationSource source, CpuMappedCopyObservation original, Action requireOriginalOwner,
        bool originalHasWriteWatch = false)
    {
        ArgumentNullException.ThrowIfNull(requireOriginalOwner);
        if (!HasOriginalWritePayload)
            throw new NotSupportedException("This command memory operation has no exact original write transaction.");
        BeginExecution();
        CpuMappedPublication? ticket = null;
        Exception? primary = null;
        var commitInProgress = false;
        CpuMappedCopyObservation? appliedSuccessor = null;
        var applied = false;
        var watchedSource = originalHasWriteWatch ? source as ICpuMappedReadWatchSource : null;
        try
        {
            if (originalHasWriteWatch && watchedSource is null)
                throw new NotSupportedException("Command memory original watch has no exact release provider.");
            if (original.SourceAddress != Address || original.SourceByteCount != ByteCount ||
                original.FootprintAddress != Address || original.FootprintByteCount != ByteCount)
                throw new NotSupportedException("Command memory original write does not cover its entire owned payload.");
            var runs = new CpuMappedWriteRun[original.Segments.Count];
            for (var index = 0; index < runs.Length; index++)
            {
                var segment = original.Segments[index];
                runs[index] = new(segment.CoveredAddress,
                    payload!.AsMemory(checked((int)(segment.CoveredAddress - Address)), checked((int)segment.CoveredByteCount)));
            }
            if (source.TryPrepareMappedPublication(original, runs, out ticket) != CpuMappedPublicationPreparationResult.Prepared || ticket is null)
                throw new NotSupportedException("Command memory write could not reserve its ORIGINAL mapping and lifetime.");
            requireOriginalOwner();
            // Same pre-write notifications as the ordinary physical memory path.
            // The original mapping is excluded and its backing pinned already;
            // an unsupported notification/mapping effect can only refuse commit.
            GuestImageWriteTracker.NotifyManagedWrite(Address, ByteCount);
            GuestGpuMemoryHook.MarkCpuWrite(Address, ByteCount);
            requireOriginalOwner();
            commitInProgress = true;
            var result = ticket.TryCommit();
            commitInProgress = false;
            if (result != CpuMappedPublicationResult.Applied)
            {
                if (result == CpuMappedPublicationResult.PartialUnknown) retainedUnknownWrite = ticket;
                throw new NotSupportedException($"Command memory ORIGINAL write did not complete: {result}.");
            }
            applied = true;
            if (originalHasWriteWatch)
                appliedSuccessor = ticket.SuccessfulObservation ?? throw new NotSupportedException(
                    "Command memory watched write lost its SAME Applied successor observation.");
            requireOriginalOwner(); // Applied is not an acknowledgement across session/owner replacement.
            GuestImageWriteTracker.TraceManagedDirtyCompletion(Address, ByteCount);
            if (GuestWriteWatch.Armed) GuestWriteWatch.Check(Address, payload!);
        }
        catch (Exception error) { if (commitInProgress || applied && originalHasWriteWatch && appliedSuccessor is null) retainedUnknownWrite = ticket; primary = error; }
        finally
        {
            var ownedSuccessor = appliedSuccessor; appliedSuccessor = null;
            // Closed Write/Fill needs no continuing read entitlement. The provider
            // consumed its original watch and minted this exact own-write
            // successor; release that bookkeeping once after notifications.
            try
            {
                if (ownedSuccessor is not null) watchedSource!.ReleaseMappedCopyWriteWatch(ownedSuccessor);
            }
            catch (Exception cleanup)
            {
                retainedUnknownWrite = ticket;
                if (primary is null) primary = cleanup;
                else try { primary.Data["CommandMemorySuccessorWatchCleanup"] = cleanup; } catch { }
            }
            var owned = ticket; ticket = null;
            try { owned?.Dispose(); }
            catch (Exception cleanup)
            {
                retainedUnknownWrite = owned;
                if (primary is null) primary = cleanup;
                else try { primary.Data["CommandMemoryWriteCleanup"] = cleanup; } catch { }
            }
        }
        if (primary is not null) ExceptionDispatchInfo.Capture(primary).Throw();
    }

    public static CommandMemoryOperation Write(ulong address, ReadOnlySpan<byte> bytes, string debugName = "WRITE_BYTES")
    {
        var owned = bytes.ToArray();
        Span<byte> scalar = stackalloc byte[8]; scalar.Clear(); owned.AsSpan(0, Math.Min(8, owned.Length)).CopyTo(scalar);
        return new(OperationKind.Write, address, (ulong)owned.Length, BinaryPrimitives.ReadUInt64LittleEndian(scalar),
            payload: owned, debugName: debugName);
    }
    public static CommandMemoryOperation Fill(ulong address, ulong bytes, uint value, bool isGds)
    {
        byte[]? owned = null;
        if (!isGds && address != 0 && address <= ulong.MaxValue - bytes &&
            (address & 3) == 0 && (bytes & 3) == 0 && bytes is > 0 and <= MaximumOriginalFillBytes)
        {
            owned = new byte[checked((int)bytes)];
            for (var offset = 0; offset < owned.Length; offset += sizeof(uint))
                BinaryPrimitives.WriteUInt32LittleEndian(owned.AsSpan(offset, sizeof(uint)), value);
        }
        return new(OperationKind.Fill, address, bytes, value, destinationIsGds: isGds, payload: owned, debugName: "FILL");
    }
    public static CommandMemoryOperation Copy(ulong destination, ulong source, ulong bytes, bool destinationIsGds, bool sourceIsGds) =>
        new(OperationKind.Copy, destination, bytes, source, source, destinationIsGds, sourceIsGds, debugName: "COPY");
    public static CommandMemoryOperation Atomic(AtomicMemoryPacket packet) =>
        new(OperationKind.Atomic, packet.Address, (ulong)packet.ByteCount, packet.SourceData, atomic: packet, debugName: "ATOMIC");
    public static CommandMemoryOperation Semaphore(MemorySemaphorePacket packet) =>
        new(packet.IsSignal ? OperationKind.SemaphoreSignal : OperationKind.SemaphoreConsume,
            packet.Address, 8, packet.IsSignal && packet.WriteSignal ? 1UL : 0UL, semaphore: packet,
            debugName: packet.IsSignal ? "SEMAPHORE_SIGNAL" : "SEMAPHORE_CONSUME");

    // All effects come from the closed payload. Invalid/unbounded selectors
    // never become a positive mapping-only writer inventory entry.
    public bool TryGetGuestWriteRange(out ulong address, out ulong bytes)
    {
        address = Address; bytes = ByteCount;
        if (Kind == OperationKind.Atomic && !atomic.IsSupported ||
            (Kind is OperationKind.SemaphoreSignal or OperationKind.SemaphoreConsume) && (!semaphore.IsSupported || !semaphore.IsAligned) ||
            Kind == OperationKind.Fill && ((Address & 3) != 0 || (ByteCount & 3) != 0) ||
            Kind == OperationKind.Copy && DestinationIsGds && SourceIsGds)
            return false;
        if (DestinationIsGds)
        {
            if (Kind is not (OperationKind.Fill or OperationKind.Copy) || Address > ManagedCommandStreamHost.GdsBytes ||
                ByteCount > (ulong)ManagedCommandStreamHost.GdsBytes - Address) return false;
            address = bytes = 0; return true; // dedicated native GDS, never a guest-addressed image
        }
        if (SourceIsGds && (SourceAddress > ManagedCommandStreamHost.GdsBytes ||
            ByteCount > (ulong)ManagedCommandStreamHost.GdsBytes - SourceAddress)) return false;
        return bytes == 0 || address != 0 && address <= ulong.MaxValue - bytes;
    }

    /// <summary>Execute once. Transfers may use the host's existing queue-position implementation.</summary>
    public void Execute(ICommandStreamHost host, Action<CommandMemoryOperation>? transferAtQueuePosition = null)
    {
        ArgumentNullException.ThrowIfNull(host);
        BeginExecution();
        switch (Kind)
        {
            case OperationKind.Write:
                if (!host.Memory.TryWrite(Address, payload!))
                    throw new InvalidOperationException($"Metal command-stream memory write failed: address=0x{Address:X16} bytes={ByteCount}.");
                break;
            case OperationKind.Fill:
            case OperationKind.Copy:
                if (transferAtQueuePosition is not null) transferAtQueuePosition(this);
                else if (Kind == OperationKind.Fill) host.FillBuffer(Address, ByteCount, (uint)Value, DestinationIsGds);
                else host.CopyBuffer(Address, SourceAddress, ByteCount, DestinationIsGds, SourceIsGds);
                break;
            case OperationKind.Atomic:
                if (!atomic.TryApply(host.TryReadGuest, host.Memory, out var prior, out _, out var compared))
                    throw host.Fatal($"The atomic address cannot be accessed: address=0x{Address:X16} size={ByteCount}.");
                PriorValue = prior; ComparePassed = compared;
                break;
            case OperationKind.SemaphoreSignal:
                if (!semaphore.IsSupported || !MemorySemaphorePacket.TrySignal(host.TryReadGuest, host.Memory, Address, semaphore.WriteSignal, out _))
                    throw host.Fatal($"The semaphore cannot be signaled: address=0x{Address:X16}.");
                break;
            case OperationKind.SemaphoreConsume:
                if (!semaphore.IsSupported || !MemorySemaphorePacket.TryConsume(host.TryReadGuest, host.Memory, Address, out var previous))
                    throw host.Fatal($"The semaphore cannot be read: address=0x{Address:X16}.");
                PriorValue = previous;
                break;
            default: throw new NotSupportedException("Unknown command memory payload.");
        }
    }
    private void BeginExecution()
    {
        if (Interlocked.CompareExchange(ref execution, 1, 0) != 0)
            throw new InvalidOperationException("An owned command memory operation cannot execute twice.");
    }
}
