// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.InteropServices;
using SharpEmu.HLE;
using SharpEmu.Libs.Gpu.GpuCommands;

namespace SharpEmu.PerfAudit;

/// <summary>One native block seen as guest memory at <see cref="Base"/>; no tracking, no wrappers.</summary>
internal sealed unsafe class NativeGuestMemory : ICpuMemory, IDisposable
{
    public const ulong Base = 0x2_0000_0000;
    private readonly byte* _block;
    private readonly ulong _size;

    public NativeGuestMemory(ulong size)
    {
        _size = size;
        _block = (byte*)NativeMemory.AlignedAlloc((nuint)size, 16384);
        NativeMemory.Clear(_block, (nuint)size);
    }

    public byte* Raw(ulong address) => _block + (address - Base);
    public ulong Size => _size;
    public void Dispose() => NativeMemory.AlignedFree(_block);

    public bool TryRead(ulong address, Span<byte> destination)
    {
        if (address < Base || address - Base + (ulong)destination.Length > _size) return false;
        new ReadOnlySpan<byte>(_block + (address - Base), destination.Length).CopyTo(destination);
        return true;
    }

    public bool TryWrite(ulong address, ReadOnlySpan<byte> source)
    {
        if (address < Base || address - Base + (ulong)source.Length > _size) return false;
        source.CopyTo(new Span<byte>(_block + (address - Base), source.Length));
        return true;
    }

    public bool CanRead(ulong address, ulong size) => address >= Base && address - Base + size <= _size;

    public void WriteWords(ulong address, ReadOnlySpan<uint> words) => TryWrite(address, MemoryMarshal.AsBytes(words));
}

/// <summary>
/// A command-stream host that does nothing: every host call is a counter bump. Isolates the PM4
/// interpreter from the host. With <c>blocks</c> on it serves command blocks like the Metal host does.
/// </summary>
internal sealed class CountingCommandHost(NativeGuestMemory memory, bool blocks) : ICommandStreamHost
{
    public long Draws, Dispatches, PendingRuns, BeginTicks, Begins;
    public ICpuMemory Memory => memory;
    public bool SupportsCommandBlocks => blocks;
    public bool TryReadGuest(ulong address, Span<byte> destination) => memory.TryRead(address, destination);

    public bool TryReadCommandBlock(ulong address, Span<byte> block, out CommandReadToken token)
    {
        token = default;
        return memory.TryRead(address, block);
    }

    public bool IsCommandBlockCurrent(in CommandReadToken token) => true;
    public void RunPendingCommands() => PendingRuns++;
    public void BeginSubmission(int queueId, ulong submissionId, object? geometrySnapshots) { BeginTicks = System.Diagnostics.Stopwatch.GetTimestamp(); Begins++; }
    public void Flush() { }
    public void FlushAndWait() { }
    public void SynchronizeGpu() { }
    public void RunGarbageCollector() { }
    public void EmitGlobalBarrier() { }
    public void FillBuffer(ulong address, ulong size, uint value, bool isGds) { }
    public void CopyBuffer(ulong destination, ulong source, ulong size, bool destinationIsGds, bool sourceIsGds) { }
    public void ReadGds(Span<uint> destination, uint wordOffset, uint wordCount) { }
    public void RecordEndOfPipe(in EndOfPipeWrite write) { }
    public void TriggerInterrupt(int eventId, uint contextId) { }
    public bool HasFlipSlot() => true;
    public ulong PrepareFlip(int handle, int index, int flipMode, long flipArgument) => 1;
    public bool IsFlipDone(int handle, int index) => true;
    public void PrepareCpuFlip(int handle, int index, ulong requestId) { }
    public void DrawIndexed(ulong submitId, in DrawIndexedArguments arguments) => Draws++;
    public void DrawAuto(ulong submitId, in DrawAutoArguments arguments) => Draws++;
    public void DispatchDirect(ulong submitId, uint groupsX, uint groupsY, uint groupsZ, uint dispatchInitiator, ulong indirectArgumentsAddress = 0) => Dispatches++;
    public void OnQueueReset(int queueId) { }
    public Exception Fatal(string message) => new InvalidOperationException(message);
}
