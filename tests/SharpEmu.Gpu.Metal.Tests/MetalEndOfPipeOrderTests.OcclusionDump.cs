// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using Xunit;

namespace SharpEmu.Gpu.Metal.Tests;

public sealed unsafe partial class MetalEndOfPipeOrderTests
{
    // A dump's blocks are noted as one run, but a pending store into the bytes between them still
    // speaks for those bytes (UE4 zeroes a query's whole block with WRITE_DATA before the begin dump),
    // and the dump itself lands the same words either way.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AnOcclusionDumpLeavesAPendingStoreBetweenItsBlocksAndLandsItsOwnWords(bool coalesce)
    {
        if (!RequireDevice()) return;
        using var rig = new Rig(Device.Value);
        var host = rig.Host;
        host.NativeOcclusionEnabled = true;
        host.CoalesceOcclusionDumps = coalesce;
        const ulong query = GuestBase + 0x30000;
        const ulong ready = 1UL << 63;

        _ = host.EnsureComputeEncoder();
        host.RecordCommandProcessorWrite(query, Enumerable.Repeat((byte)0xAB, 256).ToArray(), "WRITE_DATA");
        Assert.True(host.DumpOcclusionCounter(0, query, 0xFFFF, 16));

        // Nothing has landed: the bytes between the blocks are still the store's, and the read is answered
        // from the pending store instead of waiting for the GPU to land it.
        _ = host.TakeOverlayReads();
        Span<byte> between = stackalloc byte[sizeof(ulong)];
        for (var block = 0; block < 16; block++)
        {
            Assert.True(host.TryReadGuest(query + (ulong)block * 16 + 8, between));
            Assert.Equal(0xABABABABABABABABUL, BitConverter.ToUInt64(between));
        }

        Assert.Equal(16, host.TakeOverlayReads());

        host.FlushCommandBuffer();
        WaitForGpu(host, host.LastWorkSerial);
        WaitForCompletions(host);
        for (var block = 0; block < 16; block++)
        {
            Assert.Equal(ready, rig.Backing.Read64(query + (ulong)block * 16));
            Assert.Equal(0xABABABABABABABABUL, rig.Backing.Read64(query + (ulong)block * 16 + 8));
        }
    }
}
