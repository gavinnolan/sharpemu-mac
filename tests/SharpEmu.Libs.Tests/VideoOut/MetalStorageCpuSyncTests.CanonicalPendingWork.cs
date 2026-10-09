// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
using System.Reflection;
using SharpEmu.Gpu.Metal;
using SharpEmu.HLE;
using Xunit;

namespace SharpEmu.Libs.Tests.VideoOut;

public sealed partial class MetalStorageCpuSyncTests
{
    // Scalar refusal controls only. Native sentinels never reach a selector,
    // cancellation, completion or positive canonical acknowledgment.
    [Theory]
    [InlineData("pending", "Pending")] [InlineData("batch", "Batch")]
    [InlineData("taken", "Taken")] [InlineData("executing", "Executing")]
    [InlineData("pending-batch", "Pending")] [InlineData("batch-taken", "Batch")]
    [InlineData("taken-executing", "Taken")] [InlineData("owner-pending", "None")]
    public void CanonicalPostReleaseWorkRefusalSavesFirstScalarWithoutQueryingLowerAuthority(string variant, string expected)
    {
        ActualArchitecture(); using var fixture = new PrecommitScalarFixture();
        var names = new[] { "_pendingGuestWorkCount", "_batchOpen", "_takenGuestWorkSequence", "_executingGuestWorkSequence" };
        var saved = names.Select(name => Field(name).GetValue(null)).ToArray();
        var fields = new[] { "TextureType", "Depth", "ArrayLength", "ContentVersion" };
        var imageSaved = fields.Select(name => PrecommitGet(fixture.Image, name)).ToArray();
        var reads = fixture.Memory.MappingQueries; var statuses = fixture.Memory.WatchQueries;
        var actionCalls = 0;
        try
        {
            Set(fixture.Image, "TextureType", 3u); Set(fixture.Image, "Depth", 1u); Set(fixture.Image, "ArrayLength", 1u);
            Field("_pendingGuestWorkCount").SetValue(null, 0); Field("_batchOpen").SetValue(null, false);
            Field("_takenGuestWorkSequence").SetValue(null, 0L); Field("_executingGuestWorkSequence").SetValue(null, 0L);
            if (variant.Contains("pending")) MetalVideoPresenter.SubmitOrderedGuestAction(() => actionCalls++, "pending guard control");
            if (variant.Contains("batch")) Field("_batchOpen").SetValue(null, true);
            if (variant.Contains("taken")) Field("_takenGuestWorkSequence").SetValue(null, 9L);
            if (variant.Contains("executing")) Field("_executingGuestWorkSequence").SetValue(null, 10L);
            if (variant == "owner-pending") Set(fixture.Image, "ContentVersion", 2);
            var reservation = Activator.CreateInstance(Presenter.GetNestedType("StorageWatchReservation", BindingFlags.NonPublic)!)!;
            object?[] args = [Address, fixture.Image, fixture.Debt, fixture.Origin, 1, (nint)123,
                null, null, null, reservation, true, false];
            lock (Field("_gate").GetValue(null)!)
                Assert.Equal(CpuMappedReadWatchStatus.Stale, InvokeVolume("GetExactCanonicalDirtyOwnerStatusCoreLocked", args));
            Assert.Equal(expected, args[8]!.ToString());
            Assert.Equal(variant == "owner-pending" ? "presenter-owner-session-or-version" : "pending-work-or-batch", args[6]);
            Assert.Null(args[7]); Assert.Equal(0, actionCalls);
            Assert.Equal(reads, fixture.Memory.MappingQueries); Assert.Equal(statuses, fixture.Memory.WatchQueries);
            Assert.Null(Field("_deferredCanonicalDirty").GetValue(null));
            if (variant != "owner-pending")
            {
                TargetInvocationException failure;
                lock (Field("_gate").GetValue(null)!)
                    failure = Assert.Throws<TargetInvocationException>(() =>
                        InvokeVolume("InspectCanonicalDirtyOwnerForAcknowledgmentLocked", Address, fixture.Image,
                            fixture.Debt, fixture.Origin, 1, (nint)123, null, null, reservation, true));
                Assert.Equal("Canonical dirty ordinary post-release owner changed: pending-work-or-batch", failure.InnerException!.Message);
                Assert.Equal(expected, failure.InnerException.Data["CanonicalDirtyFirstWorkGuard"]);
                Assert.Equal(variant.StartsWith("pending") ? "CanonicalDirtyPendingWork" : "InvalidOperationException", failure.InnerException.GetType().Name);
            }
            // The narrow bookkeeping bypass never skips the active encoding
            // guards or the lower original canonical debt check.
            args[11] = true;
            lock (Field("_gate").GetValue(null)!)
                Assert.Equal(CpuMappedReadWatchStatus.Stale, InvokeVolume("GetExactCanonicalDirtyOwnerStatusCoreLocked", args));
            if (variant == "pending") Assert.Equal("canonical-debt-or-version", args[6]);
            if (variant == "pending-batch") Assert.Equal("Batch", args[8]!.ToString());
            Assert.Equal(reads, fixture.Memory.MappingQueries); Assert.Equal(statuses, fixture.Memory.WatchQueries);
        }
        finally
        {
            for (var i = 0; i < names.Length; i++) Field(names[i]).SetValue(null, saved[i]);
            for (var i = 0; i < fields.Length; i++) Set(fixture.Image, fields[i], imageSaved[i]);
        }
    }
}
