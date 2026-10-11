// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Text;
using SharpEmu.HLE;
using SharpEmu.HLE.GpuMemory;

namespace SharpEmu.Gpu.Metal.V2;

/// <summary>
/// The GPU half of <see cref="GuestSlotTrap"/>. A page the CPU cannot write without
/// faulting is still writable by the GPU: the windows map the whole backing and the device
/// ignores host page protection, so a store that resolves to the wrong host address lands
/// in a trapped block with no fault at all, and the declared-range journal cannot see it
/// because the range the host declared was somewhere else entirely.
///
/// So the trapped blocks are snapshotted when a submission is committed and compared once
/// the GPU has signalled it. A block that changed across a submission, with no CPU fault
/// on its page in the same interval, was changed by that submission — and the bindings it
/// resolved are recorded beside it, each with the window and offset it actually resolved
/// to, which is where a mis-resolved address shows.
/// </summary>
internal sealed class MetalSlotTrap
{
    private const int MaxBlocks = 1024;
    private const int MaxPending = 8;
    private const int MaxBindingsPerSubmission = 512;
    private const int MaxReports = 16;

    internal readonly record struct Binding(
        string Owner, ulong Address, ulong Size, ulong Buffer, ulong Offset, ulong GpuAddress);

    private sealed class Pending
    {
        public ulong Serial;
        public bool InUse;
        public long CpuFaults;
        public readonly byte[][] Blocks = new byte[MaxBlocks][];
        public readonly ulong[] Bases = new ulong[MaxBlocks];
        public int BlockCount;
        public readonly List<Binding> Bindings = new(MaxBindingsPerSubmission);
    }

    private readonly ICpuMemory _memory;
    private readonly Pending[] _pending = new Pending[MaxPending];
    private readonly ulong[] _blocks = new ulong[MaxBlocks];
    private readonly byte[] _current;
    private readonly List<Binding> _recording = new(MaxBindingsPerSubmission);
    private readonly int _blockBytes;
    private int _cursor;
    private long _mismatches;
    private int _reports;

    public MetalSlotTrap(ICpuMemory memory)
    {
        _memory = memory;
        // The slot's own page by default: the whole block, times every adopted block,
        // times every submission, is more copying than a diagnostic should cost.
        // SHARPEMU_TRAP_SNAPSHOT_BYTES takes the whole table when that is wanted.
        _blockBytes = (int)Math.Max(8, Math.Min(GuestSlotTrap.SnapshotBytes, GuestSlotTrap.BlockSize));
        _current = new byte[_blockBytes];
        for (var index = 0; index < _pending.Length; index++)
        {
            _pending[index] = new Pending();
            for (var block = 0; block < MaxBlocks; block++)
            {
                _pending[index].Blocks[block] = new byte[_blockBytes];
            }
        }
    }

    public static bool Enabled => GuestSlotTrap.Armed;

    public long Mismatches => _mismatches;

    /// <summary>Notes a guest range this submission resolved, and what it resolved to.</summary>
    public void NoteBinding(string owner, ulong address, ulong size, ulong buffer, ulong offset, ulong gpuAddress)
    {
        if (_recording.Count >= MaxBindingsPerSubmission)
        {
            return;
        }

        _recording.Add(new Binding(owner, address, size, buffer, offset, gpuAddress));
    }

    /// <summary>Snapshots the trapped blocks and takes ownership of this submission's bindings.</summary>
    public void OnCommit(ulong serial)
    {
        var count = GuestSlotTrap.CopyBlocks(_blocks);
        if (count == 0)
        {
            _recording.Clear();
            return;
        }

        var slot = Take();
        slot.InUse = true;
        slot.Serial = serial;
        slot.CpuFaults = GuestSlotTrap.Faults;
        slot.BlockCount = 0;
        slot.Bindings.Clear();
        slot.Bindings.AddRange(_recording);
        _recording.Clear();
        for (var index = 0; index < count; index++)
        {
            var watched = _blocks[index] + GuestSlotTrap.Slot;
            if (!_memory.TryRead(watched, slot.Blocks[slot.BlockCount].AsSpan(0, _blockBytes)))
            {
                continue;
            }

            slot.Bases[slot.BlockCount] = watched;
            slot.BlockCount++;
        }

        if (slot.BlockCount == 0)
        {
            slot.InUse = false;
        }
    }

    /// <summary>Compares every snapshot the GPU has finished with.</summary>
    public void Verify(ulong completedSerial)
    {
        foreach (var slot in _pending)
        {
            if (slot.InUse && slot.Serial <= completedSerial)
            {
                Check(slot);
                slot.InUse = false;
            }
        }
    }

    private Pending Take()
    {
        for (var attempt = 0; attempt < _pending.Length; attempt++)
        {
            var slot = _pending[_cursor];
            _cursor = _cursor + 1 == _pending.Length ? 0 : _cursor + 1;
            if (!slot.InUse)
            {
                return slot;
            }
        }

        // Full: the oldest submission is checked early rather than dropped. An early check
        // can only miss a change, never invent one.
        var oldest = _pending[_cursor];
        Check(oldest);
        oldest.InUse = false;
        return oldest;
    }

    private void Check(Pending slot)
    {
        var cpuFaults = GuestSlotTrap.Faults - slot.CpuFaults;
        for (var index = 0; index < slot.BlockCount; index++)
        {
            var span = _current.AsSpan(0, _blockBytes);
            if (!_memory.TryRead(slot.Bases[index], span))
            {
                continue;
            }

            var before = slot.Blocks[index].AsSpan(0, _blockBytes);
            for (var offset = 0; offset < _blockBytes; offset += 8)
            {
                if (before.Slice(offset, 8).SequenceEqual(span.Slice(offset, 8)))
                {
                    continue;
                }

                // The slot's legitimate traffic is the allocator pushing and popping
                // pointers through it. When a value is named, only becoming that value is
                // the event worth a line.
                if (GuestSlotTrap.Value != 0 &&
                    BitConverter.ToUInt64(span[offset..]) != GuestSlotTrap.Value)
                {
                    continue;
                }

                _mismatches++;
                Report(slot, slot.Bases[index], (ulong)offset, before, span, cpuFaults);
                return;
            }
        }
    }

    private void Report(
        Pending slot, ulong blockBase, ulong offset, ReadOnlySpan<byte> before, ReadOnlySpan<byte> after, long cpuFaults)
    {
        if (++_reports > MaxReports)
        {
            return;
        }

        Console.Error.WriteLine(
            $"[METAL-V2][SLOT-TRAP] A watched slot changed across a submission: serial={slot.Serial} " +
            $"slot=0x{blockBase:X16} offset=0x{offset:X} " +
            $"was=0x{BitConverter.ToUInt64(before[(int)offset..]):X16} " +
            $"now=0x{BitConverter.ToUInt64(after[(int)offset..]):X16} " +
            $"cpu_faults_in_interval={cpuFaults} bindings={slot.Bindings.Count}" +
            (cpuFaults == 0 ? " (no CPU write faulted: the GPU wrote it)" : string.Empty));

        var text = new StringBuilder();
        foreach (var binding in slot.Bindings)
        {
            text.Clear();
            text.Append("[METAL-V2][SLOT-TRAP]   ").Append(binding.Owner)
                .Append(" guest=0x").Append(binding.Address.ToString("X16"))
                .Append("+0x").Append(binding.Size.ToString("X"))
                .Append(" buffer=0x").Append(binding.Buffer.ToString("X"))
                .Append(" offset=0x").Append(binding.Offset.ToString("X"))
                .Append(" gpu=0x").Append(binding.GpuAddress.ToString("X16"));
            Console.Error.WriteLine(text.ToString());
        }

        Console.Error.Flush();
    }
}
