// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
using System.Buffers.Binary;
using SharpEmu.Libs.Gpu.GpuCommands;

namespace SharpEmu.Gpu.Metal;

internal sealed partial class MetalCommandStreamHost
{
    private void WriteDeferredEndOfPipeLabel(in EndOfPipeWrite write)
    {
        var bytes = write.Kind switch
        {
            EndOfPipeWriteKind.Write32 or EndOfPipeWriteKind.WriteBack32 or
            EndOfPipeWriteKind.Interrupt32 or EndOfPipeWriteKind.InterruptWriteBack32 or
            EndOfPipeWriteKind.FlipWithWrite32 or EndOfPipeWriteKind.FlipWithInterruptWriteBack32 => 4,
            EndOfPipeWriteKind.Write64 or EndOfPipeWriteKind.WriteBack64 or
            EndOfPipeWriteKind.Interrupt64 or EndOfPipeWriteKind.InterruptWriteBack64 or
            EndOfPipeWriteKind.ClockWrite or EndOfPipeWriteKind.ClockWriteBack => 8,
            // GDS bytes were already copied after a separately successful synchronization.
            EndOfPipeWriteKind.InterruptOnly or EndOfPipeWriteKind.Flip or EndOfPipeWriteKind.GdsWrite32 => 0,
            _ => throw new NotSupportedException($"Metal end-of-pipe completion kind is unsupported: kind={write.Kind}."),
        };
        if (bytes == 0) return;
        if (write.Destination == 0 || (ulong)bytes > ulong.MaxValue - write.Destination)
            throw new InvalidOperationException($"Metal end-of-pipe label has an invalid range: address=0x{write.Destination:X16} bytes={bytes}.");
        Span<byte> payload = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(payload, write.Value);
        if (!Memory.TryWrite(write.Destination, payload[..bytes]))
            throw new InvalidOperationException($"Metal end-of-pipe label cannot write guest memory: address=0x{write.Destination:X16} bytes={bytes}.");
    }
}
