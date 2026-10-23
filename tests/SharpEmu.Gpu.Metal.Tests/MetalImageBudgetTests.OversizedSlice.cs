// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Gpu.Metal.V2;
using SharpEmu.HLE.GpuMemory;
using SharpEmu.Libs.Gpu.Images;
using Xunit;

namespace SharpEmu.Gpu.Metal.Tests;

/// <summary>
/// A depth slice larger than a transfer piece. The depth kernels move a slice only whole and
/// a run used to be cut no larger than a piece, so Silent Hill's 8192² D16 (128 MB, one
/// slice, past the 32 MB piece) was neither uploaded nor written back. Here the piece is
/// lowered under a 1024² D16's 2 MB slice instead, and the slice has to land byte for byte
/// in the texture and come back out of it.
/// </summary>
public sealed partial class MetalImageBudgetTests
{
    private const ulong SmallTransferPieceBytes = 256 * 1024;

    // One window holds the slice: it moves in place, as one run past the piece size.
    [Fact]
    public void ADepthSliceLargerThanAPieceRoundTripsThroughItsWindow()
    {
        if (!RequireDevice()) return;
        var written = OversizedSliceRoundTrip(layout: null, out var carry, out var counts);

        Assert.Equal(written.Expected, written.Actual);
        Assert.Equal((1L, 0L), (counts.Writebacks, counts.Fallbacks));
        Assert.Equal((0L, 0L, 0L), carry);
    }

    // No window holds the slice (64 KB pieces of it scattered over the backing): it is
    // staged whole, through the ring up and through scratch back.
    [Fact]
    public void ADepthSliceLargerThanAPieceRoundTripsStagedWhenNoWindowHoldsIt()
    {
        if (!RequireDevice()) return;
        var size = D16TargetRequest().Description.Data.Size;
        var written = OversizedSliceRoundTrip(
            backing =>
            {
                backing.ScatterStart = ShadowMap;
                backing.ScatterBytes = size;
            },
            out var carry, out var counts);

        Assert.Equal(written.Expected, written.Actual);
        Assert.Equal((1L, 0L), (counts.Writebacks, counts.Fallbacks));
        Assert.Equal((1L, 0L, 0L), carry);
    }

    // A ring cap below the slice as well: the slice is staged whole past it, where a ring
    // copy of its prefix would be refused as shorter than the run.
    [Fact]
    public void ADepthSliceLargerThanTheRingCapIsStagedWholeOnUpload()
    {
        if (!RequireDevice()) return;
        var size = D16TargetRequest().Description.Data.Size;
        var written = OversizedSliceRoundTrip(
            backing =>
            {
                backing.ScatterStart = ShadowMap;
                backing.ScatterBytes = size;
            },
            out var carry, out var counts, ringCapBytes: size / 2);

        Assert.Equal(written.Expected, written.Actual);
        Assert.Equal((1L, 0L), (counts.Writebacks, counts.Fallbacks));
        Assert.Equal((1L, 0L, 0L), carry);
    }

    // Guest bytes uploaded into a 1024² D16 target with the piece below its slice, rendered
    // into (GPU-written), scribbled over in guest memory, then published by a store: what
    // guest memory holds afterwards is what was uploaded.
    private (byte[] Expected, byte[] Actual) OversizedSliceRoundTrip(
        Action<Backing>? layout, out (long StagedRuns, long Partial, long Unrecorded) carry,
        out MetalImageCache.StorePublishCounters counts, ulong? ringCapBytes = null)
    {
        using var rig = new Rig(Device.Value, layout: layout, ringCapBytes: ringCapBytes);
        rig.Cache.TransferPieceBytes = SmallTransferPieceBytes;
        var request = D16TargetRequest();
        var size = request.Description.Data.Size;
        Assert.True(size > SmallTransferPieceBytes);
        var expected = MetalTileOracle.Pattern((int)size, 0x4D);
        rig.WriteGuest(ShadowMap, expected);
        var identifier = rig.Cache.FindImage(ref request);
        rig.Cache.AcquireDepthTargetView(identifier, in request);
        Assert.True(rig.Cache.GetImage(identifier).IsGpuModified);
        Assert.False(rig.Cache.GetImage(identifier).IsCpuDirty);
        rig.Harness.Submit();
        rig.Harness.Begin();
        rig.WriteGuest(ShadowMap, MetalTileOracle.Pattern((int)size, 0xB2));
        rig.CacheOwner.RunOnWorker = work =>
        {
            work();
            return true;
        };
        rig.CacheOwner.WaitOffWorker = SubmitAndComplete(rig);

        Assert.True(((IGuestImageStore)rig.Cache).MarkCpuWrite(ShadowMap + StoreOffset, 8));
        carry = rig.Cache.WritebackCarryCounts;
        counts = rig.Cache.StorePublishCounts;
        return (expected, rig.ReadGuest(ShadowMap, size));
    }
}
