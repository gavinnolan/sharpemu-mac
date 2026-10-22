// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.Scheduling;

namespace SharpEmu.Gpu.Metal.V2;

/// <summary>
/// The guest reads of program resolution answered on a thread that resolves draws ahead of the
/// worker (ParallelProgramPrefetch through IConcurrentResolveHost), as the Vulkan presenter does.
/// That thread never synchronizes GPU-owned memory: a word the GPU owns, or bytes a pending
/// command-processor or label write still covers, abandon the resolution, which the worker then
/// does itself. Every ownership answer it relied on is recorded and checked again when the
/// worker takes the result (IsConcurrentResolveStillCurrent).
///
/// Work in progress, the first step of running the command stream on its own thread: nothing
/// opens a concurrent resolution yet, so every read below takes its single-thread path. Next,
/// the host needs a command thread feeding the translation's IDeferredWorkSink (the Vulkan
/// presenter's CommandThread), and to implement IConcurrentResolveHost with these methods.
/// </summary>
internal sealed partial class MetalRenderHost
{
    internal enum ConcurrentQuery : byte
    {
        Word,
        CleanWord,
        Resident,
    }

    internal sealed class ConcurrentAssumptions
    {
        public readonly List<(ConcurrentQuery Query, ulong Address, ulong Size, bool Owned)> Answers = new();

        public void Record(ConcurrentQuery query, ulong address, ulong size, bool owned)
        {
            // The last few answers repeat often (a resource table read word by word).
            for (var index = Answers.Count - 1; index >= Math.Max(0, Answers.Count - 8); index--)
            {
                var previous = Answers[index];
                if (previous.Query == query && previous.Owned == owned && previous.Address == address && previous.Size == size)
                {
                    return;
                }
            }

            Answers.Add((query, address, size, owned));
        }
    }

    // Set only on the resolving thread, around one resolution.
    [ThreadStatic]
    private static ConcurrentAssumptions? _concurrentAssumptions;

    internal void BeginConcurrentResolve() => _concurrentAssumptions = new ConcurrentAssumptions();

    internal object EndConcurrentResolve()
    {
        var assumptions = _concurrentAssumptions ?? throw new InvalidOperationException("No concurrent resolution is open.");
        _concurrentAssumptions = null;
        return assumptions;
    }

    // On the worker: the answers a resolution relied on still hold.
    internal bool IsConcurrentResolveStillCurrent(object assumptionsObject)
    {
        foreach (var (_, address, size, owned) in ((ConcurrentAssumptions)assumptionsObject).Answers)
        {
            if (IsOwnedForConcurrentRead(address, size) != owned)
            {
                return false;
            }
        }

        return true;
    }

    // What the single-thread readers would synchronize or overlay for these bytes: a GPU-modified
    // image over them, a GPU buffer write still pending there, or a command-processor or label
    // write that has yet to land.
    private bool IsOwnedForConcurrentRead(ulong address, ulong size) =>
        _imageCache.HasGpuModifiedImageBytes(address, size) ||
        // Plain fields only, so it cannot fail mid-update; a covered range counts as owned even
        // when its write has completed, and an answer read while the worker records a write is
        // caught when the worker checks it again.
        _gpuWrites.MayCover(address, size) ||
        (_cpOverlay.Count != 0 &&
            _cpOverlay.Coverage(address, size, _completedSerialReader ??= () => CompletedSerial) != MetalOverlayCoverage.None) ||
        (_labelOverlay.Count != 0 &&
            _labelOverlay.Coverage(address, size, _completedSerialReader ??= () => CompletedSerial) != MetalOverlayCoverage.None);

    private bool TryReadGuestWordConcurrent(ulong address, out uint word, ConcurrentAssumptions assumptions)
    {
        word = 0;
        var owned = IsOwnedForConcurrentRead(address, sizeof(uint));
        assumptions.Record(ConcurrentQuery.Word, address, sizeof(uint), owned);
        if (owned)
        {
            throw new GpuFatal.AbandonedWorkException($"The resolution reads GPU-owned memory: address=0x{address:X16}.");
        }

        Span<byte> bytes = stackalloc byte[sizeof(uint)];
        if (!_backing.TryReadBacking(address, bytes))
        {
            return false;
        }

        word = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(bytes);
        return true;
    }

    // The clean and resident readers refuse owned bytes on the worker too, so off it they refuse
    // them the same way, and record the answer.
    private bool TryReadResidentGuestBytesConcurrent(
        ulong address, Span<byte> destination, ConcurrentQuery query, ConcurrentAssumptions assumptions)
    {
        var owned = IsOwnedForConcurrentRead(address, (ulong)destination.Length);
        assumptions.Record(query, address, (ulong)destination.Length, owned);
        return !owned && _backing.TryReadBacking(address, destination);
    }
}
