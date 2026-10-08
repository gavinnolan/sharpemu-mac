// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
using System.Reflection;
using SharpEmu.Gpu.Metal;
using SharpEmu.HLE;
using Xunit;

namespace SharpEmu.Libs.Tests.Gpu.Metal;

public sealed partial class MetalRenderHostTests
{
    [Theory]
    [InlineData("original-content-unobserved", 0)]
    [InlineData("original-content-size", 0)]
    [InlineData("presenter-session", 0)]
    [InlineData("original-mapping-currentness", 1)]
    [InlineData("global-mapping-epoch", 1)]
    [InlineData("current-content-unobserved", 1)]
    [InlineData("original-content-token", 1)]
    public void NativeDepthOriginalVetoReportsFirstActualPredicateWithoutLaterQueries(string reason, int queries)
    {
        // All rows are managed negative controls. Only the last row enables a
        // managed-only watch; no page protection or native handle is created.
        const ulong address = 0x7FFE_0200_0000;
        var enabled = typeof(GuestImageWriteTracker).GetField("_enabled", BindingFlags.NonPublic | BindingFlags.Static)!;
        var before = enabled.GetValue(null);
        enabled.SetValue(null, reason == "original-content-token");
        try
        {
            var memory = new OriginalVetoMemory { Current = reason != "original-mapping-currentness" };
            var mapping = OriginalVetoMapping(address);
            var epoch = HostMemory.MappingGeneration;
            var token = default(GuestImageWriteTracker.ContentToken);
            if (reason == "original-content-token")
            {
                GuestImageWriteTracker.TrackManagedWriter(address, MetalDepthFacePlacement.FaceBytes, source: "depth-original-managed-negative");
                Assert.True(GuestImageWriteTracker.TryGetObservedContentToken(address, MetalDepthFacePlacement.FaceBytes, out token));
                token = token with { WatchGeneration = token.WatchGeneration + 1 }; // deliberately stale comparison tuple
            }
            var content = new MetalVideoPresenter.CpuTextureSnapshotObservation(
                reason == "global-mapping-epoch" ? unchecked(epoch - 1) : epoch,
                token, reason != "original-content-unobserved",
                reason == "original-content-size" ? 8UL : MetalDepthFacePlacement.FaceBytes);
            var session = MetalVideoPresenter.CurrentNativeDepthFaceSession;
            var original = new MetalDepthFacePlacement.Observation(memory, memory, mapping, content,
                reason == "presenter-session" ? unchecked(session - 1) : session,
                NativeFaceWords(0), address, MetalDepthFacePlacement.FaceBytes, address, 0);
            var error = Assert.Throws<NotSupportedException>(() =>
                MetalDepthFacePlacement.RequireOriginalCurrent(original, "managed-original-veto"));
            Assert.StartsWith("Metal native depth face lost its ORIGINAL mapping/session/content observation.", error.Message);
            Assert.Contains("reason=" + reason, error.Message);
            Assert.Contains("stage=require-original invocation=managed-original-veto", error.Message);
            Assert.Contains("selected_addr=0x7FFE02000000", error.Message);
            Assert.Equal(queries, memory.CurrentQueries);
            Assert.Same(mapping, original.Mapping); Assert.Equal(content, original.Content);
            // A global epoch failure must not consult the absent current token.
            if (reason == "global-mapping-epoch")
            {
                Assert.Contains($"mapping_before={unchecked(epoch - 1)} mapping_now={epoch}", error.Message);
                Assert.Contains("observed_now= token_before=", error.Message);
            }
            if (reason == "current-content-unobserved") Assert.Contains("observed_now=False", error.Message);
            if (reason == "original-content-token") Assert.Contains("observed_now=True", error.Message);
        }
        finally
        {
            if (reason == "original-content-token") GuestImageWriteTracker.UntrackWatchOnly(address);
            enabled.SetValue(null, before);
        }
    }

    [Fact]
    public void NativeDepthOriginalProviderExceptionRemainsTheOriginalCause()
    {
        const ulong address = 0x7FFE_0240_0000;
        var cause = new IOException("original provider failure");
        var memory = new OriginalVetoMemory { Failure = cause };
        var original = new MetalDepthFacePlacement.Observation(memory, memory, OriginalVetoMapping(address),
            new(HostMemory.MappingGeneration, default, true, MetalDepthFacePlacement.FaceBytes),
            MetalVideoPresenter.CurrentNativeDepthFaceSession, NativeFaceWords(0), address,
            MetalDepthFacePlacement.FaceBytes, address, 0);
        Assert.Same(cause, Record.Exception(() => MetalDepthFacePlacement.RequireOriginalCurrent(original)));
        Assert.Equal(1, memory.CurrentQueries);
    }

    private static CpuMappedCopyObservation OriginalVetoMapping(ulong address) => new(new object(), address,
        MetalDepthFacePlacement.FaceBytes, address, 8,
        [new(address, MetalDepthFacePlacement.FaceBytes, address, MetalDepthFacePlacement.FaceBytes,
            1, 1, MetalDepthFacePlacement.FaceBytes, 0, 16384, GuestPageProtection.Read | GuestPageProtection.Write)]);

    private sealed class OriginalVetoMemory : ICpuMemory, ICpuMappedCopySource
    {
        internal bool Current = true;
        internal Exception? Failure;
        internal int CurrentQueries;
        public bool TryRead(ulong address, Span<byte> bytes) => throw new InvalidOperationException("No byte read in original validation.");
        public bool TryWrite(ulong address, ReadOnlySpan<byte> bytes) => throw new InvalidOperationException("No byte write in original validation.");
        public CpuMappedCopyResult TryReadMappedCopy(ulong footprint, ulong count, ulong source, Span<byte> bytes,
            out CpuMappedCopyObservation? observation) => throw new InvalidOperationException("No new origin may be copied.");
        public bool IsMappedCopyObservationCurrent(CpuMappedCopyObservation observation)
        {
            CurrentQueries++;
            if (Failure is { } failure) throw failure;
            return Current;
        }
    }
}
