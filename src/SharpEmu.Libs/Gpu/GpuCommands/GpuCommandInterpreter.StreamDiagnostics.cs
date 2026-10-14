// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace SharpEmu.Libs.Gpu.GpuCommands;

public sealed partial class GpuCommandInterpreter
{
    // Runs only on a fatal: the stream around the cursor, the cursor stack, and a re-read of
    // the packet the walk started from. Words that changed since that read prove a rewrite.
    private void ReportStreamContext(string reason)
    {
        var execution = _execution;
        if (execution is null || execution.IsEmpty)
        {
            Console.Error.WriteLine($"[GPU][FATAL] {reason}: no cursor is loaded (queue={QueueId}).");
            DumpPacketTrace();
            return;
        }

        var topIndex = execution.Depth - 1;
        var cursor = execution.At(topIndex);
        DumpUnknownPacket(cursor, cursor.Offset);
        DumpPacketTrace();

        var packetAddress = cursor.Address + ((ulong)cursor.Offset * sizeof(uint));
        Console.Error.WriteLine(
            $"[GPU][FATAL] {reason}: queue={QueueId} depth={execution.Depth} kind={DescribeCursorKind(execution, topIndex)} " +
            $"buffer=0x{cursor.Address:X16} dwords={cursor.DwordCount} offset=0x{cursor.Offset:X5} " +
            $"packet=0x{packetAddress:X16} ringChunkBase=0x{cursor.RingChunkBase:X16}");
        for (var index = 0; index <= topIndex; index++)
        {
            var frame = execution.At(index);
            Console.Error.WriteLine(
                $"\tcursor[{index}] kind={DescribeCursorKind(execution, index)} buffer=0x{frame.Address:X16} " +
                $"dwords={frame.DwordCount} offset=0x{frame.Offset:X5}");
        }

        if (topIndex >= _payloadScratchByDepth.Count)
        {
            return;
        }

        // The window read when the walk reached this packet is still in the depth's scratch.
        var scratch = _payloadScratchByDepth[topIndex];
        var header = scratch[0];
        var length = PacketHeader.Length(header);
        var covered = (int)Math.Min(Math.Min(length, PacketWindowDwords), cursor.Remaining);
        var reread = new uint[covered];
        if (!_host.TryReadGuest(packetAddress, MemoryMarshal.AsBytes(reread.AsSpan())))
        {
            Console.Error.WriteLine($"[GPU][FATAL] The packet cannot be re-read: address=0x{packetAddress:X16}.");
            return;
        }

        var changed = 0;
        var text = new System.Text.StringBuilder();
        for (var index = 0; index < covered; index++)
        {
            text.Append(index == 0 ? " " : ", ").Append($"{scratch[index]:X8}");
            if (scratch[index] != reread[index])
            {
                changed++;
                text.Append($"->{reread[index]:X8}");
            }
        }

        Console.Error.WriteLine(
            $"[GPU][FATAL] Packet as read: header=0x{header:X8} type={PacketHeader.PacketType(header)} " +
            $"opcode=0x{PacketHeader.Opcode(header):X2} length={length} payloadDwords={covered - 1} " +
            $"dwords={text}");
        Console.Error.WriteLine(
            changed == 0
                ? "[GPU][FATAL] Re-read matches: the words did not change under the worker."
                : $"[GPU][FATAL] The guest rewrote this packet while it was being read: {changed} of {covered} dwords changed.");
    }

    private static string DescribeCursorKind(PacketCursorStack execution, int index)
    {
        if (execution.At(index).FollowedChunkAdvance)
        {
            return "followed-ring-chunk";
        }

        return index == 0 ? "top-level-dcb" : "indirect-buffer";
    }

    private Exception FatalUnknownEvent(uint eventType, uint eventIndex)
    {
        ReportStreamContext("Unknown event");
        return _host.Fatal($"The event type is unknown: type=0x{eventType:X8} index=0x{eventIndex:X8}.");
    }
}
