// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE.GpuMemory;
using SharpEmu.Libs.Gpu.Images;
using Xunit;

namespace SharpEmu.Gpu.Metal.Tests;

/// <summary>
/// A slice whose DCC claim stands is a repeat before anything is asked of it: no
/// pending-write query and no read of the metadata. Every way the metadata is rewritten
/// takes the claim away, and so does every recorded store that reaches it without
/// re-arming it; the slice is then scanned as before.
/// </summary>
public sealed unsafe partial class MetalDccMaterializationTests
{
    // A target lookup, a clear code in the metadata and a texture lookup: one clear, claimed.
    private static void ClaimClear(Rig rig)
    {
        rig.Lookup(ImageRole.ColorTarget);
        rig.FillMetadata(0x00);
        rig.Lookup();
        Assert.Equal(1, rig.Owner.Clears);
        rig.Cache.TakeDccRepeatsSkipped();
        rig.Owner.PendingQueries = 0;
        rig.Backing.MetadataBytesRead = 0;
    }

    [Fact]
    public void ClaimFirst_StandingClaimQueriesAndReadsNothing()
    {
        if (!RequireDevice()) return;
        using var rig = new Rig(Device.Value);
        ClaimClear(rig);

        rig.Lookup();
        rig.Lookup(ImageRole.ColorTarget);

        Assert.Equal(1, rig.Owner.Clears);
        Assert.Equal(2, rig.Cache.TakeDccRepeatsSkipped());
        Assert.Equal(0, rig.Owner.PendingQueries);
        Assert.Equal(0, rig.Backing.MetadataBytesRead);
    }

    // The claim stands across a pending write the metadata itself is not part of: the full
    // path would defer, and settle on the same repeat once the write lands.
    [Fact]
    public void ClaimFirst_StandingClaimIsARepeatWhileOtherWorkIsPending()
    {
        if (!RequireDevice()) return;
        using var rig = new Rig(Device.Value);
        ClaimClear(rig);
        rig.Owner.Pending = true;

        rig.Lookup(ImageRole.ColorTarget);
        rig.Owner.Pending = false;
        rig.Lookup();

        Assert.Equal(1, rig.Owner.Clears);
        Assert.Equal(0, rig.Owner.PendingQueries);
        Assert.Equal(0, rig.Cache.TakeDccClearsDropped());
    }

    [Fact]
    public void ClaimFirst_ClaimedLookupAllocatesNothing()
    {
        if (!RequireDevice()) return;
        using var rig = new Rig(Device.Value);
        ClaimClear(rig);
        var request = rig.Request(ImageRole.Texture);
        rig.Cache.FindImage(ref request);

        var before = GC.GetAllocatedBytesForCurrentThread();
        rig.Cache.FindImage(ref request);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0, allocated);
        Assert.Equal(0, rig.Backing.MetadataBytesRead);
    }

    public enum ClaimRewrite
    {
        CpuWrite,
        GpuWrite,
        HostFill,
        DmaFill,
        ClearMetadata,
        Release,
    }

    // Each rewrite re-arms the claim: the next lookup scans the slice and clears again.
    [Theory]
    [InlineData(ClaimRewrite.CpuWrite)]
    [InlineData(ClaimRewrite.GpuWrite)]
    [InlineData(ClaimRewrite.HostFill)]
    [InlineData(ClaimRewrite.DmaFill)]
    [InlineData(ClaimRewrite.ClearMetadata)]
    [InlineData(ClaimRewrite.Release)]
    public void ClaimFirst_RewriteScansTheSliceAgain(ClaimRewrite rewrite)
    {
        if (!RequireDevice()) return;
        using var rig = new Rig(Device.Value);
        ClaimClear(rig);

        switch (rewrite)
        {
            case ClaimRewrite.CpuWrite:
                Assert.True(((IGuestImageStore)rig.Cache).MarkCpuWrite(MetadataAddress + 0x40, 4));
                break;
            case ClaimRewrite.GpuWrite:
                rig.Cache.InvalidateMemoryFromGpu(MetadataAddress, SliceSize);
                break;
            case ClaimRewrite.HostFill:
                // The fill's bytes have landed by the lookup, so the slice reads settled.
                rig.Cache.RecordDccFill(MetadataAddress, SliceSize, 0);
                break;
            case ClaimRewrite.DmaFill:
                rig.Cache.TryAbsorbDccFill(MetadataAddress, SliceSize, 0);
                break;
            case ClaimRewrite.ClearMetadata:
                rig.Cache.ClearMetadata(MetadataAddress);
                break;
            case ClaimRewrite.Release:
                ((IGuestImageStore)rig.Cache).Unregister(ImageAddress, 64);
                break;
        }

        // A target lookup, so a released image comes back as an owned texture.
        rig.Lookup(ImageRole.ColorTarget);

        Assert.Equal(2, rig.Owner.Clears);
        Assert.True(rig.Owner.PendingQueries > 0);
        Assert.True(rig.Backing.MetadataBytesRead > 0);

        // Claimed again, and standing again.
        rig.Owner.PendingQueries = 0;
        rig.Backing.MetadataBytesRead = 0;
        rig.Lookup();
        Assert.Equal(2, rig.Owner.Clears);
        Assert.Equal(0, rig.Owner.PendingQueries);
        Assert.Equal(0, rig.Backing.MetadataBytesRead);
    }

    // A recorded store that does not re-arm (an image writeback, a staged-storage copy, a
    // label) only stops the claim standing: the slice is read once, found unchanged and
    // still claimed, and then stands again. A store past the slice start leaves it standing.
    [Theory]
    [InlineData(MetadataAddress, 8ul, true)]
    [InlineData(MetadataAddress - 8, 16ul, true)]
    [InlineData(MetadataAddress + 0x40, 8ul, false)]
    [InlineData(MetadataAddress + SliceSize, 8ul, false)]
    public void ClaimFirst_StoreWithoutReArmReadsTheSliceOnce(ulong address, ulong size, bool reads)
    {
        if (!RequireDevice()) return;
        using var rig = new Rig(Device.Value);
        ClaimClear(rig);

        rig.Cache.ForgetStandingDccClaims(address, size);
        rig.Lookup();

        Assert.Equal(1, rig.Owner.Clears);
        Assert.Equal(reads ? 1 : 0, rig.Owner.PendingQueries);
        Assert.Equal(reads ? 1 : 0, rig.Backing.MetadataBytesRead);

        rig.Owner.PendingQueries = 0;
        rig.Lookup();
        Assert.Equal(0, rig.Owner.PendingQueries);
        Assert.Equal(1, rig.Owner.Clears);
    }

    [Fact]
    public void ClaimFirst_UnboundedStoreStopsEveryClaimStanding()
    {
        if (!RequireDevice()) return;
        using var rig = new Rig(Device.Value);
        ClaimClear(rig);

        rig.Cache.ForgetAllStandingDccClaims();
        rig.Lookup();

        Assert.Equal(1, rig.Owner.Clears);
        Assert.Equal(1, rig.Owner.PendingQueries);
    }

    // A writeback encoded inside a lookup ends the encoders and records the staged stores
    // with the cache lock still held; forgetting the claims there must not take it again.
    [Fact]
    public void ClaimFirst_ForgettingUnderTheCacheLockDoesNotReEnterIt()
    {
        if (!RequireDevice()) return;
        using var rig = new Rig(Device.Value);
        ClaimClear(rig);

        rig.Cache.RunLockedForTest(() =>
        {
            rig.Cache.ForgetStandingDccClaims(MetadataAddress, 8);
            rig.Cache.ForgetAllStandingDccClaims();
        });
        rig.Lookup();

        Assert.Equal(1, rig.Owner.PendingQueries);
    }

    // While a recorded fill still reaches the slice the full path runs, so the fill is
    // settled (forgotten) the way it always was once its bytes land.
    [Fact]
    public void ClaimFirst_SliceWithARecordedFillTakesTheFullPath()
    {
        if (!RequireDevice()) return;
        using var rig = new Rig(Device.Value);
        rig.Lookup(ImageRole.ColorTarget);
        rig.Cache.RecordDccFill(MetadataAddress, SliceSize, 0);
        rig.Cache.InvalidateMemoryFromGpu(MetadataAddress, SliceSize);
        rig.Owner.Pending = true;
        rig.Lookup(ImageRole.ColorTarget);
        Assert.Equal(1, rig.Owner.Clears);

        rig.Owner.Pending = false;
        rig.Owner.PendingQueries = 0;
        rig.FillMetadata(0x00);
        rig.Lookup(ImageRole.ColorTarget);
        Assert.Equal(1, rig.Owner.PendingQueries);

        // Settled: the fill is gone and the claim stands from here.
        rig.Owner.PendingQueries = 0;
        rig.Lookup(ImageRole.ColorTarget);
        Assert.Equal(0, rig.Owner.PendingQueries);
        Assert.Equal(1, rig.Owner.Clears);
    }

    // The raw backing reaches no hook. Claim-first keeps the claim; the verify mode takes
    // the full path, which reads the new bytes, and reports the claim it disagrees with.
    [Fact]
    public void ClaimFirst_VerifyReportsAClaimTheBytesNoLongerHold()
    {
        if (!RequireDevice()) return;
        using var rig = new Rig(Device.Value);
        ClaimClear(rig);
        rig.FillMetadata(0x55);

        rig.Lookup();
        Assert.Equal(0, rig.Backing.MetadataBytesRead);
        Assert.Equal(0, rig.Cache.TakeDccClaimFirstMismatches());

        rig.Cache.DccClaimFirstVerify = true;
        rig.Lookup();
        Assert.True(rig.Backing.MetadataBytesRead > 0);
        Assert.Equal(1, rig.Cache.TakeDccClaimFirstMismatches());
    }

    [Fact]
    public void ClaimFirst_VerifyAgreesWithAClaimThatHolds()
    {
        if (!RequireDevice()) return;
        using var rig = new Rig(Device.Value);
        rig.Cache.DccClaimFirstVerify = true;
        ClaimClear(rig);

        rig.Lookup();
        rig.Lookup(ImageRole.ColorTarget);

        Assert.Equal(1, rig.Owner.Clears);
        Assert.Equal(2, rig.Owner.PendingQueries);
        Assert.Equal(0, rig.Cache.TakeDccClaimFirstMismatches());
    }
}
