// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
namespace SharpEmu.Gpu.Metal;

internal sealed partial class MetalCommandStreamHost
{
    public override bool TryWriteGuest(ulong address, ReadOnlySpan<byte> source, string kind = "WRITE_BYTES")
    {
        // The queue owns these bytes; no packet scratch/stack span survives
        // admission, failure, or the interpreter's subsequent packet.
        ExecuteCommandMemoryOperation(SharpEmu.Libs.Gpu.GpuCommands.CommandMemoryOperation.Write(address, source, kind));
        return true;
    }

    public override void ExecuteGuestMemoryOperation(Action operation, string kind = "MEMORY_OPERATION", ulong address = 0, ulong bytes = 0, ulong value = 0) =>
        MetalVideoPresenter.ExecuteOrderedGuestMemoryOperation(operation, kind, address, bytes, value);

    public override void ExecuteCommandMemoryOperation(SharpEmu.Libs.Gpu.GpuCommands.CommandMemoryOperation operation) =>
        MetalVideoPresenter.ExecuteTypedGuestMemoryOperation(this, operation);

    internal void ExecuteCommandMemoryOperationAtQueuePosition(SharpEmu.Libs.Gpu.GpuCommands.CommandMemoryOperation operation) =>
        operation.Execute(this, transfer =>
        {
            if (transfer.Kind == SharpEmu.Libs.Gpu.GpuCommands.CommandMemoryOperation.OperationKind.Fill)
                FillBufferAtQueuePosition(transfer.Address, transfer.ByteCount, (uint)transfer.Value, transfer.DestinationIsGds);
            else CopyBufferAtQueuePosition(transfer.Address, transfer.SourceAddress, transfer.ByteCount,
                transfer.DestinationIsGds, transfer.SourceIsGds);
        });
}
