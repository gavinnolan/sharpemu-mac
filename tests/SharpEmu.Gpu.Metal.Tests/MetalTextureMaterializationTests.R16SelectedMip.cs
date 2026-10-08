// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
using System.Buffers.Binary;
using System.Collections;
using System.Reflection;
using SharpEmu.HLE;
using SharpEmu.Libs.Agc;
using SharpEmu.Libs.Gpu;
using SharpEmu.Libs.Gpu.Images;
using SharpEmu.ShaderCompiler.Metal.Tests;
using SharpEmu.ShaderCompiler.Resources;
using Xunit;
using DeviceShader = SharpEmu.ShaderCompiler.Metal.Tests.MetalNative;

namespace SharpEmu.Gpu.Metal.Tests;

public sealed partial class MetalTextureMaterializationTests
{
    private const ulong R16SelectedAddress = 0x802F430000;
    private static uint[] R16SelectedRaw(uint mip) =>
        [0x802F4300, 0xC0D00000, 0x01FFC1FF, 0x91B00204 | mip << 12 | mip << 16, 0, 0x007000A0, 0, 0];

    private static AgcExports.TranslatedImageBinding R16SelectedBinding(uint mip, uint[]? raw = null,
        bool storage = true, GuestImageAccess access = GuestImageAccess.Write, ShaderImageShape? shape = null)
    {
        raw ??= R16SelectedRaw(mip);
        Assert.True(AgcBackendServices.TryDecodeTextureDescriptor(raw, out var descriptor));
        return new(descriptor, storage, mip, [], ResourceDescriptor: raw,
            Shape: shape ?? new ShaderImageShape(false, false, storage, false, TextureNumericClass.Float), Access: access);
    }

    private IReadOnlyList<GuestDrawTexture> R16SelectedSnapshots(ICpuMemory memory,
        params AgcExports.TranslatedImageBinding[] bindings)
    {
        var actual = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture;
        var expected = Environment.GetEnvironmentVariable("SHARPEMU_TEST_EXPECT_ARCH");
        if (!string.IsNullOrEmpty(expected)) Assert.Equal(expected, actual.ToString());
        output.WriteLine($"R16 selected actual test CLR architecture={actual}, framework={System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}, expected={expected ?? "unset"}.");
        var snapshots = Presenter.Assembly.GetTypes().Single(static type => type.Name == "MetalTextureSnapshots");
        return (IReadOnlyList<GuestDrawTexture>)snapshots.GetMethod("CreateTextureSnapshots", Methods)!.Invoke(null,
            [new CpuContext(memory, Generation.Gen5), bindings, 0])!;
    }

    private sealed class R16SelectedMemory(byte[] bytes) : ICpuMemory
    {
        public readonly List<(ulong Address, int Bytes)> Reads = new();
        public Action? AfterCopy;
        public bool TryRead(ulong address, Span<byte> destination)
        {
            Reads.Add((address, destination.Length));
            if (address < R16SelectedAddress || address - R16SelectedAddress > (ulong)bytes.Length ||
                (ulong)destination.Length > (ulong)bytes.Length - (address - R16SelectedAddress)) return false;
            bytes.AsSpan((int)(address - R16SelectedAddress), destination.Length).CopyTo(destination);
            AfterCopy?.Invoke(); return true;
        }
        public bool TryWrite(ulong address, ReadOnlySpan<byte> source) => false;
    }

    private sealed class R16SelectedObservationScope : IDisposable
    {
        private readonly bool enabled = GuestImageWriteTracker.Enabled;
        public R16SelectedObservationScope()
        {
            GuestImageWriteTracker.Configure(true);
            GuestImageWriteTracker.TrackManagedWriter(R16SelectedAddress, R16SelectedMipOracle.ChainBytes);
        }
        public void Dispose()
        {
            GuestImageWriteTracker.Untrack(R16SelectedAddress);
            GuestImageWriteTracker.Configure(enabled);
        }
    }

    [Theory]
    [InlineData(0u)] [InlineData(1u)] [InlineData(2u)] [InlineData(3u)]
    public void R16SelectedSnapshotReadsExactPhysicalMipAndEveryIndependentHalfByte(uint mip)
    {
        using var observation = new R16SelectedObservationScope();
        var (guest, expected) = R16SelectedMipOracle.Seed(); var before = guest.ToArray();
        var memory = new R16SelectedMemory(guest);
        var texture = Assert.Single(R16SelectedSnapshots(memory, R16SelectedBinding(mip)));
        var width = 2048u >> (int)mip; var offset = R16SelectedMipOracle.Offsets[mip];
        Assert.Equal((R16SelectedAddress + (ulong)offset, expected[mip].Length), Assert.Single(memory.Reads));
        Assert.Equal(width, texture.Width); Assert.Equal(width, texture.Height); Assert.Equal(width, texture.Pitch);
        Assert.Equal(R16SelectedAddress, texture.Address); Assert.Equal(mip, texture.MipLevel); Assert.Equal(mip, texture.BaseMipLevel);
        Assert.Equal(11u, texture.ResourceMipLevels); Assert.Equal(1u, texture.MipLevels);
        Assert.Equal((ulong)offset + (ulong)expected[mip].Length, texture.SourceByteCount);
        Assert.Equal(R16SelectedRaw(mip), texture.Descriptor); Assert.Equal(expected[mip], texture.RgbaPixels);
        Assert.Equal(before, guest); guest.AsSpan().Fill(0x44); Assert.Equal(expected[mip], texture.RgbaPixels);
        var format = Presenter.GetMethod("StorageTextureFormat", Methods)!.Invoke(null, [texture])!;
        Assert.Equal(25u, Convert.ToUInt32(format.GetType().GetProperty("Format")!.GetValue(format)));
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)]
    [InlineData(5)] [InlineData(6)] [InlineData(7)] [InlineData(8)] [InlineData(9)]
    [InlineData(10)] [InlineData(11)] [InlineData(12)] [InlineData(13)]
    public void R16SelectedNearRawAndUnsupportedViewsRejectBeforeAnyGuestRead(int kind)
    {
        var mip = kind == 0 ? 4u : 0u; var raw = R16SelectedRaw(mip);
        if (kind == 1) raw = raw[..4];
        if (kind == 2) raw[6] = 1; // Unknown flags, decoded consistently.
        if (kind == 3) raw[7] = 1; // Metadata address.
        if (kind == 4) raw[5] = 0x006000A0; // PERF_MOD is absent from the decoded DTO; raw admission must catch it.
        if (kind == 5) raw[3] = (raw[3] & ~0xFFFu) | 0xFACu;
        if (kind == 6) raw[0] += 1; // Unaligned base.
        if (kind == 7) raw[2] ^= 1; // Actual decoded width differs.
        if (kind == 8) raw[3] = (raw[3] & ~0x1F00000u) | 9u << 20;
        if (kind == 9) raw = [.. raw, 0]; // Extra unqualified descriptor words.
        var binding = R16SelectedBinding(mip, raw,
            storage: kind != 10, access: kind == 11 ? GuestImageAccess.Read | GuestImageAccess.Write : GuestImageAccess.Write,
            shape: kind == 12 ? new ShaderImageShape(false, false, true, true, TextureNumericClass.Float) : null);
        if (kind == 13) binding = binding with { MipLevel = 1 }; // Compiled static selection disagrees with T#.
        var memory = new R16SelectedMemory([]);
        var failure = Assert.Throws<TargetInvocationException>(() => R16SelectedSnapshots(memory, binding));
        Assert.IsType<NotSupportedException>(failure.InnerException); Assert.Empty(memory.Reads);
    }

    [Fact]
    public void R16SelectedAllFourCanonicalIntervalsCoexistButSampledAndShiftedAliasesDecline()
    {
        using var queue = new SelectedMipQueueScope(); using var observation = new R16SelectedObservationScope();
        var (guest, _) = R16SelectedMipOracle.Seed(); var memory = new R16SelectedMemory(guest);
        for (uint mip = 0; mip < 4; mip++)
        {
            var snapshot = Assert.Single(R16SelectedSnapshots(memory, R16SelectedBinding(mip)));
            Assert.Equal((long)mip + 1, EnqueueSelectedMipInQueue(snapshot, "graphics"));
        }
        Assert.Equal(4, ((IDictionary)Presenter.GetField("_selectedStorageMipOwners", Methods)!.GetValue(null)!).Count);
        var beforeReads = memory.Reads.Count;
        var sampled = R16SelectedBinding(0, storage: false);
        Assert.IsType<NotSupportedException>(Assert.Throws<TargetInvocationException>(() => R16SelectedSnapshots(memory, sampled)).InnerException);
        var shifted = R16SelectedRaw(0); shifted[0] += 0x100; // Same-sized overlapping chain, different base.
        Assert.IsType<NotSupportedException>(Assert.Throws<TargetInvocationException>(() => R16SelectedSnapshots(memory, R16SelectedBinding(0, shifted))).InnerException);
        Assert.Equal(beforeReads, memory.Reads.Count);
        Assert.False(((IDictionary)Presenter.GetField("_guestImages", Methods)!.GetValue(null)!).Contains(R16SelectedAddress));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void R16SelectedPreservesGlobalMappingAndContentVetoBeforeAdmission(bool contentWrite)
    {
        using var queue = new SelectedMipQueueScope(); using var observation = new R16SelectedObservationScope();
        var (guest, _) = R16SelectedMipOracle.Seed(); var memory = new R16SelectedMemory(guest);
        var snapshot = Assert.Single(R16SelectedSnapshots(memory, R16SelectedBinding(0)));
        var epoch = typeof(HostMemory).GetField("_mappingGeneration", Methods)!; var before = HostMemory.MappingGeneration;
        try
        {
            if (contentWrite) GuestImageWriteTracker.NotifyManagedWrite(R16SelectedAddress + 0x2B0000, 2);
            else HostMemory.OnMappingChanged();
            var failure = Assert.Throws<TargetInvocationException>(() => EnqueueSelectedMip(snapshot));
            var cause = Assert.IsType<InvalidOperationException>(failure.InnerException);
            Assert.Contains(contentWrite ? "content-token-changed" : "global-mapping-epoch", cause.Message);
            Assert.Equal(0L, Presenter.GetField("_enqueuedGuestWorkSequence", Methods)!.GetValue(null));
            Assert.Empty((IDictionary)Presenter.GetField("_selectedStorageMipOwners", Methods)!.GetValue(null)!);
        }
        finally { epoch.SetValue(null, before); }
    }

    [Fact]
    public void R16SelectedShortMipZeroSourceCannotBeZeroFabricatedOrRebased()
    {
        using var observation = new R16SelectedObservationScope();
        var memory = new R16SelectedMemory(new byte[0xAB0000 - 1]);
        Assert.IsType<InvalidOperationException>(Assert.Throws<TargetInvocationException>(() =>
            R16SelectedSnapshots(memory, R16SelectedBinding(0))).InnerException);
        Assert.Equal((R16SelectedAddress + 0x2B0000, 0x800000), Assert.Single(memory.Reads));
    }

    [Fact]
    public void R16SelectedWriteDuringEachOwnedReadExhaustsExistingThreeAttempts()
    {
        using var observation = new R16SelectedObservationScope();
        var (guest, _) = R16SelectedMipOracle.Seed(); var memory = new R16SelectedMemory(guest);
        memory.AfterCopy = () => GuestImageWriteTracker.NotifyManagedWrite(R16SelectedAddress + 0x10000, 2);
        Assert.IsType<InvalidOperationException>(Assert.Throws<TargetInvocationException>(() =>
            R16SelectedSnapshots(memory, R16SelectedBinding(3))).InnerException);
        Assert.Equal(3, memory.Reads.Count);
    }

    [Fact]
    public void R16SelectedIncompatibleOverlappingChainsInOneDispatchCannotPublishEitherOwner()
    {
        using var queue = new SelectedMipQueueScope(); using var observation = new R16SelectedObservationScope();
        var (guest, _) = R16SelectedMipOracle.Seed(); var memory = new R16SelectedMemory(guest);
        var shifted = R16SelectedRaw(3); shifted[0] += 0x100;
        // Both copies precede admission, so no existing-owner lookup can hide
        // the missing collision between the temporary prepared dictionary keys.
        try
        {
            var snapshots = R16SelectedSnapshots(memory, R16SelectedBinding(3), R16SelectedBinding(3, shifted));
            AssertR16TestDispatchCannotPublish(snapshots);
        }
        finally { GuestImageWriteTracker.Untrack(R16SelectedAddress + 0x10000); }
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void R16SelectedMixedDsOverlapInOneDispatchRejectsInEitherPreparedOrder(bool dsFirst)
    {
        using var queue = new SelectedMipQueueScope(); using var observation = new R16SelectedObservationScope();
        var (guest, _) = R16SelectedMipOracle.Seed(); var memory = new R16SelectedMemory(guest);
        var raw = SelectedMipRaw(); raw[0] = (uint)(R16SelectedAddress >> 8);
        raw[1] = (raw[1] & ~0xFFu) | (uint)(R16SelectedAddress >> 40);
        Assert.True(AgcBackendServices.TryDecodeTextureDescriptor(raw, out var descriptor));
        var ds = new AgcExports.TranslatedImageBinding(descriptor, true, 1, [], ResourceDescriptor: raw,
            Shape: new ShaderImageShape(false, false, true, false, TextureNumericClass.Float),
            Access: GuestImageAccess.Read | GuestImageAccess.Write);
        var r16 = R16SelectedBinding(3);
        // Genuine producer snapshots share one memory identity. This test is
        // about conflicting physical ownership, not interpreting the R16 seed
        // as an actual DS resource or changing DS-only admission behavior.
        var snapshots = dsFirst ? R16SelectedSnapshots(memory, ds, r16) : R16SelectedSnapshots(memory, r16, ds);
        Assert.Equal(2, memory.Reads.Count);
        AssertR16TestDispatchCannotPublish(snapshots);
    }

    private static void AssertR16TestDispatchCannotPublish(IReadOnlyList<GuestDrawTexture> snapshots)
    {
        var type = Presenter.GetNestedType("ComputeGuestDispatch", BindingFlags.NonPublic)!;
        var work = Activator.CreateInstance(type, [0UL, null, snapshots.ToArray(), Array.Empty<GuestMemoryBuffer>(),
            1u, 1u, 1u, 0u, 0u, 0u, null])!;
        lock (Presenter.GetField("_gate", Methods)!.GetValue(null)!)
            Assert.IsType<NotSupportedException>(Assert.Throws<TargetInvocationException>(() =>
                Presenter.GetMethod("EnqueueGuestWorkLocked", Methods)!.Invoke(null, [work])).InnerException);
        Assert.Equal(0L, Presenter.GetField("_enqueuedGuestWorkSequence", Methods)!.GetValue(null));
        Assert.Equal(0, Presenter.GetField("_pendingGuestWorkCount", Methods)!.GetValue(null));
        Assert.Empty((IDictionary)Presenter.GetField("_selectedStorageMipOwners", Methods)!.GetValue(null)!);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)]
    public void R16SelectedOrdinaryImageInsideSpanningOrUnknownExtentRejectsBeforeNativeCreation(int kind)
    {
        using var queue = new SelectedMipQueueScope(); using var observation = new R16SelectedObservationScope();
        var (guest, _) = R16SelectedMipOracle.Seed();
        var snapshot = Assert.Single(R16SelectedSnapshots(new R16SelectedMemory(guest), R16SelectedBinding(0)));
        var address = kind == 0 ? R16SelectedAddress + 0x10000 : kind == 1 ? R16SelectedAddress - 0x10000 : 0x7100000000;
        var images = (IDictionary)Presenter.GetField("_guestImages", Methods)!.GetValue(null)!;
        var extents = (IDictionary)Presenter.GetField("_guestImageExtents", Methods)!.GetValue(null)!;
        Assert.False(images.Contains(address)); Assert.False(extents.Contains(address));
        var imageType = Presenter.GetNestedType("GuestImage", BindingFlags.NonPublic)!;
        var ordinary = Activator.CreateInstance(imageType, nonPublic: true)!;
        images.Add(address, ordinary); if (kind != 2) extents.Add(address, (256u, 256u, 0x20000UL));
        try
        {
            Assert.IsType<NotSupportedException>(Assert.Throws<TargetInvocationException>(() =>
                Presenter.GetMethod("EnsureStorageImage", Methods)!.Invoke(null, [(nint)0, snapshot])).InnerException);
            Assert.Same(ordinary, images[address]);
            Assert.Empty((IDictionary)Presenter.GetField("_selectedStorageMipImages", Methods)!.GetValue(null)!);
        }
        finally { images.Remove(address); extents.Remove(address); }
    }

    [Fact]
    public void R16SelectedKnownDisjointOrdinaryImageExtentPassesTheActualConstructionCollisionGuard()
    {
        using var queue = new SelectedMipQueueScope(); using var observation = new R16SelectedObservationScope();
        var (guest, _) = R16SelectedMipOracle.Seed();
        var snapshot = Assert.Single(R16SelectedSnapshots(new R16SelectedMemory(guest), R16SelectedBinding(0)));
        const ulong address = 0x7100000000;
        var images = (IDictionary)Presenter.GetField("_guestImages", Methods)!.GetValue(null)!;
        var extents = (IDictionary)Presenter.GetField("_guestImageExtents", Methods)!.GetValue(null)!;
        Assert.False(images.Contains(address)); Assert.False(extents.Contains(address));
        images.Add(address, Activator.CreateInstance(Presenter.GetNestedType("GuestImage", BindingFlags.NonPublic)!, nonPublic: true)!);
        extents.Add(address, (256u, 256u, 0x20000UL));
        try
        {
            lock (Presenter.GetField("_gate", Methods)!.GetValue(null)!)
                Presenter.GetMethod("RejectConflictingSelectedStorageConstructionLocked", Methods)!.Invoke(null, [SelectedMipAssociation(snapshot)]);
        }
        finally { images.Remove(address); extents.Remove(address); }
    }

    [Fact]
    public void R16SelectedSameViewCrossQueueStillRejectsBeforeQueueMutation()
    {
        using var queue = new SelectedMipQueueScope(); using var observation = new R16SelectedObservationScope();
        var (guest, _) = R16SelectedMipOracle.Seed();
        var snapshot = Assert.Single(R16SelectedSnapshots(new R16SelectedMemory(guest), R16SelectedBinding(0)));
        Assert.Equal(1, EnqueueSelectedMipInQueue(snapshot, "graphics"));
        Assert.IsType<NotSupportedException>(Assert.Throws<TargetInvocationException>(() => EnqueueSelectedMipInQueue(snapshot, "compute")).InnerException);
        Assert.Equal(1L, Presenter.GetField("_enqueuedGuestWorkSequence", Methods)!.GetValue(null));
        Assert.Equal(1, Presenter.GetField("_pendingGuestWorkCount", Methods)!.GetValue(null));
    }
}
