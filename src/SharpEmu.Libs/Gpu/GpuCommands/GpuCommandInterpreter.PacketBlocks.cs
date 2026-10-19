// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.InteropServices;
using SharpEmu.Libs.VideoOut;

namespace SharpEmu.Libs.Gpu.GpuCommands;

// The command stream used to ask the host for every packet: about a dozen guarded reads per draw,
// each deciding afresh whether the GPU or a pending command-processor write may own those bytes.
// A host that can say so (ICommandStreamHost.SupportsCommandBlocks) instead hands over a run of
// command memory once, after the same decision for the whole run, together with a token naming the
// state the decision rests on. Packets are then served from the copy until:
//   - the host says the token is stale (a command-processor write, a recorded GPU store or an
//     image taking ownership of guest bytes: IsCommandBlockCurrent);
//   - this interpreter wrote guest memory itself (an atomic, a semaphore, a store, a fill or a
//     copy): the host does not see every one of those, so the block is dropped here;
//   - the slice ends. A block never outlives Process: whatever the guest CPU stored into the
//     buffer since is not observed by any counter (CPU stores reach nobody, and a store through
//     the raw backing reaches nobody either), so the next slice reads the buffer afresh.
// A guest CPU store into a buffer this slice has already copied is not seen until the next slice,
// the same as a command processor that has prefetched it.
// A refusal is kept the same way (a block that is only a refusal), so the packets of a region the
// host will not hand over go to the host one by one without asking for the block again at each.
public sealed partial class GpuCommandInterpreter
{
    // How much command memory one block holds: a dozen packets of a draw in the typical stream.
    internal const int PacketBlockDwords = 128;

    private sealed class PacketBlock
    {
        public readonly uint[] Words = new uint[PacketBlockDwords];
        public ulong Address;

        // Dwords the block covers; zero when it holds nothing (no block, or dropped).
        public int Count;

        // The host refused the region: its packets are read one at a time.
        public bool Refused;
        public CommandReadToken Token;
        public ulong Slice;
        public ulong Writes;

        public ulong End => Address + ((ulong)Count * sizeof(uint));
    }

    private readonly List<PacketBlock> _packetBlocks = new();
    private ulong _packetBlockSlice = 1;
    private ulong _packetBlockWrites;

    // SHARPEMU_GPU_PACKET_BLOCK=0 turns the blocks off for every interpreter; a test turns one on or off.
    internal bool UsePacketBlocks { get; set; }

    // SHARPEMU_GPU_PACKET_BLOCK_VERIFY: read each packet served from a block through the host as well.
    internal bool VerifyPacketBlocks { get; set; } = PacketBlockReads.Verify;

    /// <summary>Blocks the host handed over, and refusals it gave, since this interpreter began.</summary>
    internal long PacketBlockFills { get; private set; }

    internal long PacketBlockRefusals { get; private set; }

    // The interpreter itself wrote guest memory (or asked the host to, by a path the host's own
    // counters may not see): nothing it copied before this can be trusted for the packets after.
    private void NotePacketBlockWrite() => _packetBlockWrites++;

    private void BeginPacketBlockSlice() => _packetBlockSlice++;

    private void RecordHostEndOfPipe(in EndOfPipeWrite write)
    {
        NotePacketBlockWrite();
        _host.RecordEndOfPipe(in write);
    }

    // The dwords from packetAddress on, minDwords to maxDwords of them, read from the block of this
    // depth, filling it when the packet is not in it. Empty when blocks are off, the host refused the
    // region, or the packet cannot be had whole from a block: the caller reads it from the host.
    private ReadOnlySpan<uint> BlockWords(int depth, ulong packetAddress, uint remaining, uint minDwords, uint maxDwords)
    {
        if (!UsePacketBlocks || (packetAddress & (sizeof(uint) - 1)) != 0 || minDwords > PacketBlockDwords ||
            minDwords > remaining)
        {
            return default;
        }

        while (_packetBlocks.Count <= depth)
        {
            _packetBlocks.Add(new PacketBlock());
        }

        var block = _packetBlocks[depth];
        if (block.Count != 0 &&
            (block.Slice != _packetBlockSlice || block.Writes != _packetBlockWrites ||
             !_host.IsCommandBlockCurrent(in block.Token)))
        {
            block.Count = 0;
        }

        if (block.Count != 0 && packetAddress >= block.Address && packetAddress < block.End)
        {
            if (block.Refused)
            {
                return default;
            }

            var index = (int)((packetAddress - block.Address) / sizeof(uint));
            var available = (uint)(block.Count - index);
            if (available >= minDwords)
            {
                return ServeFromBlock(depth, block, packetAddress, index, Math.Min(available, maxDwords));
            }
        }

        // Fill from this packet on, no further than the cursor's end: the copy is never longer
        // than the buffer it serves.
        var count = (int)Math.Min(remaining, PacketBlockDwords);
        block.Count = 0;
        RenderPhaseProfile.RecordCommandRead(RenderPhaseProfile.CommandReadKind.Block, count * sizeof(uint));
        block.Refused = !_host.TryReadCommandBlock(
            packetAddress, MemoryMarshal.AsBytes(block.Words.AsSpan(0, count)), out block.Token);
        block.Address = packetAddress;
        block.Slice = _packetBlockSlice;
        block.Writes = _packetBlockWrites;
        block.Count = count;
        if (block.Refused)
        {
            PacketBlockRefusals++;
            return default;
        }

        PacketBlockFills++;
        return ServeFromBlock(depth, block, packetAddress, 0, Math.Min((uint)count, maxDwords));
    }

    private ReadOnlySpan<uint> ServeFromBlock(int depth, PacketBlock block, ulong packetAddress, int index, uint dwords)
    {
        var served = block.Words.AsSpan(index, (int)dwords);
        if (VerifyPacketBlocks && !VerifyBlockWords(depth, block, packetAddress, served))
        {
            return default;
        }

        return served;
    }

    // Reads the same words through the host and compares. On a difference the block is dropped and
    // the caller reads the packet from the host, so a stale block costs a log line, not a wrong packet.
    private bool VerifyBlockWords(int depth, PacketBlock block, ulong packetAddress, ReadOnlySpan<uint> served)
    {
        var direct = new uint[served.Length];
        if (!_host.TryReadGuest(packetAddress, MemoryMarshal.AsBytes(direct.AsSpan())))
        {
            return true;
        }

        var mismatch = served.CommonPrefixLength(direct);
        if (mismatch == served.Length)
        {
            return true;
        }

        if (PacketBlockReads.NoteVerifyMismatch() <= 64)
        {
            Console.Error.WriteLine(
                $"[PERF][VERIFY] packet-block queue={QueueId} depth={depth} address=0x{packetAddress:X16} " +
                $"dwords={served.Length} first-difference=+{mismatch} block=0x{served[mismatch]:X8} " +
                $"direct=0x{direct[mismatch]:X8} block-address=0x{block.Address:X16} block-dwords={block.Count}");
        }

        block.Count = 0;
        return false;
    }
}
