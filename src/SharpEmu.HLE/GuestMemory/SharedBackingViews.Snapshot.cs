// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.HLE.GuestMemory;

public sealed unsafe partial class SharedBackingViews
{
    // The published copy of _views that single-view accesses search without the lock, in chunks of
    // at most ChunkRecords records ordered by address. A change republishes the chunks whose address
    // span it touched and the small index over them. Copying every record each time made one
    // large-object array per guest map or unmap: Grand Theft Auto V's streaming made some 100 MB of
    // them a second, and the gen2 collections they brought paused every thread, the GPU worker too.
    internal sealed class ViewSnapshot
    {
        // 32 KiB of records: well under the large object threshold.
        public const int ChunkRecords = 1024;

        public static readonly ViewSnapshot Empty = new([], [], 0);

        // Each chunk's first record address, ascending: a chunk holds the records from its start up
        // to the next chunk's (the first from address zero, the last to the top). No chunk is empty.
        private readonly ulong[] _starts;
        private readonly ViewRecord[][] _chunks;

        private ViewSnapshot(ulong[] starts, ViewRecord[][] chunks, int count)
        {
            _starts = starts;
            _chunks = chunks;
            Count = count;
        }

        public int Count { get; }

        public int ChunkCount => _chunks.Length;

        // The same records as a new snapshot: a publication always yields a new object, which is
        // how a reservation tells that the mapping may have moved (AliasSnapshot).
        public ViewSnapshot Republished() => new(_starts, _chunks, Count);

        public ref readonly ViewRecord At(int chunk, int index) => ref _chunks[chunk][index];

        // The last record whose address is at or below this one.
        public bool TryFind(ulong address, out int chunk, out int index)
        {
            chunk = LastStartAtOrBelow(address);
            if (chunk < 0)
            {
                index = -1;
                return false;
            }

            var records = _chunks[chunk];
            var low = 0;
            var high = records.Length - 1;
            index = 0;
            while (low <= high)
            {
                var middle = low + ((high - low) >> 1);
                if (records[middle].Address <= address)
                {
                    index = middle;
                    low = middle + 1;
                }
                else
                {
                    high = middle - 1;
                }
            }

            return true;
        }

        // The record after this one, in address order; false past the last.
        public bool TryNext(ref int chunk, ref int index)
        {
            if (index + 1 < _chunks[chunk].Length)
            {
                index++;
                return true;
            }

            if (chunk + 1 >= _chunks.Length)
            {
                return false;
            }

            chunk++;
            index = 0;
            return true;
        }

        private int LastStartAtOrBelow(ulong address)
        {
            var low = 0;
            var high = _starts.Length - 1;
            var found = -1;
            while (low <= high)
            {
                var middle = low + ((high - low) >> 1);
                if (_starts[middle] <= address)
                {
                    found = middle;
                    low = middle + 1;
                }
                else
                {
                    high = middle - 1;
                }
            }

            return found;
        }

        // Every record of the list, in balanced chunks.
        public static ViewSnapshot Build(SortedList<ulong, ViewRecord> views)
        {
            if (views.Count == 0)
            {
                return new ViewSnapshot([], [], 0);
            }

            var starts = new List<ulong>();
            var chunks = new List<ViewRecord[]>();
            AppendChunks(views, 0, views.Count, starts, chunks);
            return new ViewSnapshot([.. starts], [.. chunks], views.Count);
        }

        // This snapshot with the records of the list whose addresses lie in [low, high] taken again:
        // the chunks whose spans reach that range are rebuilt from the list, the others are shared.
        // The list must differ from what this snapshot holds only inside the range.
        public ViewSnapshot Rebuilt(SortedList<ulong, ViewRecord> views, ulong low, ulong high)
        {
            if (_chunks.Length == 0 || views.Count == 0 || _chunks.Length > 2 * (views.Count / ChunkRecords) + 8)
            {
                return Build(views);
            }

            var first = Math.Max(LastStartAtOrBelow(low), 0);
            var last = Math.Max(LastStartAtOrBelow(high), first);
            var bounded = last + 1 < _chunks.Length;
            var end = bounded ? _starts[last + 1] : 0;
            var begin = first == 0 ? 0 : LowerBound(views.Keys, _starts[first]);
            var stop = bounded ? LowerBound(views.Keys, end) : views.Count;

            var starts = new List<ulong>(_starts.Length + 2);
            var chunks = new List<ViewRecord[]>(_chunks.Length + 2);
            var count = 0;
            for (var chunk = 0; chunk < first; chunk++)
            {
                starts.Add(_starts[chunk]);
                chunks.Add(_chunks[chunk]);
                count += _chunks[chunk].Length;
            }

            AppendChunks(views, begin, stop, starts, chunks);
            count += stop - begin;
            for (var chunk = last + 1; chunk < _chunks.Length; chunk++)
            {
                starts.Add(_starts[chunk]);
                chunks.Add(_chunks[chunk]);
                count += _chunks[chunk].Length;
            }

            return new ViewSnapshot([.. starts], [.. chunks], count);
        }

        // The records at list indices [begin, stop), split into chunks of nearly equal size.
        private static void AppendChunks(
            SortedList<ulong, ViewRecord> views, int begin, int stop, List<ulong> starts, List<ViewRecord[]> chunks)
        {
            var total = stop - begin;
            if (total <= 0)
            {
                return;
            }

            var values = views.Values;
            var pieces = (total + ChunkRecords - 1) / ChunkRecords;
            var position = begin;
            for (var piece = 0; piece < pieces; piece++)
            {
                var length = (total - (position - begin)) / (pieces - piece);
                var records = new ViewRecord[length];
                for (var index = 0; index < length; index++)
                {
                    records[index] = values[position + index];
                }

                starts.Add(records[0].Address);
                chunks.Add(records);
                position += length;
            }
        }

        // The index of the first key at or above the address, or the count.
        private static int LowerBound(IList<ulong> keys, ulong address)
        {
            var low = 0;
            var high = keys.Count;
            while (low < high)
            {
                var middle = (low + high) >>> 1;
                if (keys[middle] < address)
                {
                    low = middle + 1;
                }
                else
                {
                    high = middle;
                }
            }

            return low;
        }
    }

    // The addresses whose records changed since the last publication, [low, high]; empty while
    // low > high. Noted by SetViewLocked and RemoveViewAtLocked, consumed by PublishSnapshot.
    private ulong _changedLow = ulong.MaxValue;
    private ulong _changedHigh;

    private void NoteViewChangeLocked(in ViewRecord record)
    {
        _changedLow = Math.Min(_changedLow, record.Address);
        _changedHigh = Math.Max(_changedHigh, record.Size == 0 ? record.Address : record.Address + (record.Size - 1));
    }
}
