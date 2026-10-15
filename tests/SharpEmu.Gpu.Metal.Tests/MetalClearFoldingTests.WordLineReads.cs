// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Gpu.Metal.V2;
using SharpEmu.Libs.Gpu.Abstractions;
using SharpEmu.Libs.Gpu.Rendering;
using Xunit;

namespace SharpEmu.Gpu.Metal.Tests;

/// <summary>
/// Inside a materialization's word reads, a dword is served from a 64-byte line read once
/// when nothing pending covers any of the line; a line with owed command-processor bytes or
/// a pending GPU write leaves its words to the ordinary read, which sees the owed bytes and
/// waits where it always did.
/// </summary>
public sealed unsafe partial class MetalClearFoldingTests
{
    private const ulong LineTable = GuestBase + 0x50000;
    private const ulong PendingLine = GuestBase + 0x60000;

    [Fact]
    public void WordReadsOfACleanLineAreServedFromOneCheckedReadUntilAWriteIsRecorded()
    {
        if (!RequireDevice()) return;
        using var rig = new Rig(Device.Value);
        var host = rig.Host;
        for (var word = 0UL; word < 16; word++)
        {
            rig.WriteBacking32(LineTable + word * sizeof(uint), 0xD000u + (uint)word);
        }

        // A recorded write elsewhere puts the table inside the write bounds, so the
        // ordinary read would run its full pending-write test for every word.
        host.NoteGuestMemoryWrite(GuestBase + 0x70000, 0x100, MetalGpuWriteOrigin.StorageBuffer);
        var before = host.WordLineCounters;
        host.BeginGuestWordReads();
        for (var word = 0UL; word < 16; word++)
        {
            Assert.True(host.TryReadGuestWord(LineTable + word * sizeof(uint), out var value));
            Assert.Equal(0xD000u + (uint)word, value);
        }

        Assert.Equal((16L, 16L, 1L, 0L), Delta(host.WordLineCounters, before));

        // A write recorded anywhere drops the line: the next word is checked and read again.
        rig.WriteBacking32(LineTable, 0xD0D0D0D0);
        host.NoteGuestMemoryWrite(GuestBase + 0x71000, 0x100, MetalGpuWriteOrigin.StorageBuffer);
        Assert.True(host.TryReadGuestWord(LineTable, out var reread));
        Assert.Equal(0xD0D0D0D0u, reread);
        Assert.Equal((17L, 17L, 2L, 0L), Delta(host.WordLineCounters, before));
        host.EndGuestWordReads();

        // Outside the bracket every word is read afresh.
        rig.WriteBacking32(LineTable + 4, 0xE0E0E0E0);
        Assert.True(host.TryReadGuestWord(LineTable + 4, out var fresh));
        Assert.Equal(0xE0E0E0E0u, fresh);
        Assert.Equal((18L, 17L, 2L, 0L), Delta(host.WordLineCounters, before));
    }

    [Fact]
    public void ALineWithOwedOrPendingBytesLeavesItsWordsToTheOrdinaryRead()
    {
        if (!RequireDevice()) return;
        using var rig = new Rig(Device.Value);
        var host = rig.Host;
        rig.WriteBacking32(LineTable, 0x0BAD0BAD);
        rig.WriteBacking32(LineTable + 0x10, 0x12345678);
        rig.WriteBacking32(PendingLine + 8, 0x87654321);
        var before = host.WordLineCounters;
        ulong serial;
        using (host.BeginPreparation())
        {
            var target = rig.Target(TargetAddress);
            var attachment = ((IRenderHost)host).AcquireColorAttachment(in target);
            host.BeginRendering(Rig.PassOver(attachment.View));
            serial = host.SubmissionSerial;
            host.RecordCommandProcessorWrite(LineTable, BitConverter.GetBytes(0xC0FFEE11u), "WRITE_DATA");
            Assert.Equal(1, host.DeferredLabelCopyCount);
            host.NoteGuestMemoryWrite(PendingLine, sizeof(uint), MetalGpuWriteOrigin.StorageBuffer);

            host.BeginGuestWordReads();
            // The owed word reads as written; its neighbour in the line reads memory.
            Assert.True(host.TryReadGuestWord(LineTable, out var owed));
            Assert.Equal(0xC0FFEE11u, owed);
            Assert.True(host.TryReadGuestWord(LineTable + 0x10, out var neighbour));
            Assert.Equal(0x12345678u, neighbour);
            // A word beside a pending store in the same line is read without waiting.
            Assert.True(host.TryReadGuestWord(PendingLine + 8, out var beside));
            Assert.Equal(0x87654321u, beside);
            Assert.Equal((3L, 0L, 2L, 3L), Delta(host.WordLineCounters, before));
            Assert.Equal(serial, host.SubmissionSerial);
            Assert.True(host.RenderEncoderOpen);
            host.EndGuestWordReads();
            host.EndRendering();
        }

        // The pending store itself still waits for its submission.
        host.BeginGuestWordReads();
        Assert.True(host.TryReadGuestWord(PendingLine, out _));
        host.EndGuestWordReads();
        Assert.True(host.CompletedSerial >= serial);
    }

    private static (long, long, long, long) Delta(
        (long Reads, long Hits, long Fills, long Refused) after,
        (long Reads, long Hits, long Fills, long Refused) before) =>
        (after.Reads - before.Reads, after.Hits - before.Hits, after.Fills - before.Fills, after.Refused - before.Refused);
}
