// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Gpu.Metal.V2;

/// <summary>
/// Which packet or host path asked the worker to wait for the GPU. Every wait costs
/// a frame's worth of latency on an idle GPU, so the counters are per reason: the
/// only way to remove one is to know who asks for it.
/// </summary>
internal enum MetalWaitReason
{
    /// <summary>A WAIT_REG_MEM, a predication packet or another explicit CP wait.</summary>
    CommandProcessor,

    /// <summary>A full GPU synchronization: the GDS paths and the shutdown drain.</summary>
    Synchronize,

    /// <summary>A host read of bytes a GPU-written image owns.</summary>
    ReadImage,

    /// <summary>A host read of bytes a recorded shader store or writeback covers.</summary>
    ReadRange,

    /// <summary>The image cache needed the GPU to finish before touching a texture.</summary>
    ImageCache,

    Other,
}

/// <summary>Which host path recorded a GPU write range.</summary>
internal enum MetalGpuWriteOrigin : byte
{
    StorageBuffer,
    WrittenBinding,

    /// <summary>A store through the device-address page table, at an evaluated range.</summary>
    DeviceAddress,
    Fill,
    Copy,
    ImageWriteback,
    Unbounded,
}

internal static class MetalWaitReasons
{
    public const int Count = (int)MetalWaitReason.Other + 1;

    public static readonly string[] Names =
        ["cp", "sync", "read_image", "read_range", "image_cache", "other"];
}

internal static class MetalGpuWriteOrigins
{
    public const int Count = (int)MetalGpuWriteOrigin.Unbounded + 1;

    public static readonly string[] Names =
        ["storage", "binding", "device_address", "fill", "copy", "writeback", "unbounded"];
}

/// <summary>
/// The guest byte ranges recorded work may still be writing, each with the
/// submission serial that will finish it. The twin of the Vulkan
/// <c>GuestBufferCache._gpuModifiedRanges</c>: a CPU or command-processor read only
/// has to wait when it overlaps a range whose serial has not completed, and then
/// only for that serial.
///
/// Guest memory is the GPU's memory on this backend, so the alternative — one flag
/// saying "some shader stored somewhere" — serializes the command processor against
/// the GPU on every label poll, indirect-argument read and DMA source read.
///
/// Only the GPU worker thread touches this.
/// </summary>
internal sealed class MetalGpuWriteRanges
{
    // Above this the oldest records are folded together; a folded record covers more
    // bytes than were written, which can only make a read wait when it need not.
    private const int MaxRecords = 512;

    private struct Record
    {
        public ulong Start;
        public ulong End;
        public ulong Serial;
        public MetalGpuWriteOrigin Origin;
    }

    private Record[] _records = new Record[64];
    private int _count;
    // A write whose destination range is not known: every read waits for it.
    private ulong _unboundedSerial;
    private ulong _retiredSerial;
    private long _recorded;
    private long _folds;

    public int Count => _count;

    public long Recorded => _recorded;

    public long Folds => _folds;

    public bool IsEmpty => _count == 0 && _unboundedSerial == 0;

    /// <summary>Recorded work in <paramref name="serial"/> stores into this range.</summary>
    public void Note(
        ulong address,
        ulong size,
        ulong serial,
        MetalGpuWriteOrigin origin = MetalGpuWriteOrigin.StorageBuffer)
    {
        if (size == 0 || serial == 0 || serial <= _retiredSerial || address > ulong.MaxValue - size)
        {
            return;
        }

        _recorded++;
        var end = address + size;
        // Draws and dispatches bind the same storage range over and over inside one
        // submission, so the newest record almost always absorbs the next one.
        for (var index = _count - 1; index >= 0 && index >= _count - 8; index--)
        {
            ref var candidate = ref _records[index];
            if (candidate.Serial != serial || candidate.End < address || candidate.Start > end)
            {
                continue;
            }

            candidate.Start = Math.Min(candidate.Start, address);
            candidate.End = Math.Max(candidate.End, end);
            return;
        }

        if (_count == _records.Length)
        {
            if (_records.Length < MaxRecords)
            {
                Array.Resize(ref _records, _records.Length * 2);
            }
            else
            {
                FoldOldest();
            }
        }

        _records[_count++] = new Record { Start = address, End = end, Serial = serial, Origin = origin };
        _recordsByOrigin[(int)origin]++;
    }

    private readonly long[] _recordsByOrigin = new long[MetalGpuWriteOrigins.Count];

    /// <summary>How many records each host path contributed since startup.</summary>
    public long RecordsFrom(MetalGpuWriteOrigin origin) => _recordsByOrigin[(int)origin];

    /// <summary>The record the last <see cref="PendingSerial"/> call matched.</summary>
    public (ulong Start, ulong End, ulong Serial, MetalGpuWriteOrigin Origin) LastMatch { get; private set; }

    /// <summary>
    /// Recorded work in <paramref name="serial"/> may store anywhere in guest memory.
    /// Every later read waits for it, so this is the shape of the old global flag and
    /// is only recorded when a write really has no known destination.
    /// </summary>
    public void NoteUnbounded(ulong serial)
    {
        if (serial > _unboundedSerial)
        {
            _unboundedSerial = serial;
            _recordsByOrigin[(int)MetalGpuWriteOrigin.Unbounded]++;
        }
    }

    /// <summary>
    /// The serial a read of this range must wait for, or zero when the bytes are not
    /// being written by work that has yet to complete. <paramref name="completed"/> is
    /// the serial the GPU has already signalled.
    /// </summary>
    public ulong PendingSerial(ulong address, ulong size, ulong completed)
    {
        Retire(completed);
        if (size == 0 || address > ulong.MaxValue - size)
        {
            return 0;
        }

        var end = address + size;
        var pending = _unboundedSerial > completed ? _unboundedSerial : 0;
        if (pending != 0)
        {
            LastMatch = (0, ulong.MaxValue, pending, MetalGpuWriteOrigin.Unbounded);
        }

        for (var index = 0; index < _count; index++)
        {
            ref var record = ref _records[index];
            if (record.Serial > pending && record.End > address && record.Start < end)
            {
                pending = record.Serial;
                LastMatch = (record.Start, record.End, record.Serial, record.Origin);
            }
        }

        return pending;
    }

    /// <summary>Drops every record the GPU has finished.</summary>
    public void Retire(ulong completed)
    {
        if (completed <= _retiredSerial)
        {
            return;
        }

        _retiredSerial = completed;
        if (_unboundedSerial <= completed)
        {
            _unboundedSerial = 0;
        }

        var kept = 0;
        for (var index = 0; index < _count; index++)
        {
            if (_records[index].Serial > completed)
            {
                _records[kept++] = _records[index];
            }
        }

        _count = kept;
    }

    /// <summary>Everything recorded has completed.</summary>
    public void Clear(ulong completed)
    {
        _count = 0;
        _unboundedSerial = 0;
        if (completed > _retiredSerial)
        {
            _retiredSerial = completed;
        }
    }

    // Folds the oldest half into one record per serial. Serials only grow, so the
    // oldest records are the ones a read is least likely to still be waiting for.
    private void FoldOldest()
    {
        _folds++;
        var half = _count / 2;
        var start = _records[0].Start;
        var end = _records[0].End;
        var serial = _records[0].Serial;
        for (var index = 1; index < half; index++)
        {
            start = Math.Min(start, _records[index].Start);
            end = Math.Max(end, _records[index].End);
            serial = Math.Max(serial, _records[index].Serial);
        }

        _records[0] = new Record
        {
            Start = start,
            End = end,
            Serial = serial,
            Origin = _records[0].Origin,
        };
        var kept = 1;
        for (var index = half; index < _count; index++)
        {
            _records[kept++] = _records[index];
        }

        _count = kept;
    }
}
