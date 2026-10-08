// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Reflection;
using SharpEmu.HLE;
using Xunit;

namespace SharpEmu.Gpu.Metal.Tests;

public sealed partial class MetalTextureMaterializationTests
{
    [Theory]
    [InlineData(0, "presenter-session")]
    [InlineData(1, "global-mapping-epoch")]
    [InlineData(2, "content-token-missing")]
    [InlineData(3, "content-token-changed")]
    [InlineData(4, "presenter-session")] // first veto when all three are changed
    public void SelectedMipAdmissionDiagnosticIdentifiesActualVetoWithoutPublishingWork(int kind, string expectedReason)
    {
        using var state = new SelectedMipQueueScope();
        using var observation = new SelectedMipObservationScope();
        var mapping = typeof(HostMemory).GetField("_mappingGeneration", Methods)!;
        var beforeEpoch = HostMemory.MappingGeneration;
        var (bytes, _) = SelectedMipSeed();
        var texture = Assert.Single(SelectedMipSnapshots(new CountingSelectedMemory(bytes)));
        Assert.True(GuestImageWriteTracker.TryGetContentToken(SelectedMipAddress, out var beforeToken));
        var originalAssociation = SelectedMipAssociation(texture);
        var originalSession = (long)originalAssociation.GetType().GetProperty("Session")!.GetValue(originalAssociation)!;
        var domain = originalAssociation.GetType().GetProperty("Key")!.GetValue(originalAssociation)!;
        var domainId = (long)domain.GetType().GetProperty("Domain")!.GetValue(domain)!;
        try
        {
            if (kind is 0 or 4)
                Presenter.GetMethod("ReleaseSelectedStorageMipImages", Methods)!.Invoke(null, null);
            if (kind is 1 or 4)
                HostMemory.OnMappingChanged(); // Controlled scalar invalidation, no kernel mapping/protection change.
            if (kind is 2 or 4) GuestImageWriteTracker.Untrack(SelectedMipAddress);
            if (kind == 3) GuestImageWriteTracker.NotifyManagedWrite(SelectedMipAddress + 131072, 4);
            var currentEpoch = HostMemory.MappingGeneration;
            var present = GuestImageWriteTracker.TryGetContentToken(SelectedMipAddress, out var currentToken);
            var exception = Assert.Throws<TargetInvocationException>(() => EnqueueSelectedMip(texture));
            var cause = Assert.IsType<InvalidOperationException>(exception.InnerException);
            Assert.Contains($"reason={expectedReason} stage=admission", cause.Message);
            Assert.Contains($"addr=0x{SelectedMipAddress:X} selected_mip=1 domain={domainId}", cause.Message);
            if (kind is 0 or 4)
            {
                var currentSession = (long)Presenter.GetField("_selectedStorageMipSession", Methods)!.GetValue(null)!;
                Assert.Contains($"session_before={originalSession} session_now={currentSession}", cause.Message);
                Assert.DoesNotContain("mapping_now", cause.Message); Assert.DoesNotContain("token_present", cause.Message);
            }
            else if (kind == 1)
            {
                Assert.Contains($"mapping_before={beforeEpoch} mapping_now={currentEpoch}", cause.Message);
                Assert.DoesNotContain("token_present", cause.Message);
            }
            else
            {
                Assert.Contains($"token_present={present}", cause.Message);
                Assert.Contains($"watch_identity_before={beforeToken.WatchIdentity}", cause.Message);
                Assert.Contains($"watch_generation_before={beforeToken.WatchGeneration}", cause.Message);
                Assert.Contains($"watch_identity_now={currentToken.WatchIdentity}", cause.Message);
                Assert.Contains($"watch_generation_now={currentToken.WatchGeneration}", cause.Message);
                Assert.Contains($"watch_bytes_before={beforeToken.WatchByteCount}", cause.Message);
                Assert.Contains($"watch_bytes_now={currentToken.WatchByteCount}", cause.Message);
            }
            Assert.Equal(0L, Presenter.GetField("_enqueuedGuestWorkSequence", Methods)!.GetValue(null));
            Assert.Equal(0, Presenter.GetField("_pendingGuestWorkCount", Methods)!.GetValue(null));
            Assert.Equal(0UL, Presenter.GetField("_pendingGuestWorkBytes", Methods)!.GetValue(null));
        }
        finally
        {
            // This isolated worker mocked only the epoch scalar. Restoring it
            // does not claim a native mapping lifetime or a protection receipt.
            mapping.SetValue(null, beforeEpoch);
        }
    }
}
