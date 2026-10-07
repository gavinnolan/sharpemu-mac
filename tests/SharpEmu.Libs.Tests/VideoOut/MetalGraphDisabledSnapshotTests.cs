// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Reflection;
using SharpEmu.Gpu.Metal;
using SharpEmu.HLE;
using SharpEmu.Libs.Agc;
using SharpEmu.Libs.Gpu;
using SharpEmu.Libs.Gpu.Images;
using SharpEmu.Libs.Tests.Gpu.Scheduling;
using SharpEmu.ShaderCompiler;
using Xunit;

namespace SharpEmu.Libs.Tests.VideoOut;

[Collection(SchedulingStateCollection.Name)]
public sealed class MetalGraphDisabledSnapshotTests
{
    private const ulong Address = 0x71F0_0000_00;
    // Production linear snapshots retain four256-byte padded rows.
    private const ulong Footprint = 1024;

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, true, false)]
    [InlineData(false, true, true)]
    [InlineData(true, true, true)]
    public void OwnedSnapshotsDoNotRequireObservedTokensAndSurviveLaterTrackerRetirement(
        bool storage, bool tracking, bool managedWriter)
    {
        using var state = new MetalTextureSourceLeaseTests.State();
        var graph = typeof(MetalVideoPresenter).GetField("_textureSourceGraphEnabled", BindingFlags.Static | BindingFlags.NonPublic)!;
        var oldGraph = (bool)graph.GetValue(null)!;
        graph.SetValue(null, false);
        try
        {
            GuestImageWriteTracker.Untrack(Address);
            GuestImageWriteTracker.Configure(tracking);
            if (tracking)
            {
                if (managedWriter) GuestImageWriteTracker.TrackManagedWriter(Address, Footprint);
                else GuestImageWriteTracker.Track(Address, Footprint, protect: false);
            }
            Assert.True(AgcBackendServices.TryDecodeTextureDescriptor(MetalTextureSourceLeaseTests.ResourceWords(), out var descriptor));
            var binding = new AgcExports.TranslatedImageBinding(descriptor, storage, 0, [],
                ResourceDescriptor: MetalTextureSourceLeaseTests.ResourceWords(),
                Shape: new ShaderImageShape(false, false, storage, false, TextureNumericClass.Float),
                Access: storage ? GuestImageAccess.Write : GuestImageAccess.Read);
            var memory = new Memory();
            var copied = Assert.Single(MetalTextureSnapshots.CreateTextureSnapshots(
                new CpuContext(memory, Generation.Gen5), [binding], out var fallbacks));
            Assert.Equal(0, fallbacks);
            Assert.False(copied.IsFallback);
            Assert.True(copied.CpuSnapshotStable);
            Assert.Equal(Enumerable.Repeat((byte)0x37, (int)Footprint), copied.RgbaPixels);
            Assert.Equal(1, memory.Reads);
            // The fake guest address has no kernel-backed protection owner.
            // Ordinary watches cannot certify a graph source, but graph-false
            // owned CPU uploads preserve their existing generation-only path.
            Assert.Equal(tracking && managedWriter,
                GuestImageWriteTracker.TryGetObservedContentToken(Address, Footprint, out _));

            GuestImageWriteTracker.Untrack(Address);
            GuestImageWriteTracker.Configure(true);
            GuestImageWriteTracker.TrackManagedWriter(Address, Footprint);
            GuestImageWriteTracker.NotifyManagedWrite(Address, Footprint);
            memory.Value = 0xA5;
            var upload = MetalVideoPresenter.PrepareDrawTextureUpload(copied);
            Assert.Equal(Enumerable.Repeat((byte)0x37, (int)Footprint), upload.RgbaPixels);
            Assert.Equal(1, memory.Reads); // No re-read or fresh-token requirement at consumption.
            Assert.False(MetalVideoPresenter.TextureSourceGraphEnabled);
        }
        finally { graph.SetValue(null, oldGraph); }
    }

    private sealed class Memory : ICpuMemory
    {
        public int Reads;
        public byte Value = 0x37;
        public bool CanRead(ulong address, ulong size) => address >= Address && address - Address + size <= Footprint;
        public bool TryRead(ulong address, Span<byte> bytes)
        {
            Assert.True(CanRead(address, (ulong)bytes.Length));
            Reads++;
            bytes.Fill(Value);
            return true;
        }
        public bool TryWrite(ulong address, ReadOnlySpan<byte> bytes) => throw new InvalidOperationException("No native or guest writes in snapshot compatibility fixture.");
    }
}
