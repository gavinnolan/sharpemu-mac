// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
namespace SharpEmu.Gpu.Metal;

internal sealed partial class MetalCommandStreamHost
{
    public override bool TryWriteGuest(ulong address, ReadOnlySpan<byte> source, string kind = "WRITE_BYTES")
    {
        // The queue owns these bytes; no packet scratch/stack span survives
        // admission, failure, or the interpreter's subsequent packet.
        var payload = source.ToArray();
        ExecuteGuestMemoryOperation(() =>
        {
            if (!Memory.TryWrite(address, payload))
                throw new InvalidOperationException($"Metal command-stream memory write failed: address=0x{address:X16} bytes={payload.Length}.");
        }, kind, address, (ulong)payload.Length, MetalVideoPresenter.CommandMemoryTraceScalar(payload));
        return true;
    }

    public override void ExecuteGuestMemoryOperation(Action operation, string kind = "MEMORY_OPERATION", ulong address = 0, ulong bytes = 0, ulong value = 0) =>
        MetalVideoPresenter.ExecuteOrderedGuestMemoryOperation(operation, kind, address, bytes, value);
}
