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

    /// <summary>An end-of-pipe label the GPU writes where its packet sits in the stream.</summary>
    EndOfPipeLabel,
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
        ["storage", "binding", "device_address", "fill", "copy", "writeback", "eop_label", "unbounded"];
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
        // The order of the newest write the record covers, among every write recorded
        // here: which of two overlapping writes in one submission lands last.
        public ulong Sequence;
        public MetalGpuWriteOrigin Origin;
    }

    private Record[] _records = new Record[64];
    private int _count;
    // End-of-pipe labels are exact and never folded: they scatter 4-8 byte writes over
    // the whole command area, so a bounding range would block reads of unrelated memory.
    private Record[] _labels = new Record[64];
    private int _labelCount;

    // How many live labels lie on each 4 KiB page. Black Myth: Wukong records some 12,000
    // labels a second and keeps a couple of thousand live while the GPU runs behind, and
    // every pending-write query scanned all of them: ~8 us a query, 8% of the GPU worker.
    // A query whose pages hold no label skips the scan.
    private readonly Dictionary<ulong, int> _labelPages = [];
    private const int LabelPageShift = 12;

    // A query wider than this many pages scans the labels rather than looking each page up.
    private const ulong MaxLabelPageLookups = 64;
    // A write whose destination range is not known: every read waits for it.
    private ulong _unboundedSerial;
    private ulong _unboundedSequence;
    private ulong _retiredSerial;
    private ulong _sequence;
    private long _recorded;
    private long _folds;

    public int Count => _count + _labelCount;

    public long Recorded => _recorded;

    public long Folds => _folds;

    public bool IsEmpty => _count == 0 && _labelCount == 0 && _unboundedSerial == 0;

    /// <summary>
    /// Changes whenever a write is recorded or the records are cleared; retiring completed
    /// records leaves it alone, since that only ever makes a read wait for less.
    /// </summary>
    public ulong Version { get; private set; }

    /// <summary>
    /// Recorded work in <paramref name="serial"/> stores into this range. Returns the
    /// write's sequence (see <see cref="LatestSequence"/>), or zero when nothing was recorded.
    /// </summary>
    public ulong Note(
        ulong address,
        ulong size,
        ulong serial,
        MetalGpuWriteOrigin origin = MetalGpuWriteOrigin.StorageBuffer)
    {
        BeginMutation();
        try
        {
            if (size == 0 || serial == 0 || serial <= _retiredSerial || address > ulong.MaxValue - size)
            {
                return 0;
            }

            _recorded++;
            Version++;
            var sequence = ++_sequence;
            var end = address + size;
            NoteNewest(address, end, serial);
            if (origin == MetalGpuWriteOrigin.EndOfPipeLabel)
            {
                NoteLabel(address, end, serial, sequence);
                return sequence;
            }

            // Draws and dispatches bind the same storage range over and over inside one
            // submission, so the newest record almost always absorbs the next one.
            for (var index = _count - 1; index >= 0 && index >= _count - 8; index--)
            {
                ref var candidate = ref _records[index];
                if (candidate.Serial != serial || candidate.End < address || candidate.Start > end)
                {
                    continue;
                }

                if (address < candidate.Start || end > candidate.End)
                {
                    CountRecordBuckets(candidate.Start, candidate.End, -1);
                    candidate.Start = Math.Min(candidate.Start, address);
                    candidate.End = Math.Max(candidate.End, end);
                    CountRecordBuckets(candidate.Start, candidate.End, 1);
                }

                candidate.Sequence = sequence;
                return sequence;
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

            _records[_count++] = new Record { Start = address, End = end, Serial = serial, Sequence = sequence, Origin = origin };
            CountRecordBuckets(address, end, 1);
            _recordsByOrigin[(int)origin]++;
            if (address < _boundsStart)
            {
                _boundsStart = address;
            }

            if (end > _boundsEnd)
            {
                _boundsEnd = end;
            }

            return sequence;
        }
        finally
        {
            EndMutation();
        }
    }

    // The ranges written with the newest serial noted, exact: never folded and never widened
    // past their union. A guest-view bind asks whether the command buffer being recorded wrote
    // its bytes (WrittenSince), and walking every record for that cost Black Myth: Wukong's
    // worker a scan of some 500 records per storage binding. Past MaxNewestRanges a query
    // walks the records instead.
    private (ulong Start, ulong End)[] _newest = new (ulong, ulong)[16];
    private int _newestCount;
    private ulong _newestSerial;
    private bool _newestOverflow;
    private const int MaxNewestRanges = 256;

    private void NoteNewest(ulong start, ulong end, ulong serial)
    {
        if (serial < _newestSerial)
        {
            return;
        }

        if (serial > _newestSerial)
        {
            _newestSerial = serial;
            _newestCount = 0;
            _newestOverflow = false;
        }

        for (var index = _newestCount - 1; index >= 0 && index >= _newestCount - 8; index--)
        {
            ref var range = ref _newest[index];
            if (range.End < start || range.Start > end)
            {
                continue;
            }

            range.Start = Math.Min(range.Start, start);
            range.End = Math.Max(range.End, end);
            return;
        }

        if (_newestCount == MaxNewestRanges)
        {
            _newestOverflow = true;
            return;
        }

        if (_newestCount == _newest.Length)
        {
            Array.Resize(ref _newest, _newest.Length * 2);
        }

        _newest[_newestCount++] = (start, end);
    }

    /// <summary>
    /// Whether work recorded with <paramref name="serial"/> or a later one stores into this range:
    /// <c>PendingSerial(address, size, completed) &gt;= serial</c> for a serial that has not
    /// completed, without walking every record when <paramref name="serial"/> is the newest
    /// noted. Retires nothing.
    /// </summary>
    public bool WrittenSince(ulong address, ulong size, ulong serial)
    {
        if (size == 0 || serial == 0 || address > ulong.MaxValue - size)
        {
            return false;
        }

        if (_unboundedSerial >= serial)
        {
            return true;
        }

        if (serial > _newestSerial)
        {
            return false;
        }

        var end = address + size;
        if (serial == _newestSerial && !_newestOverflow)
        {
            for (var index = 0; index < _newestCount; index++)
            {
                ref var range = ref _newest[index];
                if (range.End > address && range.Start < end)
                {
                    return true;
                }
            }

            return false;
        }

        var records = RecordsMayCover(address, end) ? _count : 0;
        for (var index = 0; index < records; index++)
        {
            ref var record = ref _records[index];
            if (record.Serial >= serial && record.End > address && record.Start < end)
            {
                return true;
            }
        }

        var labels = LabelPagesMayCover(address, end) ? _labelCount : 0;
        for (var index = 0; index < labels; index++)
        {
            ref var label = ref _labels[index];
            if (label.Serial >= serial && label.End > address && label.Start < end)
            {
                return true;
            }
        }

        return false;
    }

    private void NoteLabel(ulong address, ulong end, ulong serial, ulong sequence)
    {
        _recordsByOrigin[(int)MetalGpuWriteOrigin.EndOfPipeLabel]++;
        // The same label is rewritten by consecutive submissions; keep it exact and
        // carry the newest serial.
        for (var index = _labelCount - 1; index >= 0 && index >= _labelCount - 8; index--)
        {
            ref var candidate = ref _labels[index];
            if (candidate.Start == address && candidate.End == end)
            {
                candidate.Serial = Math.Max(candidate.Serial, serial);
                candidate.Sequence = sequence;
                return;
            }
        }

        if (_labelCount == _labels.Length)
        {
            Array.Resize(ref _labels, _labels.Length * 2);
        }

        _labels[_labelCount++] = new Record
        {
            Start = address,
            End = end,
            Serial = serial,
            Sequence = sequence,
            Origin = MetalGpuWriteOrigin.EndOfPipeLabel,
        };
        CountLabelPages(address, end, 1);
        if (address < _boundsStart)
        {
            _boundsStart = address;
        }

        if (end > _boundsEnd)
        {
            _boundsEnd = end;
        }
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
        BeginMutation();
        try
        {
            Version++;
            if (serial != 0 && serial > _retiredSerial && serial >= _unboundedSerial)
            {
                _unboundedSequence = ++_sequence;
            }

            if (serial > _unboundedSerial)
            {
                _unboundedSerial = serial;
                _recordsByOrigin[(int)MetalGpuWriteOrigin.Unbounded]++;
            }
        }
        finally
        {
            EndMutation();
        }
    }

    /// <summary>
    /// The serial a read of this range must wait for, or zero when the bytes are not
    /// being written by work that has yet to complete. <paramref name="completed"/> is
    /// the serial the GPU has already signalled.
    /// </summary>
    // The span every live record lies inside. A command-stream read is almost never
    // anywhere near a range a shader wrote, and answering those in two comparisons
    // keeps the GPU-dirty test off the per-packet path: the full test reads the
    // shared event's value, which is a message send.
    private ulong _boundsStart = ulong.MaxValue;
    private ulong _boundsEnd;

    /// <summary>
    /// False when no recorded write can possibly cover these bytes. Cheap enough to
    /// call for every packet the command stream reads, and it touches nothing.
    /// </summary>
    // Odd while the worker changes the records: a reader on another thread (a resolution ahead of the
    // worker) scans them only between two equal even values, so it never trusts a torn view.
    private int _mutation;

    private void BeginMutation() => Volatile.Write(ref _mutation, _mutation + 1);

    private void EndMutation() => Volatile.Write(ref _mutation, _mutation + 1);

    /// <summary>
    /// <see cref="PendingSerial"/> for a thread other than the worker: nothing is retired, and false
    /// when the worker changed the records meanwhile, so the caller treats the bytes as pending.
    /// </summary>
    public bool TryPendingSerialSnapshot(ulong address, ulong size, ulong completed, out ulong pending)
    {
        pending = 0;
        var before = Volatile.Read(ref _mutation);
        if ((before & 1) != 0 || size == 0 || address > ulong.MaxValue - size)
        {
            return size == 0 && (before & 1) == 0;
        }

        try
        {
            var end = address + size;
            var found = _unboundedSerial > completed ? _unboundedSerial : 0;
            var records = _records;
            var count = Math.Min(_count, records.Length);
            for (var index = 0; index < count; index++)
            {
                var record = records[index];
                if (record.Serial > completed && record.Serial > found && record.End > address && record.Start < end)
                {
                    found = record.Serial;
                }
            }

            var labels = _labels;
            var labelCount = Math.Min(_labelCount, labels.Length);
            for (var index = 0; index < labelCount; index++)
            {
                var label = labels[index];
                if (label.Serial > completed && label.Serial > found && label.End > address && label.Start < end)
                {
                    found = label.Serial;
                }
            }

            pending = found;
        }
        catch (IndexOutOfRangeException)
        {
            return false;
        }

        Interlocked.MemoryBarrier();
        return Volatile.Read(ref _mutation) == before;
    }

    public bool MayCover(ulong address, ulong size) =>
        _unboundedSerial != 0 ||
        ((_count != 0 || _labelCount != 0) && size != 0 && address < _boundsEnd && _boundsStart < address + size);

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

        var records = RecordsMayCover(address, end) ? _count : 0;

        for (var index = 0; index < records; index++)
        {
            ref var record = ref _records[index];
            if (record.Serial > pending && record.End > address && record.Start < end)
            {
                pending = record.Serial;
                LastMatch = (record.Start, record.End, record.Serial, record.Origin);
            }
        }

        var labels = LabelPagesMayCover(address, end) ? _labelCount : 0;
        for (var index = 0; index < labels; index++)
        {
            ref var label = ref _labels[index];
            if (label.Serial > pending && label.End > address && label.Start < end)
            {
                pending = label.Serial;
                LastMatch = (label.Start, label.End, label.Serial, label.Origin);
            }
        }

        return pending;
    }

    /// <summary>
    /// <see cref="PendingSerial"/> for each of <c>pending.Length</c> adjacent slices of
    /// <paramref name="sliceSize"/> bytes from <paramref name="address"/>, as whether it is
    /// non-zero, in one pass over the records: a slice is marked when it would wait. True
    /// when any is. Slices already marked stay marked.
    /// </summary>
    public bool MarkPending(ulong address, ulong sliceSize, ulong completed, Span<bool> pending)
    {
        Retire(completed);
        var count = (ulong)pending.Length;
        if (sliceSize == 0 || count == 0 || sliceSize > (ulong.MaxValue - address) / count)
        {
            return false;
        }

        if (_unboundedSerial > completed)
        {
            pending.Fill(true);
            return true;
        }

        // Every live record is past the retired serial, so any overlapping one makes a slice wait.
        var end = address + sliceSize * count;
        var any = false;
        var records = RecordsMayCover(address, end) ? _count : 0;
        for (var index = 0; index < records; index++)
        {
            ref var record = ref _records[index];
            if (record.End > address && record.Start < end)
            {
                MarkSlices(pending, address, sliceSize, record.Start, record.End);
                any = true;
            }
        }

        var labels = LabelPagesMayCover(address, end) ? _labelCount : 0;
        for (var index = 0; index < labels; index++)
        {
            ref var label = ref _labels[index];
            if (label.End > address && label.Start < end)
            {
                MarkSlices(pending, address, sliceSize, label.Start, label.End);
                any = true;
            }
        }

        return any;
    }

    // Marks the slices [start, end) overlaps; it overlaps at least one.
    private static void MarkSlices(Span<bool> pending, ulong address, ulong sliceSize, ulong start, ulong end)
    {
        var first = start <= address ? 0 : (start - address) / sliceSize;
        var last = Math.Min((end - address - 1) / sliceSize, (ulong)pending.Length - 1);
        pending[(int)first..((int)last + 1)].Fill(true);
    }

    /// <summary>
    /// The sequence of the newest recorded write overlapping this range, or zero when no
    /// live record does. Every write gets a larger sequence than the ones recorded before
    /// it, and a merged or folded record carries the largest of its parts, so a write is
    /// the last to land over a range only when its sequence is this value. Call after
    /// <see cref="PendingSerial"/>, which retires what has completed.
    /// </summary>
    public ulong LatestSequence(ulong address, ulong size)
    {
        if (size == 0 || address > ulong.MaxValue - size)
        {
            return 0;
        }

        var end = address + size;
        var latest = _unboundedSerial != 0 ? _unboundedSequence : 0;
        var records = RecordsMayCover(address, end) ? _count : 0;
        for (var index = 0; index < records; index++)
        {
            ref var record = ref _records[index];
            if (record.Sequence > latest && record.End > address && record.Start < end)
            {
                latest = record.Sequence;
            }
        }

        var labels = LabelPagesMayCover(address, end) ? _labelCount : 0;
        for (var index = 0; index < labels; index++)
        {
            ref var label = ref _labels[index];
            if (label.Sequence > latest && label.End > address && label.Start < end)
            {
                latest = label.Sequence;
            }
        }

        return latest;
    }

    // How many records reach into each 2 MiB bucket, for records spanning at most MaxRecordBuckets
    // of them; the wider ones are listed by their extents. A query whose buckets hold none and that
    // no wide record reaches skips the walk of every record: the command processor reads its
    // register tables, labels and operands through here (Grand Theft Auto V: ~400,000 queries a
    // second over some 200 records each, nearly all finding nothing), and Black Myth: Wukong keeps
    // a 128 MiB written range live nearly all the time. Kept by the worker alone, like the records.
    private readonly Dictionary<ulong, int> _recordBuckets = [];
    private readonly List<(ulong Start, ulong End)> _wideRecords = [];
    private const int RecordBucketShift = 21;
    private const ulong MaxRecordBuckets = 32;

    // A query reaching more buckets than this walks the records rather than looking each one up.
    private const ulong MaxRecordBucketLookups = 16;

    private void CountRecordBuckets(ulong start, ulong end, int delta)
    {
        var first = start >> RecordBucketShift;
        var last = (end - 1) >> RecordBucketShift;
        if (last - first >= MaxRecordBuckets)
        {
            if (delta > 0)
            {
                _wideRecords.Add((start, end));
            }
            else
            {
                _wideRecords.Remove((start, end));
            }

            return;
        }

        for (var bucket = first; bucket <= last; bucket++)
        {
            ref var count = ref System.Runtime.InteropServices.CollectionsMarshal.GetValueRefOrAddDefault(_recordBuckets, bucket, out _);
            count += delta;
            if (count == 0)
            {
                _recordBuckets.Remove(bucket);
            }
        }
    }

    // Whether a live record may reach into [address, end): false only when none can.
    private bool RecordsMayCover(ulong address, ulong end)
    {
        if (_count == 0)
        {
            return false;
        }

        foreach (var (start, wideEnd) in _wideRecords)
        {
            if (wideEnd > address && start < end)
            {
                return true;
            }
        }

        var first = address >> RecordBucketShift;
        var last = (end - 1) >> RecordBucketShift;
        if (last - first >= MaxRecordBucketLookups)
        {
            return true;
        }

        for (var bucket = first; bucket <= last; bucket++)
        {
            if (_recordBuckets.ContainsKey(bucket))
            {
                return true;
            }
        }

        return false;
    }

    private void CountLabelPages(ulong start, ulong end, int delta)
    {
        for (var page = start >> LabelPageShift; page <= (end - 1) >> LabelPageShift; page++)
        {
            var count = _labelPages.GetValueOrDefault(page) + delta;
            if (count > 0)
            {
                _labelPages[page] = count;
            }
            else
            {
                _labelPages.Remove(page);
            }
        }
    }

    // Whether a live label may lie in [address, end): a page lookup per page for a narrow
    // query, true (scan them) for a wide one.
    private bool LabelPagesMayCover(ulong address, ulong end)
    {
        if (_labelCount == 0)
        {
            return false;
        }

        var first = address >> LabelPageShift;
        var last = (end - 1) >> LabelPageShift;
        if (last - first >= MaxLabelPageLookups)
        {
            return true;
        }

        for (var page = first; page <= last; page++)
        {
            if (_labelPages.ContainsKey(page))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Drops every record the GPU has finished.</summary>
    public void Retire(ulong completed)
    {
        // Most calls find nothing new to retire, and change nothing a reader on another thread sees.
        if (completed <= _retiredSerial)
        {
            return;
        }

        BeginMutation();
        try
        {
            _retiredSerial = completed;
            if (_unboundedSerial <= completed)
            {
                _unboundedSerial = 0;
            }

            var kept = 0;
            var start = ulong.MaxValue;
            var end = 0UL;
            for (var index = 0; index < _count; index++)
            {
                if (_records[index].Serial <= completed)
                {
                    CountRecordBuckets(_records[index].Start, _records[index].End, -1);
                    continue;
                }

                ref var record = ref _records[index];
                if (record.Start < start)
                {
                    start = record.Start;
                }

                if (record.End > end)
                {
                    end = record.End;
                }

                _records[kept++] = record;
            }

            _count = kept;
            kept = 0;
            for (var index = 0; index < _labelCount; index++)
            {
                ref var label = ref _labels[index];
                if (label.Serial <= completed)
                {
                    CountLabelPages(label.Start, label.End, -1);
                    continue;
                }

                start = Math.Min(start, label.Start);
                end = Math.Max(end, label.End);
                _labels[kept++] = label;
            }

            _labelCount = kept;
            _boundsStart = start;
            _boundsEnd = end;
        }
        finally
        {
            EndMutation();
        }
    }

    /// <summary>Everything recorded has completed.</summary>
    public void Clear(ulong completed)
    {
        BeginMutation();
        try
        {
            Version++;
            _count = 0;
            _recordBuckets.Clear();
            _wideRecords.Clear();
            _labelCount = 0;
            _newestCount = 0;
            _newestOverflow = false;
            _labelPages.Clear();
            _unboundedSerial = 0;
            _boundsStart = ulong.MaxValue;
            _boundsEnd = 0;
            if (completed > _retiredSerial)
            {
                _retiredSerial = completed;
            }
        }
        finally
        {
            EndMutation();
        }
    }

    // Folds the oldest half of the records into at most a quarter as many, by address.
    // Only the closest neighbours merge (smallest gaps first), so a folded record never
    // spans unrelated memory when anything tighter is available. A merged record keeps
    // the NEWEST serial of its parts: a read overlapping any part waits at least as long
    // as it did before, which can only over-wait, never miss a write.
    private void FoldOldest()
    {
        _folds++;
        var half = _count / 2;
        var old = _records.AsSpan(0, half);
        old.Sort(static (a, b) => a.Start.CompareTo(b.Start));

        var target = Math.Max(1, half / 2);
        var merges = half - target;
        var gaps = new ulong[half - 1];
        for (var index = 1; index < half; index++)
        {
            // Overlapping neighbours have no gap; a record nested in an earlier, longer
            // one is handled by the running end below.
            gaps[index - 1] = old[index].Start > old[index - 1].End ? old[index].Start - old[index - 1].End : 0;
        }

        var sorted = (ulong[])gaps.Clone();
        Array.Sort(sorted);
        var threshold = merges <= 0 ? 0 : sorted[Math.Min(merges, sorted.Length) - 1];

        var folded = new List<Record>(target + 8) { old[0] };
        for (var index = 1; index < half; index++)
        {
            var current = old[index];
            var last = folded[^1];
            if (current.Start <= last.End || gaps[index - 1] <= threshold)
            {
                if (current.Serial > last.Serial)
                {
                    last.Serial = current.Serial;
                    last.Origin = current.Origin;
                }

                last.Sequence = Math.Max(last.Sequence, current.Sequence);

                last.End = Math.Max(last.End, current.End);
                folded[^1] = last;
            }
            else
            {
                folded.Add(current);
            }
        }

        var tail = _count - half;
        var kept = folded.Count;
        Array.Copy(_records, half, _records, kept, tail);
        for (var index = 0; index < kept; index++)
        {
            _records[index] = folded[index];
        }

        _count = kept + tail;
        _recordBuckets.Clear();
        _wideRecords.Clear();
        for (var index = 0; index < _count; index++)
        {
            CountRecordBuckets(_records[index].Start, _records[index].End, 1);
        }
    }
}
