// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE.GpuMemory;
using Xunit;

namespace SharpEmu.Gpu.Metal.Tests;

public sealed unsafe partial class MetalDccMaterializationTests
{
    // A volume whose planes a settled scan found claimed whole is settled by the run's marker
    // while their first bytes read the same codes; with the markers off the full path reaches
    // the same repeats plane by plane.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void MaterializeDcc_ClaimedRunIsSettledByItsMarker(bool markers)
    {
        if (!RequireDevice()) return;
        using var rig = MarkerRig(markers);
        ClaimVolume(rig);

        LookupVolume(rig);
        LookupVolume(rig);

        Assert.Equal(1, rig.Owner.Clears);
        Assert.Equal(2 * VolumePlanes, (uint)rig.Cache.TakeDccRepeatsSkipped());
        Assert.Equal(markers ? 2 : 0, rig.Cache.TakeDccRunMarkerHits());
    }

    // A plane whose bytes changed under the marker, through the raw backing that reaches no
    // hook, sends the run through the full path, which clears it to its new code.
    [Fact]
    public void MaterializeDcc_ChangedPlaneUnderAMarkerIsScanned()
    {
        if (!RequireDevice()) return;
        using var rig = MarkerRig(markers: true);
        ClaimVolume(rig);

        FillVolumeMetadata(rig, [0x00, 0x00, 0x40, 0x00]);
        LookupVolume(rig);
        Assert.Equal(2, rig.Owner.Clears);
        Assert.Equal(0, rig.Cache.TakeDccRunMarkerHits());

        LookupVolume(rig);
        LookupVolume(rig);
        Assert.Equal(2, rig.Owner.Clears);
        Assert.Equal(1, rig.Cache.TakeDccRunMarkerHits());
    }

    // A CPU store into one plane re-arms its claim, and the run's marker goes with it: the
    // plane is cleared again, as the guest asked.
    [Fact]
    public void MaterializeDcc_ReArmedPlaneForgetsTheMarker()
    {
        if (!RequireDevice()) return;
        using var rig = MarkerRig(markers: true);
        ClaimVolume(rig);

        Assert.True(((IGuestImageStore)rig.Cache).MarkCpuWrite(MetadataAddress + 2 * SliceSize + 0x40, 4));
        LookupVolume(rig);

        Assert.Equal(2, rig.Owner.Clears);
        Assert.Equal(new uint[] { 2, 1 }, new[] { rig.Owner.ClearRanges[^1].BaseLayer, rig.Owner.ClearRanges[^1].LayerCount });
        Assert.Equal(0, rig.Cache.TakeDccRunMarkerHits());
    }

    // A scan that meets the metadata still being written defers the clear, and the deferral
    // forgets the marker; the next settled scan takes the claimed repeats (and the deferral)
    // through the full path, and only then is the marker taken again.
    [Fact]
    public void MaterializeDcc_DeferralForgetsTheMarker()
    {
        if (!RequireDevice()) return;
        using var rig = MarkerRig(markers: true);
        ClaimVolume(rig);

        rig.Owner.Pending = true;
        LookupVolume(rig);
        rig.Owner.Pending = false;
        LookupVolume(rig);
        Assert.Equal(0, rig.Cache.TakeDccRunMarkerHits());

        LookupVolume(rig);
        Assert.Equal(1, rig.Owner.Clears);
        Assert.Equal(1, rig.Cache.TakeDccRunMarkerHits());
    }

    private static Rig MarkerRig(bool markers)
    {
        var rig = new Rig(Device.Value);
        rig.Cache.DccClaimFirst = false;
        rig.Cache.DccRunMarkers = markers;
        return rig;
    }

    // The volume's planes cleared to black in one pass, and then found claimed whole by a
    // settled scan, which takes the run's marker. The counters start from zero after it.
    private static void ClaimVolume(Rig rig)
    {
        FillVolumeMetadata(rig, [0x55, 0x55, 0x55, 0x55]);
        LookupVolume(rig);
        FillVolumeMetadata(rig, [0x00, 0x00, 0x00, 0x00]);
        LookupVolume(rig);
        Assert.Equal(1, rig.Owner.Clears);
        LookupVolume(rig);
        Assert.Equal(1, rig.Owner.Clears);
        Assert.Equal(0, rig.Cache.TakeDccRunMarkerHits());
        rig.Cache.TakeDccRepeatsSkipped();
    }

    private static void LookupVolume(Rig rig)
    {
        var request = VolumeRequest();
        rig.Cache.FindImage(ref request);
    }
}
